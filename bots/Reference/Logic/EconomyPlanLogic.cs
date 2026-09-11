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
	/// <summary>
	/// One rung of a plan, stripped of which queue asked for it: "keep this many of these
	/// standing".
	/// </summary>
	/// <remarks>
	/// <see cref="BuildStep"/> and <see cref="ProductionStep"/> say the same thing in two types,
	/// and the ordering question below is the same question for both, so both convert to this.
	/// </remarks>
	/// <param name="Candidates">Alternatives for one role — a faction's variants of one thing.</param>
	/// <param name="DesiredCount">How many of that role the plan wants standing by this point.</param>
	public readonly record struct PlanRung(IReadOnlyList<string> Candidates, int DesiredCount);

	/// <summary>
	/// What a plan commits to, in what order, and what it costs to get there.
	/// </summary>
	/// <remarks>
	/// Plans are plain data, so the mistakes they can contain are ordering mistakes, and an
	/// ordering mistake is invisible: <c>BaseBuildLogic</c> walks the list top down and every
	/// rung looks reasonable on its own. The one that lost badland-ridges was the third refinery
	/// sitting ninth, behind 1,000 credits of <c>hq</c> and 2,000 of <c>afld</c> — buildings
	/// that earn nothing — on a side whose cash read 0 from 150s onwards. It was ordered at 498s
	/// and stood at 860s of a 1,023-second match, so the bot ran the entire game on the two free
	/// harvesters its first two refineries handed out and finished on roughly 12 credits a
	/// second against the winner's 88.
	/// <para>
	/// The rule that would have caught it is one sentence — <em>income comes before anything
	/// that only unlocks income</em> — and this is that sentence made checkable. It is pure
	/// judgement about plans, so it lives here rather than in <see cref="ReferencePlans"/>, and
	/// it is stated over the shipped plans rather than a copy of them so the two cannot drift.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class EconomyPlanLogic
	{
		/// <summary>Returned by the index queries when a plan never gets there at all.</summary>
		public const int NotInPlan = -1;

		/// <summary>Reads a base build plan as ordered rungs.</summary>
		public static IReadOnlyList<PlanRung> Rungs(IEnumerable<BuildStep> plan)
		{
			var rungs = new List<PlanRung>();
			if (plan == null)
				return rungs;

			foreach (var step in plan)
				rungs.Add(new PlanRung(step.Candidates, step.DesiredCount));

			return rungs;
		}

		/// <summary>Reads a unit production plan as ordered rungs.</summary>
		public static IReadOnlyList<PlanRung> Rungs(IEnumerable<ProductionStep> plan)
		{
			var rungs = new List<PlanRung>();
			if (plan == null)
				return rungs;

			foreach (var step in plan)
				rungs.Add(new PlanRung(step.Candidates, step.DesiredCount));

			return rungs;
		}

		/// <summary>The most of <paramref name="role"/> the plan ever asks to have standing.</summary>
		/// <remarks>
		/// Zero when no rung names the role, which is itself the answer to "does this plan buy
		/// any income at all".
		/// </remarks>
		public static int TargetFor(IEnumerable<PlanRung> rungs, IReadOnlyCollection<string> role)
		{
			var target = 0;
			if (rungs == null || role == null)
				return target;

			foreach (var rung in rungs)
				if (Names(rung, role) && rung.DesiredCount > target)
					target = rung.DesiredCount;

			return target;
		}

		/// <summary>
		/// Where in the plan <paramref name="role"/> first reaches <paramref name="count"/>.
		/// </summary>
		/// <remarks>
		/// The position of a rung is the whole of its meaning. Two plans containing identical
		/// rungs in a different order are different strategies, and only one of them can pay for
		/// the other.
		/// </remarks>
		/// <returns>The rung's index, or <see cref="NotInPlan"/> if the plan never reaches it.</returns>
		public static int IndexOfRung(
			IEnumerable<PlanRung> rungs, IReadOnlyCollection<string> role, int count)
		{
			if (rungs == null || role == null || count <= 0)
				return NotInPlan;

			var i = 0;
			foreach (var rung in rungs)
			{
				if (Names(rung, role) && rung.DesiredCount >= count)
					return i;

				i++;
			}

			return NotInPlan;
		}

		/// <summary>
		/// What it costs to walk the plan as far as <paramref name="count"/> of
		/// <paramref name="role"/>, including that rung and everything above it.
		/// </summary>
		/// <remarks>
		/// Counting follows <c>Until(n)</c>: a rung is satisfied by the sum of everything it
		/// lists, so each rung is charged only for the shortfall it actually has to buy, and the
		/// rung that reaches the target is charged only as far as the target. Where a rung lists
		/// faction alternatives the dearest known one is charged, so the answer is an upper
		/// bound for either faction rather than an optimistic one. Candidates absent from
		/// <paramref name="costs"/> are free — a rung nothing is priced for adds nothing.
		/// </remarks>
		/// <returns>Credits committed, or <see cref="NotInPlan"/> if the plan never gets there.</returns>
		public static int CreditsToReach(
			IEnumerable<PlanRung> rungs,
			IReadOnlyCollection<string> role,
			int count,
			IReadOnlyDictionary<string, int> costs)
		{
			if (rungs == null || role == null || count <= 0)
				return NotInPlan;

			var owned = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
			var credits = 0;

			foreach (var rung in rungs)
			{
				var names = Names(rung, role);

				// Never pay past the rung being asked about: the plan may well want more later,
				// but that is a different question from what this target costs.
				var target = names && rung.DesiredCount > count ? count : rung.DesiredCount;

				var have = Standing(owned, rung.Candidates);
				var shortfall = target - have;

				if (shortfall > 0)
				{
					credits += shortfall * UnitCost(rung.Candidates, costs);

					// Attributed to the first candidate. Rungs count by the sum over everything
					// they list, so which alternative carries the tally never changes an answer.
					var key = First(rung.Candidates);
					if (key != null)
						owned[key] = Get(owned, key) + shortfall;
				}

				if (names && Standing(owned, role) >= count)
					return credits;
			}

			return NotInPlan;
		}

		static bool Names(in PlanRung rung, IReadOnlyCollection<string> role)
		{
			if (rung.Candidates == null)
				return false;

			foreach (var candidate in rung.Candidates)
				if (Contains(role, candidate))
					return true;

			return false;
		}

		static int Standing(IReadOnlyDictionary<string, int> owned, IEnumerable<string> actors)
		{
			var total = 0;
			if (actors == null)
				return total;

			foreach (var actor in actors)
				total += Get(owned, actor);

			return total;
		}

		/// <summary>The dearest priced alternative, so a faction-portable rung is not undercosted.</summary>
		static int UnitCost(IEnumerable<string> candidates, IReadOnlyDictionary<string, int> costs)
		{
			var cost = 0;
			if (candidates == null || costs == null)
				return cost;

			foreach (var candidate in candidates)
				if (candidate != null && costs.TryGetValue(candidate, out var c) && c > cost)
					cost = c;

			return cost;
		}

		static int Get(IReadOnlyDictionary<string, int> owned, string actor) =>
			actor != null && owned.TryGetValue(actor, out var n) ? n : 0;

		static string First(IReadOnlyList<string> candidates) =>
			candidates != null && candidates.Count > 0 ? candidates[0] : null;

		static bool Contains(IReadOnlyCollection<string> actors, string item)
		{
			if (actors == null || item == null)
				return false;

			foreach (var actor in actors)
				if (string.Equals(actor, item, System.StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}
	}
}
