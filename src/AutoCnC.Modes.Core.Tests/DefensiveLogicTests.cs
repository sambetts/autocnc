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
using AutoCnC.Modes.Core;
using NUnit.Framework;

namespace AutoCnC.Modes.Core.Tests
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
	}
}
