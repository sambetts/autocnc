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
	/// <summary>Tunable knobs for <see cref="AttritionLogic"/>.</summary>
	/// <remarks>
	/// <paramref name="Losses"/> is how many members of a role may die, without the role's floor
	/// ever being met in between, before that floor stops pinning its queue.
	/// <para>
	/// Three, and the bound is set from both sides by the fight that produced this rule. ONE
	/// would fire on the first casualty, which is an ordinary event and not evidence of
	/// anything — every combat role loses its first member. TWENTY is what actually happened,
	/// and by then 6,000 credits were gone. Three consecutive replacements that never left the
	/// floor standing is the smallest number that cannot be a coincidence, and it caps the bill
	/// for discovering the role is unsurvivable at three units.
	/// </para>
	/// </remarks>
	public readonly record struct AttritionTuning(int Losses)
	{
		public static AttritionTuning Default { get; } = new(Losses: 3);
	}

	/// <summary>What <see cref="AttritionLogic.Release"/> did, and on what evidence.</summary>
	/// <remarks>
	/// The counts come back so the caller can put them in its decision reason. Whether this rule
	/// fired is not inferable from what got built — a Vehicle queue buying a tank looks identical
	/// whether the screen floor was written off or the screen simply happened to be standing — so
	/// it has to say so itself.
	/// </remarks>
	public readonly record struct AttritionRelease(
		IReadOnlyList<ProductionStep> Plan,
		int Losses,
		int Standing,
		int Target)
	{
		public bool Released => Target > 0;
	}

	/// <summary>
	/// One producing building's memory of a floor it keeps paying for and never fills.
	/// </summary>
	/// <remarks>
	/// Deliberately counts <em>deaths</em> rather than orders. An order is issued on every
	/// evaluation until the host's duplicate suppression swallows it, so counting orders would
	/// measure the tick rate; a drop in the standing count is one member dying, however many
	/// times it is observed. Held per production building rather than per player, because a
	/// factory that has just been rebuilt is entitled to find out for itself.
	/// <para>ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.</para>
	/// </remarks>
	public struct FloorAttrition
	{
		int losses;
		int lastStanding;
		bool primed;

		/// <summary>How many of the role have died since its floor was last met.</summary>
		public readonly int Losses => losses;

		/// <summary>
		/// Records what is standing now, against the floor the plan asked for.
		/// </summary>
		/// <remarks>
		/// Meeting the floor clears the tally outright. That is what makes the write-off
		/// self-healing rather than permanent: a role that survives long enough to stand at its
		/// full target has proved it can serve, and gets its floor back.
		/// </remarks>
		public void Observe(int standing, int target)
		{
			if (standing < 0)
				standing = 0;

			if (primed && standing < lastStanding)
				losses += lastStanding - standing;

			lastStanding = standing;
			primed = true;

			if (target > 0 && standing >= target)
				losses = 0;
		}
	}

	/// <summary>
	/// Stops a production floor whose members die faster than the queue can replace them from
	/// spending the rest of the match on replacements.
	/// </summary>
	/// <remarks>
	/// <see cref="ArmyBalanceLogic"/> solves the neighbouring problem — a floor that is merely
	/// short must not starve the rungs below it — and it solves it with a band computed from the
	/// floor's <em>target</em>. That band cannot reach a role that is being destroyed on sight,
	/// because the release asks whether enough of the role is standing and the answer is
	/// permanently zero.
	/// <para>
	/// <b>What that cost on 16:9.</b> <c>DefenceTrain</c> asks for a light screen of two, softened
	/// to one survivor. <c>bggy</c> lived a mean of <b>14.2 seconds</b> across the match and about
	/// <b>eight</b> for the fourteen built after 580s, so the side had zero standing at nearly
	/// every evaluation, zero is below one, and the rung was the first unmet step of the Vehicle
	/// queue for the rest of the game. <b>20 of the 24 Vehicle orders issued after the Defence
	/// switch at 210s were <c>bggy</c></b>: 6,000 credits, 20 built, 20 lost, 9 kills, 1,800
	/// credits destroyed — <b>667 credits a kill</b>. In the same 814 seconds the queue reached
	/// the armour rung once and the siege rung once. Those two units were the best buys of the
	/// match after the towers: one <c>ltnk</c> lived 183s and killed 3,100 credits' worth for 750
	/// (107 a kill), one <c>arty</c> killed 1,900 for 600 (86 a kill). Mean army value finished at
	/// <b>586</b> against a fitness reference of 6,000, and the side destroyed no enemy building
	/// all match.
	/// </para>
	/// <para>
	/// So the question a floor has to answer is not only "how many are standing" but "how many
	/// have I already bought that are not". A role that has lost <see cref="AttritionTuning.Losses"/>
	/// members without once reaching its floor is not being under-bought, it is being farmed, and
	/// the credits belong to whatever sits below it.
	/// </para>
	/// <para>
	/// <b>Two floors use this, and the second one was found the same way.</b> The light screen
	/// came first; the line-armour floor of <c>DefenceTrain</c> went through the identical hole
	/// on 16:9, where five <c>mtnk</c> lived 69, 16, 24, 78 and 37 seconds and pinned the Vehicle
	/// queue above its reach and income rungs for the rest of the match. See
	/// <see cref="Modes.TrainUnitsMode"/>, which keeps a separate tally per floor. Any future
	/// floor with this shape wants a tally of its own rather than a wider role list, because
	/// clearing is per floor: a pair that stands proves only that pair survivable.
	/// </para>
	/// <para>
	/// It writes the rung down to what is standing, exactly as <see cref="ArmyBalanceLogic.Release"/>
	/// does, rather than deleting it — so the role is still replaced by any rung below, and the
	/// original target returns intact the moment <see cref="FloorAttrition.Observe"/> sees the
	/// floor met. Endless rungs are skipped: nothing sits below them, so marking one met would
	/// silence the queue rather than free it.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.</para>
	/// </remarks>
	public static class AttritionLogic
	{
		/// <summary>
		/// Whether a floor of <paramref name="target"/> has been bought past the point of being
		/// worth buying again.
		/// </summary>
		/// <remarks>
		/// <b>A floor of one is never written off.</b> A rung asking for a single unit is a
		/// guarantee that the role exists at all — <see cref="ReferencePlans.OpeningTrain"/>'s
		/// lone screen vehicle is the bot's only scout, and exploration is a tenth of the fitness
		/// score and a precondition for reading the resource layer beyond the base. Writing that
		/// off would trade a permanent economic ceiling for 300 credits. Depth is what the
		/// evidence indicts: the rung that spent 6,000 credits on 16:9 asked for two.
		/// </remarks>
		public static bool Exhausted(int losses, int standing, int target, in AttritionTuning t)
		{
			if (t.Losses <= 0 || target <= 1 || target == int.MaxValue)
				return false;

			return standing < target && losses >= t.Losses;
		}

		/// <summary>
		/// The plan with every bounded rung naming <paramref name="role"/> marked met, while the
		/// role has been dying without ever filling its floor.
		/// </summary>
		/// <remarks>
		/// Returns the plan it was given, by reference, when nothing needed rewriting — which is
		/// every evaluation on a role that is surviving, and every evaluation before the tally
		/// reaches its bound. Reference equality is also how the caller knows whether to say so.
		/// </remarks>
		public static AttritionRelease Release(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<string> role,
			int losses,
			in AttritionTuning t)
		{
			if (plan == null || owned == null || role == null || role.Count == 0)
				return new AttritionRelease(plan, losses, 0, 0);

			var standing = ExpansionLogic.Standing(owned, role);
			List<ProductionStep> rewritten = null;
			var reportedTarget = 0;

			for (var i = 0; i < plan.Count; i++)
			{
				var step = plan[i];
				if (!AllNamed(role, step.Candidates))
					continue;

				var target = step.DesiredCount;
				if (!Exhausted(losses, standing, target, t))
					continue;

				if (rewritten == null)
				{
					rewritten = new List<ProductionStep>(plan.Count);
					for (var c = 0; c < plan.Count; c++)
						rewritten.Add(plan[c]);

					// The first rung written off is the one the queue was actually stuck on, so
					// it is the one worth naming in the decision reason.
					reportedTarget = target;
				}

				rewritten[i] = new ProductionStep(step.Queue, step.Candidates, standing);
			}

			return rewritten == null
				? new AttritionRelease(plan, losses, standing, 0)
				: new AttritionRelease(rewritten, losses, standing, reportedTarget);
		}

		/// <summary>Whether <em>every</em> candidate a rung lists belongs to the role.</summary>
		/// <remarks>
		/// "All", not "any", for the reason <see cref="ArmyBalanceLogic"/> spells out at length:
		/// <c>Until(n)</c> counts every candidate a step lists, so a rung written
		/// <c>["bggy", "ltnk"]</c> is satisfied by light tanks and is not a screen rung however
		/// it reads.
		/// </remarks>
		static bool AllNamed(IReadOnlyList<string> role, string[] candidates)
		{
			if (candidates == null || candidates.Length == 0)
				return false;

			for (var c = 0; c < candidates.Length; c++)
				if (!ArmyMixLogic.Names(role, candidates[c]))
					return false;

			return true;
		}
	}
}
