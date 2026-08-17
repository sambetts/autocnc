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

namespace AutoCnC.Modes.Core
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

			// 2. Engage, but never chase beyond the leash: the whole point of a defensive unit
			//    is that it cannot be baited away from what it is guarding.
			{
				var target = SelectTarget(state, tuning);
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
		public static ThreatSnapshot? SelectTarget(in DefensiveState state, in DefensiveTuning tuning)
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

				// Would engaging this drag us off our post? If so, ignore it entirely.
				var reachDistance = state.DistanceFromAnchorUnits + t.DistanceUnits;
				var withinWeaponRange = t.DistanceUnits <= state.WeaponRangeUnits;
				if (!withinWeaponRange && reachDistance > tuning.LeashRadiusUnits)
					continue;

				var score = ScoreThreat(t, state.WeaponRangeUnits);
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
		static int ScoreThreat(in ThreatSnapshot t, int weaponRangeUnits)
		{
			var score = 0;

			// Things actively shooting at us are the priority; they are the reason we exist.
			if (t.CanHitUs)
				score += 10_000;

			// Free shots are strictly better than shots we must reposition for.
			if (t.DistanceUnits <= weaponRangeUnits)
				score += 5_000;

			score += t.Kind switch
			{
				ThreatKind.Defence => 1_500,
				ThreatKind.Vehicle => 1_200,
				ThreatKind.Infantry => 1_000,
				ThreatKind.Aircraft => 800,
				ThreatKind.Economy => 600,
				ThreatKind.Structure => 200,
				_ => 0,
			};

			// Finish wounded targets first: removes enemy DPS from the field fastest.
			score += 100 - Clamp(t.HealthPercent, 0, 100);

			// Closer is better. Scaled so proximity never outweighs the class weights above.
			score += (32 * 1024 - Clamp(t.DistanceUnits, 0, 32 * 1024)) / 1024;

			return score;
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
