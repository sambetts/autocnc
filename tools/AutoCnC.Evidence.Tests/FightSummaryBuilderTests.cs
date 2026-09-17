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

using System.Linq;
using System.Text;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class FightSummaryBuilderTests : EvidenceTestBase
	{
		[Test]
		public void FreeActorsAreExcludedFromSpendByRulesetProvenance()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "harv", 1) +
				BattleRow(2, "built", "local", "e1", 2));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 2, 100, 600, earned: 0, spent: 0) +
				TelemetryRow(0, "enemy", 1, 50, 500) +
				TelemetryRow(60, "local", 2, 100, 600, earned: 1000, spent: 100) +
				TelemetryRow(60, "enemy", 1, 50, 500));
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", "{\"schemaVersion\":2,\"actors\":[" +
				"{\"id\":\"proc\",\"kind\":\"building\",\"cost\":2000,\"freeActors\":[\"harv\"]}," +
				"{\"id\":\"harv\",\"kind\":\"vehicle\",\"cost\":1400}," +
				"{\"id\":\"e1\",\"kind\":\"infantry\",\"cost\":100}]}");

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 60);
			var summary = FightSummaryBuilder.Build(evidence, units);
			var harvester = summary.UnitTypes.Row("harv");
			var infantry = summary.UnitTypes.Row("e1");

			Assert.That(harvester["creditsSpent"], Is.EqualTo("0"));
			Assert.That(harvester["shareOfSpendPercent"], Is.EqualTo("0"));
			Assert.That(harvester["freeCount"], Is.EqualTo("1"));
			Assert.That(infantry["shareOfSpendPercent"], Is.EqualTo("100"));
			Assert.That(summary.Provenance.FreeActorExclusion, Is.EqualTo("ruleset"));
		}

		/// <summary>
		/// A type that is both granted and buildable charges only the ones that were ordered.
		/// </summary>
		/// <remarks>
		/// In the C&amp;C rules a refinery grants a harvester and a harvester also costs 1,400
		/// credits outright, so marking the whole type free zeroed every purchased one — a large,
		/// silent error in the two figures the ledger exists to get right. The decision trace
		/// settles it: a harvester that was paid for has a Produce order naming it.
		/// </remarks>
		[Test]
		public void OrderedInstancesOfAFreeTypeAreStillCharged()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "harv", 1) +
				BattleRow(30, "built", "local", "harv", 2) +
				BattleRow(40, "built", "local", "harv", 3));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 3, 100, 600, earned: 0, spent: 0) +
				TelemetryRow(0, "enemy", 1, 50, 500));

			// Two of the three were ordered; the third arrived with the refinery.
			WriteFile("decisions.jsonl",
				"{\"event\":\"unit-decision\",\"seconds\":20,\"actor\":\"fact\",\"actorId\":9," +
				"\"mode\":\"BuildBaseMode\",\"action\":\"Produce\",\"itemName\":\"harv\"," +
				"\"queue\":\"Vehicle\",\"reason\":\"buying a harvester\"}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":35,\"actor\":\"fact\",\"actorId\":9," +
				"\"mode\":\"BuildBaseMode\",\"action\":\"Produce\",\"itemName\":\"harv\"," +
				"\"queue\":\"Vehicle\",\"reason\":\"buying a harvester\"}\n");

			WriteFile("game-rules.json", "{\"schemaVersion\":2,\"actors\":[" +
				"{\"id\":\"proc\",\"kind\":\"building\",\"cost\":2000,\"freeActors\":[\"harv\"]}," +
				"{\"id\":\"harv\",\"kind\":\"vehicle\",\"cost\":1400}]}");

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 60);
			var summary = FightSummaryBuilder.Build(evidence, units);
			var harvester = summary.UnitTypes.Row("harv");

			Assert.That(harvester["built"], Is.EqualTo("3"));
			Assert.That(harvester["freeCount"], Is.EqualTo("1"), "only the unordered one was granted");
			Assert.That(harvester["creditsSpent"], Is.EqualTo("2800"), "two ordered at 1400 each");

			// The granted one is the earliest, because a granted actor arrives with the structure
			// that granted it.
			Assert.That(units.Single(u => u.ActorId == 1).Free, Is.True);
			Assert.That(units.Single(u => u.ActorId == 2).Free, Is.False);
			Assert.That(units.Single(u => u.ActorId == 3).Free, Is.False);
		}

		[Test]
		public void OldRulesetsDeclareFreeActorExclusionUnavailable()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "harv", 1));
			WriteFile("telemetry.csv", TelemetryHeader + TelemetryRow(0, "local", 1, 100, 600) +
				TelemetryRow(0, "enemy", 1, 50, 500));
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", "{\"schemaVersion\":1,\"actors\":[" +
				"{\"id\":\"harv\",\"kind\":\"vehicle\",\"cost\":1400}]}");

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 1);
			var summary = FightSummaryBuilder.Build(evidence, units);

			Assert.That(summary.Provenance.FreeActorExclusion, Is.EqualTo("unavailable"));
			Assert.That(summary.Notes.Any(n => n.Contains("predates the freeActors field")), Is.True);
		}

		[Test]
		public void CrossoverUsesStartOfFinalLosingStretch()
		{
			var battle = BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red");
			var telemetry = new StringBuilder(TelemetryHeader);
			telemetry.Append(TelemetryRow(0, "local", 10, 1000, 1200));
			telemetry.Append(TelemetryRow(0, "enemy", 5, 500, 800));
			telemetry.Append(TelemetryRow(10, "local", 4, 400, 600));
			telemetry.Append(TelemetryRow(10, "enemy", 6, 600, 700));
			telemetry.Append(TelemetryRow(20, "local", 7, 700, 900));
			telemetry.Append(TelemetryRow(20, "enemy", 7, 700, 900));
			telemetry.Append(TelemetryRow(30, "local", 8, 800, 1000));
			telemetry.Append(TelemetryRow(30, "enemy", 6, 600, 900));
			telemetry.Append(TelemetryRow(40, "local", 5, 500, 700));
			telemetry.Append(TelemetryRow(40, "enemy", 9, 900, 1100));
			telemetry.Append(TelemetryRow(50, "local", 4, 400, 650));
			telemetry.Append(TelemetryRow(50, "enemy", 10, 1000, 1200));
			WriteFile("battle.csv", battle);
			WriteFile("telemetry.csv", telemetry.ToString());
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", MinimalRules(("e1", 100, "infantry")));

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 50);
			var summary = FightSummaryBuilder.Build(evidence, units);

			Assert.That(summary.Crossover.Units.FirstBehindSeconds, Is.EqualTo(40));
			Assert.That(summary.Crossover.Units.LastLevelOrAheadSeconds, Is.EqualTo(30));
			Assert.That(summary.Crossover.Army.FirstBehindSeconds, Is.EqualTo(40));
			Assert.That(summary.Crossover.Assets.LastLevelOrAheadSeconds, Is.EqualTo(30));
		}

		[Test]
		public void SerialisedSummaryFitsTheSizeCeilingAndDeclaresTruncation()
		{
			var battle = new StringBuilder(BattleHeader);
			battle.Append(BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold"));
			battle.Append(BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red"));
			for (var i = 0; i < 400; i++)
			{
				var type = "unit" + i.ToString("000");
				battle.Append(BattleRow(1 + i, "built", "local", type, (uint)(i + 1), x: (i % 128).ToString(), y: (i % 64).ToString()));
				battle.Append(BattleRow(600 + i, "lost", "local", type, (uint)(i + 1), "enemy", "enemy" + i.ToString("000"), 9000,
					(i % 128).ToString(), (i % 64).ToString(), "value=100"));
			}

			var telemetry = new StringBuilder(TelemetryHeader);
			for (var second = 0; second <= 10800; second += 60)
			{
				telemetry.Append(TelemetryRow(second, "local", 400, 40000, 45000, earned: second * 2, spent: second,
					lost: second / 60));
				telemetry.Append(TelemetryRow(second, "enemy", 450, 45000, 50000));
			}

			var rules = new StringBuilder("{\"schemaVersion\":2,\"actors\":[");
			for (var i = 0; i < 400; i++)
			{
				if (i > 0)
					rules.Append(',');

				rules.Append("{\"id\":\"unit").Append(i.ToString("000"))
					.Append("\",\"kind\":\"infantry\",\"cost\":100}");
			}
			rules.Append("]}");

			WriteFile("battle.csv", battle.ToString());
			WriteFile("telemetry.csv", telemetry.ToString());
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", rules.ToString());

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 10800);
			var summary = FightSummaryBuilder.Build(evidence, units);
			var json = FightSummaryBuilder.Serialise(summary);

			Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThanOrEqualTo(FightSummaryBuilder.MaxBytes));
			Assert.That(summary.Truncated, Is.Not.Empty);
		}

		[Test]
		public void SerialisedNormalSummaryDoesNotDeclareTruncation()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "e1", 1) +
				BattleRow(30, "killed", "enemy", "e1", 301, "local", "e1", 1, "4", "4", "value=100"));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 1, 100, 600, earned: 0, spent: 0) +
				TelemetryRow(0, "enemy", 1, 50, 500) +
				TelemetryRow(60, "local", 1, 100, 600, earned: 1000, spent: 100) +
				TelemetryRow(60, "enemy", 0, 0, 400));
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", MinimalRules(("e1", 100, "infantry")));

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 60);
			var summary = FightSummaryBuilder.Build(evidence, units);
			var json = FightSummaryBuilder.Serialise(summary);

			Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThanOrEqualTo(FightSummaryBuilder.MaxBytes));
			Assert.That(summary.Truncated, Is.Empty);
		}
	}
}
