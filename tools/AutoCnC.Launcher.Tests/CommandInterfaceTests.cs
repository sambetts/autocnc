// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class CommandInterfaceTests
	{
		sealed class CaptureHost : Form
		{
			protected override bool ShowWithoutActivation => true;
		}

		string root;
		LauncherSettings settings;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "command-ui-" + Guid.NewGuid().ToString("N"));
			Write("AutoCnC.sln", "");
			Write("scripts\\authoring-api.version", "5");
			Write("scripts\\run-bot.ps1", "");
			Write("scripts\\new-bot.ps1", "");
			Write("scripts\\train-bot.ps1", "");
			Write("scripts\\difficulties.json",
				"""{"default":"Normal","levels":[{"name":"Normal","botLabel":"Cabal","summary":"A balanced AI opponent for testing your doctrine."}]}""");
			Write("engine\\OpenRA.sln", "");
			Write("engine\\mods\\cnc\\maps\\test-range\\map.yaml",
				"Title: Test range (fixture)\nPlayers:\n\tPlayerReference@Multi0:\n\t\tPlayable: True\n\tPlayerReference@Multi1:\n\t\tPlayable: True\n");
			Write("mods\\autocnc\\mod.yaml",
				"GameSpeeds:\n\tDefaultSpeed: default\n\tSpeeds:\n\t\tdefault:\n\t\t\tTimestep: 40\n\t\tmaximum:\n\t\t\tTimestep: 1\n");
			Write("bots\\Reference\\ReferenceBot.csproj", "<Project />");
			settings = new LauncherSettings
			{
				RepositoryRoot = root,
				BattleBotPath = Path.Combine(root, "bots", "Reference", "ReferenceBot.csproj")
			};
		}

		void Write(string path, string content)
		{
			var target = Path.Combine(root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.WriteAllText(target, content);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		MainForm Window()
		{
			var window = new MainForm(settings, trainingRunsRoot: Path.Combine(root, "runs")) { ShowInTaskbar = false };
			window.Show();
			window.Size = DeviceSize(new Size(1200, 850), window.DeviceDpi);
			window.PerformLayout();
			return window;
		}

		[Test]
		public void StationsPreserveConfigurationAndExposeTheAuthoringLoop()
		{
			using var window = Window();
			Assert.That(window.SelectedStation, Is.EqualTo(1));
			var map = Named<ComboBox>(window, "Battle map");
			var chosen = map.SelectedItem;
			for (var i = 0; i < 4; i++)
			{
				window.SelectStation(i);
				Assert.That(window.SelectedStation, Is.EqualTo(i));
				Assert.That(Descendants(window).OfType<ActionButton>().Count(button => button.Selected), Is.EqualTo(1));
				Assert.That(map.SelectedItem, Is.SameAs(chosen));
			}
			Assert.That(Descendants(window).OfType<Button>().All(button => button is ActionButton), Is.True);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().BotName, Is.EqualTo("ReferenceBot"));
			Assert.That(((Button)window.AcceptButton).Enabled, Is.True);
		}

		[Test]
		public void ExecutionAndTrainingStatesStayConsistentAcrossStations()
		{
			using var window = Window();
			var execution = Named<ComboBox>(window, "Battle execution mode");
			var speed = Named<ComboBox>(window, "Rendered game speed");
			Assert.That(speed.Enabled, Is.False);
			execution.SelectedIndex = 1;
			Assert.That(speed.Enabled, Is.True);
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			continuous.Checked = true;
			Assert.That(((Button)window.AcceptButton).Text, Is.EqualTo("START AI TRAINING"));
			execution.SelectedIndex = 0;
			Assert.That(((Button)window.AcceptButton).Text, Is.EqualTo("START AI TRAINING"));
			Assert.That(speed.Enabled, Is.False);
			Assert.That(((GameSpeedInfo)speed.SelectedItem).Id, Is.EqualTo("maximum"));
		}

		[TestCase(BattleExecutionModes.Rendered, false, false, true)]
		[TestCase(BattleExecutionModes.Headless, false, false, false)]
		[TestCase(BattleExecutionModes.Headless, true, false, true)]
		[TestCase(BattleExecutionModes.Headless, true, true, true)]
		public void ProvingGroundMakesLatestBattleFeedbackVisible(string execution, bool replay, bool watched, bool enabled)
		{
			var run = RecordedFeedbackRun(execution);
			if (replay)
				File.WriteAllText(run.ReplayPath, "replay fixture");
			if (watched)
				run.RecordReplayPlayback(0, cancelled: false);
			settings.LastTrainingRunDirectory = run.RunDirectory;
			using var window = Window();
			var feedback = Named<Button>(window, "Review latest battle feedback");
			Assert.That(feedback.Visible, Is.True);
			Assert.That(feedback.Enabled, Is.EqualTo(enabled));
			Assert.That(feedback.Text, Is.EqualTo(execution == BattleExecutionModes.Headless && !watched
				? "Watch replay && add feedback" : "Add feedback"));
			Assert.That(Named<Label>(window, "Latest battle feedback status").Text, Does.Contain("No feedback yet"));
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			continuous.Checked = true;
			Assert.That(feedback.Enabled, Is.EqualTo(enabled), "The repeat preference alone must not lock idle feedback.");

			var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(capture))
			{
				Directory.CreateDirectory(capture);
				using var bitmap = new Bitmap(window.Width, window.Height);
				window.DrawToBitmap(bitmap, window.ClientRectangle);
				bitmap.Save(Path.Combine(capture, $"feedback-proving-{execution}-{replay}-{watched}-{window.DeviceDpi}.png"),
					ImageFormat.Png);
			}
		}

		[Test]
		public void SavingFromProvingGroundRefreshesHistoryWhileTheRepeatPreferenceIsIdle()
		{
			var run = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			settings.LastTrainingRunDirectory = run.RunDirectory;
			using var window = Window();
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			continuous.Checked = true;
			Descendants(window).OfType<Button>().Single(button => button.Text == "History && trends").PerformClick();
			var history = window.OwnedForms.OfType<ResultsWindow>().Single();
			Assert.That(Descendants(history).OfType<TabControl>().Single().SelectedTab.Text, Is.EqualTo("Recorded sessions"));
			Assert.That(history.CanReviewFeedback, Is.True);

			var shown = false;
			string error = null;
			using var timer = new System.Windows.Forms.Timer { Interval = 20 };
			timer.Tick += (_, _) =>
			{
				var dialog = Application.OpenForms.OfType<BattleFeedbackDialog>().SingleOrDefault();
				if (dialog == null)
					return;
				timer.Stop();
				shown = true;
				dialog.FeedbackText = "I left the harvesters exposed during the attack.";
				dialog.Submit();
				if (dialog.DialogResult != DialogResult.OK)
				{
					error = dialog.MessageText;
					dialog.DialogResult = DialogResult.Cancel;
					dialog.Close();
				}
			};
			timer.Start();
			Named<Button>(window, "Review latest battle feedback").PerformClick();

			Assert.That(shown, Is.True);
			Assert.That(error, Is.Null);
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Result.PlayerFeedback,
				Is.EqualTo("I left the harvesters exposed during the attack."));
			Assert.That(history.FeedbackText, Is.EqualTo("I left the harvesters exposed during the attack."));
			Assert.That(history.HistorySummaryText, Does.Contain("1 provided, 0 missing"));
			Assert.That(Named<Button>(window, "Review latest battle feedback").Text, Is.EqualTo("Edit feedback"));
		}

		[Test]
		public void HistoryStaysOnScreenWhenTheLauncherFillsTheWorkingArea()
		{
			using var window = Window();
			var area = Screen.FromControl(window).WorkingArea;
			window.Bounds = area;
			Descendants(window).OfType<Button>().Single(button => button.Text == "History && trends").PerformClick();
			var history = window.OwnedForms.OfType<ResultsWindow>().Single();
			Assert.That(area.Contains(history.Bounds), Is.True,
				$"History bounds {history.Bounds} must fit {area} at {history.DeviceDpi} DPI.");
		}

		[Test]
		public void TrainingSelectionIsExplicitRememberedAndSeparateFromTheLastBattle()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Headless);
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using (var window = Window())
			{
				window.SelectStation(2);
				Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(latest.RunDirectory));
				Assert.That(window.SelectTrainingBattle(older), Is.True);
				Assert.That(Named<Label>(window, "Selected training battle").Text, Does.Contain(older.Manifest.Id));
				Assert.That(Named<Label>(window, "Selected training battle").Text, Does.Contain("Rendered"));
				Assert.That(Named<Label>(window, "Latest battle feedback status").Text, Does.Contain("headless"));
				Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));
				Assert.That(settings.SelectedTrainingRunDirectory, Is.EqualTo(older.RunDirectory));
				window.Close();
			}

			settings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(settings));
			using var reopened = Window();
			Assert.That(reopened.SelectedTrainingRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));
		}

		[Test]
		public void RightClickTrainSelectsTheClickedHistoricalBattleWithoutStartingAnAgent()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			WriteEvidence(older);
			WriteEvidence(latest);
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			var history = OpenHistory(window);
			var row = history.RecordedSessions.Items[1].Bounds;
			history.SelectContextBattle(new Point(row.Left + 12, row.Top + row.Height / 2));
			Assert.That(history.SelectedRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(history.CanTrainFromBattle, Is.True);
			Assert.That(history.RecordedSessions.ContextMenuStrip.Items[0].Text, Is.EqualTo("Train from this battle"));
			history.RecordedSessions.ContextMenuStrip.Items[0].PerformClick();

			Assert.That(window.SelectedStation, Is.EqualTo(2));
			Assert.That(history.Visible, Is.False, "History must not obscure the selected training battle.");
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(Named<ComboBox>(window, "Battle to train from").Text, Does.StartWith("Battle #1"));
			Assert.That(TrainingRun.Load(older.RunDirectory).Manifest.Agent, Is.Null);
			Assert.That(TrainingRun.Load(latest.RunDirectory).Manifest.Agent, Is.Null);
		}

		[Test]
		public void HistoricalTrainingUsesTheSelectedEvidenceAndPinsTheCompletionToIt()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			WriteEvidence(older);
			WriteEvidence(latest);
			older.SetPlayerFeedback("Historical battle observation.");
			Write("docs\\agent-game-guide.md", "# Test game guide");
			settings.AgentPromptTemplate =
				"Improve the bot. Edit only files under {workspace}. Read {gameGuide} and {gameRules}.\n" +
				"Use {fightManifest}, {battleLog}, {telemetry}, {decisionTrace}.\n" +
				"Battle: {battle}. Result: {result}. Revision: {sourceRevision}.\n{nextPromptContract}";
			Assert.That(TrainingAgent.ValidatePromptTemplate(settings.AgentPromptTemplate, out var error), Is.True, error);
			Write("scripts\\train-bot.ps1",
				"param([string]$BattleBot, [string]$RunDirectory, [string]$AgentConfiguration)\n" +
				"$RunDirectory | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\\trained-run.txt')\n" +
				"Start-Sleep -Milliseconds 250\nexit 0\n");
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.SelectStation(2);
			Assert.That(window.SelectTrainingBattle(older), Is.True);
			Descendants(window).OfType<Button>().Single(button => button.Text == "Analyze && improve").PerformClick();
			Assert.That(Named<ComboBox>(window, "Battle to train from").Enabled, Is.False);
			var training = Descendants(window).OfType<GroupBox>().Single(group => group.Text == "IMPROVEMENT ORDERS");
			Assert.That(training.Enabled, Is.False);
			foreach (var caption in Descendants(training).Where(control =>
				control is Label or CheckBox && !string.IsNullOrWhiteSpace(control.Text)))
				CommandThemeTests.AssertReadableDisabledText(caption);
			Assert.That(window.SelectTrainingBattle(latest), Is.False);
			Assert.That(() => window.DeleteRecordedSession(latest, _ => true), Throws.InvalidOperationException);
			var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(capture))
			{
				Directory.CreateDirectory(capture);
				using var bitmap = new Bitmap(window.Width, window.Height);
				window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
				bitmap.Save(Path.Combine(capture, $"training-locked-{window.DeviceDpi}.png"), ImageFormat.Png);
			}
			WaitUntil(() => TrainingRun.Load(older.RunDirectory).Manifest.Agent?.CompletedUtc != null);

			Assert.That(File.ReadAllText(Path.Combine(root, "trained-run.txt")).Trim(), Is.EqualTo(older.RunDirectory));
			Assert.That(File.ReadAllText(older.PromptPath), Does.Contain(older.TelemetryPath));
			Assert.That(File.ReadAllText(older.PromptPath), Does.Contain("Historical battle observation."));
			Assert.That(File.ReadAllText(older.PromptPath), Does.Not.Contain(latest.TelemetryPath));
			Assert.That(TrainingRun.Load(older.RunDirectory).Manifest.Status, Is.EqualTo("improved"));
			Assert.That(TrainingRun.Load(latest.RunDirectory).Manifest.Agent, Is.Null);
			Assert.That(window.OwnedForms.OfType<ImprovementWindow>().Single().ShownRun.RunDirectory,
				Is.EqualTo(older.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));
			Assert.That(Named<ComboBox>(window, "Battle to train from").Enabled, Is.True);
		}

		[Test]
		public void TrainingReplayUsesTheSelectedBattleRatherThanTheNewestReplay()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			older.SetPlayerFeedback("Existing battle observation.");
			older.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			older.Save();
			var latest = RecordedFeedbackRun(BattleExecutionModes.Headless);
			File.WriteAllText(older.ReplayPath, "older replay fixture");
			File.WriteAllText(latest.ReplayPath, "latest replay fixture");
			Write("scripts\\launch.ps1",
				"param([string]$Replay)\n" +
				"$Replay | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\\watched-replay.txt')\nexit 0\n");
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.SelectStation(2);
			window.SelectTrainingBattle(older);
			var watch = Named<Button>(window, "Watch training battle replay");
			Assert.That(watch.Enabled, Is.True);
			watch.PerformClick();
			WaitUntil(() => TrainingRun.Load(older.RunDirectory).Manifest.ReplayWatchedUtc != null);

			Assert.That(File.ReadAllText(Path.Combine(root, "watched-replay.txt")).Trim(), Is.EqualTo(older.ReplayPath));
			Assert.That(TrainingRun.Load(latest.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));
		}

		[Test]
		public void VerificationRetryRecordsItsResultOnTheSelectedHistoricalBattle()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			WritePreparedEvidence(older);
			WorkspaceSnapshot.Capture(older);
			older.AgentStarted("test agent");
			older.AgentFinished(1, 0, failurePhase: "verification", failureMessage: "Fixture build failure.");
			Write("scripts\\verify-bot.ps1",
				"param([string]$BattleBot, [string]$RunDirectory)\n" +
				"$RunDirectory | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\\verified-run.txt')\nexit 0\n");
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.SelectStation(2);
			window.SelectTrainingBattle(older);
			var retry = Descendants(window).OfType<Button>().Single(button => button.Text == "Retry verification");
			Assert.That(retry.Visible && retry.Enabled, Is.True);
			retry.PerformClick();
			WaitUntil(() => TrainingRun.Load(older.RunDirectory).Manifest.Agent?.CompletedUtc != null);

			Assert.That(File.ReadAllText(Path.Combine(root, "verified-run.txt")).Trim(), Is.EqualTo(older.RunDirectory));
			Assert.That(TrainingRun.Load(older.RunDirectory).Manifest.Agent.ExitCode, Is.Zero);
			Assert.That(TrainingRun.Load(latest.RunDirectory).Manifest.Agent, Is.Null);
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(older.RunDirectory));
		}

		[Test]
		public void DeletionClosesTheRemovedBattlesAgentWorkspace()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			WritePreparedEvidence(older);
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.SelectStation(2);
			window.SelectTrainingBattle(older);
			Descendants(window).OfType<Button>().Single(button => button.Text == "Agent workspace").PerformClick();
			var workspace = window.OwnedForms.OfType<ImprovementWindow>().Single();
			Assert.That(workspace.ShownRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(window.DeleteRecordedSession(older, _ => true), Is.True);
			Assert.That(workspace.IsDisposed, Is.True);
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(latest.RunDirectory));
		}

		[Test]
		public void CancellingDeletionKeepsTheSessionSelectionAndFeedback()
		{
			var run = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			run.SetPlayerFeedback("Keep this feedback.");
			settings.LastTrainingRunDirectory = run.RunDirectory;
			using var window = Window();
			var history = OpenHistory(window);
			string confirmation = null;
			Assert.That(window.DeleteRecordedSession(run, message =>
			{
				confirmation = message;
				return false;
			}), Is.False);
			Assert.That(confirmation, Does.Contain(run.Manifest.Id));
			Assert.That(confirmation, Does.Contain("restore snapshots"));
			Assert.That(confirmation, Does.Contain("cannot be undone"));
			Assert.That(File.Exists(run.ManifestPath), Is.True);
			Assert.That(history.RecordedSessions.Items.Count, Is.EqualTo(1));
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(run.RunDirectory));
			Assert.That(history.FeedbackText, Is.EqualTo("Keep this feedback."));
		}

		[Test]
		public void DeletionRefreshesSelectedAndLatestBattlesWithoutResurrectingThem()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			older.SetPlayerFeedback("Old observation.");
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			var history = OpenHistory(window);
			window.SelectTrainingBattle(older);
			Assert.That(window.DeleteRecordedSession(older, _ => true), Is.True);
			Assert.That(history.RecordedSessions.Items.Count, Is.EqualTo(1));
			Assert.That(history.HistorySummaryText, Does.Contain("0 provided, 1 missing"));
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(latest.RunDirectory));
			Assert.That(settings.SelectedTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(latest.RunDirectory));

			Assert.That(window.DeleteRecordedSession(latest, _ => true), Is.True);
			Assert.That(history.RecordedSessions.Items.Count, Is.Zero);
			Assert.That(history.HistorySummaryText, Does.Contain("0 recorded session(s)"));
			Assert.That(history.CanDeleteSession, Is.False);
			Assert.That(window.SelectedTrainingRun, Is.Null);
			Assert.That(settings.LastTrainingRunDirectory, Is.Null);
			Assert.That(settings.SelectedTrainingRunDirectory, Is.Null);
			Assert.That(Named<Button>(window, "Review latest battle feedback").Enabled, Is.False);
			Assert.That(Named<Button>(window, "Watch training battle replay").Enabled, Is.False);
			Assert.That(File.Exists(settings.BattleBotPath), Is.True);
			history.Close();
			Assert.That(OpenHistory(window).RecordedSessions.Items.Count, Is.Zero);
			using var reopened = Window();
			Assert.That(reopened.SelectedTrainingRun, Is.Null);
		}

		[Test]
		public void DeletingTheLatestSessionPreservesAnOlderTrainingSelection()
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.SelectTrainingBattle(older);
			Assert.That(window.DeleteRecordedSession(latest, _ => true), Is.True);
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(older.RunDirectory));
			Assert.That(Named<Label>(window, "Selected training battle").Text, Does.Contain(older.Manifest.Id));
		}

		[Test]
		public void FailedDeletionDoesNotRemoveTheHistoryRowOrChangeTheSelection()
		{
			var run = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			File.WriteAllText(run.TelemetryPath, "locked");
			settings.LastTrainingRunDirectory = run.RunDirectory;
			using var window = Window();
			var history = OpenHistory(window);
			using (File.Open(run.TelemetryPath, FileMode.Open, FileAccess.Read, FileShare.None))
				Assert.That(() => window.DeleteRecordedSession(run, _ => true), Throws.InstanceOf<IOException>());
			Assert.That(File.Exists(run.ManifestPath), Is.True);
			Assert.That(history.RecordedSessions.Items.Count, Is.EqualTo(1));
			Assert.That(window.SelectedTrainingRun.RunDirectory, Is.EqualTo(run.RunDirectory));
			Assert.That(settings.LastTrainingRunDirectory, Is.EqualTo(run.RunDirectory));
		}

		[Test]
		public void SwitchingBotsClearsThePreviousTrainingBattle()
		{
			var run = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			settings.LastTrainingRunDirectory = run.RunDirectory;
			using var window = Window();
			Write("bots\\Other\\Other.csproj", "<Project />");
			Named<TextBox>(window, "Battle bot project or assembly").Text = Path.Combine(root, "bots", "Other", "Other.csproj");
			Assert.That(window.SelectedTrainingRun, Is.Null);
			Assert.That(Named<ComboBox>(window, "Battle to train from").Items.Count, Is.Zero);
			Assert.That(window.SelectTrainingBattle(run), Is.False);
			Assert.That(Named<Button>(window, "Watch training battle replay").Enabled, Is.False);
		}

		[TestCase(940, 700)]
		[TestCase(1200, 850)]
		public void TheSelectedTrainingBattleAndReplayActionAreVisibleWithoutScrolling(int width, int height)
		{
			var older = RecordedFeedbackRun(BattleExecutionModes.Rendered);
			var latest = RecordedFeedbackRun(BattleExecutionModes.Headless);
			WriteEvidence(older);
			older.Manifest.Battle.Map = "Tiberium rift";
			older.Manifest.Battle.Difficulty = "Hard";
			older.Save();
			File.WriteAllText(older.ReplayPath, "replay fixture");
			settings.LastTrainingRunDirectory = latest.RunDirectory;
			using var window = Window();
			window.Size = DeviceSize(new Size(width, height), window.DeviceDpi);
			window.SelectStation(2);
			window.SelectTrainingBattle(older);
			window.PerformLayout();
			foreach (var control in new Control[]
			{
				Named<ComboBox>(window, "Battle to train from"),
				Named<Button>(window, "Watch training battle replay"),
				Named<Label>(window, "Selected training battle")
			})
			{
				var origin = window.PointToClient(control.PointToScreen(Point.Empty));
				Assert.That(control.Visible, Is.True);
				Assert.That(window.ClientRectangle.Contains(new Rectangle(origin, control.Size)), Is.True, control.AccessibleName);
			}
			var summary = Named<Label>(window, "Selected training battle");
			Assert.That(summary.Height, Is.GreaterThanOrEqualTo(summary.PreferredHeight));
			Assert.That(summary.Text, Does.Contain("Tiberium rift / Hard"));
			Assert.That(summary.Text, Does.Contain(older.Manifest.Id));
			var output = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(output))
			{
				Directory.CreateDirectory(output);
				using var bitmap = new Bitmap(window.Width, window.Height);
				window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
				bitmap.Save(Path.Combine(output, $"training-selection-{width}-{height}-{window.DeviceDpi}.png"), ImageFormat.Png);
			}
		}

		static void WriteEvidence(TrainingRun run)
		{
			File.WriteAllText(run.BattleLogPath, "seconds,event,player\n");
			File.WriteAllText(run.TelemetryPath, "seconds,player,units\n");
			File.WriteAllText(run.DecisionTracePath, "");
			File.WriteAllText(run.GameRulesPath, "{}");
		}

		static void WritePreparedEvidence(TrainingRun run)
		{
			WriteEvidence(run);
			File.WriteAllText(run.PromptPath, "Prepared fixture prompt");
			File.WriteAllText(run.GameGuidePath, "Prepared fixture guide");
			run.ExportFightManifest();
		}

		static void WaitUntil(Func<bool> predicate)
		{
			var timer = Stopwatch.StartNew();
			while (timer.Elapsed < TimeSpan.FromSeconds(15))
			{
				Application.DoEvents();
				if (predicate())
					return;
				Thread.Sleep(20);
			}
			Assert.Fail("The scripted launcher operation did not finish within 15 seconds.");
		}

		static ResultsWindow OpenHistory(MainForm window)
		{
			Descendants(window).OfType<Button>().Single(button => button.Text == "History && trends").PerformClick();
			return window.OwnedForms.OfType<ResultsWindow>().Single();
		}

		TrainingRun RecordedFeedbackRun(string execution)
		{
			var run = TrainingRun.Create(settings.BattleBotPath,
				new TrainingBattleConfiguration { ExecutionMode = execution }, Path.Combine(root, "runs"));
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Status = "finished";
			run.Manifest.Result = new TrainingBattleResult
			{
				Outcome = "Lost",
				LocalPlayer = "You",
				DurationSeconds = 180,
				Players = [new TrainingPlayerResult { Name = "You", Outcome = "Lost" }]
			};
			run.Save();
			return run;
		}

		[Test]
		public void PrebuiltBotsAndMissingMapsCannotClaimTrainingReadiness()
		{
			Write("bots\\Prebuilt.dll", "");
			settings.BattleBotPath = Path.Combine(root, "bots", "Prebuilt.dll");
			using var window = Window();
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			Assert.That(continuous.Enabled, Is.False);
			Directory.Delete(Path.Combine(root, "engine", "mods", "cnc", "maps", "test-range"), recursive: true);
			Descendants(window).OfType<Button>().Single(button => button.Text == "Refresh").PerformClick();
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Named<TextBox>(window, "Battle bot project or assembly").Text += "missing";
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().Readiness, Does.StartWith("AWAITING BOT"));
		}

		[TestCase(1200, 850)]
		[TestCase(940, 700)]
		[TestCase(940, 600)]
		public void AllStationsRenderWithReachableDeploymentControls(int width, int height)
		{
			using var window = Window();
			window.MinimumSize = Size.Empty;
			window.Size = DeviceSize(new Size(width, height), window.DeviceDpi);
			Assert.That(window.Size, Is.EqualTo(DeviceSize(new Size(width, height), window.DeviceDpi)));
			for (var station = 0; station < 4; station++)
			{
				window.SelectStation(station);
				window.PerformLayout();
				using var bitmap = new Bitmap(window.Width, window.Height);
				Assert.That(() => window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)), Throws.Nothing);
				var launch = (Button)window.AcceptButton;
				var origin = window.PointToClient(launch.PointToScreen(Point.Empty));
				Assert.That(new Rectangle(Point.Empty, window.ClientSize).Contains(new Rectangle(origin, launch.Size)), Is.True,
					"The primary action must remain fully inside the window at every supported size.");
				var rail = Descendants(window).OfType<Label>().Single(label => label.AccessibleName == "Operation status").Parent;
				var heading = Descendants(rail).OfType<Label>().Single(label => label.Text == "COMMAND DECK");
				Assert.That(heading.Height, Is.GreaterThanOrEqualTo(heading.PreferredHeight));
				var visible = rail.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
				for (var i = 0; i < visible.Length; i++)
					for (var j = i + 1; j < visible.Length; j++)
						Assert.That(visible[i].Bounds.IntersectsWith(visible[j].Bounds), Is.False,
							$"{visible[i].Text} must not overlap {visible[j].Text}.");
				var output = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
				if (!string.IsNullOrEmpty(output))
				{
					Directory.CreateDirectory(output);
					bitmap.Save(Path.Combine(output, $"command-{window.DeviceDpi}-{width}-{height}-{station}.png"), ImageFormat.Png);
				}
			}
		}

		[Test]
		public void InvalidRepositoryClearsReadinessRatherThanUsingThePreviousCheckout()
		{
			using var window = Window();
			Named<TextBox>(window, "AutoC&C repository").Text = Path.Combine(root, "missing");
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Assert.That(Named<ComboBox>(window, "Battle map").Items.Count, Is.Zero);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().Readiness, Does.StartWith("SETUP REQUIRED"));
		}

		[Test]
		public void BattleWindowsUseTheSameNavigationAndActionLanguage()
		{
			using var results = new ResultsWindow(new MatchLog());
			using var output = new OutputWindow(new BattleEventLog());
			using var improvement = new ImprovementWindow();
			foreach (var window in new BattleWindow[] { results, output, improvement })
			{
				var tabs = Descendants(window).OfType<TabControl>().Single();
				Assert.That(tabs, Is.TypeOf<CommandTabs>());
				for (var index = 0; index < tabs.TabCount; index++)
				{
					tabs.SelectedIndex = index;
					Assert.That(tabs.SelectedTab, Is.SameAs(tabs.TabPages[index]));
				}
				Assert.That(window.BackColor, Is.EqualTo(CommandTheme.Background));
			}
		}

		[Test]
		public void SharedSurfacesRenderTheirControls()
		{
			using var results = new ResultsWindow(new MatchLog());
			using var output = new OutputWindow(new BattleEventLog());
			using var improvement = new ImprovementWindow();
			using var agent = new AgentSettingsDialog("copilot", TrainingAgent.DefaultArguments);
			using var newBot = new NewBotDialog(root);
			Form[] windows = [results, output, improvement, agent, newBot];
			for (var index = 0; index < windows.Length; index++)
			{
				var window = windows[index];
				using var host = new CaptureHost { ClientSize = new Size(960, 760), ShowInTaskbar = false };
				window.TopLevel = false;
				window.FormBorderStyle = FormBorderStyle.None;
				window.Dock = DockStyle.Fill;
				host.Controls.Add(window);
				host.Show();
				window.Show();
				window.PerformLayout();
				using var bitmap = new Bitmap(host.Width, host.Height);
				Assert.That(() => host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size)), Throws.Nothing);
				var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
				if (!string.IsNullOrEmpty(capture))
				{
					Directory.CreateDirectory(capture);
					bitmap.Save(Path.Combine(capture, $"shared-{index}.png"), ImageFormat.Png);
				}
			}
		}

		[TestCase(false)]
		[TestCase(true)]
		public void PrimaryActionPaintsInBothStates(bool enabled)
		{
			using var button = new ActionButton { Primary = true, Enabled = enabled, Text = "DEPLOY & FIGHT", Size = new Size(240, 56), AutoSize = false };
			using var bitmap = new Bitmap(240, 56);
			Assert.That(() => button.DrawToBitmap(bitmap, button.ClientRectangle), Throws.Nothing);
			Assert.That(bitmap.GetPixel(8, 8).ToArgb(),
				Is.EqualTo((enabled ? CommandTheme.Amber : CommandTheme.Surface).ToArgb()));
		}

		[Test]
		public void ResizingTheDossierInvalidatesItsPreviousDrawing()
		{
			using var host = new CaptureHost { ClientSize = new Size(800, 210), ShowInTaskbar = false };
			using var dossier = new BotDossier { Dock = DockStyle.Fill };
			host.Controls.Add(dossier);
			host.Show();
			host.Update();
			var invalidated = new List<Rectangle>();
			dossier.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);

			foreach (var width in new[] { 960, 620, 840, 800 })
			{
				invalidated.Clear();
				host.ClientSize = new Size(width, 210);
				Assert.That(invalidated.Any(rectangle => rectangle.Contains(dossier.ClientRectangle)), Is.True,
					$"Resizing to {width} must invalidate the whole old schematic, not just the newly exposed strip.");
				host.Update();
			}
		}

		[Test]
		public void InitialLayoutFitsTheActualMonitorDpi()
		{
			using var window = new MainForm(settings) { ShowInTaskbar = false };
			window.Show();
			window.PerformLayout();
			TestContext.WriteLine($"Monitor DPI: {window.DeviceDpi}; scale baseline: {window.AutoScaleDimensions}; size: {window.Size}");
			var dossier = Descendants(window).OfType<BotDossier>().Single();
			Assert.Multiple(() =>
			{
				foreach (var label in dossier.Controls.OfType<Label>())
					Assert.That(dossier.ClientRectangle.Contains(label.Bounds), Is.True,
						$"Dossier label '{label.Text}' at {label.Bounds} must fit {dossier.ClientRectangle} at {window.DeviceDpi} DPI.");
				var title = Descendants(window).OfType<Label>().Single(label => label.Text == "Prepare for contact.");
				Assert.That(title.Height, Is.GreaterThanOrEqualTo(title.PreferredHeight),
					"The station title must include the complete font height and padding.");
				var faction = Named<ComboBox>(window, "Your faction");
				var textWidth = TextRenderer.MeasureText("Random", faction.Font).Width;
				Assert.That(faction.ClientSize.Width, Is.GreaterThanOrEqualTo(textWidth + 28 * window.DeviceDpi / 96),
					"The complete faction name must fit beside the drop-down arrow.");
			});
		}

		[Test]
		public void MonitorScaledStationsKeepLabelsAndActionsUsableAfterResizing()
		{
			using var window = new MainForm(settings) { ShowInTaskbar = false };
			window.Show();
			window.MinimumSize = Size.Empty;
			foreach (var size in new[] { new Size(1200, 850), new Size(940, 700), new Size(1500, 950), new Size(1200, 850) })
			{
				window.Size = DeviceSize(size, window.DeviceDpi);
				for (var station = 0; station < 4; station++)
				{
					window.SelectStation(station);
					window.Update();
					var dossier = Descendants(window).OfType<BotDossier>().Single();
					foreach (var label in dossier.Controls.OfType<Label>())
						Assert.That(dossier.ClientRectangle.Contains(label.Bounds), Is.True,
							$"{label.Text} must fit after resizing at {window.DeviceDpi} DPI.");
					var launch = (Button)window.AcceptButton;
					var origin = window.PointToClient(launch.PointToScreen(Point.Empty));
					Assert.That(window.ClientRectangle.Contains(new Rectangle(origin, launch.Size)), Is.True);
					var history = Descendants(window).OfType<Button>().Single(button => button.Text == "History && trends");
					Assert.That(history.Height, Is.LessThanOrEqualTo(history.GetPreferredSize(Size.Empty).Height),
						"Resizing must not stretch the history action into empty navigation space.");
					Assert.That(history.Height, Is.LessThanOrEqualTo(3 * history.Font.Height),
						"History must stay a single-line action after growing and shrinking the window.");
					var title = Descendants(window).OfType<Label>().Single(label => label.Name == "StationTitle");
					Assert.That(title.Height, Is.GreaterThanOrEqualTo(title.PreferredHeight));

					var output = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
					if (!string.IsNullOrEmpty(output))
					{
						Directory.CreateDirectory(output);
						using var bitmap = new Bitmap(window.Width, window.Height);
						window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
						bitmap.Save(Path.Combine(output, $"resize-{window.DeviceDpi}-{size.Width}-{size.Height}-{station}.png"), ImageFormat.Png);
					}
				}
			}
		}

		static Size DeviceSize(Size logical, int dpi) =>
			new((int)Math.Round(logical.Width * dpi / 96f), (int)Math.Round(logical.Height * dpi / 96f));

		static T Named<T>(Control root, string name) where T : Control =>
			Descendants(root).OfType<T>().Single(control => control.AccessibleName == name);

		static IEnumerable<Control> Descendants(Control root)
		{
			foreach (Control child in root.Controls)
			{
				yield return child;
				foreach (var nested in Descendants(child))
					yield return nested;
			}
		}
	}
}
