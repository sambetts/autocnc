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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="DefensiveLogic"/>. All distances in world units (1024 == 1 cell).</summary>
	public readonly record struct DefensiveTuning(
		int RetreatBelowHealthPercent,
		int ResumeAboveHealthPercent,
		int TetherRadiusUnits,
		int LeashRadiusUnits)
	{
		public static DefensiveTuning Default { get; } = new(
			RetreatBelowHealthPercent: 30,
			ResumeAboveHealthPercent: 80,
			TetherRadiusUnits: 5 * 1024,
			LeashRadiusUnits: 8 * 1024);
	}

	/// <summary>
	/// Pure decision logic for a unit guarding a position.
	/// </summary>
	/// <remarks>
	/// This type has ZERO OpenRA dependencies by design — the project is structured so that the
	/// compiler enforces it. Everything here is integer-only and side-effect free, so it is both
	/// lockstep-safe and testable without booting the engine.
	/// </remarks>
	public static class DefensiveLogic
	{
		public static UnitDecision Decide(in DefensiveState state, in DefensiveTuning tuning)
			=> Decide(state, tuning, WeaponRole.Unknown);

		public static UnitDecision Decide(in DefensiveState state, in DefensiveTuning tuning, WeaponRole role)
		{
			// 0. A unit with no weapon cannot defend anything, so this mode has nothing useful to
			//    say about it. Bail out rather than interfering: without this, step 3 below drags
			//    harvesters off tiberium and back to their anchor, killing the economy the moment
			//    a player runs "/mode all DefensiveMode".
			if (!state.HasWeapon)
				return UnitDecision.Continue;

			// 1. Self-preservation outranks everything. A dead unit guards nothing.
			if (state.RepairAvailable && state.HealthPercent <= tuning.RetreatBelowHealthPercent)
				return UnitDecision.Retreat($"health {state.HealthPercent}% <= {tuning.RetreatBelowHealthPercent}%");

			// 2. Engage, but never chase beyond the leash, and never chase anything that cannot
			//    be caught: the whole point of a defensive unit is that it cannot be baited
			//    away from what it is guarding.
			{
				var target = SelectTarget(state, tuning, role);
				if (target.HasValue)
					return UnitDecision.Attack(target.Value.ActorId, $"engaging {target.Value.Kind} at {target.Value.DistanceUnits}u");
			}

			// 3. Nothing to shoot: get back on post if we have drifted.
			if (state.CanMove && state.DistanceFromAnchorUnits > tuning.TetherRadiusUnits)
				return UnitDecision.ReturnToAnchor($"drifted {state.DistanceFromAnchorUnits}u > tether {tuning.TetherRadiusUnits}u");

			// 4. On post, no threats. Only assert Hold when genuinely idle, so we don't
			//    stomp an activity that is still legitimately running.
			return state.IsIdle ? UnitDecision.Hold("on post, no threats") : UnitDecision.Continue;
		}

		/// <summary>
		/// Picks the best threat to engage, or null if none is worth engaging.
		/// </summary>
		/// <remarks>
		/// Two things are filtered out rather than merely deprioritised, because both cost
		/// movement that can never end in a shot: a target whose engagement would carry us past
		/// the leash, and any aircraft not already inside weapon range.
		/// </remarks>
		public static ThreatSnapshot? SelectTarget(in DefensiveState state, in DefensiveTuning tuning)
			=> SelectTarget(state, tuning, WeaponRole.Unknown);

		/// <inheritdoc cref="SelectTarget(in DefensiveState, in DefensiveTuning)"/>
		/// <remarks>
		/// The <paramref name="role"/> overload lets a unit prefer what its own warhead can
		/// actually hurt. It only ever reorders candidates that survived the filters above;
		/// nothing becomes engageable because of it, and nothing stops being engageable either.
		/// </remarks>
		public static ThreatSnapshot? SelectTarget(in DefensiveState state, in DefensiveTuning tuning, WeaponRole role)
		{
			var threats = state.Threats;
			if (threats == null || threats.Count == 0)
				return null;

			ThreatSnapshot? best = null;
			var bestScore = int.MinValue;

			for (var i = 0; i < threats.Count; i++)
			{
				var t = threats[i];
				if (!t.IsAttackable)
					continue;

				var withinWeaponRange = t.DistanceUnits <= state.WeaponRangeUnits;

				// Movement only pays if the target can be caught, and an aircraft cannot.
				// Every aircraft in the ruleset outruns every ground unit this side fields,
				// so a step taken toward one ends with the aircraft somewhere else and us
				// standing on whatever ground it happened to be over. Shoot the ones that
				// come to us — our reach is the longer of the two — and ignore the rest.
				if (!withinWeaponRange && t.Kind == ThreatKind.Aircraft)
					continue;

				// Would engaging this drag us off our post? If so, ignore it entirely.
				var reachDistance = state.DistanceFromAnchorUnits + t.DistanceUnits;
				if (!withinWeaponRange && reachDistance > tuning.LeashRadiusUnits)
					continue;

				var score = ScoreThreat(t, state.WeaponRangeUnits, role);
				if (score > bestScore || (score == bestScore && best.HasValue && t.ActorId < best.Value.ActorId))
				{
					bestScore = score;
					best = t;
				}
			}

			return best;
		}

		/// <summary>
		/// Higher is more urgent. Integer-only so the ordering is bit-identical on every client.
		/// </summary>
		static int ScoreThreat(in ThreatSnapshot t, int weaponRangeUnits, WeaponRole role)
		{
			var score = 0;

			// Things actively shooting at us are the priority; they are the reason we exist.
			if (t.CanHitUs)
				score += 10_000;

			// Free shots are strictly better than shots we must reposition for.
			if (t.DistanceUnits <= weaponRangeUnits)
				score += 5_000;

			// How dangerous the class is. Unchanged, and deliberately so: this is a statement
			// about the enemy, and it is true whoever is doing the looking.
			score += t.Kind switch
			{
				// An aircraft that is actually in range outranks everything, because the shot
				// is both scarce and perishable: almost nothing on this side can shoot upwards
				// at all, and the target is crossing our envelope several times faster than we
				// can turn to follow it. A tank still in range next evaluation is a shot kept;
				// an aircraft is not. SelectTarget has already discarded the unreachable ones,
				// so this weight only ever applies to a shot we can take right now.
				ThreatKind.Aircraft => 2_000,
				ThreatKind.Defence => 1_500,
				ThreatKind.Vehicle => 1_200,
				ThreatKind.Infantry => 1_000,
				ThreatKind.Economy => 600,
				ThreatKind.Structure => 200,
				_ => 0,
			};

			// ...and how much of that danger this particular weapon can do anything about. This
			// is the term the table above cannot carry, because it is a statement about us.
			//
			// Weighted above the class spread on purpose. An e3 rocket does 319 damage a second
			// to infantry and 1,593 to a vehicle; preferring the vehicle by 200 points, as the
			// class table alone did, is not a preference proportional to a five-fold difference
			// in outcome. It sits below the in-range bonus just as deliberately, so a better
			// matchup is never a reason to give up a shot we already have and walk.
			score += WeaponMatchLogic.MatchBonus(role, t.Kind);

			// Finish wounded targets first: removes enemy DPS from the field fastest.
			score += 100 - Clamp(t.HealthPercent, 0, 100);

			// Closer is better. Scaled so proximity never outweighs the class weights above.
			score += (32 * 1024 - Clamp(t.DistanceUnits, 0, 32 * 1024)) / 1024;

			return score;
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
