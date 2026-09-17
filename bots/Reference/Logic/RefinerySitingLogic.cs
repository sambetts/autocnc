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
	/// <summary>A cell, with no engine type in it. Enough to say where a refinery is standing.</summary>
	public readonly record struct SiteCell(int X, int Y);

	/// <summary>Tunable knobs for <see cref="RefinerySitingLogic"/>. Distances in cells.</summary>
	public readonly record struct RefinerySitingTuning(
		int ClaimRadiusCells,
		int StepCells,
		int MaxRungs)
	{
		public static RefinerySitingTuning Default { get; } = new(
			// How close a refinery has to be for a field to count as already served, and — the
			// same number, deliberately — how far past the field the ring is still allowed to
			// reach. Twelve is not a guess: OpenRA's built-in harvester search is capped at 12
			// cells from the last cell it cut, so a refinery inside 12 cells of a field is one
			// whose harvesters can find that field by themselves, and a refinery outside 12
			// cells is one whose harvesters cannot.
			ClaimRadiusCells: 12,

			// How far the near edge walks inward between rungs. Matches
			// BasePlacementLogic.LadderStepCells for the same reason it was chosen there: a
			// ladder that descends in wide steps has two rungs, the ambition and the fallback,
			// and falls past the frontier every time.
			StepCells: 2,

			// Ceiling on rungs, so the loop is bounded whatever the numbers do. ClaimRadius /
			// Step + 1 = 7 is what the tuning above actually produces.
			MaxRungs: 8);
	}

	/// <summary>
	/// Which tiberium field the next refinery is for, and how far out to look for it.
	/// </summary>
	/// <remarks>
	/// <b>Every refinery this bot builds is sited by a counter that has never seen the map.</b>
	/// <see cref="BasePlacementLogic.RingAt"/> takes the number of refineries already standing
	/// and asks for a ring six cells further out per refinery, clamped at a 16-cell near edge.
	/// That fixed the failure it was written for — refineries stacked on the yard — and it cannot
	/// fix this one, because a ring index knows how many refineries exist and nothing whatever
	/// about where the tiberium is.
	/// <para>
	/// On badland-ridges the five refineries went up at (83,6), (69,15), (70,7), (93,6) and
	/// (93,20) around a yard at (82,13): every one of them in the same north-east corner, the
	/// furthest 13.2 cells out. The harvesters then spent the match driving to fields nobody had
	/// built beside. Of the 89 harvest orders issued, <b>56 — 63% — named a field more than 15
	/// cells from the nearest refinery standing at the time</b>:
	/// </para>
	/// <list type="bullet">
	/// <item>(48,14) and (46–47,12–15), 21–23 cells out, worked at 386–401s, 637–661s, 751s and
	/// 999–1011s — four separate visits across 625 seconds, and no refinery was ever built there.</item>
	/// <item>(62,33), 19.3 cells out, worked at 687–690s and 1,047–1,108s.</item>
	/// <item>(7,39), (6,38) and (7,42), <b>66.5 to 67.6 cells out</b>, worked at 724–739s,
	/// 782–785s and 862s. One of those fields still held 280 density.</item>
	/// </list>
	/// <para>
	/// The arithmetic of that is the whole economy. A <c>harv</c> moves 1.758 cells per game
	/// second and carries roughly 700 credits, so a 7-cell refinery is a 14-cell round trip —
	/// 8 seconds of driving — and a 23-cell one is 26 seconds, and a 67-cell one is
	/// <b>76 seconds of driving for the same 700 credits</b>. Against a cutting time of roughly
	/// 20 seconds that is 25 credits a second, 15, and 7.3: the far fields yield about
	/// <b>a third</b> of what a field with a refinery beside it yields. Lifetime income worked
	/// out at 46.6 credits a second against a winner who finished on 63,920 of army, 49,100 of
	/// base and 4,104 in hand.
	/// </para>
	/// <para>
	/// So the ring is derived from the ground instead. This picks the nearest field that no
	/// refinery is close enough to work and asks for a band of rings bracketing <em>that
	/// field's</em> distance from the base centre.
	/// </para>
	/// <para>
	/// <b>What this can and cannot do.</b> <c>FindBuildLocation</c> takes a minimum and a maximum
	/// range from the base centre and nothing else — there is no direction, no origin and no way
	/// to say "over there". A ring is therefore all the precision the SDK offers, and a ring
	/// through a field also passes through ground nowhere near it. What the ring does guarantee
	/// is the thing the counter got wrong: the refinery is looked for at the distance the
	/// tiberium actually is, rather than at six-cells-per-refinery-so-far. In practice the
	/// buildable area is a contiguous blob grown from the structures already standing, so the
	/// legal cells in a far annulus are the ones on the side the base has already reached.
	/// </para>
	/// <para>
	/// It is a preference with the old behaviour as its fallback. Returning no rungs when every
	/// field is already served means <see cref="Modes.BuildBaseMode"/> uses exactly the ladder it
	/// used before, and the rungs this does return are tried <em>before</em> that ladder rather
	/// than instead of it — so a map this rule has nothing to say about builds the bot that
	/// fought the last match.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class RefinerySitingLogic
	{
		static readonly PlacementRing[] NoOpinion = [];

		/// <summary>
		/// Whether some refinery is already close enough to <paramref name="field"/> for its
		/// harvesters to find it unaided.
		/// </summary>
		/// <remarks>
		/// Measured to <see cref="FieldOption.NearestX"/>/<see cref="FieldOption.NearestY"/>
		/// rather than to the centre, for the same reason <see cref="HarvesterLogic"/> aims at
		/// the near edge: the near edge is the part a harvester actually reaches, and a long
		/// field's centre can be twenty cells past it.
		/// <para>
		/// Squared distances throughout, so there is no square root and no floating point in a
		/// decision that becomes an order.
		/// </para>
		/// </remarks>
		public static bool IsClaimed(in FieldOption field, IReadOnlyList<SiteCell> refineries, int claimRadiusCells)
		{
			if (refineries == null || refineries.Count == 0)
				return false;

			var limit = claimRadiusCells * claimRadiusCells;

			for (var i = 0; i < refineries.Count; i++)
			{
				var dx = refineries[i].X - field.NearestX;
				var dy = refineries[i].Y - field.NearestY;
				if ((dx * dx) + (dy * dy) <= limit)
					return true;
			}

			return false;
		}

		/// <summary>
		/// The index of the field the next refinery should be built for, or -1 if every field
		/// worth working already has one.
		/// </summary>
		/// <remarks>
		/// Nearest first, because the scan returns them that way and because among fields that
		/// are equally unserved the nearest is the shortest round trip — which is the entire
		/// quantity this rule exists to shorten. Mined-out fields are skipped:
		/// <see cref="FieldOption.CellCount"/> counts ground and
		/// <see cref="FieldOption.TotalDensity"/> counts tiberium, so a field ground down to a
		/// rind is wide and empty.
		/// </remarks>
		public static int ChooseField(
			IReadOnlyList<FieldOption> fields,
			IReadOnlyList<SiteCell> refineries,
			in RefinerySitingTuning t)
		{
			if (fields == null)
				return -1;

			for (var i = 0; i < fields.Count; i++)
			{
				if (fields[i].TotalDensity <= 0)
					continue;

				if (IsClaimed(fields[i], refineries, t.ClaimRadiusCells))
					continue;

				return i;
			}

			return -1;
		}

		/// <summary>
		/// The rings to try for a refinery meant to serve a field <paramref name="distanceCells"/>
		/// from the base centre, furthest out first.
		/// </summary>
		/// <remarks>
		/// The same shape as <see cref="BasePlacementLogic.Ladder"/> and for the same reason: the
		/// near edge walks inward while the far edge stays put, so each rung contains the one
		/// before it and the first rung that matches is the furthest-out band the base can
		/// legally build in.
		/// <para>
		/// Both edges are set by <see cref="RefinerySitingTuning.ClaimRadiusCells"/>, so every
		/// rung is a band from which the refinery would still claim the field. The near edge
		/// stops there rather than sliding home — below it this has stopped being a refinery for
		/// that field and is just another refinery in the base, which is what the caller's
		/// existing ladder already asks for.
		/// </para>
		/// <para>
		/// Both edges are also clamped to <see cref="BasePlacementLogic.MaxSearchRangeCells"/>,
		/// and that clamp is not cosmetic. <c>FindResourceFields</c> is deliberately uncapped —
		/// that is the whole reason it exists — so <c>distanceCells</c> is whatever the map
		/// offers, and on a large map a field 60 or 70 cells out is ordinary. The engine's tile
		/// search throws above 50 rather than clamping, and a throw out of a mode's tick is a
		/// <c>BuildBaseMode</c> that silently stops building: <see cref="BasePlacementLogic.RingAt"/>
		/// did exactly that on badland-ridges for the last 1,814 seconds of the match. A field
		/// beyond the search limit therefore collapses to a single legal band rather than an
		/// illegal request; it will simply fail to match and the caller's own ladder answers,
		/// which is the neutral fallback this had before the siting rule existed.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<PlacementRing> RingsFor(int distanceCells, in RefinerySitingTuning t)
		{
			if (distanceCells <= 0)
				return NoOpinion;

			var step = t.StepCells < 1 ? 1 : t.StepCells;
			var claim = t.ClaimRadiusCells < 0 ? 0 : t.ClaimRadiusCells;

			var far = distanceCells + claim;
			if (far > BasePlacementLogic.MaxSearchRangeCells)
				far = BasePlacementLogic.MaxSearchRangeCells;

			var floor = distanceCells - claim;
			if (floor < BasePlacementLogic.DefaultMinRangeCells)
				floor = BasePlacementLogic.DefaultMinRangeCells;

			// Never let the near edge pass the far one. Ordinarily it cannot — floor is
			// distanceCells - claim and far is distanceCells + claim — but the clamp above can
			// pull far below both, so both ends follow it down.
			var near = distanceCells;
			if (near > far)
				near = far;

			if (floor > far)
				floor = far;

			var rungs = new List<PlacementRing>();
			for (var min = near; min >= floor && rungs.Count < t.MaxRungs; min -= step)
				rungs.Add(new PlacementRing(min, far));

			return rungs.Count > 0 ? rungs : NoOpinion;
		}

		/// <summary>
		/// Where to look for the next refinery, given the fields this side has explored and the
		/// refineries it already owns. Empty means "no opinion" — use the existing ladder.
		/// </summary>
		public static IReadOnlyList<PlacementRing> LadderFor(
			IReadOnlyList<FieldOption> fields,
			IReadOnlyList<SiteCell> refineries,
			in RefinerySitingTuning t)
		{
			var index = ChooseField(fields, refineries, t);
			if (index < 0)
				return NoOpinion;

			// DistanceUnits is measured from whatever the scan was centred on, which for the
			// construction yard is the base centre — the same origin FindBuildLocation measures
			// its ring from. 1024 world units is one cell.
			return RingsFor(fields[index].DistanceUnits / 1024, t);
		}
	}
}
