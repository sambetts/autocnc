// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	public sealed partial class MainForm
	{
		TrainingRun trainingRun;
		TrainingRun activeTrainingRun;
		ComboBox trainingBattleBox;
		Label trainingBattleSummary;
		Button trainingReplayButton;
		bool loadingTrainingBattles;

		internal TrainingRun SelectedTrainingRun => trainingRun;
		bool OperationInProgress => runner.IsRunning || activeJob != null || queue.Count > 0 ||
			battleRunning || continuousLoop.IsRunning;

		Control BuildTrainingBattlePanel()
		{
			var panel = new TableLayoutPanel
			{
				AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2,
				Margin = new Padding(0, 0, 0, 14)
			};
			panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			var heading = new Label
			{
				Text = "TRAIN FROM BATTLE", AutoSize = true, ForeColor = CommandTheme.Green,
				Margin = new Padding(3, 0, 3, 6)
			};
			panel.Controls.Add(heading, 0, 0);
			panel.SetColumnSpan(heading, 2);
			trainingBattleBox = new ComboBox
			{
				Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
				AccessibleName = "Battle to train from", Margin = new Padding(3, 3, 8, 3)
			};
			trainingBattleBox.SelectedIndexChanged += (_, _) =>
			{
				if (loadingTrainingBattles)
					return;
				trainingRun = (trainingBattleBox.SelectedItem as RecordedBattleChoice)?.Run;
				settings.SelectedTrainingRunDirectory = trainingRun?.RunDirectory;
				PersistSettings();
				UpdateEnabledState();
			};
			panel.Controls.Add(trainingBattleBox, 0, 1);
			trainingReplayButton = new ActionButton
			{
				Text = "Watch replay", AccessibleName = "Watch training battle replay",
				Margin = new Padding(3, 0, 3, 0)
			};
			trainingReplayButton.Click += (_, _) => WatchReplay(trainingRun);
			panel.Controls.Add(trainingReplayButton, 1, 1);
			trainingBattleSummary = new Label
			{
				AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false,
				ForeColor = CommandTheme.Muted, AccessibleName = "Selected training battle",
				Margin = new Padding(3, 8, 3, 0)
			};
			panel.Controls.Add(trainingBattleSummary, 0, 2);
			panel.SetColumnSpan(trainingBattleSummary, 2);
			return panel;
		}

		void RefreshTrainingBattleChoices()
		{
			var wanted = trainingRun?.RunDirectory ?? settings.SelectedTrainingRunDirectory;
			loadingTrainingBattles = true;
			trainingBattleBox.Items.Clear();
			for (var index = loadedHistory.Runs.Count - 1; index >= 0; index--)
				trainingBattleBox.Items.Add(new RecordedBattleChoice { Number = index + 1, Run = loadedHistory.Runs[index] });
			var choices = trainingBattleBox.Items.Cast<RecordedBattleChoice>().ToList();
			var selected = choices.FindIndex(choice => SamePath(choice.Run.RunDirectory, wanted));
			trainingBattleBox.SelectedIndex = selected >= 0 ? selected : choices.Count > 0 ? 0 : -1;
			trainingRun = (trainingBattleBox.SelectedItem as RecordedBattleChoice)?.Run;
			loadingTrainingBattles = false;
			UpdateTrainingBattleState();
		}

		void UpdateTrainingBattleState()
		{
			if (trainingBattleBox == null)
				return;
			trainingBattleBox.Enabled = !OperationInProgress && trainingBattleBox.Items.Count > 0;
			trainingReplayButton.Enabled = !OperationInProgress && repo != null &&
				trainingRun?.HasRecordedBattle == true && File.Exists(trainingRun.ReplayPath);
			if (trainingRun == null)
			{
				trainingBattleSummary.Text = "No recorded battle selected. Fight first, or select a bot with recorded sessions.";
				return;
			}

			var battle = trainingRun.Manifest.Battle;
			trainingBattleSummary.Text =
				$"Map: {battle?.Map ?? "unknown"} / {battle?.Difficulty ?? "unknown difficulty"} / " +
				$"{(trainingRun.IsHeadless ? "Headless" : "Rendered")} / {BattleFeedback.Status(trainingRun)}\n" +
				$"Session: {trainingRun.Manifest.Id}\n" +
				"Uses this battle's saved evidence to improve your bot's current source.";
		}

		internal bool SelectTrainingBattle(TrainingRun run)
		{
			if (OperationInProgress || !RunMatchesSelectedBot(run) || !File.Exists(run.ManifestPath))
			{
				Status("Finish the current operation and select a recorded battle for this bot.");
				return false;
			}

			var choice = trainingBattleBox.Items.Cast<RecordedBattleChoice>()
				.FirstOrDefault(item => SamePath(item.Run.RunDirectory, run.RunDirectory));
			if (choice == null)
			{
				Status("This battle is no longer in the selected bot's history. Refresh History & trends.");
				return false;
			}

			trainingBattleBox.SelectedItem = choice;
			return true;
		}

		void TrainFromHistory(TrainingRun run)
		{
			if (!SelectTrainingBattle(run))
				return;
			SelectStation(2);
			resultsWindow?.Hide();
			Activate();
			trainingBattleBox.Focus();
			Status("Training battle selected. Watch its replay or choose Analyze & improve.");
		}

		void ConfirmDeleteRecordedSession(TrainingRun run)
		{
			try
			{
				DeleteRecordedSession(run, message => MessageBox.Show(resultsWindow ?? (IWin32Window)this,
					message, "Delete recorded session", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
					MessageBoxDefaultButton.Button2) == DialogResult.Yes);
			}
			catch (IOException ex)
			{
				ReportSessionDeletionFailure(ex.Message);
			}
			catch (UnauthorizedAccessException ex)
			{
				ReportSessionDeletionFailure(ex.Message);
			}
			catch (InvalidOperationException ex)
			{
				ReportSessionDeletionFailure(ex.Message);
			}
			catch (System.Text.Json.JsonException ex)
			{
				ReportSessionDeletionFailure(ex.Message);
			}
			catch (ArgumentException ex)
			{
				ReportSessionDeletionFailure(ex.Message);
			}
		}

		void ReportSessionDeletionFailure(string detail)
		{
			var message = "Could not delete the recorded session. Close any files using its folder and try again.\n\n" + detail;
			Append(message);
			Status("Session deletion failed. Its history entry has not been removed.");
			MessageBox.Show(resultsWindow ?? (IWin32Window)this, message, "Delete recorded session",
				MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		internal bool DeleteRecordedSession(TrainingRun run, Func<string, bool> confirm)
		{
			ArgumentNullException.ThrowIfNull(confirm);
			if (OperationInProgress || run?.CanDelete != true || !RunMatchesSelectedBot(run))
				throw new InvalidOperationException("Finish or stop the current operation before deleting a completed session.");

			var message = $"Permanently delete the recorded session from {run.Manifest.CreatedUtc.ToLocalTime():g}?\n\n" +
				$"Session: {run.Manifest.Id}\nResult: {run.Manifest.Result?.Outcome ?? run.Manifest.Status}\n\n" +
				"This removes its feedback, statistics, logs, replay copy, agent history, and restore snapshots. " +
				"It also removes the session from trends. This cannot be undone.\n\n" +
				"Your bot's current source, other sessions, and the original OpenRA replay will not be changed.";
			if (!confirm(message))
				return false;
			if (OperationInProgress)
				throw new InvalidOperationException("An operation started while confirmation was open. Stop it before deleting the session.");

			run.Delete(trainingRunsRoot);
			loadedHistory = loadedHistory.WithoutRun(run);
			if (SamePath(lastRun?.RunDirectory, run.RunDirectory))
			{
				lastRun = loadedHistory.Runs.LastOrDefault();
				settings.LastTrainingRunDirectory = lastRun?.RunDirectory;
			}
			if (SamePath(trainingRun?.RunDirectory, run.RunDirectory))
			{
				trainingRun = loadedHistory.Runs.LastOrDefault();
				settings.SelectedTrainingRunDirectory = trainingRun?.RunDirectory;
			}
			if (SamePath(matchLog.Path, run.TelemetryPath))
			{
				matchTimer.Stop();
				matchLog.Watch(null);
				battleLog.Watch(null);
				battleFinished = false;
				outputWindow?.ClearBattle();
			}
			if (SamePath(improvementWindow?.ShownRun?.RunDirectory, run.RunDirectory))
				improvementWindow.Close();

			PersistSettings();
			RefreshResultsHistory(reload: true);
			UpdateEnabledState();
			Status("Recorded session deleted. Your bot's source is unchanged.");
			return true;
		}
	}
}
