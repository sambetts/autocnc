// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TrainingRunDeletionTests
	{
		string root;
		string project;
		string runs;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "session-deletion-" + Guid.NewGuid().ToString("N"));
			var source = Path.Combine(root, "Bot");
			Directory.CreateDirectory(source);
			project = Path.Combine(source, "Bot.csproj");
			File.WriteAllText(project, "<Project />");
			File.WriteAllText(Path.Combine(source, "Strategy.cs"), "current source");
			runs = Path.Combine(root, "runs");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		TrainingRun NewRun(bool completed = true)
		{
			var run = TrainingRun.Create(project,
				new TrainingBattleConfiguration { ExecutionMode = BattleExecutionModes.Rendered }, runs);
			if (completed)
			{
				run.Manifest.CompletedUtc = DateTime.UtcNow;
				run.Manifest.Status = "finished";
				run.Manifest.Result = new TrainingBattleResult
				{
					Outcome = "Lost", DurationSeconds = 120, LocalPlayer = "You",
					Players = [new TrainingPlayerResult { Name = "You", Units = 5, ArmyValue = 700, Outcome = "Lost" }]
				};
				run.Save();
			}
			return run;
		}

		[Test]
		public void DeletionRemovesOnlyTheChosenSessionAndItsCopiesOfEvidence()
		{
			var run = NewRun();
			var survivor = NewRun();
			run.SetPlayerFeedback("A saved observation.");
			WorkspaceSnapshot.Capture(run);
			var originalReplay = Path.Combine(root, "original.orarep");
			File.WriteAllText(originalReplay, "original replay");
			run.CaptureReplay(originalReplay);
			File.WriteAllText(run.AgentTranscriptPath, "old agent transcript");

			run.Delete(runs);

			Assert.That(Directory.Exists(run.RunDirectory), Is.False);
			Assert.That(Directory.Exists(runs), Is.True);
			Assert.That(TrainingRun.Load(run.RunDirectory), Is.Null);
			Assert.That(File.ReadAllText(originalReplay), Is.EqualTo("original replay"));
			Assert.That(File.Exists(survivor.ManifestPath), Is.True);
			Assert.That(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project), "Strategy.cs")), Is.EqualTo("current source"));
			Assert.That(TrainingHistory.Load(project, runs).Runs.Select(item => item.Manifest.Id),
				Is.EqualTo(new[] { survivor.Manifest.Id }));
		}

		[Test]
		public void FailedSessionsWithoutBattleTelemetryCanBeDeleted()
		{
			var run = NewRun();
			run.Manifest.Status = "failed";
			run.Manifest.Result = new TrainingBattleResult();
			run.Save();
			Assert.That(run.HasRecordedBattle, Is.False);
			Assert.That(run.CanDelete, Is.True);
			run.Delete(runs);
			Assert.That(Directory.Exists(run.RunDirectory), Is.False);
		}

		[TestCase("running")]
		[TestCase("improving")]
		[TestCase("verifying")]
		public void ActiveBattlesAndImprovementsCannotBeDeleted(string state)
		{
			var run = NewRun(completed: state != "running");
			run.Manifest.Status = state;
			run.Save();
			Assert.That(run.CanDelete, Is.False);
			Assert.That(() => run.Delete(runs), Throws.InvalidOperationException);
			Assert.That(File.Exists(run.ManifestPath), Is.True);
		}

		[Test]
		public void ANewOperationOnDiskIsNotDeletedThroughAStaleHistoryRow()
		{
			var run = NewRun();
			var newer = TrainingRun.Load(run.RunDirectory);
			newer.AgentStarted("test agent");
			Assert.That(run.CanDelete, Is.True);
			Assert.That(() => run.Delete(runs), Throws.InvalidOperationException);
			Assert.That(File.Exists(run.ManifestPath), Is.True);
		}

		[TestCase("..")]
		[TestCase(".")]
		[TestCase("subdirectory\\another-run")]
		[TestCase("a-different-session")]
		public void InvalidIdentifiersCannotBroadenTheDeletionTarget(string id)
		{
			var run = NewRun();
			run.Manifest.Id = id;
			Assert.That(() => run.Delete(runs), Throws.TypeOf<InvalidDataException>());
			Assert.That(File.Exists(run.ManifestPath), Is.True);
			Assert.That(File.Exists(project), Is.True);
		}

		[Test]
		public void TheArchiveRootAndBotSourceAreNeverDeletionTargets()
		{
			var run = NewRun();
			Assert.That(() => run.Delete(Path.Combine(root, "wrong-archive")), Throws.TypeOf<InvalidDataException>());
			var sourceManifest = Path.Combine(Path.GetDirectoryName(project), "manifest.json");
			File.Copy(run.ManifestPath, sourceManifest);
			var misplaced = TrainingRun.Load(Path.GetDirectoryName(project));
			Assert.That(() => misplaced.Delete(runs), Throws.TypeOf<InvalidDataException>());
			Assert.That(File.Exists(project), Is.True);
			Assert.That(File.Exists(run.ManifestPath), Is.True);
		}

		[Test]
		public void SourceNestedInsideASessionPreventsDeletion()
		{
			var run = NewRun();
			run.Manifest.BotDirectory = Path.Combine(run.RunDirectory, "current-source");
			Directory.CreateDirectory(run.Manifest.BotDirectory);
			File.WriteAllText(Path.Combine(run.Manifest.BotDirectory, "Strategy.cs"), "keep");
			run.Save();
			Assert.That(() => run.Delete(runs), Throws.TypeOf<InvalidDataException>());
			Assert.That(File.Exists(run.ManifestPath), Is.True);
		}

		[Test]
		public void LockedEvidenceLeavesTheManifestVisibleAndDeletionCanBeRetried()
		{
			var run = NewRun();
			File.WriteAllText(run.TelemetryPath, "locked evidence");
			using (File.Open(run.TelemetryPath, FileMode.Open, FileAccess.Read, FileShare.None))
				Assert.That(() => run.Delete(runs), Throws.InstanceOf<IOException>());
			Assert.That(TrainingHistory.Load(project, runs).Runs.Count, Is.EqualTo(1));
			run.Delete(runs);
			Assert.That(TrainingHistory.Load(project, runs).Runs, Is.Empty);
		}

		[Test]
		public void RemovingARunRecalculatesTrendsWithoutMutatingTheOriginalHistory()
		{
			var run = NewRun();
			var survivor = NewRun();
			var history = TrainingHistory.FromRuns([run, survivor]);
			var updated = history.WithoutRun(run);
			Assert.That(history.Runs.Count, Is.EqualTo(2));
			Assert.That(updated.Runs.Single(), Is.SameAs(survivor));
			Assert.That(updated.Iterations.Single().Run, Is.SameAs(survivor));
			Assert.That(updated.Iterations.Single().Number, Is.EqualTo(1));
			Assert.That(updated.WithoutRun(survivor).Iterations, Is.Empty);
		}
	}
}
