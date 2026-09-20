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

			// How wide each band is, and how far the near edge walks outward between them. Thin
			// bands rather than nested ones, because the caller verifies every cell a band
			// offers: nested bands share a far edge, so each one is a superset of the last and
			// several probes can return the same rejected cell. Two cells is the smallest step
			// that still terminates quickly over the whole claim radius.
			StepCells: 2,

			// Ceiling on rungs, so the loop is bounded whatever the numbers do. The band walks
			// from ClaimRadius short of the field to ClaimRadius past it in steps of StepCells,
			// so 2 * ClaimRadius / StepCells = 12 is what the tuning above actually produces.
			MaxRungs: 16);
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
	/// to say "over there". A band is therefore all the precision the SDK offers on the way in,
	/// and a band through a field also passes through ground nowhere near it. The direction is
	/// recovered on the way <em>out</em> instead: the caller measures the cell the band actually
	/// returned against the field it was asked for, using the same <see cref="IsClaimed"/> test a
	/// standing refinery is judged by, and throws the cell away when it does not reach. So the
	/// bands are a search and the check is the guarantee.
	/// </para>
	/// <para>
	/// It is a preference with the old behaviour as its fallback. Returning no rungs when every
	/// field is already served — or returning only cells that fail the check — means
	/// <see cref="Modes.BuildBaseMode"/> uses exactly the ladder it used before, and the cells
	/// this does accept are taken <em>before</em> that ladder rather than instead of it, so a map
	/// this rule has nothing to say about builds the bot that fought the last match.
	/// </para>
	/// <para>
	/// <b>None of which mattered for a whole round, because nothing called it.</b> The mode's
	/// placement path used <see cref="BasePlacementLogic.LadderFor"/> and only that, so every
	/// word above described code the match never ran. See
	/// <c>Modes.BuildBaseMode.RefineryRings</c>, and note the <c>economy.refinery-sited-on-field</c>
	/// reason id that now proves it does.
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
		/// The bands to probe for a refinery meant to serve a field <paramref name="distanceCells"/>
		/// from the base centre, nearest home first.
		/// </summary>
		/// <remarks>
		/// Composed with <see cref="ChooseField"/> and <see cref="IsClaimed"/> by
		/// <see cref="Modes.BuildBaseMode"/>, which asks each band for a cell and then asks
		/// <see cref="IsClaimed"/> whether a refinery on that cell would actually reach the
		/// field. That last step is not optional. A ring has a radius and no direction, and
		/// <b>this file spent a whole round being right and never being called</b>: placement
		/// went through <see cref="BasePlacementLogic.LadderFor"/> alone, the two refineries on
		/// 16:9 went up six and thirteen cells <em>west</em> of the yard while the only field
		/// either harvester ever worked was east of it, and the side earned 2,100 credits in 977
		/// seconds. A band whose cell fails the check is discarded and the caller's own ladder
		/// answers, so the rule can only move a refinery towards tiberium and never away from
		/// where the bot would otherwise have put it.
		/// <para>
		/// <b>Thin bands, and nearest home first — not the nested ladder the rest of this bot
		/// uses.</b> A nested ladder shares its far edge, so every rung is a superset of the one
		/// before it and several probes can hand back the same already-rejected cell; with a
		/// verification step that wastes the probe. Each band here is
		/// <see cref="RefinerySitingTuning.StepCells"/> wide, they tile the range from
		/// <see cref="RefinerySitingTuning.ClaimRadiusCells"/> short of the field to the same
		/// distance past it — every cell that could claim it lies in that range — and they are
		/// walked outward so the first one that both offers a legal cell and passes the check is
		/// the shortest haul from the base that still reaches the field.
		/// </para>
		/// <para>
		/// Both edges are clamped to <see cref="BasePlacementLogic.MaxSearchRangeCells"/>, and
		/// that clamp is not cosmetic. <c>FindResourceFields</c> is deliberately uncapped — that
		/// is the whole reason it exists — so <c>distanceCells</c> is whatever the map offers,
		/// and on a large map a field 60 or 70 cells out is ordinary. The engine's tile search
		/// throws above 50 rather than clamping, and a throw out of a mode's tick is a
		/// <c>BuildBaseMode</c> that silently stops building: <see cref="BasePlacementLogic.RingAt"/>
		/// did exactly that on badland-ridges for the last 1,814 seconds of the match. A field
		/// beyond the search limit therefore collapses to a single legal band rather than an
		/// illegal request; it will simply fail the check and the caller's own ladder answers,
		/// which is the neutral fallback this had before the siting rule existed.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<PlacementRing> RingsFor(int distanceCells, in RefinerySitingTuning t)
		{
			if (distanceCells <= 0)
				return NoOpinion;

			var step = t.StepCells < 1 ? 1 : t.StepCells;
			var claim = t.ClaimRadiusCells < 0 ? 0 : t.ClaimRadiusCells;
			var ceiling = BasePlacementLogic.MaxSearchRangeCells;

			var far = distanceCells + claim;
			if (far > ceiling)
				far = ceiling;

			var near = distanceCells - claim;
			if (near < BasePlacementLogic.DefaultMinRangeCells)
				near = BasePlacementLogic.DefaultMinRangeCells;

			// The clamp above can pull the far edge below the near one on a field past the
			// engine's search limit. Collapse rather than emit an inverted band.
			if (near > far)
				near = far;

			var rungs = new List<PlacementRing>();
			for (var min = near; min < far && rungs.Count < t.MaxRungs; min += step)
			{
				var max = min + step;
				rungs.Add(new PlacementRing(min, max > far ? far : max));
			}

			if (rungs.Count == 0 && far > 0)
				rungs.Add(new PlacementRing(near, far));

			return rungs.Count > 0 ? rungs : NoOpinion;
		}
	}
}
