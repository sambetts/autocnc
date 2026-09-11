// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	public sealed partial class MainForm
	{
		Label battleFeedbackSummary;
		Button battleFeedbackButton;
		Button trainingFeedbackButton;

		Control BuildProvingGround()
		{
			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0)
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			var feedback = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0)
			};
			feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			battleFeedbackSummary = new Label
			{
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = CommandTheme.Muted,
				UseMnemonic = false, AccessibleName = "Latest battle feedback status"
			};
			feedback.Controls.Add(battleFeedbackSummary, 0, 0);
			battleFeedbackButton = new ActionButton
			{
				Text = "Add feedback", Primary = true,
				AccessibleName = "Review latest battle feedback", Margin = new Padding(3, 8, 3, 8)
			};
			battleFeedbackButton.Click += (_, _) => ReviewBattleFeedback(lastRun);
			feedback.Controls.Add(battleFeedbackButton, 0, 1);
			feedback.Controls.Add(new Label
			{
				Text = "Manual rendered battles ask for feedback when they finish. For headless battles, watch the recorded replay first.",
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = CommandTheme.Muted
			}, 0, 2);
			layout.Controls.Add(Group("YOUR BATTLE FEEDBACK", feedback), 0, 0);
			layout.Controls.Add(BuildBattleGroup(), 0, 1);
			return layout;
		}

		void UpdateFeedbackActions(bool busy)
		{
			var run = LastRunMatchesSelectedBot() ? lastRun : null;
			battleFeedbackButton.Enabled = !busy && BattleFeedback.CanReview(run) &&
				(!run.NeedsReplayForFeedback || repo != null);
			battleFeedbackButton.Text = BattleFeedback.ActionText(run);
			trainingFeedbackButton.Enabled = !busy && BattleFeedback.CanReview(trainingRun) &&
				(!trainingRun.NeedsReplayForFeedback || repo != null);
			trainingFeedbackButton.Text = BattleFeedback.ActionText(trainingRun);

			battleFeedbackSummary.Text = run == null ? BattleFeedback.Description(null)
				: $"{run.Manifest.CreatedUtc.ToLocalTime():g} / " +
					$"{run.Manifest.Result?.Outcome ?? run.Manifest.Status} / {BattleFeedback.Status(run)}\n" +
					BattleFeedback.Description(run, busy);
			battleFeedbackSummary.ForeColor = run?.HasRecordedBattle == true && !run.HasPlayerFeedback
				? CommandTheme.Amber : CommandTheme.Muted;
		}

		bool ReviewBattleFeedback(TrainingRun run, bool beforeImprovement = false)
		{
			if (runner.IsRunning || continuousLoop.IsRunning || run?.HasRecordedBattle != true)
			{
				Status("Feedback needs a completed, recorded battle and no operation in progress.");
				return false;
			}

			if (!beforeImprovement && run.NeedsReplayForFeedback)
			{
				WatchReplay(run);
				return false;
			}

			using var dialog = new BattleFeedbackDialog(run, value => SavePlayerFeedback(run, value),
				beforeImprovement);
			var answer = dialog.ShowDialog(resultsWindow?.ContainsFocus == true ? resultsWindow : this);
			if (answer == DialogResult.Retry)
			{
				WatchReplay(run, improveAfterWatching: beforeImprovement);
				return false;
			}

			return answer is DialogResult.OK or DialogResult.Ignore;
		}

		void SavePlayerFeedback(TrainingRun run, string feedback)
		{
			if (runner.IsRunning || continuousLoop.IsRunning)
				throw new InvalidOperationException("Wait for the current operation to finish before saving feedback.");

			run.SetPlayerFeedback(feedback);
			RefreshFeedbackRun(run);
			try
			{
				if (repo != null && run.IsEditable &&
					File.Exists(run.BattleLogPath) && File.Exists(run.TelemetryPath) &&
					File.Exists(run.DecisionTracePath))
					EnsureAgentContext(run);
			}
			catch (IOException ex)
			{
				ReportFeedbackContextFailure(ex);
			}
			catch (UnauthorizedAccessException ex)
			{
				ReportFeedbackContextFailure(ex);
			}
			catch (InvalidOperationException ex)
			{
				ReportFeedbackContextFailure(ex);
			}
		}

		void ReportFeedbackContextFailure(Exception error)
		{
			var message = "Feedback saved, but the agent inputs could not be refreshed: " + error.Message +
				" Analyze & improve will retry preparing them.";
			Append(message);
			Status(message);
		}

		void RefreshFeedbackRun(TrainingRun run)
		{
			if (lastRun != null && SamePath(run.RunDirectory, lastRun.RunDirectory))
				lastRun = run;
			if (trainingRun != null && SamePath(run.RunDirectory, trainingRun.RunDirectory))
				trainingRun = run;
			if (loadedHistory.Runs.Any(existing => SamePath(existing.RunDirectory, run.RunDirectory)))
				loadedHistory = loadedHistory.WithRun(run);
			RefreshResultsHistory();
			UpdateEnabledState();
		}

		void WatchReplay(TrainingRun run, bool improveAfterWatching = false)
		{
			if (repo == null || runner.IsRunning || continuousLoop.IsRunning ||
				run?.HasRecordedBattle != true || !File.Exists(run.ReplayPath))
			{
				Status("This battle's recorded replay is unavailable, or another operation is running.");
				return;
			}

			Save();
			ClearOutput();
			queue.Clear();
			queue.Enqueue(new ScriptJob
			{
				Title = $"Watching battle {run.Manifest.CreatedUtc.ToLocalTime():g}",
				ScriptPath = repo.LaunchScript,
				Arguments = ["-Replay", run.ReplayPath],
				Completed = code => FinishReplayReview(run, code, improveAfterWatching)
			});
			RunNext();
		}

		void FinishReplayReview(TrainingRun run, int exitCode, bool improveAfterWatching)
		{
			if (!run.RecordReplayPlayback(exitCode, stopRequested || closing))
				return;

			RefreshFeedbackRun(run);
			if (run.HasPlayerFeedback && !improveAfterWatching)
				return;

			// Let JobFinished drain the replay job before a dialog can start an improvement job.
			BeginInvoke((Action)(() =>
			{
				if (closing || runner.IsRunning || continuousLoop.IsRunning)
					return;
				var sameBattle = RunMatchesSelectedBot(run) &&
					SamePath(run.RunDirectory, trainingRun?.RunDirectory);
				var improve = improveAfterWatching && sameBattle;
				if (ReviewBattleFeedback(run, beforeImprovement: improve) && improve)
					ImproveBot(feedbackReviewed: true);
				else if (improveAfterWatching && !sameBattle)
					Status("Replay reviewed. Select the original bot and battle before starting its improvement.");
			}));
		}
	}
}
