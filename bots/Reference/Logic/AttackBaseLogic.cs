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
	/// Where a push marches when it cannot see anything to shoot: the last place this side saw
	/// an enemy structure.
	/// </summary>
	/// <remarks>
	/// An assault has to start before it can see its target. Two bases on an ordinary map are
	/// further apart than any unit's sight — and sensing only ever returns what is visible
	/// *now* — so an army standing in its own base sees no objective at all. Without somewhere
	/// to march it holds, and an attack doctrine whose units all hold is indistinguishable from
	/// no attack doctrine at all.
	/// </remarks>
	public readonly record struct ApproachOrders(bool HasTarget, int X, int Y, int DistanceUnits)
	{
		/// <summary>Nowhere to go: this side has never seen an enemy structure.</summary>
		public static ApproachOrders None { get; } = new(false, 0, 0, 0);
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
			=> Decide(state, tuning, ApproachOrders.None);

		public static UnitDecision Decide(in AssaultState state, in AssaultTuning tuning, in ApproachOrders approach)
		{
			// 0. An unarmed unit cannot assault anything. Leave it alone rather than marching it
			//    into the enemy base to die. See the equivalent guard in DefensiveLogic.
			if (!state.HasWeapon)
				return UnitDecision.Continue;

			// 1. Optional bail-out. Off by default: an assault that retreats isn't an assault.
			if (tuning.RetreatBelowHealthPercent > 0 && state.HealthPercent <= tuning.RetreatBelowHealthPercent)
				return UnitDecision.Retreat($"health {state.HealthPercent}% <= {tuning.RetreatBelowHealthPercent}%");

			if (!state.HasObjective)
				return Approach(state, approach);

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
		/// What to do with no objective in sensor range: close on the last known enemy base,
		/// fight whatever is already in range if there is nowhere to close on, and only hold if
		/// there is genuinely nothing to do.
		/// </summary>
		/// <remarks>
		/// Attack-move rather than move, because the whole point is to arrive able to fight. It
		/// is not a breach of the never-chase rule: the destination is fixed before the unit
		/// sets off, so nothing it meets on the way can redirect it. As soon as a structure
		/// comes into range the objective rules above take over.
		/// <para>
		/// The last-stand branch below is the one that badland-ridges was lost for. The push
		/// levelled everything it could see, dropped the stale sighting, and every unit fell
		/// through to <c>Hold</c> — and a held unit in this mode does not shoot, because holding
		/// fire at things it has not been sent to kill is the entire point of the mode. So a
		/// hundred-unit army stood in a five-cell cluster at (35-39, 69-73) while their
		/// counter-attack walked into it and killed 71 of them for zero kills in return between
		/// 595s and 710s. Refusing to be baited off an assault is a virtue; refusing to shoot
		/// back when there is no assault left to be baited off is not.
		/// </para>
		/// </remarks>
		static UnitDecision Approach(in AssaultState state, in ApproachOrders approach)
		{
			if (approach.HasTarget && state.CanMove)
				return UnitDecision.AttackMoveTo(approach.X, approach.Y,
					$"nothing in sight, closing on their base, {approach.DistanceUnits}u out");

			var target = SelectLastStandTarget(state);
			if (target.HasValue)
				return UnitDecision.Attack(target.Value.ActorId,
					$"nowhere to push, engaging {target.Value.Kind} at {target.Value.DistanceUnits}u");

			return state.IsIdle ? UnitDecision.Hold("no objective assigned") : UnitDecision.Continue;
		}

		/// <summary>
		/// The best thing to shoot for a unit that has no objective and nowhere to march.
		/// </summary>
		/// <remarks>
		/// Deliberately looser than <see cref="SelectBlocker"/>. A blocker is filtered down to
		/// what is worth interrupting an advance for, so it skips anything that cannot shoot
		/// back; a unit with no advance left to protect is interrupting nothing, and every shot
		/// inside its weapon range is free. The one rule kept from the mode's contract is the
		/// one that defines it: never leave weapon range, so this can still never turn into a
		/// chase.
		/// </remarks>
		public static ThreatSnapshot? SelectLastStandTarget(in AssaultState state)
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

				if (t.DistanceUnits > state.WeaponRangeUnits)
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

		/// <summary>
		/// The best thing to shoot without giving up any forward progress, or null if nothing
		/// is worth interrupting the advance for.
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
