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
	public readonly record struct ApproachOrders(bool HasTarget, int X, int Y, int DistanceUnits, string Why)
	{
		/// <summary>Nowhere to go: this side has never seen an enemy structure.</summary>
		public static ApproachOrders None { get; } = new(false, 0, 0, 0, null);
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
			=> Decide(state, tuning, ApproachOrders.None, WeaponRole.Unknown);

		public static UnitDecision Decide(in AssaultState state, in AssaultTuning tuning, in ApproachOrders approach)
			=> Decide(state, tuning, approach, WeaponRole.Unknown);

		public static UnitDecision Decide(in AssaultState state, in AssaultTuning tuning, in ApproachOrders approach, WeaponRole role)
		{
			// 0. An unarmed unit cannot assault anything. Leave it alone rather than marching it
			//    into the enemy base to die. See the equivalent guard in DefensiveLogic.
			if (!state.HasWeapon)
				return UnitDecision.Continue;

			// 1. Optional bail-out. Off by default: an assault that retreats isn't an assault.
			if (tuning.RetreatBelowHealthPercent > 0 && state.HealthPercent <= tuning.RetreatBelowHealthPercent)
				return UnitDecision.Retreat($"health {state.HealthPercent}% <= {tuning.RetreatBelowHealthPercent}%");

			if (!state.HasObjective)
				return Approach(state, approach, role);

			// 2. In range of the objective: clear only enemies already able to fight us, then
			//    resume the sticky objective. This is not a chase: every candidate is already
			//    inside weapon range, and the existing role scorer gives each weapon its job.
			if (state.DistanceToObjectiveUnits <= state.WeaponRangeUnits)
			{
				var damaged = ObjectiveIsDamaged(state);
				var screen = SelectObjectiveScreen(state, tuning, role);
				if (screen.HasValue && !damaged)
					return UnitDecision.Attack(screen.Value.ActorId,
						"screening immediate threat before objective",
						"assault.screen-before-objective");

				return damaged
					? UnitDecision.Attack(state.ObjectiveActorId,
						"finishing damaged objective rather than starting another",
						"assault.finish-damaged-objective")
					: UnitDecision.Attack(state.ObjectiveActorId, "objective in range", "assault.objective-in-range");
			}

			// 3. Opportunistic fire only — strictly targets already inside weapon range, so
			//    taking the shot costs us no forward progress.
			{
				var blocker = SelectBlocker(state, tuning, role);
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
		static UnitDecision Approach(in AssaultState state, in ApproachOrders approach, WeaponRole role)
		{
			if (approach.HasTarget && state.CanMove)
			{
				// Two different marches share this branch and they are not the same claim. One
				// walks at a place somebody on this side has actually seen a structure; the
				// other walks at a cell deduced from the map's own symmetry, with no sighting
				// behind it at all. Whichever it is says so, so the decision trace can tell a
				// probe that found nothing from an assault that arrived.
				var why = string.IsNullOrEmpty(approach.Why)
					? "nothing in sight, closing on their base"
					: approach.Why;

				return UnitDecision.AttackMoveTo(approach.X, approach.Y,
					$"{why}, {approach.DistanceUnits}u out");
			}

			var target = SelectLastStandTarget(state, role);
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
			=> SelectLastStandTarget(state, WeaponRole.Unknown);

		/// <inheritdoc cref="SelectLastStandTarget(in AssaultState)"/>
		public static ThreatSnapshot? SelectLastStandTarget(in AssaultState state, WeaponRole role)
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

				var score = ScoreBlocker(t, role);
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
			=> SelectBlocker(state, tuning, WeaponRole.Unknown);

		/// <summary>
		/// An attackable local defender that must be removed before structure fire can continue.
		/// The objective itself is excluded so this branch proves a real tactical preemption.
		/// </summary>
		static ThreatSnapshot? SelectObjectiveScreen(
			in AssaultState state,
			in AssaultTuning tuning,
			WeaponRole role)
		{
			var threats = state.Threats;
			if (threats == null || threats.Count == 0)
				return null;

			ThreatSnapshot? best = null;
			var bestScore = int.MinValue;

			for (var i = 0; i < threats.Count; i++)
			{
				var t = threats[i];
				if (t.ActorId == state.ObjectiveActorId || !t.IsAttackable)
					continue;

				if (t.DistanceUnits > state.WeaponRangeUnits)
					continue;

				var isDefence = t.Kind == ThreatKind.Defence;
				if (isDefence && !tuning.ClearDefencesEnRoute)
					continue;

				if (!isDefence && !t.CanHitUs)
					continue;

				var score = ScoreBlocker(t, role);
				if (score > bestScore || (score == bestScore && best.HasValue && t.ActorId < best.Value.ActorId))
				{
					bestScore = score;
					best = t;
				}
			}

			return best;
		}

		static bool ObjectiveIsDamaged(in AssaultState state)
		{
			var threats = state.Threats;
			if (threats == null)
				return false;

			for (var i = 0; i < threats.Count; i++)
			{
				var threat = threats[i];
				if (threat.ActorId == state.ObjectiveActorId)
					return threat.HealthPercent < 100;
			}

			return false;
		}

		/// <inheritdoc cref="SelectBlocker(in AssaultState, in AssaultTuning)"/>
		public static ThreatSnapshot? SelectBlocker(in AssaultState state, in AssaultTuning tuning, WeaponRole role)
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

				// A harvester cannot shoot back, so the two filters below used to discard it —
				// and that is how a push walks straight past the thing paying for the army it is
				// fighting. Shooting one costs no forward progress at all, because every
				// candidate here is already inside weapon range; the only thing that changes is
				// what a unit with nothing else to shoot does with the shot.
				var isEconomy = t.Kind == ThreatKind.Economy;

				if (isDefence && !tuning.ClearDefencesEnRoute)
					continue;
				if (!isDefence && !isEconomy && !t.CanHitUs && !tuning.ReturnFireWhileAdvancing)
					continue;
				if (!isDefence && !isEconomy && !t.CanHitUs)
					continue;

				var score = ScoreBlocker(t, role);
				if (score > bestScore || (score == bestScore && best.HasValue && t.ActorId < best.Value.ActorId))
				{
					bestScore = score;
					best = t;
				}
			}

			return best;
		}

		static int ScoreBlocker(in ThreatSnapshot t, WeaponRole role)
		{
			var score = 0;

			// Static defences are the real obstacle to a base assault; they don't disengage.
			if (t.Kind == ThreatKind.Defence)
				score += 10_000;

			if (t.CanHitUs)
				score += 5_000;

			// An enemy harvester is worth far more than the 1,100 credits it costs to replace:
			// it is the income that pays for everything currently shooting at us, and a side
			// whose economy is intact rebuilds an army faster than a push can raze it. Cabal
			// finished badland-ridges with 147 units and 70,960 of army; in 3,957 engagement
			// orders this bot aimed at an enemy harvester exactly once.
			//
			// The weight is bounded so it can never displace a shot at something that shoots
			// back. MatchBonus tops out at 3,000 and health and distance add at most 132, so an
			// economy target reaches 1,500 + 3,000 + 132 = 4,632, while the *worst* total a
			// CanHitUs target can score is 5,000 + 600 (AntiArmour against Infantry, the lowest
			// entry in the table) = 5,600. A defence starts at 10,000 either way. An enemy
			// harvester therefore only ever wins when the alternative is not shooting at all,
			// which is exactly "kill their economy on the way past".
			if (t.Kind == ThreatKind.Economy)
				score += 1_500;

			// What this unit's warhead can do to that armour. A push is a mixed force standing
			// in the same place, so this is how the work gets divided without anyone
			// coordinating it: the rockets take the tower and the tank, the rifles take the
			// infantry defending them. An e1 firing on a gtwr does 10% damage, which is not an
			// assault, it is a delay.
			score += WeaponMatchLogic.MatchBonus(role, t.Kind);

			score += 100 - Clamp(t.HealthPercent, 0, 100);
			score += (32 * 1024 - Clamp(t.DistanceUnits, 0, 32 * 1024)) / 1024;

			return score;
		}

		/// <summary>
		/// Ranks candidate structures to pick a base-assault objective. Production and static
		/// defence outrank storage, so a push degrades the enemy's ability to respond first.
		/// </summary>
		public static uint? SelectObjective(IReadOnlyList<ThreatSnapshot> candidates)
			=> SelectObjective(candidates, 0);

		/// <summary>
		/// How much of a structure's missing health is worth, per percent, when choosing what to
		/// shoot.
		/// </summary>
		/// <remarks>
		/// Sized to dominate the class spread deliberately. The gap between a production
		/// structure and a static defence is 2,000 points, so at 40 a point a building that has
		/// already lost half of itself outranks a pristine one of any class — which is the whole
		/// intent. A push that spreads its fire finishes nothing.
		/// </remarks>
		public const int DamageWeight = 40;

		/// <summary>What the objective this unit already committed to is worth keeping.</summary>
		/// <remarks>
		/// Below one quarter of <see cref="DamageWeight"/>'s range on purpose: enough that a
		/// unit does not swap between two untouched buildings of equal class and distance, and
		/// never enough to keep it on an untouched one while the rest of the push is 40% of the
		/// way through something else.
		/// </remarks>
		public const int CommitmentBonus = 900;

		/// <summary>
		/// Ranks candidate structures, preferring whatever this side has already hurt.
		/// </summary>
		/// <remarks>
		/// <b>A push that picks the nearest building finishes none of them.</b> On 16:9 this
		/// bot's army dealt <b>378,000 damage to enemy structures and destroyed one</b>: 227,720
		/// spread over construction yards, 55,920 over refineries that need about 72,000 each,
		/// 49,850 over guard towers and 28,875 over an advanced tower, all of which the other
		/// side simply repaired. <c>buildingsDestroyed</c> scored <b>0.125 of 1.0</b>, the worst
		/// component in the fight, and this method was why: it scored class and proximity and
		/// ignored <c>HealthPercent</c> entirely, so every unit independently walked to whatever
		/// was closest to it and a mixed force arriving from one direction still split its fire
		/// across a whole base.
		/// <para>
		/// Damage is the one signal that makes an uncoordinated force converge, because it is
		/// left behind by the force itself: the moment anybody scratches a refinery, every unit
		/// that can see it agrees that is where the fire goes, and it keeps agreeing more
		/// strongly as the building gets closer to falling. Nothing here needs a leader, a
		/// shared target list or map knowledge — only <c>ThreatSnapshot.HealthPercent</c>, which
		/// is visible to anything that can see the structure at all.
		/// </para>
		/// <para>
		/// <paramref name="currentObjectiveId"/> is the objective this unit already holds, and 0
		/// when it holds none. It only breaks ties among equally untouched candidates; see
		/// <see cref="CommitmentBonus"/>.
		/// </para>
		/// </remarks>
		public static uint? SelectObjective(IReadOnlyList<ThreatSnapshot> candidates, uint currentObjectiveId)
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

				// Finish what the push has started. See the remarks above.
				score += (100 - Clamp(c.HealthPercent, 0, 100)) * DamageWeight;

				if (c.ActorId == currentObjectiveId)
					score += CommitmentBonus;

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
