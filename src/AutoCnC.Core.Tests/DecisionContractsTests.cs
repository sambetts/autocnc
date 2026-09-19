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
		public void SdkActionsAppendEnumValuesAndReuseTheHistoricalDecisionShape()
		{
			var repair = UnitDecision.RepairBuilding(
				17, "repair damaged refinery", "economy.repair.refinery");
			var cancel = UnitDecision.CancelProduction(
				"Vehicle", "mtnk", 2, "change the queue", "production.cancel-armour");
			var support = UnitDecision.ActivateSupportPower(
				"IonCannonPowerInfoOrder", 12, 34, "fire ion cannon", "support.ion-cannon");

			Assert.Multiple(() =>
			{
				Assert.That((byte)UnitAction.Harvest, Is.EqualTo(11));
				Assert.That((byte)UnitAction.RepairBuilding, Is.EqualTo(12));
				Assert.That((byte)UnitAction.CancelProduction, Is.EqualTo(13));
				Assert.That((byte)UnitAction.ActivateSupportPower, Is.EqualTo(14));

				Assert.That(repair.TargetActorId, Is.EqualTo(17u));
				Assert.That(repair.ReasonId, Is.EqualTo("economy.repair.refinery"));

				Assert.That(cancel.Queue, Is.EqualTo("Vehicle"));
				Assert.That(cancel.ItemName, Is.EqualTo("mtnk"));
				Assert.That(cancel.Count, Is.EqualTo(2));
				Assert.That(cancel.TargetX, Is.EqualTo(2),
					"the additive factory reuses the historical positional payload");
				Assert.That(cancel.ReasonId, Is.EqualTo("production.cancel-armour"));

				Assert.That(support.Power, Is.EqualTo("IonCannonPowerInfoOrder"));
				Assert.That(support.ItemName, Is.EqualTo("IonCannonPowerInfoOrder"));
				Assert.That(support.TargetX, Is.EqualTo(12));
				Assert.That(support.TargetY, Is.EqualTo(34));
				Assert.That(support.ReasonId, Is.EqualTo("support.ion-cannon"));
			});
		}

		[Test]
		public void NewActionIntentIncludesExactCountPowerAndTarget()
		{
			var cancelOne = UnitDecision.CancelProduction("Vehicle", "mtnk", 1, "cancel");
			var cancelTwo = UnitDecision.CancelProduction("Vehicle", "mtnk", 2, "cancel");
			var firstPower = UnitDecision.ActivateSupportPower("power-a", 4, 5, "fire");
			var otherPower = UnitDecision.ActivateSupportPower("power-b", 4, 5, "fire");
			var otherCell = UnitDecision.ActivateSupportPower("power-a", 5, 5, "fire");

			Assert.Multiple(() =>
			{
				Assert.That(cancelOne.SameIntent(cancelTwo), Is.False);
				Assert.That(firstPower.SameIntent(otherPower), Is.False);
				Assert.That(firstPower.SameIntent(otherCell), Is.False);
			});
		}

		[Test]
		public void ProductionQueueStateKeepsItsHistoricalPositionalShape()
		{
			var legacy = new ProductionQueueState("Vehicle", false, new[] { "mtnk" });
			var (queue, isIdle, buildable) = legacy;
			var enriched = legacy with
			{
				CurrentItem = "mtnk",
				CurrentProgressPercent = 40,
				CurrentCost = 800,
				CurrentRemainingCost = 480,
				CurrentItemCount = 2,
				QueuedCount = 3
			};

			Assert.Multiple(() =>
			{
				Assert.That(queue, Is.EqualTo("Vehicle"));
				Assert.That(isIdle, Is.False);
				Assert.That(buildable, Is.EqualTo(new[] { "mtnk" }));
				Assert.That(legacy.CurrentItem, Is.Null);
				Assert.That(legacy.QueuedCount, Is.Zero);
				Assert.That(enriched.CurrentItem, Is.EqualTo("mtnk"));
				Assert.That(enriched.CurrentProgressPercent, Is.EqualTo(40));
				Assert.That(enriched.CurrentCost, Is.EqualTo(800));
				Assert.That(enriched.CurrentRemainingCost, Is.EqualTo(480));
				Assert.That(enriched.CurrentItemCount, Is.EqualTo(2));
				Assert.That(enriched.QueuedCount, Is.EqualTo(3));
			});
		}

		[Test]
		public void OwnedBuildingAndSupportPowerStatesAreEngineFreeSnapshots()
		{
			var building = new OwnedBuildingState(
				21, "proc", 7, 8, 65, true, true, false);
			var power = new SupportPowerState(
				"IonCannonPowerInfoOrder", "IonCannonPowerInfoOrder",
				true, true, false, 0, 4500);

			Assert.Multiple(() =>
			{
				Assert.That(building.ActorId, Is.EqualTo(21u));
				Assert.That(building.HealthPercent, Is.EqualTo(65));
				Assert.That(building.IsRepairable, Is.True);
				Assert.That(building.RepairRequested, Is.True);
				Assert.That(building.RepairActive, Is.False);
				Assert.That(power.Key, Is.EqualTo("IonCannonPowerInfoOrder"));
				Assert.That(power.OrderName, Is.EqualTo("IonCannonPowerInfoOrder"));
				Assert.That(power.Active, Is.True);
				Assert.That(power.Ready, Is.True);
				Assert.That(power.Disabled, Is.False);
				Assert.That(power.RemainingTicks, Is.Zero);
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
		public void UrgentDoctrineDecisionOnlyBypassesDwellForImmediateDefencePressure()
		{
			var ordinaryDefence = DoctrineDecision.SwitchTo(
				"Defence", "visible pressure", "doctrine.defence");
			var urgentDefence = DoctrineDecision.SwitchUrgentlyTo(
				"Defence", "base breached", "doctrine.defence.base-breached");
			var urgentAttack = DoctrineDecision.SwitchUrgentlyTo(
				"Attack", "counter-attack now", "doctrine.attack.urgent");
			var safe = State();
			var recentBuildingLoss = State(buildingsLost: 1);
			var attacked = State(enemiesNearBase: 1);

			Assert.Multiple(() =>
			{
				Assert.That(ordinaryDefence.CanBypassMinimumDwell(attacked), Is.False,
					"a doctrine name alone must not imply urgency");
				Assert.That(urgentDefence.CanBypassMinimumDwell(safe), Is.False,
					"an urgent decision does not bypass dwell without immediate pressure");
				Assert.That(urgentDefence.CanBypassMinimumDwell(recentBuildingLoss), Is.False,
					"a rolling building loss is not immediate pressure");
				Assert.That(urgentAttack.CanBypassMinimumDwell(attacked), Is.False,
					"the urgent bypass is restricted to Defence");
				Assert.That(urgentDefence.CanBypassMinimumDwell(attacked), Is.True);
				Assert.That(urgentDefence.ReasonId, Is.EqualTo("doctrine.defence.base-breached"));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(ordinaryDefence, attacked, 30),
					Is.EqualTo(DoctrineDwellResult.MinimumDwell));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(urgentDefence, safe, 30),
					Is.EqualTo(DoctrineDwellResult.UrgentWithoutImmediateDefencePressure));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(urgentDefence, recentBuildingLoss, 30),
					Is.EqualTo(DoctrineDwellResult.UrgentWithoutImmediateDefencePressure));
				Assert.That(DoctrineTransitionPolicy.CheckDwell(urgentAttack, attacked, 30),
					Is.EqualTo(DoctrineDwellResult.UrgentWithoutImmediateDefencePressure));
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
				HasValueTradeData = true,
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
				Assert.That(state.HasValueTradeData, Is.True);
				Assert.That(state.IncomeEarned, Is.EqualTo(1200));
				Assert.That(state.VisibleEnemyValue, Is.EqualTo(700));
				Assert.That(state.EnemyValueNearBase, Is.EqualTo(500));
				Assert.That(state.OwnArmyValueNearBase, Is.EqualTo(800));
				Assert.That(state.VisibleEnemyMix.Sum(v => v.Count), Is.EqualTo(3));
				Assert.That(State().VisibleEnemyMix, Is.Empty,
					"the legacy positional constructor initializes the additive collection");
				Assert.That(State().HasValueTradeData, Is.False);
			});
		}

		[Test]
		public void WinningRequiresSafetyAndAFavourableValueTrade()
		{
			var legacyWinning = State(unitsLost: 1, unitsKilled: 2);
			var noObservedValueLead = legacyWinning with { HasValueTradeData = true };
			var losingValueTrade = legacyWinning with
			{
				CreditsKilled = 100,
				CreditsLost = 500
			};
			var baseUnderAttack = legacyWinning with { EnemiesNearBase = 1 };

			Assert.Multiple(() =>
			{
				Assert.That(legacyWinning.Winning, Is.True,
					"legacy states without value data retain the unit-count fallback");
				Assert.That(noObservedValueLead.Winning, Is.False,
					"runtime states do not fall back to omniscient kill counts");
				Assert.That(losingValueTrade.Winning, Is.False);
				Assert.That(baseUnderAttack.Winning, Is.False);
			});
		}

		[Test]
		public void BattleStateEqualityUsesVisibleEnemyMixValuesInsteadOfListIdentity()
		{
			var first = State() with
			{
				VisibleEnemyMix =
				[
					new ThreatValueSummary(ThreatKind.Infantry, 2, 200),
					new ThreatValueSummary(ThreatKind.Vehicle, 1, 500)
				]
			};
			var second = State() with
			{
				VisibleEnemyMix =
				[
					new ThreatValueSummary(ThreatKind.Vehicle, 1, 500),
					new ThreatValueSummary(ThreatKind.Infantry, 2, 200)
				]
			};
			var different = second with
			{
				VisibleEnemyMix =
				[
					new ThreatValueSummary(ThreatKind.Infantry, 2, 200),
					new ThreatValueSummary(ThreatKind.Vehicle, 1, 600)
				]
			};

			Assert.Multiple(() =>
			{
				Assert.That(first, Is.EqualTo(second));
				Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
				Assert.That(first, Is.Not.EqualTo(different));
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

		[Test]
		public void KillValueCountsOnlyEnemiesVisibleInTheLatestSampleAndKilledByUs()
		{
			var ledger = new ObservedKillValueLedger();
			ledger.BeginVisibilitySample();
			ledger.ObserveVisibleEnemy(1);
			ledger.ObserveVisibleEnemy(2);
			var unseenValueRead = false;

			Assert.Multiple(() =>
			{
				Assert.That(ledger.ObserveKill(3, killedBySelf: true, valueFactory: () =>
				{
					unseenValueRead = true;
					return 900;
				}), Is.False,
					"an unseen kill must not leak value");
				Assert.That(unseenValueRead, Is.False,
					"the hidden actor's value must not even be read");
				Assert.That(ledger.ObserveKill(1, killedBySelf: false, valueFactory: () => 500), Is.False,
					"somebody else's kill is not ours");
				Assert.That(ledger.ObserveKill(2, killedBySelf: true, valueFactory: () => 700), Is.True);
				Assert.That(ledger.ObserveKill(2, killedBySelf: true, valueFactory: () => 700), Is.False,
					"one observed death is counted once");
				Assert.That(ledger.TotalValue, Is.EqualTo(700));
			});

			ledger.ObserveVisibleEnemy(4);
			ledger.BeginVisibilitySample();
			Assert.That(ledger.ObserveKill(4, killedBySelf: true, valueFactory: () => 1000), Is.False,
				"visibility does not carry across samples");
		}

		[Test]
		public void VisibleOneShotKillBetweenScansIsConservativelyOmitted()
		{
			var ledger = new ObservedKillValueLedger();
			ledger.BeginVisibilitySample();
			var valueRead = false;
			var counted = ledger.ObserveKill(
				9, killedBySelf: true, valueFactory: () =>
			{
				valueRead = true;
				return 450;
			});

			Assert.Multiple(() =>
			{
				Assert.That(counted, Is.False);
				Assert.That(valueRead, Is.False);
				Assert.That(ledger.TotalValue, Is.Zero);
			});
		}

		[Test]
		public void StealthedLethalHitCannotUsePostDamageUncloakState()
		{
			var ledger = new ObservedKillValueLedger();
			ledger.BeginVisibilitySample();
			var cloakedValueRead = false;

			// The actor was absent from the pre-damage sample. Even if damage handlers uncloak it
			// before the kill notification, the ledger never accepts that post-damage state.
			var counted = ledger.ObserveKill(10, killedBySelf: true, valueFactory: () =>
			{
				cloakedValueRead = true;
				return 900;
			});

			Assert.Multiple(() =>
			{
				Assert.That(counted, Is.False);
				Assert.That(cloakedValueRead, Is.False);
				Assert.That(ledger.TotalValue, Is.Zero);
			});
		}
	}
}
