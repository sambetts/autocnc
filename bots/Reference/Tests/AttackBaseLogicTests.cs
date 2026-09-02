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

using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	[TestFixture]
	public class AttackBaseLogicTests
	{
		const int Cell = 1024;

		static ThreatSnapshot Threat(
			uint id = 1,
			int distanceCells = 2,
			int healthPercent = 100,
			ThreatKind kind = ThreatKind.Infantry,
			bool attackable = true,
			bool canHitUs = true)
			=> new(id, distanceCells * Cell, healthPercent, kind, attackable, canHitUs);

		static AssaultState State(
			int healthPercent = 100,
			bool hasObjective = true,
			uint objectiveId = 99,
			int distanceToObjectiveCells = 20,
			int weaponRangeCells = 4,
			params ThreatSnapshot[] threats)
			=> new(
				HealthPercent: healthPercent,
				IsIdle: true,
				HasWeapon: true,
				CanMove: true,
				HasObjective: hasObjective,
				ObjectiveActorId: objectiveId,
				DistanceToObjectiveUnits: distanceToObjectiveCells * Cell,
				WeaponRangeUnits: weaponRangeCells * Cell,
				Threats: threats);

		[Test]
		public void AdvancesOnObjectiveWhenOutOfRange()
		{
			var decision = AttackBaseLogic.Decide(State(), AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.AdvanceToObjective));
			Assert.That(decision.TargetActorId, Is.EqualTo(99u));
		}

		[Test]
		public void AttacksObjectiveOnceInRange()
		{
			var decision = AttackBaseLogic.Decide(
				State(distanceToObjectiveCells: 3, weaponRangeCells: 4),
				AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(99u));
		}

		[Test]
		public void IgnoresDistractionsOutsideWeaponRange()
		{
			// The defining behaviour: a tempting target 10 cells away must NOT divert the push.
			var decision = AttackBaseLogic.Decide(
				State(weaponRangeCells: 4, threats: Threat(id: 5, distanceCells: 10)),
				AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.AdvanceToObjective),
				"must never chase a target outside weapon range");
		}

		[Test]
		public void TakesFreeShotsAtThingsAlreadyInRange()
		{
			var decision = AttackBaseLogic.Decide(
				State(weaponRangeCells: 4, threats: Threat(id: 5, distanceCells: 2, canHitUs: true)),
				AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(5u));
		}

		[Test]
		public void PrioritisesStaticDefencesOverOtherBlockers()
		{
			var infantry = Threat(id: 1, distanceCells: 1, kind: ThreatKind.Infantry, canHitUs: true);
			var pillbox = Threat(id: 2, distanceCells: 3, kind: ThreatKind.Defence, canHitUs: true);

			var decision = AttackBaseLogic.Decide(
				State(weaponRangeCells: 4, threats: [infantry, pillbox]),
				AssaultTuning.Default);

			Assert.That(decision.TargetActorId, Is.EqualTo(2u), "static defences are the real obstacle");
		}

		[Test]
		public void IgnoresHarmlessUnitsInRangeThatCannotHitUs()
		{
			// A passing harvester is not worth interrupting the advance for.
			var decision = AttackBaseLogic.Decide(
				State(weaponRangeCells: 4, threats: Threat(id: 5, distanceCells: 2, kind: ThreatKind.Economy, canHitUs: false)),
				AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.AdvanceToObjective));
		}

		[Test]
		public void DoesNotRetreatByDefault()
		{
			var decision = AttackBaseLogic.Decide(State(healthPercent: 5), AssaultTuning.Default);

			Assert.That(decision.Action, Is.Not.EqualTo(UnitAction.Retreat),
				"an assault that retreats is not an assault");
		}

		[Test]
		public void RetreatsWhenExplicitlyConfiguredTo()
		{
			var cautious = AssaultTuning.Default with { RetreatBelowHealthPercent = 25 };
			var decision = AttackBaseLogic.Decide(State(healthPercent: 20), cautious);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Retreat));
		}

		[Test]
		public void HoldsWhenNoObjectiveAssignedAndNowhereToGo()
		{
			// Nothing in sight and this side has never seen an enemy structure: there is genuinely
			// nowhere to march.
			var decision = AttackBaseLogic.Decide(State(hasObjective: false), AssaultTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold));
		}

		// --- Getting there at all ----------------------------------------------
		//
		// Sensing is centred on the unit and shows only what is visible now, so an army that
		// starts a push from its own base sees no objective whatsoever. Standing still is then
		// the difference between an attack doctrine and no attack doctrine.

		[Test]
		public void MarchesOnTheLastKnownBaseWhenNothingIsInSight()
		{
			var decision = AttackBaseLogic.Decide(
				State(hasObjective: false),
				AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.AttackMoveTo),
				"a push with nothing in sight must close the distance, not hold");
			Assert.That(decision.TargetX, Is.EqualTo(78));
			Assert.That(decision.TargetY, Is.EqualTo(12));
		}

		[Test]
		public void StillHoldsWhenItCannotMove()
		{
			var immobile = State(hasObjective: false) with { CanMove = false };

			var decision = AttackBaseLogic.Decide(immobile, AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold));
		}

		[Test]
		public void AVisibleObjectiveAlwaysBeatsAMarch()
		{
			// Once something is actually in view the remembered location stops mattering.
			var decision = AttackBaseLogic.Decide(
				State(distanceToObjectiveCells: 20, weaponRangeCells: 4),
				AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.AdvanceToObjective));
			Assert.That(decision.TargetActorId, Is.EqualTo(99u));
		}

		[Test]
		public void AnUnarmedUnitIsNotMarchedAcrossTheMap()
		{
			var unarmed = State(hasObjective: false) with { HasWeapon = false };

			var decision = AttackBaseLogic.Decide(unarmed, AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Continue));
		}

		[Test]
		public void MarchingSaysHowFarItHasToGo()
		{
			var decision = AttackBaseLogic.Decide(
				State(hasObjective: false),
				AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));

			Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty);
			Assert.That(decision.Reason, Does.Contain((97 * Cell).ToString()));
		}

		[Test]
		public void TheMarchIsOneOrderNotAStreamOfThem()
		{
			// The reason carries a distance that changes every step. Intent must not, or the
			// executor re-issues the move order each evaluation and the push stutters instead of
			// walking.
			var far = AttackBaseLogic.Decide(State(hasObjective: false), AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 97 * Cell));
			var closer = AttackBaseLogic.Decide(State(hasObjective: false), AssaultTuning.Default,
				new ApproachOrders(true, 78, 12, 40 * Cell));

			Assert.That(far.SameIntent(closer), Is.True);
			Assert.That(far.Reason, Is.Not.EqualTo(closer.Reason), "but it should still say how far");
		}

		[Test]
		public void TheTwoArgumentOverloadStillMeansNowhereToGo()
		{
			var withoutOrders = AttackBaseLogic.Decide(State(hasObjective: false), AssaultTuning.Default);
			var withNone = AttackBaseLogic.Decide(State(hasObjective: false), AssaultTuning.Default, ApproachOrders.None);

			Assert.That(withNone, Is.EqualTo(withoutOrders));
			Assert.That(ApproachOrders.None.HasTarget, Is.False);
		}

		[Test]
		public void ObjectiveSelectionPrefersProductionOverDefences()
		{
			var turret = new ThreatSnapshot(1, 5 * Cell, 100, ThreatKind.Defence, true, true);
			var factory = new ThreatSnapshot(2, 12 * Cell, 100, ThreatKind.Structure, true, false);

			var chosen = AttackBaseLogic.SelectObjective([turret, factory]);

			Assert.That(chosen, Is.EqualTo(2u), "degrade the enemy's ability to respond first");
		}

		[Test]
		public void ObjectiveSelectionIsDeterministic()
		{
			var a = new ThreatSnapshot(8, 10 * Cell, 100, ThreatKind.Structure, true, false);
			var b = new ThreatSnapshot(4, 10 * Cell, 100, ThreatKind.Structure, true, false);

			Assert.That(AttackBaseLogic.SelectObjective([a, b]), Is.EqualTo(AttackBaseLogic.SelectObjective([b, a])));
			Assert.That(AttackBaseLogic.SelectObjective([a, b]), Is.EqualTo(4u));
		}

		[Test]
		public void ObjectiveSelectionIgnoresMobileUnits()
		{
			var tank = new ThreatSnapshot(1, 2 * Cell, 100, ThreatKind.Vehicle, true, true);

			Assert.That(AttackBaseLogic.SelectObjective([tank]), Is.Null);
			Assert.That(AttackBaseLogic.SelectObjective([]), Is.Null);
			Assert.That(AttackBaseLogic.SelectObjective(null), Is.Null);
		}
	}
}
