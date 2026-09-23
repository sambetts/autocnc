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
	/// <summary>What the side's watcher is doing.</summary>
	public enum WatchPhase
	{
		/// <summary>Standing on the approach, so an attack is seen coming rather than arriving.</summary>
		Picket,

		/// <summary>Running to the edge of their base to see what it is building and fielding.</summary>
		Rescout,
	}

	/// <summary>Tunable knobs for <see cref="IntelWatchLogic"/>. Distances in cells, times in ticks.</summary>
	public readonly record struct WatchTuning(
		int PicketPercent,
		int RescoutIntervalTicks,
		int RescoutTimeoutTicks,
		int RescoutStandoffCells,
		int ArrivedCells,
		int EvadeCells)
	{
		public static WatchTuning Default { get; } = new(
			// Two fifths of the way from home to their base. Far enough out that an attack is
			// seen with time to answer it — in 16 fights at d1a695b, 52% of the value lost went to
			// unit types first seen within 20 cells of this side's own base — and near enough
			// home that the picket is not the first thing their army runs over. Three tenths,
			// tried once, scored worse on hard-16-9 (0.522 mean fitness against 0.611).
			PicketPercent: 40,

			// Every 100 seconds. Their army's make-up changes on the scale of minutes, and the
			// helipad that sent the orcas was built long before it mattered.
			RescoutIntervalTicks: 100 * 25,

			// Give up on a look after 40 seconds and go back to the picket, rather than dying
			// in a traffic jam of their army on the way there.
			RescoutTimeoutTicks: 40 * 25,

			// Stop short of the remembered structure: a jeep sees 8 cells and a guard tower
			// reaches 6, so from 7 cells out it sees the base edge without standing in it.
			RescoutStandoffCells: 7,

			ArrivedCells: 3,

			// Far enough to break contact with infantry, the same step ScoutSearchLogic uses.
			EvadeCells: 6);
	}

	/// <summary>Where the watcher is, where home and their base are, and what is shooting at it.</summary>
	public readonly record struct WatchState(
		int X,
		int Y,
		int HomeX,
		int HomeY,
		int EnemyX,
		int EnemyY,
		bool Threatened,
		int ThreatX,
		int ThreatY,
		int Tick);

	/// <summary>The watcher's side-level memory. It outlives any one mode instance.</summary>
	public readonly record struct WatchMemory(WatchPhase Phase, int PhaseStartTick, int NextRescoutTick)
	{
		public static WatchMemory Start(int tick, in WatchTuning t) => new(WatchPhase.Picket, tick, tick + t.RescoutIntervalTicks);
	}

	public readonly record struct WatchOutcome(int X, int Y, WatchMemory Memory, string Reason, string ReasonId);

	/// <summary>
	/// One cheap fast unit that keeps looking after the base has been found.
	/// </summary>
	/// <remarks>
	/// Before this, the only scouting was the opening's search for their base. Once it was found
	/// — around 4:45 in the median fight, after their first attack had already landed in 14 of
	/// 16 — nothing on this side looked at the enemy again except the army when it fought them.
	/// Aircraft, artillery and every new tech were first seen on arrival.
	/// <para>
	/// So one unit now stands on the approach between the bases, and every
	/// <see cref="WatchTuning.RescoutIntervalTicks"/> it runs to the edge of their base to see
	/// what they are building, then comes back. Everything it sees goes into
	/// <c>EnemySightings</c>, which is what production reads to choose a counter.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design.</para>
	/// </remarks>
	public static class IntelWatchLogic
	{
		public static (int X, int Y) Picket(in WatchState s, in WatchTuning t) =>
			(s.HomeX + (s.EnemyX - s.HomeX) * t.PicketPercent / 100,
				s.HomeY + (s.EnemyY - s.HomeY) * t.PicketPercent / 100);

		/// <summary>A point short of their base, on the side facing home.</summary>
		public static (int X, int Y) Lookout(in WatchState s, in WatchTuning t)
		{
			var dx = s.HomeX - s.EnemyX;
			var dy = s.HomeY - s.EnemyY;
			var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
			if (length < 1)
				return (s.EnemyX, s.EnemyY);

			return ((int)Math.Round(s.EnemyX + dx * t.RescoutStandoffCells / length),
				(int)Math.Round(s.EnemyY + dy * t.RescoutStandoffCells / length));
		}

		public static WatchOutcome Decide(in WatchState s, in WatchMemory m, in WatchTuning t)
		{
			var memory = m;

			if (memory.Phase == WatchPhase.Picket && s.Tick >= memory.NextRescoutTick)
				memory = new WatchMemory(WatchPhase.Rescout, s.Tick, memory.NextRescoutTick);

			if (memory.Phase == WatchPhase.Rescout)
			{
				var look = Lookout(s, t);
				var arrived = Within(s.X, s.Y, look.X, look.Y, t.ArrivedCells);
				if (arrived || s.Tick - memory.PhaseStartTick >= t.RescoutTimeoutTicks)
					memory = new WatchMemory(WatchPhase.Picket, s.Tick, s.Tick + t.RescoutIntervalTicks);
				else if (s.Threatened)
				{
					// Around, not home: abandoning the look at the first shot is how the first
					// version of this never completed a single re-scout — the approach it
					// crosses is exactly where their army is.
					var around = Around(s, look.X, look.Y, t);
					return new WatchOutcome(around.X, around.Y, memory,
						$"re-scouting their base, going around a threat toward {look.X},{look.Y}", "intel.rescout-detour");
				}
				else
					return new WatchOutcome(look.X, look.Y, memory,
						$"re-scouting their base from {look.X},{look.Y}", "intel.rescout");
			}

			if (s.Threatened)
			{
				var away = Away(s, t);
				return new WatchOutcome(away.X, away.Y, memory,
					"watcher threatened, falling back toward home", "intel.evade");
			}

			var post = Picket(s, t);
			return new WatchOutcome(post.X, post.Y, memory,
				$"watching the approach from {post.X},{post.Y}", "intel.picket");
		}

		/// <summary>A step that opens the range on the threat without giving up the journey.</summary>
		/// <remarks>The same construction as ScoutSearchLogic.Sidestep.</remarks>
		static (int X, int Y) Around(in WatchState s, int toX, int toY, in WatchTuning t)
		{
			var stepX = Math.Sign(s.X - s.ThreatX) + Math.Sign(toX - s.X);
			var stepY = Math.Sign(s.Y - s.ThreatY) + Math.Sign(toY - s.Y);
			if (stepX == 0 && stepY == 0)
			{
				stepX = Math.Sign(s.X - s.ThreatX);
				stepY = Math.Sign(s.Y - s.ThreatY);
			}

			return (s.X + Math.Sign(stepX) * t.EvadeCells, s.Y + Math.Sign(stepY) * t.EvadeCells);
		}

		/// <summary>A step away from the threat, biased toward home so the watcher is not chased off the map.</summary>
		static (int X, int Y) Away(in WatchState s, in WatchTuning t)
		{
			var awayX = Math.Sign(s.X - s.ThreatX) + Math.Sign(s.HomeX - s.X);
			var awayY = Math.Sign(s.Y - s.ThreatY) + Math.Sign(s.HomeY - s.Y);
			if (awayX == 0 && awayY == 0)
			{
				awayX = Math.Sign(s.HomeX - s.X);
				awayY = Math.Sign(s.HomeY - s.Y);
			}

			return (s.X + Math.Sign(awayX) * t.EvadeCells, s.Y + Math.Sign(awayY) * t.EvadeCells);
		}

		static bool Within(int x, int y, int toX, int toY, int cells) =>
			Math.Abs(x - toX) <= cells && Math.Abs(y - toY) <= cells;
	}
}
