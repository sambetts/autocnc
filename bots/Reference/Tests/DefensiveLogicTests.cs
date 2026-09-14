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
	/// <summary>
	/// Guards the rule that ended the fifth badland-ridges: no ground unit in the ruleset can catch
	/// an aircraft, so an aircraft outside weapon range is not a target at all.
	/// </summary>
	[TestFixture]
	public class DefensiveLogicTests
	{
		// An e3 rocket soldier: six cells of reach, an 18-cell leash from DefensiveMode's range * 3.
		const int RocketRangeUnits = 6 * 1024;

		static DefensiveTuning Tuning => DefensiveTuning.Default with
		{
			TetherRadiusUnits = RocketRangeUnits * 2,
			LeashRadiusUnits = RocketRangeUnits * 3,
		};

		static DefensiveState OnPost(params ThreatSnapshot[] threats) => new(
			HealthPercent: 100,
			DistanceFromAnchorUnits: 0,
			WeaponRangeUnits: RocketRangeUnits,
			IsIdle: true,
			HasWeapon: true,
			CanMove: true,
			RepairAvailable: false,
			Threats: new List<ThreatSnapshot>(threats));

		static ThreatSnapshot Threat(uint id, int distanceUnits, ThreatKind kind, bool canHitUs = false)
			=> new(id, distanceUnits, 100, kind, true, canHitUs);

		[Test]
		public void AnAircraftBeyondReachIsNotATarget()
		{
			// 544s: twenty-one rocket soldiers were each ordered to attack actor 482 at 8,602 to
			// 12,440 units, against a weapon that reaches 6,144. Every order was an order to walk,
			// and fifteen of them walked into tiberium and died.
			var state = OnPost(Threat(482, 8_602, ThreatKind.Aircraft, canHitUs: true));

			Assert.That(DefensiveLogic.SelectTarget(state, Tuning), Is.Null,
				"an orca at 4.541 cells per second cannot be caught by an e3 at 0.952");
		}

		[Test]
		public void AGroundTargetAtTheSameDistanceIsStillEngaged()
		{
			// This must stay a statement about catchability, not a quiet shrinking of the leash.
			var state = OnPost(Threat(482, 8_602, ThreatKind.Vehicle, canHitUs: true));

			var target = DefensiveLogic.SelectTarget(state, Tuning);

			Assert.That(target, Is.Not.Null);
			Assert.That(target.Value.ActorId, Is.EqualTo(482u));
		}

		[Test]
		public void AnAircraftInsideReachOutranksEverythingElse()
		{
			// The shot is scarce — almost nothing this side builds can shoot upwards — and
			// perishable, because the target crosses the envelope while a tank stays put.
			var state = OnPost(
				Threat(1, 4_000, ThreatKind.Aircraft),
				Threat(2, 4_000, ThreatKind.Vehicle),
				Threat(3, 4_000, ThreatKind.Infantry));

			var target = DefensiveLogic.SelectTarget(state, Tuning);

			Assert.That(target, Is.Not.Null);
			Assert.That(target.Value.ActorId, Is.EqualTo(1u));
		}

		[Test]
		public void ATargetBeyondTheLeashIsNotChased()
		{
			var state = OnPost(Threat(9, 20 * 1024, ThreatKind.Vehicle)) with { DistanceFromAnchorUnits = 2 * 1024 };

			Assert.That(DefensiveLogic.SelectTarget(state, Tuning), Is.Null);
		}

		[Test]
		public void AHarvesterIsNeverDraggedBackToAnAnchor()
		{
			// Without this a "/mode all DefensiveMode" pulls every harvester off tiberium.
			var state = OnPost() with { HasWeapon = false, DistanceFromAnchorUnits = 30 * 1024 };

			Assert.That(DefensiveLogic.Decide(state, Tuning).Action, Is.EqualTo(UnitAction.Continue));
		}
	}
}
