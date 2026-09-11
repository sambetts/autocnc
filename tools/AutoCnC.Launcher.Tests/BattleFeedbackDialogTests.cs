// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
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
	public sealed class BattleFeedbackDialogTests
	{
		sealed class CaptureHost : Form
		{
			protected override bool ShowWithoutActivation => true;
		}

		string root;
		TrainingRun run;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Feedback Dialog", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var project = Path.Combine(root, "Bot.csproj");
			File.WriteAllText(project, "<Project />");
			run = TrainingRun.Create(project,
				new TrainingBattleConfiguration { ExecutionMode = BattleExecutionModes.Rendered },
				Path.Combine(root, "runs"));
			run.Manifest.Status = "finished";
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Result = new TrainingBattleResult
			{
				Outcome = "Lost", DurationSeconds = 240,
				LocalPlayer = "You", Players = [new TrainingPlayerResult { Name = "You", Outcome = "Lost" }]
			};
			run.Save();
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, true);

		[Test]
		public void SavingFeedbackBeforeImprovementPersistsTheObservation()
		{
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback, beforeImprovement: true);
			Assert.That(dialog.CanSubmit, Is.False, "An empty assessment must not look like provided feedback.");
			dialog.FeedbackText = "I pushed before replacing the lost units.";
			Assert.That(dialog.CanEditFeedback, Is.True);
			Assert.That(dialog.CanSubmit, Is.True);
			dialog.Submit();
			Assert.That(dialog.DialogResult, Is.EqualTo(DialogResult.OK));
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Result.PlayerFeedback,
				Is.EqualTo("I pushed before replacing the lost units."));
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Contain("replacing the lost units"));
		}

		[Test]
		public void ImprovementOffersEvidenceOnlyAndCancelWithoutInventingFeedback()
		{
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback, beforeImprovement: true);
			var buttons = Descendants(dialog).OfType<Button>().ToList();
			Assert.That(buttons.Single(button => button.Text == "Improve without feedback").DialogResult,
				Is.EqualTo(DialogResult.Ignore));
			Assert.That(buttons.Single(button => button.Text == "Cancel").DialogResult, Is.EqualTo(DialogResult.Cancel));
			Assert.That(run.HasPlayerFeedback, Is.False);
		}

		[Test]
		public void SavingAnEditCanAlsoClearFeedback()
		{
			run.SetPlayerFeedback("An earlier observation.");
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback);
			Assert.That(dialog.FeedbackText, Is.EqualTo("An earlier observation."));
			Assert.That(dialog.CanSubmit, Is.False);
			dialog.FeedbackText = "";
			Assert.That(dialog.CanSubmit, Is.True);
			dialog.Submit();
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);
		}

		[Test]
		public void AFailedSaveKeepsTheDraftAndDoesNotClaimSuccess()
		{
			using var dialog = new BattleFeedbackDialog(run, _ => throw new IOException("Disk full."));
			dialog.FeedbackText = "Do not lose this observation.";
			dialog.Submit();
			Assert.That(dialog.DialogResult, Is.EqualTo(DialogResult.None));
			Assert.That(dialog.FeedbackText, Is.EqualTo("Do not lose this observation."));
			Assert.That(dialog.MessageText, Does.Contain("Disk full."));
			Assert.That(dialog.CanSubmit, Is.True);
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);
		}

		[Test]
		public void AnOverlongAssessmentIsRejectedWithoutLosingTheDraft()
		{
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback);
			dialog.FeedbackText = new string('x', TrainingRun.MaxPlayerFeedbackLength + 1);
			dialog.Submit();
			Assert.That(dialog.DialogResult, Is.EqualTo(DialogResult.None));
			Assert.That(dialog.MessageText, Does.Contain("cannot exceed"));
			Assert.That(dialog.FeedbackText.Length, Is.EqualTo(TrainingRun.MaxPlayerFeedbackLength + 1));
		}

		[Test]
		public void RequestingAHeadlessReplayCannotSaveOrMarkItWatched()
		{
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			File.WriteAllText(run.ReplayPath, "replay fixture");
			var saved = false;
			using var dialog = new BattleFeedbackDialog(run, _ => saved = true, beforeImprovement: true);
			Assert.That(dialog.CanEditFeedback, Is.False);
			Assert.That(dialog.CanSubmit, Is.True);
			dialog.Submit();
			Assert.That(dialog.DialogResult, Is.EqualTo(DialogResult.Retry));
			Assert.That(saved, Is.False);
			Assert.That(run.Manifest.ReplayWatchedUtc, Is.Null);
		}

		[Test]
		public void MissingHeadlessReplayStillAllowsEvidenceOnlyImprovement()
		{
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback, beforeImprovement: true);
			Assert.That(dialog.CanEditFeedback, Is.False);
			Assert.That(dialog.CanSubmit, Is.False);
			Assert.That(Descendants(dialog).OfType<Button>().Single(button => button.Text == "Improve without feedback").Enabled,
				Is.True);
		}

		[TestCase(560, 440, false)]
		[TestCase(760, 480, true)]
		public void ReviewDialogRendersAnEditableDraftOrClearReplayGate(int width, int height, bool headless)
		{
			run.Manifest.Battle.ExecutionMode = headless ? BattleExecutionModes.Headless : BattleExecutionModes.Rendered;
			if (headless)
				File.WriteAllText(run.ReplayPath, "replay fixture");
			using var dialog = new BattleFeedbackDialog(run, run.SetPlayerFeedback, beforeImprovement: true);
			if (!headless)
				dialog.FeedbackText = "I lost map control when both harvesters moved through the enemy attack.";
			using var host = new CaptureHost { ShowInTaskbar = false };
			dialog.TopLevel = false;
			dialog.FormBorderStyle = FormBorderStyle.None;
			dialog.Dock = DockStyle.Fill;
			host.Controls.Add(dialog);
			host.Show();
			host.ClientSize = new Size(width * host.DeviceDpi / 96, height * host.DeviceDpi / 96);
			dialog.Show();
			dialog.PerformLayout();
			foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Visible))
			{
				var origin = dialog.PointToClient(button.PointToScreen(Point.Empty));
				Assert.That(dialog.ClientRectangle.Contains(new Rectangle(origin, button.Size)), Is.True, button.Text);
			}
			var editor = Descendants(dialog).OfType<TextBox>().Single();
			Assert.That(editor.Height, Is.GreaterThan(40 * dialog.DeviceDpi / 96));
			using var bitmap = new Bitmap(dialog.Width, dialog.Height);
			dialog.DrawToBitmap(bitmap, dialog.ClientRectangle);
			var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
			if (!string.IsNullOrEmpty(capture))
			{
				Directory.CreateDirectory(capture);
				bitmap.Save(Path.Combine(capture, $"feedback-dialog-{width}-{height}-{dialog.DeviceDpi}.png"), ImageFormat.Png);
			}
		}

		static IEnumerable<Control> Descendants(Control parent) =>
			parent.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(Descendants(control)));
	}
}
