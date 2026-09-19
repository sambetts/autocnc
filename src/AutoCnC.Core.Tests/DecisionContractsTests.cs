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
using AutoCnC.Core;
using NUnit.Framework;

namespace AutoCnC.Core.Tests
{
	[TestFixture]
	public sealed class DecisionContractsTests
	{
		[Test]
		public void UnitDecisionKeepsItsHistoricalPositionalShape()
		{
			var decision = new UnitDecision(UnitAction.Attack, 7, 1, 2, "item", "queue", "legacy reason");
			var (action, targetActorId, targetX, targetY, itemName, queue, reason) = decision;

			Assert.Multiple(() =>
			{
				Assert.That(action, Is.EqualTo(UnitAction.Attack));
				Assert.That(targetActorId, Is.EqualTo(7u));
				Assert.That(targetX, Is.EqualTo(1));
				Assert.That(targetY, Is.EqualTo(2));
				Assert.That(itemName, Is.EqualTo("item"));
				Assert.That(queue, Is.EqualTo("queue"));
				Assert.That(reason, Is.EqualTo("legacy reason"));
				Assert.That(decision.ReasonId, Is.Null);
			});
		}

		[Test]
		public void UnitDecisionFactoriesAddReasonIdWithoutChangingIntent()
		{
			var first = UnitDecision.Attack(7, "engaging armour", "combat.focus-armour");
			var second = UnitDecision.Attack(7, "different prose", "combat.focus-priority");

			Assert.Multiple(() =>
			{
				Assert.That(first.ReasonId, Is.EqualTo("combat.focus-armour"));
				Assert.That(first.SameIntent(second), Is.True);
			});
		}

		[Test]
		public void DoctrineDecisionKeepsItsHistoricalPositionalShape()
		{
			var decision = new DoctrineDecision("Defence", "legacy reason");
			var (doctrine, reason) = decision;

			Assert.Multiple(() =>
			{
				Assert.That(doctrine, Is.EqualTo("Defence"));
				Assert.That(reason, Is.EqualTo("legacy reason"));
				Assert.That(decision.ReasonId, Is.Null);
				Assert.That(decision.IsUrgent, Is.False);
			});
		}

