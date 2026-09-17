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

namespace AutoCnC.Reference.Logic
{
	/// <summary>How far from the base centre a structure should be looked for, in cells.</summary>
	/// <param name="MinRangeCells">Closest cell from the base centre that will be considered.</param>
	/// <param name="MaxRangeCells">Furthest cell from the base centre that will be considered.</param>
	public readonly record struct PlacementRing(int MinRangeCells, int MaxRangeCells);

	/// <summary>A class of structure that should push outward as it multiplies.</summary>
	/// <param name="Candidates">The faction alternatives for one role, e.g. <c>powr</c>/<c>nuke</c>.</param>
	/// <param name="HomeCount">
	/// How many of this role are built inside the base before the rest lead the frontier. The
	/// first refinery is the entire economy and wants the starting field; the first two power
	/// plants exist before there is anywhere to expand to.
	/// </param>
	public readonly record struct ExpandingRole(IReadOnlyList<string> Candidates, int HomeCount);

	/// <summary>A class of structure that is only worth building where its weapon covers something.</summary>
	/// <param name="Candidates">The faction alternatives for one role, e.g. <c>gtwr</c>/<c>gun</c>.</param>
	/// <param name="ReachCells">
	/// The shortest weapon range in the candidate list, in cells — the pessimistic member of the
	/// pair, so the ring is honest whichever faction is playing. <c>gtwr</c> and <c>gun</c> both
	/// reach 6; <c>atwr</c> reaches 7 on the ground and <c>sam</c> 10, so that pair is 7.
	/// </param>
	/// <param name="HomeCount">
	/// How many of this role stay in the base before the rest go out to cover the economy. The
	/// first tower of each kind guards the yard, the barracks and the production cluster, which
	/// nothing else does.
	/// </param>
	public readonly record struct CoveringRole(IReadOnlyList<string> Candidates, int ReachCells, int HomeCount);

