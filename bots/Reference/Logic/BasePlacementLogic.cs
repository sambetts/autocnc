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
		/// — a structure needs one — and stays close enough that <see cref="Modes.RunHomeMode"/>
		/// has somewhere useful to send a harvester under fire.
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

		/// <summary>The ring the default overload of <c>FindBuildLocation</c> would use.</summary>
		public static PlacementRing Default { get; } = new(DefaultMinRangeCells, DefaultMaxRangeCells);

		/// <summary>
		/// Where to look for <paramref name="item"/>, given what is already standing.
		/// </summary>
		/// <param name="item">The actor about to be placed.</param>
		/// <param name="owned">Live building counts by actor name.</param>
		/// <param name="expandingStructures">
		/// Actors that should push outward as they multiply — the income buildings. Everything
		/// else keeps the default ring, because a power plant or a tower wants to be behind the
		/// defences rather than beyond them.
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

		/// <summary>The ring for the n-th structure of an expanding role, counting from zero.</summary>
		public static PlacementRing RingAt(int index)
		{
			if (index <= 0)
				return Default;

			var min = DefaultMinRangeCells + (RingStepCells * index);
			if (min > MaxMinRangeCells)
				min = MaxMinRangeCells;

			return new PlacementRing(min, DefaultMaxRangeCells + (RingStepCells * index));
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