		[Test]
		public void UrgentDoctrineDecisionOnlyBypassesDwellDuringBaseAttack()
		{
			var ordinaryDefence = DoctrineDecision.SwitchTo(
				"Defence", "visible pressure", "doctrine.defence");
			var urgentDefence = DoctrineDecision.SwitchUrgentlyTo(
				"Defence", "base breached", "doctrine.defence.base-breached");
			var safe = State();
			var attacked = State(enemiesNearBase: 1);

			Assert.Multiple(() =>
			{
				Assert.That(ordinaryDefence.CanBypassMinimumDwell(attacked), Is.False,
					"a doctrine name alone must not imply urgency");
				Assert.That(urgentDefence.CanBypassMinimumDwell(safe), Is.False,
					"an urgent decision does not bypass dwell without a base attack");
				Assert.That(urgentDefence.CanBypassMinimumDwell(attacked), Is.True);
				Assert.That(urgentDefence.ReasonId, Is.EqualTo("doctrine.defence.base-breached"));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(ordinaryDefence, attacked, 30),
					Is.EqualTo(DoctrineDwellResult.MinimumDwell));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(urgentDefence, safe, 30),
					Is.EqualTo(DoctrineDwellResult.UrgentWithoutBaseAttack));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(urgentDefence, attacked, 30),
					Is.EqualTo(DoctrineDwellResult.UrgentDwellBypass));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(ordinaryDefence,
					attacked with { DoctrineSeconds = 30 }, 30), Is.EqualTo(DoctrineDwellResult.Allowed));
			});
		}

		[Test]
		public void BattleStateKeepsPositionalConstructionAndAddsValueData()
		{
			var state = State() with
			{
				CreditsKilled = 900,
				CreditsLost = 400,
				IncomeEarned = 1200,
				VisibleEnemyValue = 700,
				EnemyValueNearBase = 500,
				OwnArmyValueNearBase = 800,
				VisibleEnemyMix =
				[
					new ThreatValueSummary(ThreatKind.Infantry, 2, 200),
					new ThreatValueSummary(ThreatKind.Vehicle, 1, 500)
				]
			};

			Assert.Multiple(() =>
			{
				Assert.That(state.CreditsKilled, Is.EqualTo(900));
				Assert.That(state.CreditsLost, Is.EqualTo(400));
				Assert.That(state.IncomeEarned, Is.EqualTo(1200));
				Assert.That(state.VisibleEnemyValue, Is.EqualTo(700));
				Assert.That(state.EnemyValueNearBase, Is.EqualTo(500));
				Assert.That(state.OwnArmyValueNearBase, Is.EqualTo(800));
				Assert.That(state.VisibleEnemyMix.Sum(v => v.Count), Is.EqualTo(3));
				Assert.That(State().VisibleEnemyMix, Is.Empty,
					"the legacy positional constructor initializes the additive collection");
			});
		}

		[Test]
		public void WinningRequiresSafetyAndAFavourableValueTrade()
		{
			var legacyWinning = State(unitsLost: 1, unitsKilled: 2);
			var losingValueTrade = legacyWinning with { CreditsKilled = 100, CreditsLost = 500 };
			var baseUnderAttack = legacyWinning with { EnemiesNearBase = 1 };

			Assert.Multiple(() =>
			{
				Assert.That(legacyWinning.Winning, Is.True,
					"legacy states without value data retain the unit-count fallback");
				Assert.That(losingValueTrade.Winning, Is.False);
				Assert.That(baseUnderAttack.Winning, Is.False);
			});
		}

		[Test]
		public void ThreatSnapshotKeepsItsHistoricalConstructorAndAddsMetadata()
		{
			var legacy = new ThreatSnapshot(7, 1024, 75, ThreatKind.Vehicle, true, false);
			var (actorId, distance, health, kind, attackable, canHitUs) = legacy;
			var enriched = legacy with
			{
				ActorType = "mtnk",
				CellX = 12,
				CellY = 34,
				Value = 800,
				WeaponRangeUnits = 4096
			};

			Assert.Multiple(() =>
			{
				Assert.That(actorId, Is.EqualTo(7u));
				Assert.That(distance, Is.EqualTo(1024));
				Assert.That(health, Is.EqualTo(75));
				Assert.That(kind, Is.EqualTo(ThreatKind.Vehicle));
				Assert.That(attackable, Is.True);
				Assert.That(canHitUs, Is.False);
				Assert.That(enriched.ActorType, Is.EqualTo("mtnk"));
				Assert.That(enriched.CellX, Is.EqualTo(12));
				Assert.That(enriched.CellY, Is.EqualTo(34));
				Assert.That(enriched.Value, Is.EqualTo(800));
				Assert.That(enriched.WeaponRangeUnits, Is.EqualTo(4096));
			});
		}

		static BattleState State(int unitsLost = 0, int buildingsLost = 0, int unitsKilled = 0,
			int enemiesNearBase = 0)
		{
			return new BattleState(
				1, "Opening", 5,
				1000, 10, 1, 1,
				3, 900, 2, 1200,
				60, unitsLost, buildingsLost, unitsKilled,
				enemiesNearBase, enemiesNearBase, enemiesNearBase > 0 ? 5 : -1, 0, false);
		}
	}

	[TestFixture]
	public sealed class BattleValueAccumulatorTests
	{
		[Test]
		public void CountsOnlyVisibleEnemiesAndAggregatesValueByThreatKind()
		{
			var values = new BattleValueAccumulator();
			values.ObserveEnemy(false, true, ThreatKind.Vehicle, 900);
			values.ObserveEnemy(true, false, ThreatKind.Infantry, 100);
			values.ObserveEnemy(true, true, ThreatKind.Vehicle, 500);
			values.ObserveEnemy(true, true, ThreatKind.Vehicle, -50);

			var mix = values.VisibleEnemyMix();

			Assert.Multiple(() =>
			{
				Assert.That(values.EnemiesInSight, Is.EqualTo(3));
				Assert.That(values.EnemiesNearBase, Is.EqualTo(2));
				Assert.That(values.VisibleEnemyValue, Is.EqualTo(600));
				Assert.That(values.EnemyValueNearBase, Is.EqualTo(500));
				Assert.That(mix.Select(v => v.Kind),
					Is.EqualTo(new[] { ThreatKind.Infantry, ThreatKind.Vehicle }));
				Assert.That(mix.Single(v => v.Kind == ThreatKind.Vehicle),
					Is.EqualTo(new ThreatValueSummary(ThreatKind.Vehicle, 2, 500)));
			});
		}

		[Test]
		public void RollingWindowUsesValueAndIncomeDeltasAndClampsCounterResets()
		{
			var previous = new BattleCumulativeTotals(10, 2, 1, 3, 500, 800, 1200);
			var current = new BattleCumulativeTotals(70, 5, 0, 7, 900, 1500, 2100);

			var delta = BattleWindowCalculator.Difference(current, previous);

			Assert.Multiple(() =>
			{
				Assert.That(delta.UnitsLost, Is.EqualTo(3));
				Assert.That(delta.BuildingsLost, Is.Zero);
				Assert.That(delta.UnitsKilled, Is.EqualTo(4));
				Assert.That(delta.CreditsLost, Is.EqualTo(400));
				Assert.That(delta.CreditsKilled, Is.EqualTo(700));
				Assert.That(delta.IncomeEarned, Is.EqualTo(900));
			});
		}
	}
}
