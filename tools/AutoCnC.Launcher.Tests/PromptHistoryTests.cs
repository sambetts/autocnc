// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class PromptHistoryTests
	{
		string root;
		string archiveRoot;
		string runs;
		string project;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC.PromptHistory.Tests",
				Guid.NewGuid().ToString("N"));
			archiveRoot = Path.Combine(root, "prompt-history");
			runs = Path.Combine(root, "runs");
			var workspace = Path.Combine(root, "My Bot");
			project = Path.Combine(workspace, "MyBot.csproj");
			Directory.CreateDirectory(workspace);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
				Directory.Delete(root, true);
		}

		[Test]
		public void EveryAdoptedTemplateBecomesAReadableNumberedRevision()
		{
			var history = new PromptHistory(archiveRoot);

			var first = history.Record("Edit only {workspace}. First.", PromptOrigin.Baseline);
			var second = history.Record("Edit only {workspace}. Second.", PromptOrigin.Continuous);

			Assert.That(first.Revision, Is.EqualTo(1));
			Assert.That(second.Revision, Is.EqualTo(2));
			Assert.That(first.File, Is.EqualTo("0001-baseline.txt"));
			Assert.That(second.File, Is.EqualTo("0002-continuous.txt"));
			Assert.That(history.TextOf(first), Is.EqualTo("Edit only {workspace}. First."));
			Assert.That(history.TextOf(second), Is.EqualTo("Edit only {workspace}. Second."));
			Assert.That(history.Read().Revisions.Select(revision => revision.Revision),
				Is.EqualTo(new[] { 1, 2 }));

			// Nothing in the launcher shows this folder, so the folder has to explain itself.
			Assert.That(File.Exists(Path.Combine(archiveRoot, "README.txt")), Is.True);
		}

		[Test]
		public void ARevisionRemembersTheFightThatArguedForIt()
		{
			var run = TrainingRun.Create(project, new TrainingBattleConfiguration(), runs);
			run.Manifest.Result = new TrainingBattleResult { Outcome = "Lost" };
			run.Save();
			var history = new PromptHistory(archiveRoot);

			var revision = history.Record("Edit only {workspace}. Attack later.",
				PromptOrigin.Continuous, run);

			Assert.That(revision.Bot, Is.EqualTo("MyBot"));
			Assert.That(revision.RunId, Is.EqualTo(run.Manifest.Id));
			Assert.That(revision.BattleOutcome, Is.EqualTo("Lost"));
			Assert.That(revision.Origin, Is.EqualTo("continuous"));
			Assert.That(revision.Length, Is.EqualTo("Edit only {workspace}. Attack later.".Length));
		}

		[Test]
		public void AnUnchangedTemplateIsNotANewRevision()
		{
			var history = new PromptHistory(archiveRoot);
			history.Record("Edit only {workspace}.", PromptOrigin.Baseline);

			Assert.That(history.Record("Edit only {workspace}.", PromptOrigin.Manual), Is.Null);
			Assert.That(history.Record("  Edit only {workspace}.  ", PromptOrigin.Manual), Is.Null);
			Assert.That(history.Record("   ", PromptOrigin.Manual), Is.Null);
			Assert.That(history.Record(null, PromptOrigin.Manual), Is.Null);
			Assert.That(history.Read().Revisions, Has.Count.EqualTo(1));

			Assert.That(history.Record("Edit only {workspace}. Changed.", PromptOrigin.Manual),
				Is.Not.Null);
			Assert.That(history.Read().Revisions, Has.Count.EqualTo(2));
		}

		[Test]
		public void AFileLeftBehindByAFailedWriteDoesNotDuplicateARevision()
		{
			var history = new PromptHistory(archiveRoot);
			history.Record("Edit only {workspace}. First.", PromptOrigin.Baseline);
			File.WriteAllText(Path.Combine(archiveRoot, "0002-manual.txt"), "abandoned attempt");

			var recorded = history.Record("Edit only {workspace}. Second.", PromptOrigin.Continuous);

			Assert.That(recorded.File, Is.EqualTo("0002-continuous.txt"));
			Assert.That(Directory.EnumerateFiles(archiveRoot, "0002-*.txt").Select(Path.GetFileName),
				Is.EqualTo(new[] { "0002-continuous.txt" }));
		}

		[Test]
		public void AnUnreadableIndexDoesNotStopTheNextPromptBeingKept()
		{
			var history = new PromptHistory(archiveRoot);
			history.Record("Edit only {workspace}. First.", PromptOrigin.Baseline);
			File.WriteAllText(history.IndexPath, "{ not json");

			Assert.That(history.Read().Revisions, Is.Empty);
			Assert.That(history.Record("Edit only {workspace}. Second.", PromptOrigin.Manual).Revision,
				Is.EqualTo(1));
			Assert.That(history.Read().Revisions, Has.Count.EqualTo(1));
		}

		[Test]
		public void AnArchiveThatHasNeverBeenWrittenReadsAsEmptyRatherThanFailing()
		{
			var history = new PromptHistory(archiveRoot);

			Assert.That(Directory.Exists(archiveRoot), Is.False);
			Assert.That(history.Read().Revisions, Is.Empty);
			Assert.That(history.TextOf(new PromptRevision { File = "0001-manual.txt" }), Is.Null);
			Assert.That(history.PathOf(new PromptRevision { File = "..\\escape.txt" }), Is.Null);
		}
	}
}
