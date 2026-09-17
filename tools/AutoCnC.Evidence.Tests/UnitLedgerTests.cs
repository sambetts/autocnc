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
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class UnitLedgerTests : EvidenceTestBase
	{
		[Test]
		public void BuiltLostAndKilledRowsReconcileToOwnUnitLedger()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(1, "built", "local", "e1", 1) +
				BattleRow(2, "built", "local", "e1", 2) +
				BattleRow(3, "built", "local", "e1", 3) +
				BattleRow(20, "lost", "local", "e1", 2, "enemy", "e1", 202, "5", "5", "value=100") +
				BattleRow(30, "lost", "local", "e1", 3, "enemy", "e1", 203, "6", "5", "value=100") +
				BattleRow(40, "killed", "enemy", "e1", 301, "local", "e1", 1, "7", "5", "value=100") +
				BattleRow(50, "killed", "enemy", "e1", 302, "local", "e1", 1, "8", "5", "value=100"));
			WriteFile("decisions.jsonl", "");
			WriteFile("game-rules.json", MinimalRules(("e1", 100, "infantry")));

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 60);
			var own = units.Where(u => u.Owner == "local").ToList();

			Assert.That(units.Count(u => u.BornSeconds != null), Is.EqualTo(3));
			Assert.That(own.Count(u => u.DiedSeconds != null), Is.EqualTo(2));
			Assert.That(own.Sum(u => u.Kills), Is.EqualTo(2));
		}

		[Test]
		public void UnitLivesCarryDamageModesAndUnguesedEnemyBirths()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(5, "built", "local", "e1", 1, x: "1", y: "1") +
				BattleRow(6, "built", "local", "e1", 2, x: "2", y: "1") +
				BattleRow(10, "attacked", "local", "e1", 1, "enemy", "e1", 201, "3", "1", "damage=7") +
				BattleRow(12, "attacked", "local", "e1", 1, "enemy", "e1", 201, "4", "1", "damage=11") +
				BattleRow(25, "lost", "local", "e1", 1, "enemy", "e1", 201, "5", "1", "value=100") +
				BattleRow(30, "killed", "enemy", "e2", 301, "local", "e1", 2, "6", "1", "value=200"));
			WriteFile("decisions.jsonl",
				"{\"event\":\"unit-decision\",\"seconds\":7,\"actor\":\"e1\",\"actorId\":1,\"mode\":\"Attack\",\"action\":\"Move\",\"reason\":\"open\"}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":8,\"actor\":\"e1\",\"actorId\":1,\"mode\":\"Guard\",\"action\":\"Move\",\"reason\":\"hold\"}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":9,\"actor\":\"e1\",\"actorId\":1,\"mode\":\"Attack\",\"action\":\"Move\",\"reason\":\"open\"}\n");
			WriteFile("game-rules.json", MinimalRules(("e1", 100, "infantry"), ("e2", 200, "infantry")));

			var evidence = LoadEvidence();
			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, 100);
			var dead = units.Single(u => u.ActorId == 1);
			var survivor = units.Single(u => u.ActorId == 2);
			var enemyVictim = units.Single(u => u.ActorId == 301);

			Assert.That(dead.LifetimeSeconds, Is.EqualTo(20));
			Assert.That(survivor.DiedSeconds, Is.Null);
			Assert.That(survivor.LifetimeSeconds, Is.EqualTo(94));
			Assert.That(dead.DamageTaken, Is.EqualTo(18));
			Assert.That(dead.ModesUsed, Is.EqualTo("Attack|Guard"));
			Assert.That(enemyVictim.BornSeconds, Is.Null);
		}
	}
}
