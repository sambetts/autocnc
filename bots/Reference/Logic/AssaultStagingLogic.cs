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
	/// <summary>Tunable knobs for <see cref="AssaultStagingLogic"/>. Distances in world units (1024 == 1 cell).</summary>
	public readonly record struct MusterTuning(
		int StageBeyondUnits,
		int StandoffUnits,
		int MusterRadiusUnits,
		int PatienceEvaluations,
		int MaxWaitEvaluations)
	{
		public static MusterTuning Default { get; } = new(
			// Shorter than this and the force is already effectively in contact: the staging
			// point would be behind most of it, and sending units backwards to reach it costs
			// more than the stagger it would save. The march that lost badland-ridges was
			// 68-75 cells.
			StageBeyondUnits: 24 * 1024,

			// How far short of their base to gather. Outside every reach in the ruleset —
			// msam 11 cells, sam 10, atwr 8 air / 7 ground, gtwr 6 — so the army forms up
			// where nothing static can shoot it. Also comfortably outside AttackBaseMode's
			// five-cell ArrivedRadius, which matters more than it looks: a unit standing on
			// the remembered cell drops the sighting for the whole side, so a lone fast unit
			// that never reaches it can no longer cancel everyone else's attack.
			StandoffUnits: 14 * 1024,

			// Arrival tolerance for the staging cell, and the radius that counts allies as
			// "formed up with us". One number does both jobs deliberately: the crowd that
			// releases the push is the same crowd that will arrive with it.
			MusterRadiusUnits: 6 * 1024,

			// Consecutive evaluations with nobody new joining before the muster is called
			// complete. An evaluation is about 1.4 game seconds, so this is roughly 22s.
			//
			// It has to clear the largest gap between two speed classes arriving, or the fast
			// half leaves before the slow half lands. Over the 54-cell approach this replaces
			// (68 cells less the standoff) the classes arrive at jeep 15s, mtnk 22s, e2 33s,
			// e1 41s, e3 57s: the widest gap is e1 to e3 at 16 seconds.
			PatienceEvaluations: 16,

			// Hard cap on standing still, about 105 seconds. The muster releases on its own
			// long before this; this exists so that a push can never be deadlocked by one that
			// is never going to be joined.
			MaxWaitEvaluations: 75);
	}

	/// <summary>What the staging rule decided to do with one unit.</summary>
	public enum MusterVerdict
	{
		/// <summary>No staging opinion — the normal assault rule applies.</summary>
		NotRequired,

		/// <summary>Walk to the staging point and form up.</summary>
		MoveToMuster,

		/// <summary>Formed up; wait for the rest of the army.</summary>
		WaitForTheRest,

		/// <summary>The muster is complete. Go in.</summary>
		Release,
	}

	/// <summary>Everything the staging rule needs, with no engine types in it.</summary>
	public readonly record struct MusterState(
		bool HasTarget,
		bool CanMove,
		bool ObjectiveInRange,
		int DistanceToTargetUnits,
		int MusterX,
		int MusterY,
		int DistanceToMusterUnits,
		int AlliesNearbyCount);

	/// <summary>
	/// What one unit has seen while forming up. Carried by the mode between evaluations and
	/// advanced here, so the counting is as testable as the rule that reads it.
	/// </summary>
	public readonly record struct MusterWatchdog(
		int PeakAllies,
		int StaleEvaluations,
		int WaitedEvaluations,
		bool Released)
	{
		/// <summary>A unit that has not started forming up yet.</summary>
		public static MusterWatchdog Start { get; } = new(0, 0, 0, false);
	}

	/// <summary>A staging decision plus the watchdog state that produced it.</summary>
	/// <remarks>
	/// A null <see cref="Decision"/> means the staging rule has no opinion and
	/// <see cref="AttackBaseLogic"/> should answer instead.
	/// </remarks>
	public readonly record struct MusterOutcome(UnitDecision? Decision, MusterWatchdog Watchdog);

	/// <summary>
	/// Pure decision logic for gathering an assault before it goes in.
	/// </summary>
	/// <remarks>
	/// Every unit used to be sent at the enemy base independently, the instant the doctrine
	/// flipped, from wherever it happened to be standing. Across a map-length approach that is
	/// not one attack, it is one attack per speed class: this bot's units run from 0.952 cells a
	/// second (<c>e3</c>) to 3.54 (<c>jeep</c>), a 3.7x spread, so a 68-cell march arrives over
	/// about forty seconds.
	/// <para>
	/// On badland-ridges that is exactly what happened. 46 units were committed at 475s; the
	/// <c>jeep</c> was attacking their base alone at 487s and the <c>mtnk</c> alone at 520s,
	/// while the 27 <c>e3</c> that were two-thirds of the army's value were still 25-32 cells
	/// out at 528s. At 517s the column spanned 32.2 cells. The army was destroyed a class at a
	/// time: 46 units and 12,140 credits lost between 475s and 610s for 18 kills, 31.6% of
	/// everything the bot spent all match.
	/// </para>
	/// <para>
	/// So the force forms up short of their base and goes in together. The release condition is
	/// deliberately not an army-size constant — it is "the crowd around me has stopped growing",
	/// which self-tunes to however many units this push actually has and cannot be wrong about
	/// the size of an army it never counted.
	/// </para>
	/// This type has ZERO OpenRA dependencies by design and is integer-only, so it is both
	/// lockstep-safe and testable without booting the engine.
	/// </remarks>
	public static class AssaultStagingLogic
	{
		public static MusterOutcome Decide(
			in AssaultState assault,
			in MusterState muster,
			in MusterWatchdog watchdog,
			in MusterTuning tuning)
		{
			// An unarmed unit has no business in an assault at all. AttackBaseLogic deliberately
			// leaves it where it stands rather than marching it into the enemy base to die, and
			// walking it to the staging point would undo exactly that.
			if (!assault.HasWeapon)
				return new MusterOutcome(null, watchdog);

			var verdict = Judge(muster, watchdog, tuning);

			switch (verdict)
			{
				case MusterVerdict.MoveToMuster:
					// Deliberately does not advance the watchdog: the clocks in it measure
					// standing still at the staging point, and a slow unit still crossing the
					// map must not spend the army's patience on its own walk.
					return new MusterOutcome(
						UnitDecision.MoveTo(muster.MusterX, muster.MusterY,
							$"forming up {muster.DistanceToTargetUnits}u short of their base"),
						watchdog);

				case MusterVerdict.WaitForTheRest:
				{
					var seen = Observe(watchdog, muster, tuning);

					// Holding is not the same as not shooting. A stationary unit gives up no
					// forward progress by firing on whatever is already inside its weapon
					// range, and the last thing this bot needs is another army that stood
					// still under fire because it had not been told to shoot back.
					var target = AttackBaseLogic.SelectLastStandTarget(assault);
					if (target.HasValue)
						return new MusterOutcome(
							UnitDecision.Attack(target.Value.ActorId,
								$"mustering, engaging {target.Value.Kind} at {target.Value.DistanceUnits}u"),
							seen);

					return new MusterOutcome(
						UnitDecision.Hold($"mustering, {muster.AlliesNearbyCount} with us after {seen.WaitedEvaluations} evaluations"),
						seen);
				}

				case MusterVerdict.Release:
					// Latch it. A released unit that walked past the staging point must never
					// decide it is out of radius and turn round, or the assault becomes a shuttle.
					return new MusterOutcome(null, watchdog with { Released = true });

				default:
					return new MusterOutcome(null, watchdog);
			}
		}

		/// <summary>The staging verdict for one unit, with no side effects.</summary>
		public static MusterVerdict Judge(in MusterState s, in MusterWatchdog watchdog, in MusterTuning t)
		{
			// Already let go: this push does not form up twice.
			if (watchdog.Released)
				return MusterVerdict.NotRequired;

			// Nowhere to stage toward, or no way to get there.
			if (!s.HasTarget || !s.CanMove)
				return MusterVerdict.NotRequired;

			// Already shooting at the thing we were sent to kill. Walking away from a target
			// inside weapon range to go and stand in a field is not caution, it is a bug.
			if (s.ObjectiveInRange)
				return MusterVerdict.NotRequired;

			// A short approach does not spread a force out, and the staging point would be
			// behind the unit rather than in front of it.
			if (s.DistanceToTargetUnits <= t.StageBeyondUnits)
				return MusterVerdict.NotRequired;

			// Already inside their perimeter — committed. Walking back out to form up would
			// cross the defended ground twice.
			if (s.DistanceToTargetUnits <= t.StandoffUnits)
				return MusterVerdict.NotRequired;

			// The backstop that keeps a preference from becoming a demand that silently fails.
			if (watchdog.WaitedEvaluations >= t.MaxWaitEvaluations)
				return MusterVerdict.Release;

			if (s.DistanceToMusterUnits > t.MusterRadiusUnits)
				return MusterVerdict.MoveToMuster;

			// Nobody new for a while: either the army is all here, or the rest of it is not
			// coming. Both mean go.
			return watchdog.StaleEvaluations >= t.PatienceEvaluations
				? MusterVerdict.Release
				: MusterVerdict.WaitForTheRest;
		}

		/// <summary>
		/// Advances the watchdog. The staleness clock resets whenever the crowd reaches a new
		/// high, so reinforcements still arriving always buy more time.
		/// </summary>
		/// <remarks>
		/// The peak is monotonic rather than the live count, so units drifting in and out of the
		/// radius — or dying — cannot keep resetting the clock and pin the army in place.
		/// </remarks>
		public static MusterWatchdog Observe(in MusterWatchdog watchdog, in MusterState s, in MusterTuning tuning)
		{
			var waited = watchdog.WaitedEvaluations >= tuning.MaxWaitEvaluations
				? tuning.MaxWaitEvaluations
				: watchdog.WaitedEvaluations + 1;

			if (s.AlliesNearbyCount > watchdog.PeakAllies)
				return watchdog with { PeakAllies = s.AlliesNearbyCount, StaleEvaluations = 0, WaitedEvaluations = waited };

			return watchdog with { StaleEvaluations = watchdog.StaleEvaluations + 1, WaitedEvaluations = waited };
		}

		/// <summary>
		/// Where the army forms up: a cell on the line from their base back towards ours,
		/// <see cref="MusterTuning.StandoffUnits"/> short of the target.
		/// </summary>
		/// <remarks>
		/// Derived from the side's own base rather than from the unit asking, so every unit in
		/// the push computes the same cell and they converge instead of each gathering its own
		/// private crowd.
		/// <para>
		/// Integer-only, matching <see cref="HarvesterLogic"/>: floating point in a decision that
		/// becomes an order is a lockstep hazard.
		/// </para>
		/// </remarks>
		public static (int X, int Y) MusterCell(int baseX, int baseY, int targetX, int targetY, int standoffCells)
		{
			var dx = baseX - targetX;
			var dy = baseY - targetY;

			var distance = IntSqrt(dx * dx + dy * dy);
			if (distance <= 0 || distance <= standoffCells)
				return (baseX, baseY);

			return (targetX + dx * standoffCells / distance, targetY + dy * standoffCells / distance);
		}

		/// <summary>Integer square root, rounded down. Newton's method on integers.</summary>
		public static int IntSqrt(int value)
		{
			if (value <= 0)
				return 0;

			var x = value;
			var y = (x + 1) / 2;
			while (y < x)
			{
				x = y;
				y = (x + value / x) / 2;
			}

			return x;
		}
	}
}