	/// <summary>
	/// Where to put the next structure — specifically, how far out.
	/// </summary>
	/// <remarks>
	/// Everything this bot builds used to go in the same place: <c>ModeContext.FindBuildLocation</c>
	/// defaults to a 2–14 cell ring around the base centre, and <see cref="Modes.BuildBaseMode"/>
	/// took that default for every structure in every plan. For a power plant or a guard tower
	/// that is right. For a refinery it is how a bot starves.
	/// <para>
	/// A Tiberian Dawn harvester always works the closest tiberium to the refinery it is docked
	/// with, so refineries stacked inside one 14-cell ring all share one patch, and the patch
	/// runs out. On badland-ridges all three refineries went up inside that ring (51s, 117s,
	/// 398s) and the whole fleet ground the same field flat: income fell from 5,300 credits per
	/// 100s over 300–400s to 3,620 over 500–600s and 2,400 over 700–800s, <em>while</em> the
	/// harvester fleet grew from two to five and refineries from two to three. Per-harvester
	/// yield collapsed roughly three-fold, from about 1,900 credits per 100s each to 640. Cash
	/// read 0 at 30 of the 35 assessments sampled from 240s to the end.
	/// </para>
	/// <para>
	/// The round trip tells the same story from the other side. A <c>harv</c> moves 1.758 cells
	/// per game second and carries roughly 700 credits: at 2,550 credits per 100s each over
	/// 200–300s the harvesters were turning a load around in about 27 seconds, and at 640 each
	/// over 500–700s they were taking about 92. They were not idle and they were not dead — five
	/// were alive and working from 443s until the first died at 838s. They were walking, because
	/// the tiberium they could still reach was a long way from every refinery the bot owned.
	/// </para>
	/// <para>
	/// So each refinery after the first is looked for further out, in a ring that starts beyond
	/// the ground the previous ones have already stripped. It is a preference, not a demand:
	/// <see cref="Modes.BuildBaseMode"/> falls back to the default ring when nothing out there is
	/// legal, so a cramped base still builds its refinery rather than stalling with one paid for
	/// and nowhere to put it.
	/// </para>
	/// <para>
	/// That preference then failed in the most instructive way possible: it asked for one ring
	/// far outside the buildable area, got nothing, and fell straight home. On the next
	/// badland-ridges every one of the bot's eighteen structures stood within <em>7.07 cells</em>
	/// of the construction yard for the whole 1,519-second match, and the three refineries that
	/// used an expanded ring landed <em>closer</em> to the yard (3.00, 4.24 and 5.00 cells at
	/// 117s, 178s and 237s) than the one that used the plain default ring (7.07 cells at 51s).
	/// The expansion was not merely inert, it was negative: the fallback search re-ran from
	/// scratch and took the first near cell it found.
	/// </para>
	/// <para>
	/// Two things follow, and together they are <see cref="Ladder"/> and
	/// <see cref="ExpandingRole"/>.
	/// </para>
	/// <para>
	/// <b>A ring is an ambition; a ladder is what is reachable.</b> Rather than one far request
	/// and then the default, the near edge is walked inward in <see cref="LadderStepCells"/>
	/// steps while the far edge stays put. Each rung is therefore a superset of the one before,
	/// so the first rung that matches anything is the furthest-out band the base can actually
	/// build in. Refinery two asks 8–20, then 6–20, then 4–20, then the default — and lands at
	/// the frontier instead of at three cells.
	/// </para>
	/// <para>
	/// <b>Only some structures can move the frontier.</b> A Tiberian Dawn cell is buildable when
	/// it is close enough to a structure with <c>GivesBuildableArea</c>. Of everything this bot
	/// builds, <c>silo</c>, <c>gtwr</c>, <c>gun</c>, <c>atwr</c> and <c>sam</c> have
	/// <c>RequiresBuildableArea</c> and do <em>not</em> give it, so no number of cheap towers
	/// ever extends the base. The cheapest structure that does is the power plant at 500 credits
	/// — against 1,500 for a refinery and 2,000 for a factory — and every plan already builds
	/// four or five of them. So power plants lead the expansion and the refineries follow into
	/// the ground they opened. The bot's five power plants stood at 2.00, 3.00, 4.00, 5.39 and
	/// 5.83 cells: all of them behind refinery number one, none of them buying an inch.
	/// </para>
	/// <para>
	/// <b>And a tower is only defence where its weapon reaches.</b> The rule above was written
	/// with "production and defence are deliberately absent — a tower that walks off to the
	/// frontier is a tower not defending anything". badland-ridges falsified the second half of
	/// that sentence. The economy had been pushed out correctly — four refineries at 7.07, 11.05,
	/// 13.42 and 13.42 cells from the yard — while every defence took the default 2–14 ring,
	/// which <c>FindBuildLocation</c> answers with the nearest legal cell. All three the bot ever
	/// built landed on top of the yard: <c>gtwr</c> at 1.41 cells (105s), <c>gtwr</c> at 2.24
	/// (130s), <c>sam</c> at 2.83 (250s) — 1,850 credits, and a <c>gtwr</c> reaches 6 cells, so
	/// not one of them covered a single outer refinery. The nearest tower was 10.20 cells from
	/// the closest one it missed and 12.08 from the other two.
	/// </para>
	/// <para>
	/// Enemy <c>e3</c> then walked up to the refineries and killed all four harvesters at 670s,
	/// 686s, 696s and 699s, at 8.54 to 10.05 cells from that nearest tower — 2.5 to 4.1 cells
	/// outside its reach, against a rocket soldier that also reaches 6 and could therefore stand
	/// where nothing answered it. The bot held four refineries and zero harvesters for 733 of the
	/// match's 1,453 seconds. Income went from 23.2 credits a second over the first 662s to 0.76
	/// over the last 791s, a 96.7% collapse, and the build log shows one 623-second gap with
	/// nothing completed between 662s and 1,285s.
	/// </para>
	/// <para>
	/// So defence expands too, and <see cref="CoveringRole"/> is how. A covering structure is
	/// looked for on the ring the economy has already reached and walked inward only as far as
	/// its own weapon can still reach that ring — below that it has stopped being cover and is
	/// just a building. This works because <c>gtwr</c>, <c>gun</c>, <c>atwr</c> and <c>sam</c>
	/// all require buildable area without giving any: out at the frontier the only legal cells
	/// are the ones beside the outer refineries and power plants, which is exactly where the
	/// tower is wanted. The ladder still ends at <see cref="Default"/>, so a cramped base puts
	/// its tower up at home rather than leaving 600 credits stuck in the queue.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class BasePlacementLogic
	{
		/// <summary>The SDK's own default ring, repeated here so the fallback is explicit.</summary>
		public const int DefaultMinRangeCells = 2;

		/// <inheritdoc cref="DefaultMinRangeCells"/>
		public const int DefaultMaxRangeCells = 14;

		/// <summary>How much further out each successive refinery is looked for, in cells.</summary>
		/// <remarks>
		/// Six cells is deliberately less than the default ring is wide. The point is to move the
		/// frontier, not to abandon the base: a refinery six cells beyond the last one still sits
		/// inside the previous ring's outer edge, so it stays connected to the buildable area
		/// — a structure needs one — and stays close enough that <c>HarvesterMode</c> has
		/// somewhere useful to send a harvester under fire, and somewhere to search outward from
		/// when the ground under it is finished.
		/// </remarks>
		public const int RingStepCells = 6;

		/// <summary>
		/// How far out the near edge of the ring is ever allowed to go, in cells.
		/// </summary>
		/// <remarks>
		/// Without a ceiling the fourth refinery asks for open ground 20 cells out, finds none
		/// legal, and falls back to the default ring every time — which is the old behaviour with
		/// extra steps. Clamping the near edge keeps the request answerable while the far edge
		/// carries on growing.
		/// </remarks>
		public const int MaxMinRangeCells = 16;

		/// <summary>
		/// How far the near edge moves inward between rungs of a placement ladder, in cells.
		/// </summary>
		/// <remarks>
		/// Deliberately smaller than <see cref="RingStepCells"/>, and that inequality is the
		/// whole mechanism. A ladder that descended in ring-sized steps would have exactly two
		/// rungs — the ambition and the default — which is the behaviour that put three
		/// refineries at 3.00, 4.24 and 5.00 cells while the frontier sat at 7.07. Two cells
		/// gives the search a real chance to stop at the frontier rather than falling past it.
		/// </remarks>
		public const int LadderStepCells = 2;

		/// <summary>The most rungs any ladder will ever have.</summary>
		/// <remarks>
		/// Each rung costs one <c>FindBuildLocation</c> call. Placement happens a couple of dozen
		/// times in a match, so the cost is irrelevant — but an unbounded loop in a mode's tick
		/// is not a thing to leave lying around.
		/// </remarks>
		public const int MaxLadderRungs = 12;

		/// <summary>
		/// How far out a placement search may ever be asked to look, in cells.
		/// </summary>
		/// <remarks>
		/// This is the engine's own limit, not a preference. <c>FindBuildLocation</c> is backed by
		/// a tile search whose <c>MaximumTileSearchRange</c> is 50 cells, and asking for more does
		/// not clamp or return nothing — it <b>throws</b>, out of a mode's tick, which is a
		/// silently dead <c>BuildBaseMode</c> for as long as the request keeps being made.
		/// <para>
		/// <see cref="RingAt"/> grew its far edge without a ceiling: <c>DefaultMaxRangeCells + 6n</c>
		/// reaches 56 at n = 7, one past the first illegal value. On badland-ridges that is exactly
		/// what happened — 337 "The requested range (56) cannot exceed the value of
		/// MaximumTileSearchRange (50)" errors between 3,039s and 3,375s, the last structure of the
		/// whole match went up at 3,007s, and nothing was built in the remaining 1,814 seconds
		/// (37.6% of it) while the base fell from 26 buildings to none. The near edge had a ceiling
		/// (<see cref="MaxMinRangeCells"/>) from the day it was written; the far edge never did.
		/// </para>
		/// </remarks>
		public const int MaxSearchRangeCells = 50;

		/// <summary>The ring the default overload of <c>FindBuildLocation</c> would use.</summary>
		public static PlacementRing Default { get; } = new(DefaultMinRangeCells, DefaultMaxRangeCells);

		/// <summary>
		/// Where to look for <paramref name="item"/>, given what is already standing.
		/// </summary>
		/// <param name="item">The actor about to be placed.</param>
		/// <param name="owned">Live building counts by actor name.</param>
		/// <param name="expandingStructures">
		/// Actors that should push outward as they multiply, all counted as one role with the
		/// first staying home. The simple form of the rule; see the
		/// <see cref="ExpandingRole"/> overload for the one the bot ships, which expands power
		/// plants too because they are the only cheap thing that can move the buildable frontier.
		/// </param>
		public static PlacementRing RingFor(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyCollection<string> expandingStructures)
		{
			if (string.IsNullOrEmpty(item) || expandingStructures == null)
				return Default;

			if (!Contains(expandingStructures, item))
				return Default;

			// How many of this role are already up. The first one stays home: it is the entire
			// economy, it wants the starting field, and it wants to be inside the base.
			var standing = CountOf(owned, expandingStructures);

			return RingAt(standing);
		}

		/// <summary>
		/// Where to look for <paramref name="item"/>, given what is already standing.
		/// </summary>
		/// <param name="item">The actor about to be placed.</param>
		/// <param name="owned">Live building counts by actor name.</param>
		/// <param name="roles">
		/// The structure classes that push outward as they multiply, each with how many of it
		/// stay home first. Anything named by no role keeps the default ring, because a barracks
		/// or a tower wants to be behind the defences rather than beyond them.
		/// </param>
		/// <remarks>
		/// Counting is per role. A power plant going up must not advance the refinery ring, or
		/// the two roles leapfrog each other into ground neither can reach.
		/// </remarks>
		public static PlacementRing RingFor(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<ExpandingRole> roles)
		{
			if (string.IsNullOrEmpty(item) || roles == null)
				return Default;

			for (var i = 0; i < roles.Count; i++)
			{
				var role = roles[i];
				if (role.Candidates == null || !Contains(role.Candidates, item))
					continue;

				var home = role.HomeCount < 1 ? 1 : role.HomeCount;

				return RingAt(CountOf(owned, role.Candidates) - home + 1);
			}

			return Default;
		}

		/// <summary>
		/// The rings to try for <paramref name="item"/>, furthest out first, ending at the default.
		/// </summary>
		public static IReadOnlyList<PlacementRing> LadderFor(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<ExpandingRole> roles)
			=> Ladder(RingFor(item, owned, roles));

		/// <summary>
		/// The rings to try for <paramref name="item"/>, treating covering structures as a class
		/// that follows the economy out rather than one that stays home.
		/// </summary>
		/// <remarks>
		/// A structure named by <paramref name="covering"/> is looked for on the frontier the
		/// expanding roles have already reached, and the ladder is floored at the closest ring
		/// from which its weapon still covers that frontier. Anything else falls through to the
		/// three-argument overload unchanged, so this can only move towers.
		/// </remarks>
		public static IReadOnlyList<PlacementRing> LadderFor(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<ExpandingRole> expanding,
			IReadOnlyList<CoveringRole> covering)
		{
			var ring = CoveringRingFor(item, owned, expanding, covering, out var floor);

			return ring == null
				? LadderFor(item, owned, expanding)
				: Ladder(ring.Value, floor);
		}

		/// <summary>
		/// The furthest ring any expanding role has actually reached — where the economy <em>is</em>,
		/// not where the next one is going.
		/// </summary>
		public static PlacementRing FrontierRing(
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<ExpandingRole> roles)
		{
			var frontier = Default;
			if (roles == null)
				return frontier;

			for (var i = 0; i < roles.Count; i++)
			{
				var role = roles[i];
				if (role.Candidates == null)
					continue;

				var home = role.HomeCount < 1 ? 1 : role.HomeCount;

				// The ring the most recently placed one asked for. RingFor uses "+ 1" because it
				// is siting the next structure; the frontier is the last one that landed.
				var ring = RingAt(CountOf(owned, role.Candidates) - home);
				if (ring.MinRangeCells > frontier.MinRangeCells)
					frontier = ring;
			}

			return frontier;
		}

		/// <summary>
		/// Where a covering structure should be looked for, or null if it is not one, is the one
		/// staying home, or has nothing out there to cover yet.
		/// </summary>
		/// <param name="floorMinCells">
		/// The closest near edge worth trying: any nearer and the structure's weapon no longer
		/// reaches the frontier it was bought to cover.
		/// </param>
		public static PlacementRing? CoveringRingFor(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<ExpandingRole> expanding,
			IReadOnlyList<CoveringRole> covering,
			out int floorMinCells)
		{
			floorMinCells = DefaultMinRangeCells;

			if (string.IsNullOrEmpty(item) || covering == null)
				return null;

			for (var i = 0; i < covering.Count; i++)
			{
				var role = covering[i];
				if (role.Candidates == null || !Contains(role.Candidates, item))
					continue;

				// The first of each kind guards the base itself. Nothing else does, and a base
				// whose every tower is on the frontier loses its yard to the first thing that
				// walks past them.
				var home = role.HomeCount < 0 ? 0 : role.HomeCount;
				if (CountOf(owned, role.Candidates) < home)
					return null;

				// Nothing has expanded yet, so there is no frontier to cover and home is correct.
				var frontier = FrontierRing(owned, expanding);
				if (frontier.MinRangeCells <= DefaultMinRangeCells)
					return null;

				var reach = role.ReachCells < 0 ? 0 : role.ReachCells;
				var floor = frontier.MinRangeCells - reach;
				floorMinCells = floor < DefaultMinRangeCells ? DefaultMinRangeCells : floor;

				return frontier;
			}

			return null;
		}

		/// <summary>
		/// <paramref name="target"/>, then progressively closer bands, ending at <see cref="Default"/>.
		/// </summary>
		/// <remarks>
		/// The near edge walks inward while the far edge stays where the ambition put it, so each
		/// rung contains the one before it. The first rung that matches a legal cell is therefore
		/// the furthest-out band this base can actually build in — which is the question worth
		/// asking, and the one a single far request followed by the default never asks.
		/// <para>
		/// The last rung is always the default ring, so a structure that has been paid for always
		/// has somewhere to go. This can only ever do better than the old behaviour: the rungs in
		/// between are extra chances, and the fallback is unchanged.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<PlacementRing> Ladder(PlacementRing target)
			=> Ladder(target, DefaultMinRangeCells);

		/// <summary>
		/// <paramref name="target"/>, walked inward no further than <paramref name="floorMinCells"/>,
		/// still ending at <see cref="Default"/> so anything already paid for has somewhere to go.
		/// </summary>
		/// <remarks>
		/// The floor is what makes a covering ring mean something. Without it the ladder slides
		/// all the way to two cells and puts the tower back on the yard — which is the behaviour
		/// that left four refineries at 7.07 to 13.42 cells guarded by two <c>gtwr</c> at 1.41
		/// and 2.24 cells, each of which reaches 6.
		/// <para>
		/// The floor is a rung in its own right, because it is the closest placement that still
		/// covers anything. <see cref="Default"/> follows it regardless: a tower at home beats
		/// 600 credits wedged in the Support queue with nowhere legal to stand.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<PlacementRing> Ladder(PlacementRing target, int floorMinCells)
		{
			if (target.MinRangeCells <= DefaultMinRangeCells || target.MaxRangeCells <= DefaultMaxRangeCells)
				return [Default];

			var floor = floorMinCells < DefaultMinRangeCells ? DefaultMinRangeCells : floorMinCells;
			if (floor > target.MinRangeCells)
				floor = target.MinRangeCells;

			var rungs = new List<PlacementRing>();

			for (var min = target.MinRangeCells;
				min > floor && rungs.Count < MaxLadderRungs - 1;
				min -= LadderStepCells)
				rungs.Add(new PlacementRing(min, target.MaxRangeCells));

			if (floor > DefaultMinRangeCells && rungs.Count < MaxLadderRungs - 1)
				rungs.Add(new PlacementRing(floor, target.MaxRangeCells));

			rungs.Add(Default);

			return rungs;
		}

		/// <summary>The ring for the n-th structure of an expanding role, counting from zero.</summary>
		/// <remarks>
		/// Both edges are clamped, and both clamps are load-bearing. The near edge stops at
		/// <see cref="MaxMinRangeCells"/> so the request stays answerable; the far edge stops at
		/// <see cref="MaxSearchRangeCells"/> so the request stays <em>legal</em>. Ordering matters
		/// on one degenerate input: with the near edge pinned at 16 and the far edge pinned at 50
		/// the ring is still 34 cells wide, so clamping can never invert it.
		/// </remarks>
		public static PlacementRing RingAt(int index)
		{
			if (index <= 0)
				return Default;

			var min = DefaultMinRangeCells + (RingStepCells * index);
			if (min > MaxMinRangeCells)
				min = MaxMinRangeCells;

			var max = DefaultMaxRangeCells + (RingStepCells * index);
			if (max > MaxSearchRangeCells)
				max = MaxSearchRangeCells;

			return new PlacementRing(min, max);
		}

		static int CountOf(IReadOnlyDictionary<string, int> owned, IReadOnlyCollection<string> actors)
		{
			if (owned == null)
				return 0;

			var total = 0;
			foreach (var actor in actors)
				foreach (var pair in owned)
					if (string.Equals(pair.Key, actor, System.StringComparison.OrdinalIgnoreCase))
						total += pair.Value;

			return total;
		}

		static bool Contains(IReadOnlyCollection<string> actors, string item)
		{
			foreach (var actor in actors)
				if (string.Equals(actor, item, System.StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}
	}
}
