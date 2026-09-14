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

using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="HarvesterLogic"/>. Distances in world units unless named Cells.</summary>
	public readonly record struct HarvesterTuning(
		int PanicRadiusUnits,
		int SafeDistanceUnits,
		int FleeBelowHealthPercent,
		int StallEvaluations,
		int FirstProbeCells,
		int ProbeStepCells,
		int MaxProbeCells,
		int ProbeResetEvaluations)
	{
		public static HarvesterTuning Default { get; } = new(
			PanicRadiusUnits: 7 * 1024,
			SafeDistanceUnits: 4 * 1024,

			// A harvester that runs from every scratch never finishes a load. On badland-ridges
			// one took damage=30 against health=99 at 354s — a single rifle burst, one percent of
			// its hit points — fled, and never harvested again. Below 70% it is genuinely being
			// taken apart and the load is not worth the harvester.
			FleeBelowHealthPercent: 70,

			// Consecutive evaluations of "idle AND has not changed cell" before we intervene. A
			// harvester that is driving, cutting tiberium or unloading has a live activity and is
			// not idle, so this cannot fire on one that is still earning.
			StallEvaluations: 4,

			FirstProbeCells: 6,
			ProbeStepCells: 5,
			MaxProbeCells: 24,

			// Evaluations of healthy behaviour before the search ladder folds back to the start.
			ProbeResetEvaluations: 8);
	}

	/// <summary>Everything the harvester rule needs, with no engine types in it.</summary>
	public readonly record struct HarvesterState(
		int HealthPercent,
		bool CanMove,
		bool IsIdle,
		bool DangerNearby,
		bool HasRefinery,
		int RefineryX,
		int RefineryY,
		int DistanceToRefineryUnits,
		int X,
		int Y,
		int BaseX,
		int BaseY,
		int MapMinX,
		int MapMinY,
		int MapMaxX,
		int MapMaxY);

	/// <summary>
	/// What one harvester has been seen doing. Carried by the mode between evaluations, advanced
	/// here so the counting is as testable as the rule that reads it.
	/// </summary>
	public readonly record struct HarvesterWatchdog(
		int LastX,
		int LastY,
		int StillEvaluations,
		int ProbeIndex,
		int MovingEvaluations)
	{
		/// <summary>A harvester we have never seen. The impossible cell counts as "moved".</summary>
		public static HarvesterWatchdog Start { get; } = new(int.MinValue, int.MinValue, 0, 0, 0);
	}

	/// <summary>A decision plus the watchdog state that produced it.</summary>
	public readonly record struct HarvesterOutcome(UnitDecision Decision, HarvesterWatchdog Watchdog);

	/// <summary>
	/// Pure decision logic for a harvester: keep it working, and restart it when it stops.
	/// </summary>
	/// <remarks>
	/// The rule this replaces had exactly two outcomes — <c>Continue</c> and
	/// <c>MoveTo(refinery)</c> — which means it could stop a harvester and could never start one.
	/// <c>Continue</c> is defined as "leave the unit's current activity alone", and after a flee
	/// order the current activity is nothing, so a fled harvester stayed parked for the rest of
	/// the match. On badland-ridges that was the whole game: six flee orders were every order any
	/// harvester received, and income went from 15.0 credits a second to 1.23.
	/// <para>
	/// So this rule is built the other way round. Stopping is the exception and has to be earned;
	/// a harvester that is provably stopped is always restarted, first by sending it home to
	/// unload and then by walking it outward in widening steps until it finds ground worth
	/// cutting. No resource layer is read — the ladder is expressed entirely in move orders, the
	/// same way <c>ScoutMode</c> searches for a base it cannot see.
	/// </para>
	/// This type has ZERO OpenRA dependencies by design and is integer-only, so it is both
	/// lockstep-safe and testable without booting the engine.
	/// </remarks>
	public static class HarvesterLogic
	{
		// An octagon, in tenths, so the ladder fans out without floating point.
		static readonly int[] DirX = [10, 7, 0, -7, -10, -7, 0, 7];
		static readonly int[] DirY = [0, 7, 10, 7, 0, -7, -10, -7];

		public static HarvesterOutcome Decide(in HarvesterState state, in HarvesterWatchdog watchdog, in HarvesterTuning tuning)
		{
			var seen = Observe(watchdog, state, tuning);

			// Nothing we order can help a harvester that cannot move.
			if (!state.CanMove)
				return new HarvesterOutcome(UnitDecision.Continue, seen);

			var awayFromRefinery = state.HasRefinery && state.DistanceToRefineryUnits > tuning.SafeDistanceUnits;

			// 1. Run, but only once staying is actually costing us the harvester. Every flee order
			//    cancels the harvest activity, and cancelling is the expensive half of this rule.
			if (state.DangerNearby && state.HealthPercent < tuning.FleeBelowHealthPercent && awayFromRefinery)
				return new HarvesterOutcome(
					UnitDecision.MoveTo(state.RefineryX, state.RefineryY, $"hurt at {state.HealthPercent}%, running to the refinery"),
					seen with { StillEvaluations = 0 });

			// 2. Still earning, or not yet provably stopped: leave it alone.
			if (seen.StillEvaluations < tuning.StallEvaluations)
				return new HarvesterOutcome(UnitDecision.Continue, seen);

			// 3. Stopped. First try the cheap explanation — a cancelled delivery, with a full hold
			//    and nowhere it thinks it should take it.
			if (seen.ProbeIndex == 0 && awayFromRefinery)
				return new HarvesterOutcome(
					UnitDecision.MoveTo(state.RefineryX, state.RefineryY, $"stopped for {seen.StillEvaluations} evaluations, returning to the refinery"),
					seen with { StillEvaluations = 0, ProbeIndex = 1 });

			// 4. Stopped at the refinery, so the ground under it is finished. Walk outward in
			//    widening steps until the harvester finds something to cut.
			var probe = seen.ProbeIndex < 1 ? 1 : seen.ProbeIndex;
			var target = ProbeCell(state, probe, tuning, out var cells);

			return new HarvesterOutcome(
				UnitDecision.MoveTo(target.X, target.Y, $"stopped for {seen.StillEvaluations} evaluations, searching {cells} cells out"),
				seen with { StillEvaluations = 0, ProbeIndex = probe + 1 });
		}

		/// <summary>
		/// Advances the watchdog. Only a harvester that is idle AND has not changed cell counts as
		/// stopped; anything else resets the count, so a working harvester is never interrupted.
		/// </summary>
		public static HarvesterWatchdog Observe(in HarvesterWatchdog watchdog, in HarvesterState state, in HarvesterTuning tuning)
		{
			var moved = state.X != watchdog.LastX || state.Y != watchdog.LastY;
			if (moved || !state.IsIdle)
			{
				var moving = watchdog.MovingEvaluations >= tuning.ProbeResetEvaluations
					? tuning.ProbeResetEvaluations
					: watchdog.MovingEvaluations + 1;

				// Sustained healthy behaviour means the search paid off: start the ladder again
				// from the bottom next time, rather than leaping straight to the far edge.
				var probe = moving >= tuning.ProbeResetEvaluations ? 0 : watchdog.ProbeIndex;
				return new HarvesterWatchdog(state.X, state.Y, 0, probe, moving);
			}

			return new HarvesterWatchdog(state.X, state.Y, watchdog.StillEvaluations + 1, watchdog.ProbeIndex, 0);
		}

		/// <summary>How far out the n-th search step reaches, in cells.</summary>
		public static int ProbeRadiusCells(int probe, in HarvesterTuning tuning)
		{
			if (probe < 1)
				probe = 1;

			var cells = tuning.FirstProbeCells + (probe - 1) * tuning.ProbeStepCells;
			return cells > tuning.MaxProbeCells ? tuning.MaxProbeCells : cells;
		}

		/// <summary>
		/// Where the n-th search step sends the harvester: a widening octagon around the refinery
		/// it works out of, clamped to the map, and never the cell it is already standing on.
		/// </summary>
		public static (int X, int Y) ProbeCell(in HarvesterState state, int probe, in HarvesterTuning tuning, out int cells)
		{
			var originX = state.HasRefinery ? state.RefineryX : state.BaseX;
			var originY = state.HasRefinery ? state.RefineryY : state.BaseY;

			// A clamped probe can land back where we started, and a repeat of the order the unit is
			// already carrying out does nothing. Step the ladder on until it names somewhere else.
			for (var attempt = 0; attempt < DirX.Length; attempt++)
			{
				var step = probe + attempt;
				cells = ProbeRadiusCells(step, tuning);

				var i = ((step % DirX.Length) + DirX.Length) % DirX.Length;
				var x = Clamp(originX + cells * DirX[i] / 10, state.MapMinX, state.MapMaxX);
				var y = Clamp(originY + cells * DirY[i] / 10, state.MapMinY, state.MapMaxY);

				if (x != state.X || y != state.Y)
					return (x, y);
			}

			cells = ProbeRadiusCells(probe, tuning);
			return (Clamp(originX, state.MapMinX, state.MapMaxX), Clamp(originY, state.MapMinY, state.MapMaxY));
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
