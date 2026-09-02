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
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class ResultsWindowTests
	{
		[Test]
		public void ManualBattleAcceptsFeedbackButContinuousModeDoesNot()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Results Window",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				run.Manifest.Status = "finished";
				run.Manifest.CompletedUtc = DateTime.UtcNow;
				run.Manifest.Result = new TrainingBattleResult
				{
					DurationSeconds = 90,
					LocalPlayer = "You",
					Outcome = "Lost",
					Players =
					[
						new TrainingPlayerResult { Name = "You", Outcome = "Lost" },
						new TrainingPlayerResult { Name = "Opponent", IsBot = true, Outcome = "Won" }
					]
				};
				run.Save();
				var history = TrainingHistory.FromRuns([run]);

				using var window = new ResultsWindow(new MatchLog());
				window.SetHistory(history, run, continuous: false);
				string accepted = null;
				window.PlayerFeedbackSaved += (_, value) => accepted = value;
				window.SetFeedbackText("I attacked before replacing my losses.");

				Assert.That(window.CanSaveFeedback, Is.True);
				window.SubmitFeedback();
				Assert.That(accepted, Is.EqualTo("I attacked before replacing my losses."));
				Assert.That(window.HistorySummaryText, Does.Contain("1 iteration(s): 0 won, 1 lost."));

				window.SetHistory(history, run, continuous: true);
				window.SetFeedbackText("This must not be submitted.");
				Assert.That(window.CanSaveFeedback, Is.False);
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Test]
		public void HistoricalSelectionAndUnsavedFeedbackSurviveNewIterations()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Results Selection",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var first = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				Complete(first, "Lost", 120);
				var second = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				Complete(second, "Won", 90);
				var history = TrainingHistory.FromRuns([first, second]);
				using (var chart = new IterationChart("Army", iteration => iteration.LocalPlayer.ArmyValue,
					iteration => iteration.Opponents.ArmyValue)
					{ Size = new Size(640, 360), Iterations = history.Iterations })
				using (var bitmap = new Bitmap(chart.Width, chart.Height))
					Assert.That(() => chart.DrawToBitmap(bitmap, chart.ClientRectangle), Throws.Nothing);

				using var window = new ResultsWindow(new MatchLog());
				window.SetHistory(history, second, continuous: false);
				window.SelectedBattleIndex = 0;
				window.SetFeedbackText("Keep this draft while another iteration starts.");

				var third = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				window.SetHistory(history.WithRun(third), third, continuous: false);

				Assert.That(window.SelectedBattleIndex, Is.EqualTo(0));
				Assert.That(window.FeedbackText, Is.EqualTo("Keep this draft while another iteration starts."));
				Assert.That(window.CanSaveFeedback, Is.True);
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		static void Complete(TrainingRun run, string outcome, int duration)
		{
			run.Manifest.Status = "finished";
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Result = new TrainingBattleResult
			{
				DurationSeconds = duration,
				LocalPlayer = "You",
				Outcome = outcome,
				Players =
				[
					new TrainingPlayerResult { Name = "You", Outcome = outcome },
					new TrainingPlayerResult
					{
						Name = "Opponent",
						IsBot = true,
						Outcome = outcome == "Won" ? "Lost" : "Won"
					}
				]
			};
			run.Save();
		}
	}
}
