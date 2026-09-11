#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class ResultsWindowTests
	{
		sealed class CaptureHost : Form
		{
			protected override bool ShowWithoutActivation => true;
		}

		string root;
		string project;
		int runCount;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Results Window", Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			runCount = 0;
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, true);

		[Test]
		public void ManualBattleCanBeReviewedButRunningOperationsLockFeedback()
		{
			var run = NewRun();
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([run]), run, busy: false);
			TrainingRun requested = null;
			window.FeedbackRequested += value => requested = value;

			Assert.That(window.CanReviewFeedback, Is.True);
			window.RequestFeedback();
			Assert.That(requested, Is.SameAs(run));
			Assert.That(window.HistorySummaryText, Does.Contain("1 recorded session(s): 0 won, 1 lost."));
			Assert.That(window.HistorySummaryText, Does.Contain("0 provided, 1 missing"));

			requested = null;
			window.SetOperationState(busy: true);
			Assert.That(window.CanReviewFeedback, Is.False);
			window.RequestFeedback();
			Assert.That(requested, Is.Null);
			window.SetOperationState(busy: false);
			Assert.That(window.CanReviewFeedback, Is.True);
		}

		[Test]
		public void DeleteRequestsUseTheSelectedSessionAndAreBlockedDuringOperations()
		{
			var first = NewRun();
			var latest = NewRun();
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([first, latest]), latest, busy: false);
			window.SelectedBattleIndex = 0;
			TrainingRun requested = null;
			window.DeleteRequested += run => requested = run;
			Assert.That(window.CanDeleteSession, Is.True);
			window.RequestSessionDeletion();
			Assert.That(requested, Is.SameAs(first));

			window.SetOperationState(busy: true);
			requested = null;
			window.RequestSessionDeletion();
			Assert.That(window.CanDeleteSession, Is.False);
			Assert.That(requested, Is.Null);
			window.SetHistory(TrainingHistory.Empty, null, busy: false);
			Assert.That(window.CanDeleteSession, Is.False);
		}

		[Test]
		public void FailedSessionsCanBeDeletedButLiveBattlesCannot()
		{
			var failed = NewRun();
			failed.Manifest.Status = "failed";
			failed.Manifest.Result = new TrainingBattleResult();
			var live = NewRun(completed: false);
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([failed, live]), live, busy: false);
			Assert.That(window.CanDeleteSession, Is.False);
			window.SelectedBattleIndex = 0;
			Assert.That(window.CanDeleteSession, Is.True);
			failed.Manifest.Status = "verifying";
			window.SetOperationState(busy: false);
			Assert.That(window.CanDeleteSession, Is.False);
		}

		[Test]
		public void TrainingFromHistoryRequiresAnEditableBattleWithEvidenceAndAnIdleLauncher()
		{
			var run = NewRun(BattleExecutionModes.Headless);
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([run]), run, busy: false);
			Assert.That(window.CanTrainFromBattle, Is.False);
			File.WriteAllText(run.BattleLogPath, "battle fixture");
			File.WriteAllText(run.TelemetryPath, "telemetry fixture");
			File.WriteAllText(run.DecisionTracePath, "decision fixture");
			window.SetOperationState(busy: false);
			Assert.That(window.CanTrainFromBattle, Is.True, "Personal feedback and a replay are optional for evidence-only training.");
			TrainingRun requested = null;
			window.TrainRequested += value => requested = value;
			window.RequestTraining();
			Assert.That(requested, Is.SameAs(run));

			requested = null;
			window.SetOperationState(busy: true);
			window.RequestTraining();
			Assert.That(requested, Is.Null);
			Assert.That(window.CanTrainFromBattle, Is.False);
			run.Manifest.BotProject = null;
			window.SetOperationState(busy: false);
			Assert.That(window.CanTrainFromBattle, Is.False);
		}

		[Test]
		public void HistoricalSelectionAndUnsavedFeedbackSurviveNewIterations()
		{
			var first = NewRun();
			var second = NewRun(outcome: "Won");
			var history = TrainingHistory.FromRuns([first, second]);
			using (var chart = new IterationChart("Army", iteration => iteration.LocalPlayer.ArmyValue,
				iteration => iteration.Opponents.ArmyValue)
				{ Size = new Size(640, 360), Iterations = history.Iterations })
			using (var bitmap = new Bitmap(chart.Width, chart.Height))
				Assert.That(() => chart.DrawToBitmap(bitmap, chart.ClientRectangle), Throws.Nothing);

			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(history, second, busy: false);
			window.SelectedBattleIndex = 0;
			using var dialog = new BattleFeedbackDialog(first, first.SetPlayerFeedback);
			dialog.FeedbackText = "Keep this draft while history refreshes.";

			var third = NewRun(completed: false);
			window.SetHistory(history.WithRun(third), third, busy: false);

			Assert.That(window.SelectedBattleIndex, Is.EqualTo(0));
			Assert.That(window.SelectedRun, Is.SameAs(first));
			Assert.That(dialog.FeedbackText, Is.EqualTo("Keep this draft while history refreshes."));
			dialog.Submit();
			Assert.That(first.Manifest.Result.PlayerFeedback, Is.EqualTo("Keep this draft while history refreshes."));
			Assert.That(second.HasPlayerFeedback, Is.False);
		}

		[Test]
		public void SessionRowsShowFeedbackAndAllLocalStatsNewestFirst()
		{
			var first = NewRun();
			var second = NewRun(outcome: "Won");
			second.SetPlayerFeedback("Earlier scouting helped this time.");
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([first, second]), second, busy: false);
			window.ShowRecordedSessions();

			Assert.That(window.RecordedSessions.Items.Count, Is.EqualTo(2));
			var latest = window.RecordedSessions.Items[0].SubItems
				.Cast<ListViewItem.ListViewSubItem>().Select(item => item.Text).ToArray();
			Assert.That(latest[0], Is.EqualTo("#2"));
			Assert.That(latest.Skip(2), Is.EqualTo(new[]
			{
				"Won", "Feedback provided", "1:30", "12", "1200", "4", "2300", "8", "15", "300", "Rendered"
			}));
			Assert.That(window.RecordedSessions.Items[1].SubItems[3].Text, Is.EqualTo("No feedback yet"));
			Assert.That(window.HistorySummaryText, Does.Contain("1 provided, 1 missing"));
			Assert.That(window.FeedbackText, Is.EqualTo("Earlier scouting helped this time."));
			Assert.That(window.FeedbackActionText, Is.EqualTo("Edit feedback"));
		}

		[Test]
		public void HistoricalHeadlessReplayActionsTargetTheSelectedBattle()
		{
			var first = NewRun(BattleExecutionModes.Headless);
			var second = NewRun(BattleExecutionModes.Headless);
			File.WriteAllText(first.ReplayPath, "first replay fixture");
			File.WriteAllText(second.ReplayPath, "second replay fixture");
			var history = TrainingHistory.FromRuns([first, second]);
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(history, second, busy: false);
			window.SelectedBattleIndex = 0;
			TrainingRun requested = null;
			window.FeedbackRequested += run => requested = run;

			Assert.That(window.CanReviewFeedback, Is.True);
			Assert.That(window.CanWatchReplay, Is.True);
			Assert.That(window.FeedbackActionText, Does.Contain("Watch replay"));
			window.RequestFeedback();
			Assert.That(requested, Is.SameAs(first));
			Assert.That(requested.ReplayPath, Is.Not.EqualTo(second.ReplayPath));

			first.RecordReplayPlayback(0, cancelled: false);
			window.SetHistory(history, second, busy: false);
			Assert.That(window.SelectedRun, Is.SameAs(first));
			Assert.That(window.FeedbackActionText, Is.EqualTo("Add feedback"));
			Assert.That(second.NeedsReplayForFeedback, Is.True);
		}

		[Test]
		public void MissingReplayDoesNotHideLegacyFeedbackOrAllowEditingIt()
		{
			var run = NewRun(BattleExecutionModes.Headless);
			run.Manifest.Result.PlayerFeedback = "Feedback from an older launcher.";
			run.Save();
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([run]), run, busy: false);
			Assert.That(window.CanReviewFeedback, Is.False);
			Assert.That(window.CanWatchReplay, Is.False);
			Assert.That(window.FeedbackText, Is.EqualTo(run.Manifest.Result.PlayerFeedback));
			Assert.That(window.HistorySummaryText, Does.Contain("1 provided, 0 missing"));
		}

		[Test]
		public void FailedAndRunningSessionsAreListedWithoutInventedStats()
		{
			var failed = NewRun(completed: false);
			failed.Manifest.Status = "failed";
			failed.Manifest.CompletedUtc = DateTime.UtcNow;
			failed.Manifest.Result = new TrainingBattleResult();
			var running = NewRun(completed: false);
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([failed, running]), running, busy: false);
			Assert.That(window.RecordedSessions.Items.Count, Is.EqualTo(2));
			Assert.That(window.RecordedSessions.Items[0].SubItems[3].Text, Is.EqualTo("Battle in progress"));
			Assert.That(window.RecordedSessions.Items[1].SubItems[3].Text, Is.EqualTo("No battle recorded"));
			Assert.That(window.RecordedSessions.Items[1].SubItems[5].Text, Is.EqualTo("-"));
			Assert.That(window.CanReviewFeedback, Is.False);
			window.SelectedBattleIndex = 0;
			Assert.That(window.CanReviewFeedback, Is.False);
			Assert.That(window.HistorySummaryText, Does.Contain("2 recorded session(s)"));
			Assert.That(window.HistorySummaryText, Does.Contain("0 provided, 0 missing"));
		}

		[Test]
		public void FeedbackStatusAndSelectionSurviveReloadAndReflectClearedNotes()
		{
			var first = NewRun();
			var second = NewRun(outcome: "Won");
			using var window = new ResultsWindow(new MatchLog());
			window.SetHistory(TrainingHistory.FromRuns([first, second]), second, busy: false);
			window.SelectedBattleIndex = 0;
			first.SetPlayerFeedback("I should have protected the refinery.");
			window.SetHistory(TrainingHistory.Load(project, Path.Combine(root, "runs")), second, busy: false);
			Assert.That(window.SelectedRun.RunDirectory, Is.EqualTo(first.RunDirectory));
			Assert.That(window.FeedbackText, Does.Contain("protected the refinery"));
			Assert.That(window.RecordedSessions.Items[1].SubItems[3].Text, Is.EqualTo("Feedback provided"));
			Assert.That(window.RecordedSessions.Items[0].SubItems[3].Text, Is.EqualTo("No feedback yet"));

			first.SetPlayerFeedback("");
			window.SetHistory(TrainingHistory.Load(project, Path.Combine(root, "runs")), second, busy: false);
			Assert.That(window.RecordedSessions.Items[1].SubItems[3].Text, Is.EqualTo("No feedback yet"));
			Assert.That(window.FeedbackActionText, Is.EqualTo("Add feedback"));
		}

		[TestCase(520, 360, true)]
		[TestCase(560, 420, false)]
		[TestCase(940, 700, false)]
		public void HistoryKeepsItsFeedbackActionsReachable(int width, int height, bool headless)
		{
			var first = NewRun();
			var second = NewRun(headless ? BattleExecutionModes.Headless : BattleExecutionModes.Rendered, outcome: "Won");
			if (headless)
				File.WriteAllText(second.ReplayPath, "replay fixture");
			else
				second.SetPlayerFeedback("I defended the expansion and kept producing units before the second attack.");
			using var window = new ResultsWindow(new MatchLog()) { ShowInTaskbar = false };
			window.SetHistory(TrainingHistory.FromRuns([first, second]), second, busy: false);
			using var host = new CaptureHost { ShowInTaskbar = false };
			window.TopLevel = false;
			window.FormBorderStyle = FormBorderStyle.None;
			window.Dock = DockStyle.Fill;
			host.Controls.Add(window);
			host.Show();
			host.ClientSize = new Size(width * host.DeviceDpi / 96, height * host.DeviceDpi / 96);
			window.Show();
			window.ShowRecordedSessions();
			window.PerformLayout();

			foreach (var button in Descendants(window).OfType<Button>().Where(button => button.Visible))
			{
				var origin = window.PointToClient(button.PointToScreen(Point.Empty));
				Assert.That(window.ClientRectangle.Contains(new Rectangle(origin, button.Size)), Is.True, button.Text);
			}
			Assert.That(window.RecordedSessions.Height, Is.GreaterThan(80 * window.DeviceDpi / 96),
				string.Join(Environment.NewLine, Descendants(window).OfType<TableLayoutPanel>()
					.Where(panel => panel.Visible)
					.Select(panel => $"{panel.Size}: rows {string.Join(", ", panel.GetRowHeights())}; auto size {panel.AutoSize}")));
			foreach (ColumnHeader column in window.RecordedSessions.Columns)
				Assert.That(column.Width, Is.GreaterThanOrEqualTo(TextRenderer.MeasureText(column.Text, window.Font).Width),
					$"The {column.Text} heading must fit at {window.DeviceDpi} DPI.");
			var feedbackCell = window.RecordedSessions.Items[0].SubItems[3].Text;
			Assert.That(window.RecordedSessions.Columns[3].Width,
				Is.GreaterThanOrEqualTo(TextRenderer.MeasureText(feedbackCell, window.Font).Width));
			using var bitmap = new Bitmap(window.Width, window.Height);
			window.DrawToBitmap(bitmap, window.ClientRectangle);
			var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(capture))
			{
				Directory.CreateDirectory(capture);
				bitmap.Save(Path.Combine(capture, $"feedback-history-{width}-{height}-{window.DeviceDpi}.png"), ImageFormat.Png);
			}
		}

		static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent) =>
			parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Descendants(control)));

		TrainingRun NewRun(string execution = BattleExecutionModes.Rendered, string outcome = "Lost", bool completed = true)
		{
			var run = TrainingRun.Create(project, new TrainingBattleConfiguration { ExecutionMode = execution },
				Path.Combine(root, "runs"));
			run.Manifest.CreatedUtc = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc).AddMinutes(runCount++);
			if (completed)
			{
				run.Manifest.Status = "finished";
				run.Manifest.CompletedUtc = DateTime.UtcNow;
				run.Manifest.Result = new TrainingBattleResult
				{
					DurationSeconds = 90, LocalPlayer = "You", Outcome = outcome,
					Players =
					[
						new TrainingPlayerResult { Name = "Opponent", IsBot = true, Outcome = outcome == "Won" ? "Lost" : "Won" },
						new TrainingPlayerResult
						{
							Name = "You", Outcome = outcome, Units = 12, ArmyValue = 1200,
							Buildings = 4, BaseValue = 2300, Killed = 8, Lost = 15, Cash = 300
						}
					]
				};
			}
			run.Save();
			return run;
		}
	}
}
