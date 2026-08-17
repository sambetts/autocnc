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
	/// <summary>Tunable knobs for <see cref="AttackBaseLogic"/>. All distances in world units (1024 == 1 cell).</summary>
	public readonly record struct AssaultTuning(
		int RetreatBelowHealthPercent,
		bool ReturnFireWhileAdvancing,
		bool ClearDefencesEnRoute)
	{
		public static AssaultTuning Default { get; } = new(
			RetreatBelowHealthPercent: 0,          // 0 == press the attack, never retreat
			ReturnFireWhileAdvancing: true,
			ClearDefencesEnRoute: true);
	}

	/// <summary>
	/// Pure decision logic for a unit pushing into an enemy base.
	/// </summary>
	/// <remarks>
	/// The defining behaviour is what this mode *refuses* to do: it never chases. A unit only
	/// ever shoots what is already inside its weapon range, so a lone scout cannot peel an
	/// assault force off its objective. This is the deliberate opposite of AutoTarget.
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class AttackBaseLogic
	{
		public static UnitDecision Decide(in AssaultState state, in AssaultTuning tuning)
		{
			// 0. An unarmed unit cannot assault anything. Leave it alone rather than marching it
			//    into the enemy base to die. See the equivalent guard in DefensiveLogic.
			if (!state.HasWeapon)
				return UnitDecision.Continue;

			// 1. Optional bail-out. Off by default: an assault that retreats isn't an assault.
			if (tuning.RetreatBelowHealthPercent > 0 && state.HealthPercent <= tuning.RetreatBelowHealthPercent)
				return UnitDecision.Retreat($"health {state.HealthPercent}% <= {tuning.RetreatBelowHealthPercent}%");

			if (!state.HasObjective)
				return state.IsIdle ? UnitDecision.Hold("no objective assigned") : UnitDecision.Continue;

			// 2. In range of the objective: hit it. The objective always wins over distractions.
			if (state.DistanceToObjectiveUnits <= state.WeaponRangeUnits)
				return UnitDecision.Attack(state.ObjectiveActorId, "objective in range");

			// 3. Opportunistic fire only — strictly targets already inside weapon range, so
			//    taking the shot costs us no forward progress.
			{
				var blocker = SelectBlocker(state, tuning);
				if (blocker.HasValue)
					return UnitDecision.Attack(blocker.Value.ActorId, $"clearing {blocker.Value.Kind} en route");
			}

			// 4. Otherwise: keep walking. Ignore everything else.
			if (state.CanMove)
				return UnitDecision.AdvanceToObjective(state.ObjectiveActorId, $"advancing, {state.DistanceToObjectiveUnits}u to objective");

			return state.IsIdle ? UnitDecision.Hold("immobile, objective out of range") : UnitDecision.Continue;
		}

		/// <summary>
		/// Chooses something worth shooting *without deviating from the advance*, or null.
		/// </summary>
		public static ThreatSnapshot? SelectBlocker(in AssaultState state, in AssaultTuning tuning)
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

				// The rule that makes this mode "ignore distractions": never leave weapon range.
				if (t.DistanceUnits > state.WeaponRangeUnits)
					continue;

				var isDefence = t.Kind == ThreatKind.Defence;
				if (isDefence && !tuning.ClearDefencesEnRoute)
					continue;
				if (!isDefence && !t.CanHitUs && !tuning.ReturnFireWhileAdvancing)
					continue;
				if (!isDefence && !t.CanHitUs)
					continue;

				var score = ScoreBlocker(t);
				if (score > bestScore || (score == bestScore && best.HasValue && t.ActorId < best.Value.ActorId))
				{
					bestScore = score;
					best = t;
				}
			}

			return best;
		}

		static int ScoreBlocker(in ThreatSnapshot t)
		{
			var score = 0;

			// Static defences are the real obstacle to a base assault; they don't disengage.
			if (t.Kind == ThreatKind.Defence)
				score += 10_000;

			if (t.CanHitUs)
				score += 5_000;

			score += 100 - Clamp(t.HealthPercent, 0, 100);
			score += (32 * 1024 - Clamp(t.DistanceUnits, 0, 32 * 1024)) / 1024;

			return score;
		}

		/// <summary>
		/// Ranks candidate structures to pick a base-assault objective. Production and static
		/// defence outrank storage, so a push degrades the enemy's ability to respond first.
		/// </summary>
		public static uint? SelectObjective(IReadOnlyList<ThreatSnapshot> candidates)
		{
			if (candidates == null || candidates.Count == 0)
				return null;

			uint? best = null;
			var bestScore = int.MinValue;

			for (var i = 0; i < candidates.Count; i++)
			{
				var c = candidates[i];
				if (!c.IsAttackable)
					continue;

				if (c.Kind != ThreatKind.Structure && c.Kind != ThreatKind.Defence && c.Kind != ThreatKind.Economy)
					continue;

				var score = c.Kind switch
				{
					ThreatKind.Structure => 3_000,
					ThreatKind.Economy => 2_000,
					ThreatKind.Defence => 1_000,
					_ => 0,
				};

				score += (64 * 1024 - Clamp(c.DistanceUnits, 0, 64 * 1024)) / 1024;

				if (score > bestScore || (score == bestScore && best.HasValue && c.ActorId < best.Value))
				{
					bestScore = score;
					best = c.ActorId;
				}
			}

			return best;
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
