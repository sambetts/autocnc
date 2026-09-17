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
	/// <summary>Tunable knobs for <see cref="ExpansionLogic"/>.</summary>
	/// <remarks>
	/// Separated from the rules for the same reason <see cref="HarvesterTuning"/> is: the numbers
	/// are the argument, and they should be readable in one place rather than hunted for.
	/// </remarks>
	public readonly record struct ExpansionTuning(
		int MinFieldCells,
		int MaxFieldsConsidered,
		int MaxRefineries,
		int HarvestersPerRefinery,
		int MaxHarvesters,
		int PowerMargin,
		int RescanEvaluations)
	{
		public static ExpansionTuning Default { get; } = new(
			// The smallest patch worth 1,500 credits and a building. Twice
			// <see cref="HarvesterTuning.MinFieldCells"/>, because a harvester will happily
			// detour to four cells and a refinery has to still be there in ten minutes. The
			// scan filters on this, so a field ground below it simply stops being counted —
			// which is a density-scale-independent way of saying "worked out" and works
			// whatever a cell's maximum density is in this mod.
			MinFieldCells: 8,

			// FindResourceFields walks every cell, so this caps what comes back, not the walk.
			// It returns nearest first, so this is also the furthest field the rule will ever
			// notice. Twelve is comfortably more than the refinery ceiling below.
			MaxFieldsConsidered: 12,

			// The ceiling on refineries, and the only thing standing between a tiberium-rich map
			// and a base made entirely of refineries. Ten is 15,000 credits of building — less
			// than half the 32,950 this bot spent on units in the 842 seconds after its build
			// plan ran out on badland-ridges, and that army was worth 0 at the end.
			//
			// It also cannot outrun the placement ladder. BasePlacementLogic.RingAt walks the
			// near edge out by RingStepCells (6) per refinery and clamps it at MaxMinRangeCells
			// (16), so from the fourth refinery onward every extra one asks for the same 16-cell
			// near edge and the ladder walks inward from there. The tenth asks for exactly the
			// band the fourth did; the cap can never create an unanswerable request.
			MaxRefineries: 10,

			// A Tiberian Dawn refinery has one docking bay and comfortably feeds two harvesters.
			HarvestersPerRefinery: 2,

			// ...but only so many. Each refinery hands out one FreeActor harvester, so at the
			// refinery ceiling ten of the sixteen are free and the plan pays for six — 6,600
			// credits, against 14,250 spent on ltnk alone after 744s on badland-ridges. Without
			// this the Vehicle queue would owe twenty harvesters before it built one tank.
			MaxHarvesters: 16,

			// Power balance below which the expansion buys a plant before it buys anything else.
			// A plant is 500 credits and the cheapest structure this bot builds that carries
			// GivesBuildableArea, so it is both the power for the next refinery and the ground
			// to put it on. On badland-ridges the balance after the plan ran out read 63, 63,
			// 93, 63, 23, 38 and 28 — so this fires exactly in the late game, where the frontier
			// stopped moving.
			PowerMargin: 50,

			// Evaluations of an idle build plan between map scans. The scan is a map walk, not a
			// lookup, and this one is paid for by a single construction yard rather than by every
			// harvester, but an unbounded scan in a tick is not a thing to leave lying around.
			RescanEvaluations: 16);
	}

	/// <summary>
	/// How big an economy the tiberium this side has actually found is worth.
	/// </summary>
	/// <remarks>
	/// <b>A build plan is finite; a map is not.</b> Every doctrine's plan is a ladder of
	/// <c>Until(n)</c> rungs with a constant at the top — four refineries in
	/// <see cref="ReferencePlans.Economy"/>, five in <see cref="ReferencePlans.AttackBuild"/> —
	/// and once the last rung is met the construction yard has nothing to ask for ever again.
	/// On badland-ridges every step of the Attack plan was satisfied when the fifth refinery
	/// landed at 744s: five <c>proc</c>, five <c>nuke</c>, two <c>hand</c>, two <c>afld</c>, one
	/// <c>hq</c>, two <c>gtwr</c>, one <c>sam</c>. The <c>Building</c> queue issued no further
	/// planned order for the remaining 842 seconds of a 1,586-second match — only a replacement
	/// <c>proc</c> at 1,040s for the one an <c>orca</c> killed at 980s — and the bot's buildings
	/// peaked at 22 while the winner grew from 21 to 52.
	/// <para>
	/// The credits did not stop; they went somewhere worse. From 744s the bot spent 38,400, of
	/// which <b>32,950 — 86% — went on units</b>: 19 <c>ltnk</c> (14,250), 41 <c>e3</c> (12,300),
	/// 37 <c>e1</c> (3,700), 9 <c>bggy</c> (2,700). It finished the match with zero units and an
	/// army value of zero. Lifetime spend was 81,450 credits, 51.4 a second; the winner ended on
	/// 84,680 of army, 56,900 of base and 13,905 in hand, roughly 2.6 times everything this bot
	/// ever bought.
	/// </para>
	/// <para>
	/// The harvesters had already reported the problem. Their field-distance reasons walked out
	/// from a median of 13 cells in the first five minutes to 23 cells at 600–900s, with a
	/// maximum of 52 — half the map — and <b>331 of their 492 decisions were "hurt at N%,
	/// running"</b>, because ground that far out is ground no tower covers. A refinery is not
	/// only income, it is the thing that makes a field close.
	/// </para>
	/// <para>
	/// So the target is not a constant. It is how many patches of tiberium this side has
	/// <em>found</em> — which is shroud-filtered, so it rewards scouting, and self-limiting, so
	/// a poor map cannot turn the budget into refineries. Every answer here is floored at what
	/// the shipped plan already wanted, so where the plan is still the bigger number nothing
	/// changes at all.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class ExpansionLogic
	{
		/// <summary>How many discovered fields are worth putting a refinery beside.</summary>
		/// <remarks>
		/// The scan is already filtered to <see cref="ExpansionTuning.MinFieldCells"/>, so this
		/// only has to drop anything left with no tiberium in it at all.
		/// </remarks>
		public static int FieldsWorthWorking(IReadOnlyList<FieldOption> fields, in ExpansionTuning t)
		{
			if (fields == null)
				return 0;

			var worth = 0;
			for (var i = 0; i < fields.Count; i++)
				if (fields[i].CellCount >= t.MinFieldCells && fields[i].TotalDensity > 0)
					worth++;

			return worth;
		}

		/// <summary>
		/// How many refineries this side should own, given what it has found and what its plan
		/// already asked for.
		/// </summary>
		/// <remarks>
		/// Never below <paramref name="planTarget"/>: the shipped ladder is the floor, and this
		/// is only ever allowed to raise it. That is what makes the whole rule safe to add — a
		/// map with one small field produces the number the plan would have produced anyway.
		/// </remarks>
		public static int DesiredRefineries(
			IReadOnlyList<FieldOption> fields, int planTarget, in ExpansionTuning t)
		{
			var wanted = FieldsWorthWorking(fields, t);
			if (wanted > t.MaxRefineries)
				wanted = t.MaxRefineries;

			return wanted > planTarget ? wanted : planTarget;
		}

		/// <summary>How many harvesters a fleet of <paramref name="refineries"/> should run.</summary>
		/// <remarks>
		/// Floored at <paramref name="planTarget"/> for the same reason, so the opening — one or
		/// two refineries against a plan that already wants eight harvesters — is untouched.
		/// </remarks>
		public static int DesiredHarvesters(int refineries, int planTarget, in ExpansionTuning t)
		{
			var wanted = refineries > 0 ? refineries * t.HarvestersPerRefinery : 0;
			if (wanted > t.MaxHarvesters)
				wanted = t.MaxHarvesters;

			return wanted > planTarget ? wanted : planTarget;
		}

		/// <summary>
		/// The build plan with an economic frontier appended, or the plan unchanged when the map
		/// does not justify one.
		/// </summary>
		/// <remarks>
		/// Appended rather than inserted, and that position is the whole safety argument.
		/// <c>Until(n)</c> is cumulative and counts live actors, so a refinery bought by the
		/// rung below also satisfies every <c>proc</c> rung above it and the plan can never
		/// re-buy it; and because <see cref="BaseBuildLogic"/> walks the list top down, nothing
		/// here can be reached until every rung the doctrine wrote is met. The caller reinforces
		/// that by only asking at all once the doctrine's own plan has returned nothing to do.
		/// <para>
		/// The power rung leads the refinery rung because a refinery needs power to run and
		/// buildable ground to stand on, and a power plant is the cheapest source of both. It is
		/// conditional on the balance rather than on a count, so a base with headroom skips
		/// straight to the refinery.
		/// </para>
		/// <para>
		/// Turtling is its own guard. <see cref="ReferencePlans.DefenceBuild"/> has four rungs
		/// the other plans do not, so while the bot is under siege its plan is normally unmet
		/// and this is never consulted — the base spends a siege buying towers, not frontier.
		/// </para>
		/// </remarks>
		/// <param name="plan">The doctrine's own build plan. Returned as-is when nothing is due.</param>
		/// <param name="refineryRole">Actor names that count as a refinery, for either faction.</param>
		/// <param name="powerRole">Actor names that count as a power plant, for either faction.</param>
		public static IReadOnlyList<BuildStep> Expand(
			IReadOnlyList<BuildStep> plan,
			IReadOnlyList<FieldOption> fields,
			IReadOnlyDictionary<string, int> owned,
			int powerBalance,
			string[] refineryRole,
			string[] powerRole,
			in ExpansionTuning t)
		{
			if (plan == null || refineryRole == null || refineryRole.Length == 0)
				return plan;

			var planTarget = EconomyPlanLogic.TargetFor(EconomyPlanLogic.Rungs(plan), refineryRole);
			var desired = DesiredRefineries(fields, planTarget, t);
			if (desired <= planTarget)
				return plan;

			var extended = new List<BuildStep>(plan.Count + 2);
			extended.AddRange(plan);

			// One plant at a time, because the next evaluation re-reads the balance. Asking for
			// "standing + 1" rather than a fixed count means the rung retires itself the moment
			// the balance recovers, so a raided frontier gets its power back and an intact one
			// does not keep buying plants it does not need.
			if (powerBalance < t.PowerMargin && powerRole != null && powerRole.Length > 0)
				extended.Add(new BuildStep(powerRole, Standing(owned, powerRole) + 1));

			extended.Add(new BuildStep(refineryRole, desired));

			return extended;
		}
		/// <summary>
		/// The production plan with its harvester <em>floor</em> sized from the refineries that
		/// are actually standing, so the rung is reachable rather than pre-satisfied.
		/// </summary>
		/// <remarks>
		/// <b>This bot bought zero harvesters in a 1,289-second match.</b> Every plan writes the
		/// floor as a constant equal to <c>RefineryCore</c> — four — and a refinery carries a
		/// <c>FreeActor</c> harvester, so the four refineries standing from 263s satisfied the
		/// rung four-out-of-four and it could never fire. The <c>Vehicle</c> queue received
		/// <b>twelve orders in the whole match</b> — four <c>jeep</c>, seven <c>msam</c> and one
		/// <c>harv</c> at 1,144s, 248 seconds after the last harvester had already died. Every
		/// harvester the bot ever owned appeared in the same second as a <c>proc</c>: 51s, 117s,
		/// 174s, 263s.
		/// <para>
		/// The saturation rung below could not rescue it, because <see cref="Saturate"/> only
		/// raises the <em>last</em> harvester rung and that rung sits under
		/// <c>SiegeVehicles x 4</c>. Siege vehicles die — six <c>msam</c> built, six lost — so
		/// that rung was permanently unmet, <c>UnitProductionLogic.ChooseNext</c> returns the
		/// first unmet step, and the queue answered <c>msam</c> at 456s, 515s, 558s, 622s, 724s,
		/// 791s and 814s without ever reaching the income underneath. This is the third time the
		/// same shape has cost a match: tanks once blocked harvesters, harvesters once blocked
		/// tanks, and now siege blocks harvesters. Position alone cannot protect income, so the
		/// floor is sized here instead of being trusted to stay above whatever dies next.
		/// </para>
		/// <para>
		/// <b>The arithmetic this must not cross.</b> Between 300s and 840s — the window with
		/// exactly four harvesters standing — the bot spent 23,380 credits in 540 seconds, which
		/// is 43.3 a second, or <b>10.8 credits a second per harvester</b>. A harvester costs
		/// 1,100, so it repays itself in <b>102 seconds</b>. Four refineries have eight docking
		/// places and handed out four free actors, so this rung can owe at most four bought
		/// harvesters at once: 4,400 credits, 11.4% of the 38,680 this bot spent all match, and
		/// 102 seconds of the income the four it already had were earning. It can therefore never
		/// cost more than the single harvester it buys first returns. At the ceilings above —
		/// <see cref="ExpansionTuning.MaxRefineries"/> ten, <see cref="ExpansionTuning.MaxHarvesters"/>
		/// sixteen — the worst case is sixteen wanted against ten free, so six bought and 6,600
		/// credits outstanding.
		/// </para>
		/// <para>
		/// It raises and never lowers, and it does nothing at all with no refinery standing: a
		/// harvester with nowhere to dock earns zero, and replacing the refinery is the
		/// <c>Building</c> queue's job. That guard is why the last 269 seconds of the match, with
		/// every refinery gone, still buy things that shoot.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> Reinforce(
			IReadOnlyList<ProductionStep> plan,
			int refineries,
			IReadOnlyList<string> harvesterRole,
			in ExpansionTuning t)
		{
			if (plan == null || harvesterRole == null || harvesterRole.Count == 0 || refineries <= 0)
				return plan;

			var first = FirstRungNaming(plan, harvesterRole);
			if (first < 0)
				return plan;

			var step = plan[first];
			var desired = DesiredHarvesters(refineries, step.DesiredCount, t);
			if (desired <= step.DesiredCount)
				return plan;

			return Resized(plan, first, desired);
		}

		/// <summary>
		/// The production plan with its harvester saturation rung sized from the refineries that
		/// are actually standing.
		/// </summary>
		/// <remarks>
		/// Every plan in <see cref="ReferencePlans"/> names harvesters exactly twice: a floor
		/// near the top that guarantees a minimum fleet, and a saturation rung lower down that
		/// is the last thing bought before the plan goes back to buying things that shoot. This
		/// raises <b>only the last of them</b>, so the floor keeps its position and its number
		/// and the queue cannot be monopolised any earlier in the plan than it already could be.
		/// <para>
		/// It raises and never lowers, so a plan whose constant is already the bigger number is
		/// returned untouched — and a bot with no refineries standing gets exactly the plan its
		/// doctrine wrote.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> Saturate(
			IReadOnlyList<ProductionStep> plan,
			int refineries,
			IReadOnlyList<string> harvesterRole,
			in ExpansionTuning t)
		{
			if (plan == null || harvesterRole == null || harvesterRole.Count == 0)
				return plan;

			var last = LastRungNaming(plan, harvesterRole);
			if (last < 0)
				return plan;

			var step = plan[last];
			var desired = DesiredHarvesters(refineries, step.DesiredCount, t);
			if (desired <= step.DesiredCount)
				return plan;

			var scaled = new List<ProductionStep>(plan.Count);
			for (var i = 0; i < plan.Count; i++)
				scaled.Add(i == last
					? new ProductionStep(step.Queue, step.Candidates, desired)
					: plan[i]);

			return scaled;
		}

		/// <summary>The plan with one rung's count replaced, and every other rung shared.</summary>
		static IReadOnlyList<ProductionStep> Resized(
			IReadOnlyList<ProductionStep> plan, int index, int desired)
		{
			var step = plan[index];
			var scaled = new List<ProductionStep>(plan.Count);
			for (var i = 0; i < plan.Count; i++)
				scaled.Add(i == index
					? new ProductionStep(step.Queue, step.Candidates, desired)
					: plan[i]);

			return scaled;
		}

		/// <summary>
		/// The first rung whose candidates are <em>all</em> named by <paramref name="role"/>.
		/// </summary>
		/// <remarks>
		/// "All" rather than "any", for the reason spelled out on <see cref="LastRungNaming"/>:
		/// <c>Until(n)</c> counts every candidate a step lists, so a rung written
		/// <c>["harv", "mtnk"]</c> is satisfied by tanks and is not a harvester rung however it
		/// reads.
		/// </remarks>
		public static int FirstRungNaming(IReadOnlyList<ProductionStep> plan, IReadOnlyList<string> role)
		{
			if (plan == null || role == null)
				return -1;

			for (var i = 0; i < plan.Count; i++)
			{
				var candidates = plan[i].Candidates;
				if (candidates == null || candidates.Length == 0)
					continue;

				var all = true;
				for (var c = 0; c < candidates.Length && all; c++)
					all = Contains(role, candidates[c]);

				if (all)
					return i;
			}

			return -1;
		}

		/// <summary>
		/// The last rung whose candidates are <em>all</em> named by <paramref name="role"/>.
		/// </summary>
		/// <remarks>
		/// "All", not "any", because <c>Until(n)</c> counts every candidate a step lists: a rung
		/// written <c>["harv", "mtnk"]</c> is satisfied by tanks and is not a harvester rung
		/// however it reads. The shipped plans name <c>harv</c> alone for that same reason.
		/// </remarks>
		public static int LastRungNaming(IReadOnlyList<ProductionStep> plan, IReadOnlyList<string> role)
		{
			var found = -1;
			if (plan == null || role == null)
				return found;

			for (var i = 0; i < plan.Count; i++)
			{
				var candidates = plan[i].Candidates;
				if (candidates == null || candidates.Length == 0)
					continue;

				var all = true;
				for (var c = 0; c < candidates.Length && all; c++)
					all = Contains(role, candidates[c]);

				if (all)
					found = i;
			}

			return found;
		}

		/// <summary>How many of a role are standing, summed over every faction's name for it.</summary>
		public static int Standing(IReadOnlyDictionary<string, int> owned, IReadOnlyList<string> actors)
		{
			var total = 0;
			if (owned == null || actors == null)
				return total;

			for (var i = 0; i < actors.Count; i++)
				if (actors[i] != null && owned.TryGetValue(actors[i], out var n))
					total += n;

			return total;
		}

		static bool Contains(IReadOnlyList<string> actors, string item)
		{
			if (actors == null || item == null)
				return false;

			for (var i = 0; i < actors.Count; i++)
				if (string.Equals(actors[i], item, System.StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}
	}
}
