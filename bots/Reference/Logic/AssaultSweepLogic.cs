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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="AssaultSweepLogic"/>. Times in ticks, 25 to the game second.</summary>
	public readonly record struct AssaultSweepTuning(
		int StartRung,      // which rung of the search ladder a sweep opens on
		int MinRungTicks,   // how long a rung must have stood before an arrival may retire it
		int MaxRungTicks,   // ...and how long before an unreachable one is retired anyway
		int ArrivedCells)   // close enough to say "nothing here either"
	{
		public static AssaultSweepTuning Default { get; } = new(
			// Rung 0 of the ladder is our own base mirrored through the centre of the map, and a
			// sweep only ever begins where a push has already arrived — which on a symmetric map
			// is that exact cell. Opening on it would send the army to where it is standing. Rung
			// 1 is the first guess the push has not already answered.
			StartRung: 1,

			// The rung belongs to the side, so every sweeping unit walks at the same cell and the
			// push stays a push. That makes the debounce load-bearing: 180 units arriving inside
			// one evaluation would otherwise burn 180 rungs between them and the army would chase
			// a destination moving faster than it walks. Fifteen seconds is long enough for the
			// trailing half of a column to close up on the leaders who got there first.
			MinRungTicks: 375,

			// A rung nothing can reach has to retire on the clock instead, or the sweep becomes
			// the stall it was written to end. A minute is four times the debounce and still
			// walks the whole rim inside eight.
			MaxRungTicks: 1500,

			// The same radius AttackBaseMode already calls "standing on the spot".
			ArrivedCells: 5);
	}

	/// <summary>
	/// How a push that has run out of things to shoot decides where to look next.
	/// </summary>
	/// <remarks>
	/// <b>This is what 16:9 cost.</b> <c>AttackBaseMode</c> marched the army at the remembered
	/// enemy base, and the moment a unit stood within five cells of it with nothing in sensor
	/// range the approach returned no orders at all — so <c>AttackBaseLogic</c> fell through to
	/// <c>Hold("no objective assigned")</c> and the unit stopped, permanently. That hold was
	/// evaluated <b>62,649 times across 180 actors</b>, three quarters of every assault
	/// evaluation in the match: 29,651 for <c>e1</c>, 8,280 for <c>arty</c>, 5,258 for <c>e3</c>.
	/// Idle unit time was <b>46,305 seconds</b> against a prior median of 9,710, the army banked
	/// 32,200 credits of which a mean of 9,745 was ever committed, and the other side was still
	/// alive with a building standing at 1,329s.
	/// <para>
	/// Standing still is also how that army was priced. 17 <c>e1</c> and 9 <c>e3</c> were killed
	/// by enemy <c>arty</c>, which cannot be shot back at from where they stood and does not have
	/// to be accurate against something parked — <c>e1</c> returned 400 damage for 97,500 taken —
	/// and three of the four largest loss clusters of the match sit deep in their half of the
	/// map, where the push had arrived and then stopped.
	/// </para>
	/// <para>
	/// So a push with nothing left in front of it hunts instead of holding, on the same ladder
	/// <see cref="ScoutSearchLogic.Objective"/> gives a scout — mirrors first, then the map's rim
	/// — ordered as an attack-move, so it fights through what it meets and explores permanently
	/// on the way. The rung belongs to the side rather than to the unit, so the army converges on
	/// one cell at a time instead of dissolving into 180 private searches, and it only ever
	/// advances, so a side that has already swept half the map does not start again from the
	/// beginning every time it levels something.
	/// </para>
	/// <para>
	/// Nothing here deletes the side's sighting memory, and the sweep is abandoned the instant
	/// any structure enters sensor range, because the objective rules above it take over on their
	/// own. A sweep is therefore only ever what a unit does instead of nothing.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.</para>
	/// </remarks>
	public static class AssaultSweepLogic
	{
		/// <summary>
		/// The rung a sweep should be on, given how long the current one has stood and whether
		/// somebody has reached it.
		/// </summary>
		/// <remarks>
		/// Monotonic. A rung is retired by being answered — a unit standing on it and still
		/// seeing nothing — or by outlasting <see cref="AssaultSweepTuning.MaxRungTicks"/>, which
		/// is the only evidence available that the cell cannot be reached at all. A clock running
		/// backwards changes nothing, for the same reason
		/// <see cref="SightingMemoryLogic.Corroborated"/> reads a negative difference as safe.
		/// </remarks>
		public static int Advance(int rung, int ticksOnRung, bool arrived, in AssaultSweepTuning t)
		{
			if (rung < 0)
				return 0;

			if (ticksOnRung < 0)
				return rung;

			if (arrived && ticksOnRung >= t.MinRungTicks)
				return rung + 1;

			if (t.MaxRungTicks > 0 && ticksOnRung >= t.MaxRungTicks)
				return rung + 1;

			return rung;
		}
	}
}
