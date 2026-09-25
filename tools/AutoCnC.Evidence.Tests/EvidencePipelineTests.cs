#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	/// <summary>
	/// A run directory is created before a match starts, so one exists whether or not a fight
	/// ever happened in it. Deciding which of those count is the pipeline's job.
	/// </summary>
	[TestFixture]
	public sealed class EvidencePipelineTests : EvidenceTestBase
	{
		[Test]
		public void AFightWithNoBattleLogIsNotAddedToTheHistory()
		{
			WriteFile("telemetry.csv", TelemetryHeader + TelemetryRow(0, "local", 0, 0, 0));

			var history = Path.Combine(TempDirectory, "history.json");
			var outcome = EvidencePipeline.Run(TempDirectory, history, bot: "TestBot");

			Assert.That(outcome.Indexed, Is.False);
			Assert.That(outcome.Skipped, Does.Contain("no battle log"));
			Assert.That(File.Exists(history), Is.False, "a cancelled launch must not create an index");

			// The artifacts are still written: they document what was there.
			Assert.That(File.Exists(Path.Combine(TempDirectory, "summary.json")), Is.True);
		}

		/// <summary>
		/// A one-second run is a cancelled launch, and scoring almost zero it would drag every
		/// median it touched and manufacture regressions in metrics nobody changed.
		/// </summary>
		[Test]
		public void AFightShorterThanTheMinimumIsNotAddedToTheHistory()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(1, "over", "local", detail: "result=Lost"));
			WriteFile("telemetry.csv", TelemetryHeader + TelemetryRow(1, "local", 0, 0, 0));

			var history = Path.Combine(TempDirectory, "history.json");
			var outcome = EvidencePipeline.Run(TempDirectory, history, bot: "TestBot");

			Assert.That(outcome.Indexed, Is.False);
			Assert.That(outcome.Skipped, Does.Contain("cancelled launch"));
			Assert.That(File.Exists(history), Is.False);
		}

		[Test]
		public void ARealFightIsAddedToTheHistory()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "e1", 1) +
				BattleRow(600, "over", "local", detail: "result=Won"));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 1, 100, 600) +
				TelemetryRow(600, "local", 1, 100, 600));

			var history = Path.Combine(TempDirectory, "history.json");
			var outcome = EvidencePipeline.Run(TempDirectory, history, bot: "TestBot");

			Assert.That(outcome.Indexed, Is.True);
			Assert.That(outcome.Skipped, Is.Null);
			Assert.That(RunIndex.Read(history).Runs.Count, Is.EqualTo(1));
		}

		/// <summary>
		/// The rules a fight was played under are read from beside its battle log, where
		/// run-bot.ps1 wrote them at launch, and carried into the summary and the index.
		/// </summary>
		[Test]
		public void TheRulesFingerprintBesideTheBattleLogReachesTheSummaryAndTheHistory()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(600, "over", "local", detail: "result=Won"));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 1, 100, 600) +
				TelemetryRow(600, "local", 1, 100, 600));
			WriteFile("rules-fingerprint.json",
				"{ \"schemaVersion\": 1, \"fingerprint\": \"44db50ef17715b00\", \"files\": [\"rules/units.yaml\"] }");

			var history = Path.Combine(TempDirectory, "history.json");
			var outcome = EvidencePipeline.Run(TempDirectory, history, bot: "TestBot");

			Assert.That(outcome.Summary.Fight.RulesFingerprint, Is.EqualTo("44db50ef17715b00"));
			Assert.That(RunIndex.Read(history).Runs.Single().RulesFingerprint, Is.EqualTo("44db50ef17715b00"));
		}

		[Test]
		public void AFightWithoutARulesFingerprintRecordsNone()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(600, "over", "local", detail: "result=Lost"));
			WriteFile("rules-fingerprint.json", "not json");

			var outcome = EvidencePipeline.Run(TempDirectory);

			Assert.That(outcome.Summary.Fight.RulesFingerprint, Is.Null);
		}

		/// <summary>
		/// The index outlives the evidence folders it describes, so overwriting it keeps the
		/// previous version one step behind. Rebuilding by scanning directories once cost twelve
		/// entries for runs whose folders a player had already deleted.
		/// </summary>
		[Test]
		public void WritingTheHistoryKeepsThePreviousVersionAlongsideIt()
		{
			var history = Path.Combine(TempDirectory, "history.json");
			var first = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(first, "TestBot", new RunHistoryEntry { RunId = "one", Fitness = 0.5 });
			RunIndex.WriteHistory(history, first);

			Assert.That(File.Exists(history + ".bak"), Is.False, "nothing to back up on first write");

			var second = RunIndex.Read(history);
			RunIndex.Record(second, "TestBot", new RunHistoryEntry { RunId = "two", Fitness = 0.6 });
			RunIndex.WriteHistory(history, second);

			Assert.That(File.Exists(history + ".bak"), Is.True);
			Assert.That(RunIndex.Read(history).Runs.Count, Is.EqualTo(2));
			Assert.That(RunIndex.Read(history + ".bak").Runs.Count, Is.EqualTo(1));
		}
	}
}
