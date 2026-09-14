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

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	[TestFixture]
	public class DefensiveLogicTests
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

		static DefensiveState State(
			int healthPercent = 100,
			int distanceFromAnchorCells = 0,
			int weaponRangeCells = 4,
			bool repairAvailable = true,
			params ThreatSnapshot[] threats)
			=> new(
				HealthPercent: healthPercent,
				DistanceFromAnchorUnits: distanceFromAnchorCells * Cell,
				WeaponRangeUnits: weaponRangeCells * Cell,
				IsIdle: true,
				HasWeapon: true,
				CanMove: true,
				RepairAvailable: repairAvailable,
				Threats: threats);

		[Test]
		public void RetreatsWhenHealthDropsBelowThreshold()
		{
			var decision = DefensiveLogic.Decide(
				State(healthPercent: 20, threats: Threat()),
				DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Retreat));
		}

		[Test]
		public void FightsOnWhenHurtButNoRepairAvailable()
		{
			// Retreating to a repair bay that does not exist would just be a suicide walk.
			var decision = DefensiveLogic.Decide(
				State(healthPercent: 10, repairAvailable: false, threats: Threat()),
				DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
		}

		[Test]
		public void EngagesThreatInRange()
		{
			var decision = DefensiveLogic.Decide(
				State(threats: Threat(id: 42, distanceCells: 3)),
				DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(42u));
		}

		[Test]
		public void RefusesToBeBaitedBeyondTheLeash()
		{
			// Sitting on the anchor, a target 30 cells away is far outside both weapon range
			// and the leash. A defensive unit must ignore it rather than chase.
			var tuning = DefensiveTuning.Default;
			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 4, threats: Threat(distanceCells: 30)),
				tuning);

			Assert.That(decision.Action, Is.Not.EqualTo(UnitAction.Attack));
		}

		[Test]
		public void ReturnsToAnchorWhenDriftedAndNoThreats()
		{
			var decision = DefensiveLogic.Decide(
				State(distanceFromAnchorCells: 12),
				DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.ReturnToAnchor));
		}

		[Test]
		public void HoldsWhenOnPostWithNothingToDo()
		{
			var decision = DefensiveLogic.Decide(State(), DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold));
		}

		[Test]
		public void PrioritisesTheThreatThatIsShootingUs()
		{
			var harmless = Threat(id: 1, distanceCells: 1, kind: ThreatKind.Economy, canHitUs: false);
			var shooter = Threat(id: 2, distanceCells: 3, kind: ThreatKind.Vehicle, canHitUs: true);

			var decision = DefensiveLogic.Decide(State(threats: [harmless, shooter]), DefensiveTuning.Default);

			Assert.That(decision.TargetActorId, Is.EqualTo(2u), "should engage the unit that can actually hurt us");
		}

		[Test]
		public void IgnoresUnattackableThreats()
		{
			// e.g. an aircraft when we only have a ground-only weapon.
			var decision = DefensiveLogic.Decide(
				State(threats: Threat(kind: ThreatKind.Aircraft, attackable: false)),
				DefensiveTuning.Default);

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold));

			// Positive control, so this keeps testing IsAttackable rather than quietly passing
			// because of the catchability rule: the identical threat, in range and attackable,
			// must be engaged.
			var attackable = DefensiveLogic.Decide(
				State(threats: Threat(kind: ThreatKind.Aircraft, attackable: true)),
				DefensiveTuning.Default);

			Assert.That(attackable.Action, Is.EqualTo(UnitAction.Attack));
		}

		[Test]
		public void TargetSelectionIsDeterministicForIdenticalThreats()
		{
			// Two identical threats must always resolve to the same one, or clients desync.
			var a = Threat(id: 7, distanceCells: 2);
			var b = Threat(id: 3, distanceCells: 2);

			var forward = DefensiveLogic.Decide(State(threats: [a, b]), DefensiveTuning.Default);
			var reversed = DefensiveLogic.Decide(State(threats: [b, a]), DefensiveTuning.Default);

			Assert.That(forward.TargetActorId, Is.EqualTo(reversed.TargetActorId),
				"tie-break must not depend on enumeration order");
			Assert.That(forward.TargetActorId, Is.EqualTo(3u), "lowest ActorID wins ties");
		}

		[Test]
		public void HandlesEmptyAndNullThreatLists()
		{
			Assert.That(DefensiveLogic.Decide(State(), DefensiveTuning.Default).Action, Is.EqualTo(UnitAction.Hold));

			var nullThreats = new DefensiveState(100, 0, 4096, true, true, true, false, null);
			Assert.That(DefensiveLogic.Decide(nullThreats, DefensiveTuning.Default).Action, Is.EqualTo(UnitAction.Hold));
		}

		[Test]
		public void FinishesWoundedTargetsFirst()
		{
			var healthy = Threat(id: 1, distanceCells: 2, healthPercent: 100);
			var wounded = Threat(id: 2, distanceCells: 2, healthPercent: 15);

			var decision = DefensiveLogic.Decide(State(threats: [healthy, wounded]), DefensiveTuning.Default);

			Assert.That(decision.TargetActorId, Is.EqualTo(2u));
		}

		// --- Aircraft cannot be caught -----------------------------------------------------
		//
		// DefensiveMode scales the leash off the unit's own reach (leash = 3x weapon range), so
		// a rocket soldier guarding a base carries an 18-cell leash around a 6-cell weapon. That
		// is the tuning these cases are written against, because it is the tuning that shipped.

		static DefensiveTuning ScaledTuning(int weaponRangeCells)
			=> DefensiveTuning.Default with
			{
				TetherRadiusUnits = weaponRangeCells * 2 * Cell,
				LeashRadiusUnits = weaponRangeCells * 3 * Cell
			};

		[Test]
		public void NeverWalksTowardAnAircraftEvenWellInsideTheLeash()
		{
			// An orca 9 cells away is inside a rocket soldier's 18-cell leash but outside its
			// 6-cell reach, and it moves 4.5 cells a game second against the soldier's 0.95.
			// Walking at it is movement that can never end in a shot.
			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: Threat(id: 9, distanceCells: 9, kind: ThreatKind.Aircraft)),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold),
				"a defender on its anchor must stay on it rather than chase an aircraft");
		}

		[TestCase(8602)]
		[TestCase(9113)]
		[TestCase(10432)]
		[TestCase(11951)]
		[TestCase(12440)]
		public void HoldsAgainstEveryAircraftDistanceThatEmptiedTheBase(int distanceUnits)
		{
			// These are the real ranges at which twenty-one rocket soldiers were each ordered
			// onto the same orca in a single evaluation. All of them are outside the 6-cell
			// rocket and inside the 18-cell leash, which is exactly the gap this rule closes.
			var threat = new ThreatSnapshot(1, distanceUnits, 100, ThreatKind.Aircraft, true, true);

			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: threat),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.Not.EqualTo(UnitAction.Attack));
		}

		[Test]
		public void StillWalksTowardAGroundTargetAtTheSameDistance()
		{
			// The rule is about catchability, not about distance: a ground unit at the exact
			// distance rejected above is still worth closing on, so this stays a preference
			// about targets rather than a quiet shrinking of the leash.
			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: Threat(id: 9, distanceCells: 9, kind: ThreatKind.Vehicle)),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(9u));
		}

		[Test]
		public void ShootsAnAircraftThatComesIntoRange()
		{
			// The graceful half: refusing to chase must not mean refusing to fire. A rocket
			// reaches 6 cells and an orca has to close to 4.75 to attack anything, so a
			// defender that simply stays put still gets the shot.
			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: Threat(id: 11, distanceCells: 5, kind: ThreatKind.Aircraft)),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(11u));
		}

		[Test]
		public void PrefersAnInRangeAircraftOverAnInRangeGroundTarget()
		{
			// Scarce and perishable: hardly anything on this side can shoot upwards, and the
			// aircraft will have left the envelope by the next evaluation while the tank
			// will not.
			var tank = Threat(id: 1, distanceCells: 3, kind: ThreatKind.Vehicle);
			var orca = Threat(id: 2, distanceCells: 5, kind: ThreatKind.Aircraft);

			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: [tank, orca]),
				ScaledTuning(6));

			Assert.That(decision.TargetActorId, Is.EqualTo(2u));
		}

		[Test]
		public void ComesHomeInsteadOfStandingInTheOpenUnderAnUncatchableAircraft()
		{
			// The failure this replaces: a unit dragged out by an aircraft stopped where the
			// aircraft had been and burned to death on tiberium. With nothing engageable left,
			// a drifted unit must fall back rather than hold wherever it stopped.
			var decision = DefensiveLogic.Decide(
				State(
					distanceFromAnchorCells: 14,
					weaponRangeCells: 6,
					threats: Threat(id: 9, distanceCells: 9, kind: ThreatKind.Aircraft)),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.ReturnToAnchor));
		}

		[Test]
		public void AnUncatchableAircraftDoesNotMaskAReachableGroundThreat()
		{
			// A strafing orca scores 10,000 for shooting at us, so before this rule it beat an
			// APC that was merely driving at the harvesters — and the defender walked away from
			// the target it could actually stop. Skipping the aircraft must leave the rest of
			// the list intact rather than abort the scan.
			var orca = Threat(id: 1, distanceCells: 7, kind: ThreatKind.Aircraft, canHitUs: true);
			var apc = Threat(id: 2, distanceCells: 9, kind: ThreatKind.Vehicle, canHitUs: false);

			var decision = DefensiveLogic.Decide(
				State(weaponRangeCells: 6, threats: [orca, apc]),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(2u));
		}

		[Test]
		public void AnAircraftShootingUsFromOutOfReachIsStillNotChased()
		{
			// CanHitUs is worth 10,000 points and the aircraft outranges nothing we own, so
			// without the catchability filter this is exactly the target that pulled the whole
			// army off its anchor. Being shot at is not a reason to start an unwinnable race.
			var decision = DefensiveLogic.Decide(
				State(
					weaponRangeCells: 6,
					threats: Threat(id: 4, distanceCells: 12, kind: ThreatKind.Aircraft, canHitUs: true)),
				ScaledTuning(6));

			Assert.That(decision.Action, Is.Not.EqualTo(UnitAction.Attack));
		}
	}
}
