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

using System;
using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>
	/// What this side has actually watched the enemy field, counted once per enemy actor.
	/// </summary>
	/// <remarks>
	/// Mobile actors only. Structures and static defences are excluded deliberately: a base is
	/// always there and always visible once found, so counting it would drown the army it is
	/// supposed to describe. Harvesters count as armour, because that is what they wear — a
	/// <c>harv</c> is Heavy plate, and the warhead that kills one is the warhead that kills a
	/// tank.
	/// <para>No engine types, so the rule that reads it can be read on its own.</para>
	/// </remarks>
	public readonly record struct EnemyMix(int Infantry, int Armour)
	{
		public static EnemyMix None { get; } = new(0, 0);

		public int Total => Infantry + Armour;
	}

	/// <summary>Tunable knobs for <see cref="ArmyMixLogic"/>.</summary>
	public readonly record struct MixTuning(int MinimumSeen, int ArmourSharePercent)
	{
		public static MixTuning Default { get; } = new(
			// How much of the enemy has to have been looked at before the plan is allowed to
			// change on the strength of it. One scout glimpsing one buggy is not a composition.
			//
			// Six, and the bound is set from below rather than from taste: this side saw fifteen
			// distinct enemy actors in the whole of the badland-ridges loss, structures included,
			// so a floor in double figures is a rule that would simply never fire. Six is two
			// more than a raiding party and reachable inside the first contact.
			MinimumSeen: 6,

			// The share of that mix which has to be plated before every leftover credit stops
			// buying rifles.
			//
			// Bounded on both sides by matches this bot has already played, which is why it is
			// 60 rather than a round 50:
			//
			//  * ABOVE the infantry-heavy match. 63% of the army's engagement orders there were
			//    against Infantry, so an armour share near 37% — and a rifle rung, which is what
			//    won that argument: e1 does 1875 damage a second to None armour where e3 does
			//    319, and endless e3 cost 11,400 credits for 11 kills.
			//  * BELOW the armour-only match. The badland-ridges loss met ltnk, bike, ftnk, arty,
			//    heli, a10 and harv and essentially no enemy infantry, so an armour share at or
			//    near 100% — and a rocket rung, because e1 does 125 a second to Heavy where e3
			//    does 1593, and because 20 of the 54 riflemen this bot built died to aircraft
			//    they cannot shoot at all while e3 can.
			//
			// A mix genuinely balanced between the two lands under 60 and keeps the cheap body,
			// which is the safe way round: rifles are a third of the price, so a wrong rifle
			// wastes 100 credits and a wrong rocket wastes 300.
			ArmourSharePercent: 60);
	}

	/// <summary>
	/// Which body the endless infantry rung buys, decided from the enemy rather than from a
	/// constant.
	/// </summary>
	/// <remarks>
	/// <c>UnitProductionLogic.ChooseNext</c> returns the first unmet step, and an endless step is
	/// never met — so the endless rung is not a fallback, it is <b>what the bot spends the rest
	/// of the match on</b>. Both bodies have now been that rung, and each was right once and
	/// wrong once:
	/// <para>
	/// <b>Endless <c>e3</c> lost a match.</b> 38 rocket soldiers, 11,400 credits, 29.3% of all
	/// spend, for 11 kills — while 63% of the army's engagement orders were against Infantry,
	/// which an <c>e3</c> hurts at 319 damage a second against an <c>e1</c>'s 1,875. That is why
	/// the rung was changed to <c>e1</c>.
	/// </para>
	/// <para>
	/// <b>Endless <c>e1</c> then lost the next one.</b> On badland-ridges at Hard the opponent
	/// fielded <c>ltnk</c>, <c>bike</c>, <c>ftnk</c>, <c>arty</c>, <c>heli</c>, <c>a10</c> and
	/// <c>harv</c> and essentially no infantry at all. The rung bought 54 riflemen for 5,400
	/// credits; they dealt 97,620 damage, absorbed 247,239 and killed 1,650 credits' worth, and
	/// their mean life was 191s. The thirteen <c>e3</c> beside them cost 3,900, dealt
	/// <b>121,111</b>, absorbed <b>33,440</b> and lived 304s — 1.7x the damage per credit and a
	/// fifth of the damage taken. Against <c>ltnk</c> alone <c>e3</c> dealt 85,120 for 3,400
	/// taken. And <b>20 of the 54 riflemen were killed by aircraft</b>, which a rifle cannot
	/// shoot at all and a rocket can.
	/// </para>
	/// <para>
	/// Neither constant was wrong; asking the question with a constant was. Both matches are
	/// answered correctly by the same sentence — <em>buy the body that beats what is actually out
	/// there</em> — so that sentence is the rung now, and the counting that feeds it is the
	/// ordinary shroud-filtered sensing every mode already does.
	/// </para>
	/// <para>
	/// Only <em>endless</em> rungs are retargeted. The bounded ones are a composition the plan
	/// asked for on purpose — a rifle core that is the best answer to other infantry whatever the
	/// enemy mostly is, and a rocket core that is the only thing this bot builds which shoots
	/// upwards — and a rule that rewrote those would delete the mixed army rather than choose
	/// what tops it up.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.</para>
	/// </remarks>
	public static class ArmyMixLogic
	{
		/// <summary>
		/// Whether what this side has seen is plated enough to spend its spare credits on rockets.
		/// </summary>
		/// <remarks>
		/// False on an empty mix, so a side that has met nobody yet behaves exactly as it did
		/// before this rule existed. A rule that degrades to the previous behaviour when it is
		/// uninformed cannot be worse than not having one.
		/// </remarks>
		public static bool PreferAntiArmour(in EnemyMix mix, in MixTuning t)
		{
			var total = mix.Total;
			if (total <= 0 || total < t.MinimumSeen)
				return false;

			return mix.Armour * 100 >= total * t.ArmourSharePercent;
		}

		/// <summary>
		/// The plan with every endless rung of <paramref name="queue"/> that names only
		/// <paramref name="from"/> rewritten to buy <paramref name="to"/> instead.
		/// </summary>
		/// <remarks>
		/// Returns the plan it was handed, by reference, when nothing needed rewriting — which is
		/// the common case and every case before first contact, so this is free on the path it
		/// does not change. Reference equality is also how the caller knows whether to say so in
		/// its decision reason.
		/// </remarks>
		public static IReadOnlyList<ProductionStep> Retarget(
			IReadOnlyList<ProductionStep> plan,
			string queue,
			IReadOnlyList<string> from,
			string[] to)
		{
			if (plan == null || string.IsNullOrEmpty(queue) || from == null || from.Count == 0)
				return plan;

			if (to == null || to.Length == 0)
				return plan;

			List<ProductionStep> rewritten = null;

			for (var i = 0; i < plan.Count; i++)
			{
				var step = plan[i];

				// Endless only. A bounded rung is a composition, not a default.
				if (step.DesiredCount != int.MaxValue)
					continue;

				if (!string.Equals(step.Queue, queue, StringComparison.OrdinalIgnoreCase))
					continue;

				if (!AllNamed(from, step.Candidates))
					continue;

				rewritten ??= new List<ProductionStep>(plan);
				rewritten[i] = new ProductionStep(step.Queue, to, step.DesiredCount);
			}

			return rewritten ?? plan;
		}

		/// <summary>Whether an actor type belongs to a named role.</summary>
		public static bool Names(IReadOnlyList<string> role, string actorType)
		{
			if (role == null || string.IsNullOrEmpty(actorType))
				return false;

			for (var i = 0; i < role.Count; i++)
				if (string.Equals(role[i], actorType, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		/// <summary>
		/// Whether <em>every</em> candidate a rung lists belongs to the role.
		/// </summary>
		/// <remarks>
		/// "All", not "any", for the reason <see cref="ArmyBalanceLogic"/> spells out at length:
		/// <c>Until(n)</c> counts every candidate a step lists, so a rung is only the role it
		/// reads as when nothing else is on it. A rung written <c>["e1", "e2"]</c> is a body rung
		/// with a grenadier in it and is not this rule's to rewrite.
		/// </remarks>
		static bool AllNamed(IReadOnlyList<string> role, string[] candidates)
		{
			if (candidates == null || candidates.Length == 0)
				return false;

			for (var c = 0; c < candidates.Length; c++)
				if (!Names(role, candidates[c]))
					return false;

			return true;
		}
	}
}
