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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="HuntLogic"/>. Distances in cells.</summary>
	public readonly record struct HuntTuning(
		int ArmyValue,
		int SectorCells,
		int ArrivedCells,
		int StallEvaluations,
		int SightCells)
	{
		public static HuntTuning Default { get; } = new(
			// Two and a half times what it takes to start a push. Below this the Scout doctrine is
			// the opening's search for their base, and the army belongs at home. Above it the
			// army is the only thing big enough to find what is left. In the three benchmark
			// games that timed out at 2,400s, the army was worth 76,000 to 131,000 and stood at
			// home for the last 1,300s.
			ArmyValue: 10_000,

			// A sector is about what a group of units sees while crossing it. On 16:9, 98 by 58
			// cells, that is 45 sectors, so a few hundred idle units cover each several times.
			SectorCells: 12,

			ArrivedCells: 3,

			// Evaluations without moving before a sector is written off as unreachable.
			StallEvaluations: 6,

			// How far a hunter looks for a structure. Wider than any unit sees, but sensing only
			// returns what this side can see, so this is how far a hunter will walk to one that a
			// neighbour has spotted.
			SightCells: 10);
	}

	/// <summary>
	/// Where each unit of a winning army searches for the last of the enemy.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A game only ends when the loser has nothing left, and this bot had no way to finish one.
	/// Once their main base fell and nothing more was in sight, the Scout doctrine took over for
	/// the rest of the match. Its army holds at home, and one or two scouts walk the map's rim. In
	/// three hard-16-9 benchmark games, two for a candidate and one for the champion itself, the
	/// match timed out at 2,400s with 380 to 700 of our units standing at home ("on post, no
	/// threats", around 40,000 evaluations in the last 400s) while the enemy had no army and one or
	/// two buildings the scouts never found. A timed-out benchmark game voids its pair, and three
	/// voided pairs stopped training outright.
	/// </para>
	/// <para>
	/// So a big enough army splits the map into sectors and every unit walks them on an
	/// attack-move, starting from a sector picked by its actor id so the army spreads instead of
	/// marching in one column. Everything is derived from the map's bounds at runtime: nothing
	/// here knows which map is being played.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design.</para>
	/// </remarks>
	public static class HuntLogic
	{
		public static (int Columns, int Rows) Grid(int minX, int minY, int maxX, int maxY, in HuntTuning t)
		{
			var size = Math.Max(1, t.SectorCells);
			return (Math.Max(1, (maxX - minX + size) / size), Math.Max(1, (maxY - minY + size) / size));
		}

		public static int Sectors(int minX, int minY, int maxX, int maxY, in HuntTuning t)
		{
			var (columns, rows) = Grid(minX, minY, maxX, maxY, t);
			return columns * rows;
		}

		/// <summary>The middle of one sector, clamped inside the map.</summary>
		public static (int X, int Y) Center(int sector, int minX, int minY, int maxX, int maxY, in HuntTuning t)
		{
			var (columns, rows) = Grid(minX, minY, maxX, maxY, t);
			var index = ((sector % (columns * rows)) + columns * rows) % (columns * rows);
			var width = (maxX - minX + 1) / (double)columns;
			var height = (maxY - minY + 1) / (double)rows;
			var x = minX + (int)((index % columns + 0.5) * width);
			var y = minY + (int)((index / columns + 0.5) * height);
			return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
		}

		/// <summary>Where a unit starts, spread across the grid by its id rather than all in one place.</summary>
		public static int Start(uint actorId, int sectors) =>
			sectors <= 0 ? 0 : (int)(actorId * 2654435761u % (uint)sectors);

		/// <summary>The next sector along, row by row, so each step is a short walk.</summary>
		public static int Next(int sector, int sectors) =>
			sectors <= 0 ? 0 : (sector + 1) % sectors;

		/// <summary>Chebyshev distance, which is how a unit that can move diagonally travels.</summary>
		public static bool Within(int x, int y, int toX, int toY, int cells) =>
			Math.Abs(x - toX) <= cells && Math.Abs(y - toY) <= cells;
	}
}
