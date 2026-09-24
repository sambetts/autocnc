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

		/// <summary>
		/// A small GDI-against-Nod fight: a buggy seen long before it attacks, a helicopter seen two
		/// seconds before it kills a rifleman, and an unarmed MCV that is no threat at all.
		/// </summary>
		void WriteIntelFight(bool withMatchups)
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(10, "built", "local", "pyle", 50, detail: "kind=Building") +
				BattleRow(20, "built", "local", "weap", 51, detail: "kind=Building") +
				BattleRow(30, "built", "local", "e1", 1, detail: "kind=Infantry") +
				BattleRow(31, "built", "local", "e3", 2, detail: "kind=Infantry") +
				BattleRow(100, "spotted", "enemy", "bggy", 100, "local", x: "40", y: "20", detail: "kind=Vehicle frombase=35") +
				BattleRow(150, "spotted", "enemy", "hand", 110, "local", x: "60", y: "20", detail: "kind=Structure frombase=40") +
				BattleRow(200, "spotted", "enemy", "heli", 120, "local", x: "12", y: "20", detail: "kind=Aircraft frombase=12") +
				BattleRow(202, "attacked", "local", "e1", 1, "enemy", "heli", 120, "10", "20", "damage=500 health=90") +
				BattleRow(205, "lost", "local", "e1", 1, "enemy", "heli", 120, "10", "20", "value=100 life=175 kind=Infantry") +
				BattleRow(300, "attacked", "local", "e3", 2, "enemy", "bggy", 100, "11", "20", "damage=900 health=80") +
				BattleRow(305, "lost", "local", "e3", 2, "enemy", "bggy", 100, "11", "20", "value=300 life=274 kind=Infantry") +
				BattleRow(310, "spotted", "enemy", "mcv", 130, "local", x: "50", y: "20", detail: "kind=Vehicle frombase=50"));
			WriteFile("telemetry.csv", TelemetryHeader +
				TelemetryRow(0, "local", 2, 400, 600, earned: 0, spent: 0) +
				TelemetryRow(0, "enemy", 2, 1500, 3000, earned: 0, spent: 0) +
				TelemetryRow(400, "local", 0, 0, 600, earned: 5000, spent: 4000) +
				TelemetryRow(400, "enemy", 3, 2700, 5000, earned: 9000, spent: 8000));
			WriteFile("decisions.jsonl", "");

			const string Armed = ",\"armaments\":[{\"name\":\"primary\"}]";
			WriteFile("game-rules.json", "{\"schemaVersion\":2,\"actors\":[" +
				"{\"id\":\"e1\",\"kind\":\"mobile\",\"cost\":100,\"build\":{\"queues\":[\"Infantry.GDI\",\"Infantry.Nod\"]}" + Armed + "}," +
				"{\"id\":\"e3\",\"kind\":\"mobile\",\"cost\":300,\"build\":{\"queues\":[\"Infantry.GDI\",\"Infantry.Nod\"]}" + Armed + "}," +
				"{\"id\":\"apc\",\"kind\":\"mobile\",\"cost\":600,\"build\":{\"queues\":[\"Vehicle.GDI\"]}" + Armed + "}," +
				"{\"id\":\"bike\",\"kind\":\"mobile\",\"cost\":500,\"build\":{\"queues\":[\"Vehicle.Nod\"]}" + Armed + "}," +
				"{\"id\":\"bggy\",\"kind\":\"mobile\",\"cost\":300,\"build\":{\"queues\":[\"Vehicle.Nod\"]}" + Armed + "}," +
				"{\"id\":\"heli\",\"kind\":\"aircraft\",\"cost\":1200,\"build\":{\"queues\":[\"Aircraft.Nod\"]}" + Armed + "}," +
				"{\"id\":\"mcv\",\"kind\":\"mobile\",\"cost\":3000,\"build\":{\"queues\":[\"Vehicle.GDI\",\"Vehicle.Nod\"]}}," +
				"{\"id\":\"pyle\",\"kind\":\"building\",\"cost\":500,\"produces\":[\"Infantry.GDI\"]}," +
				"{\"id\":\"weap\",\"kind\":\"building\",\"cost\":2000,\"produces\":[\"Vehicle.GDI\"]}," +
				"{\"id\":\"hand\",\"kind\":\"building\",\"cost\":500,\"produces\":[\"Infantry.Nod\"]}]}");

			if (withMatchups)
				WriteFile("matchups.json", "{\"schemaVersion\":1,\"units\":[\"e1\",\"e3\",\"apc\",\"bike\",\"bggy\",\"heli\"]," +
					"\"cost\":{" +
					"\"e1\":{\"e3\":0.9,\"bggy\":0.4,\"heli\":-0.4}," +
					"\"e3\":{\"e1\":-0.9,\"bggy\":-0.5,\"heli\":0.6}," +
					"\"apc\":{\"e1\":-0.6,\"bggy\":0.8,\"heli\":0.9}," +
					"\"bike\":{\"bggy\":-0.5,\"heli\":0.95}," +
					"\"bggy\":{\"e1\":-0.4,\"e3\":0.5,\"apc\":-0.8}," +
					"\"heli\":{\"e1\":0.4,\"e3\":-0.6,\"apc\":-0.9}}}");
		}

		[Test]
		public void IntelTimesEachEnemyTypeAgainstItsFirstHitAndNamesCountersOurFactionCanBuild()
		{
			WriteIntelFight(withMatchups: true);

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 400);
			var summary = FightSummaryBuilder.Build(evidence, units);
			var intel = summary.Intel;
			var heli = intel.Threats.Row("heli");
			var buggy = intel.Threats.Row("bggy");

			Assert.That(summary.Provenance.HasMatchups, Is.True);
			Assert.That(intel.Threats.Row("mcv"), Is.Null, "an unarmed unit is not a threat");
			Assert.That(intel.Threats.Rows[0], Does.StartWith("bggy,"), "most damaging first");

			Assert.That(heli["firstSeenSeconds"], Is.EqualTo("200"));
			Assert.That(heli["firstSeenCellsFromBase"], Is.EqualTo("12"));
			Assert.That(heli["firstHitSeconds"], Is.EqualTo("202"));
			Assert.That(heli["leadSeconds"], Is.EqualTo("2"));
			Assert.That(heli["counters"], Is.EqualTo("apc +0.90|e3 +0.60"),
				"the Nod-only bike is left out and the rifle, which cannot answer it, is below the bar");
			Assert.That(heli["creditsSpentOnCounters"], Is.EqualTo("300"));

			Assert.That(buggy["leadSeconds"], Is.EqualTo("200"));
			Assert.That(buggy["counters"], Is.EqualTo("apc +0.80|e1 +0.40"));
			Assert.That(buggy["creditsSpentOnCounters"], Is.EqualTo("100"));

			Assert.That(intel.LateSightingLossPercent, Is.EqualTo(25d), "the helicopter's 100 of 400 lost");
			Assert.That(intel.NearBaseSightingLossPercent, Is.EqualTo(25d));
			Assert.That(intel.FirstEnemyHitSeconds, Is.EqualTo(202));
			Assert.That(intel.EnemyBaseFirstSeenSeconds, Is.EqualTo(150));
			Assert.That(intel.EnemyStructuresFirstSeen["hand"], Is.EqualTo(150));

			// Weighted by value seen: 300 of buggy, 1,200 of helicopter.
			Assert.That(intel.MixCounters, Is.EqualTo("apc +0.88|e3 +0.38"));

			// 100 on rifles scoring -0.24 against that mix, 300 on rockets scoring +0.38.
			Assert.That(intel.CounterMatchPercent, Is.EqualTo(61));

			Assert.That(summary.Scale.OwnSpentPerSecond, Is.EqualTo(10d));
			Assert.That(summary.Scale.OpponentSpentPerSecond, Is.EqualTo(20d));
			Assert.That(summary.Scale.SpendVsOpponentPercent, Is.EqualTo(50d));
			Assert.That(summary.Scale.OwnUnitFactoriesPeak, Is.EqualTo(2));
			Assert.That(summary.Scale.OwnSecondUnitFactorySeconds, Is.EqualTo(20));
			Assert.That(summary.Notes.Any(n => n.Contains("the gap is how much was produced")), Is.True);

			var report = Checks.Evaluate(new CheckDocument
			{
				Checks =
				[
					new Check { Id = "lead", Query = "summary.intel.threats[heli].leadSeconds", Operator = "<=", Value = "5" },
					new Check { Id = "spend", Query = "summary.scale.spendVsOpponentPercent", Operator = "==", Value = "50" },
					new Check { Id = "late", Query = "summary.intel.lateSightingLossPercent", Operator = "<=", Value = "25" }
				]
			}, summary, units, evidence.Trace);
			Assert.That(report.Results.All(r => r.Passed), Is.True,
				string.Join("; ", report.Results.Select(r => $"{r.Id}: {r.Actual} {r.Error}")));

			var entry = RunIndex.Entry(summary, null);
			Assert.That(entry.Headline["lateSightingLossPercent"], Is.EqualTo(25d));
			Assert.That(entry.Headline["counterMatchPercent"], Is.EqualTo(61d));
			Assert.That(entry.Headline["spendVsOpponentPercent"], Is.EqualTo(50d));
		}

		[Test]
		public void WithoutMatchupsIntelKeepsItsTimingsButNamesNoCounters()
		{
			WriteIntelFight(withMatchups: false);

			var evidence = new EvidenceSet(TempDirectory) { MatchupsPath = "none" }.Load();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 400);
			var summary = FightSummaryBuilder.Build(evidence, units);

			Assert.That(summary.Provenance.HasMatchups, Is.False);
			Assert.That(summary.Intel.Threats.Row("heli")["leadSeconds"], Is.EqualTo("2"));
			Assert.That(summary.Intel.Threats.Row("heli")["counters"], Is.EqualTo(""));
			Assert.That(summary.Intel.MixCounters, Is.Null);
			Assert.That(summary.Intel.CounterMatchPercent, Is.Null);
			Assert.That(summary.Notes.Any(n => n.Contains("matchups.json was not found")), Is.True);
			Assert.That(RunIndex.Entry(summary, null).Headline.ContainsKey("counterMatchPercent"), Is.False);
		}
	}
}
