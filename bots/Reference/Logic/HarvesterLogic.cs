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
	/// <summary>Tunable knobs for <see cref="HarvesterLogic"/>. Distances in world units unless named Cells.</summary>
	public readonly record struct HarvesterTuning(
		int PanicRadiusUnits,
		int SafeDistanceUnits,
		int FleeBelowHealthPercent,
		int PreemptiveThreatCount,
		int ThreatClearTicks,
		int StallEvaluations,
		int FirstProbeCells,
		int ProbeStepCells,
		int MaxProbeCells,
		int ProbeResetEvaluations,
		int MinFieldCells,
		int ReviewEvaluations,
		int MaxFieldsConsidered,
		int DistanceScaleUnits,
		int SwitchScoreMultiplier,
		int FieldMatchCells,
		int MaxHaulCells)
	{
		public static HarvesterTuning Default { get; } = new(
			PanicRadiusUnits: 7 * 1024,
			SafeDistanceUnits: 4 * 1024,

			// A harvester that runs from every scratch never finishes a load. On badland-ridges
			// one took damage=30 against health=99 at 354s — a single rifle burst, one percent of
			// its hit points — fled, and never harvested again. Below 70% it is genuinely being
			// taken apart and the load is not worth the harvester.
			FleeBelowHealthPercent: 70,

			// Proximity is not proof that a harvester is the target. Once it has taken damage,
			// two attack-capable contacts justify leaving before they remove the rest.
			PreemptiveThreatCount: 2,

			// A threat can briefly leave the panic radius while a harvester is still reaching
			// cover. Keep the retreat active long enough for that gap not to restart harvesting.
			ThreatClearTicks: 300,

			// Consecutive evaluations of "idle AND has not changed cell" before we intervene. A
			// harvester that is driving, cutting tiberium or unloading has a live activity and is
			// not idle, so this cannot fire on one that is still earning.
			StallEvaluations: 4,

			FirstProbeCells: 6,
			ProbeStepCells: 8,

			// The probe is now only used to reveal shroud, so it has to be able to leave the
			// area the engine's own search already covers. Anything at or below 24 cells is
			// inside the bubble that stalled the harvester in the first place.
			MaxProbeCells: 48,

			// Evaluations of healthy behaviour before the search ladder folds back to the start.
			ProbeResetEvaluations: 8,

			// The smallest patch worth crossing the map for. Below this a harvester spends more
			// time driving than cutting. It is also the "worked out" signal: a field ground down
			// below this many contiguous cells simply stops being returned by the scan, which is
			// a density-scale-independent way of noticing that the ground under a harvester has
			// gone, and works whatever a cell's maximum density happens to be in this mod.
			MinFieldCells: 4,

			// Evaluations between scheduled reviews of which field to work. Waiting for a stall
			// was the bug: a harvester grinding the last scraps of a dying patch is driving and
			// cutting, so it is never idle, so the stall watchdog never fires and the field
			// rule never runs. On badland-ridges it fired exactly once in 1,178 seconds — at
			// 839s, after the base was already being overrun — while income per harvester fell
			// from 15.9 credits a second at 180s to 4.8 at 720s. Reviewing on a clock catches
			// the decay. It is deliberately slow, because each review is a map walk and a field
			// does not die inside one window: a stalled harvester still gets an answer at once,
			// and a field ground below MinFieldCells is noticed by the next review either way.
			ReviewEvaluations: 32,

			// FindResourceFields walks every cell, so this caps what comes back, not the walk. It
			// returns nearest first, so a low cap drops the far fields — including, if it were
			// low enough, the one this harvester is currently assigned to, which would read as
			// "worked out" and drag it back inside the bubble it was sent out of.
			MaxFieldsConsidered: 16,

			// How far a harvester travels before distance costs it half its throughput. A
			// harvester cycles field to refinery and back at 1.758 cells a game second, so the
			// distance that matters is the refinery's, not the harvester's, and the scan is
			// centred on the refinery for exactly that reason.
			DistanceScaleUnits: 12 * 1024,

			// A further field has to be worth twice the one we are on before we move. That
			// margin is the whole anti-dither mechanism: after a switch the new field is the
			// best one, so the field we just left cannot immediately beat it twice over.
			SwitchScoreMultiplier: 2,

			// How far a flood-filled patch centre may drift between scans and still count as the
			// same field. A patch's centre moves as it is mined; this keeps that from reading as
			// a brand new field every review.
			FieldMatchCells: 6,

			// How far from its refinery a harvester will be *sent*, when anything closer exists.
			//
			// The score below is a hyperbola, so it discounts distance and never refuses it. On
			// badland-ridges that walked the fleet off the map: the field reasons went 12, 17,
			// 11, 18 cells out for the first four minutes, then 27-35 cells from 447s, and at
			// 839s the rule crossed to a field 62-71 cells out at (59,35) — nine cells from the
			// enemy refinery at (62,37). Enemy e3 killed three of the bot's four harvesters at
			// 888s, 890s and 896s at (45,43), (49,39) and (53,39), 45 to 56 cells from its own
			// construction yard at (13,82). Income fell from 43.3 credits a second across
			// 300-840s to 1.56 across 840-1289s, and the bot never built another structure.
			//
			// Thirty-six cells, from the throughput the trade is actually made on. A harvester
			// moves 1.758 cells a game second, so the round trip is 2d/1.758 = 1.14d seconds: 15
			// seconds at the 13-cell fields this bot worked safely, 41 at 36 cells, 71 at 62. A
			// load is roughly 700 credits and the measured rate over 300-840s was 10.8 credits a
			// second per harvester, so a cycle was about 65 seconds of which 15 was travel. At 36
			// cells the cycle is 91 seconds and the rate 7.7 — 71% of the near-field rate, which
			// is where a further field stops being an economy and becomes a commute. At 62 cells
			// it is 121 seconds and 5.8, 54%, before any risk is counted at all.
			//
			// The bound it must not cross: it has to admit every field this bot worked without
			// losing a harvester, which ran out to 35 cells, and exclude the one that killed
			// three, which began at 61. Thirty-six is the smallest number above the first and the
			// largest below the second. It is a preference rather than a rule — see
			// SelectFieldExcept — so a map with nothing inside it behaves exactly as before.
			MaxHaulCells: 36);
	}

	/// <summary>One tiberium field, as the harvester rule needs to see it.</summary>
	/// <remarks>
	/// A copy rather than the SDK's own type, because sensing methods reuse their buffers and
	/// nothing here may retain one, and because the judgement below has to stay engine-free.
	/// <c>DistanceUnits</c> is measured from whatever origin the scan was centred on — the
	/// refinery, for a harvester that has one.
	/// </remarks>
	public readonly record struct FieldOption(
		int NearestX,
		int NearestY,
		int CenterX,
		int CenterY,
		int CellCount,
		int TotalDensity,
		int DistanceUnits);

	/// <summary>Everything the harvester rule needs, with no engine types in it.</summary>
	public readonly record struct HarvesterState(
		int HealthPercent,
		bool CanMove,
		bool IsIdle,
		bool DangerNearby,
		int NearbyThreatCount,
		int WorldTick,
		bool HasThreatCenter,
		int ThreatCenterX,
		int ThreatCenterY,
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
	/// What one harvester has been seen doing, and which field it has been told to work. Carried
	/// by the mode between evaluations, advanced here so the counting is as readable as the rule
	/// that reads it.
	/// </summary>
	public readonly record struct HarvesterWatchdog(
		int LastX,
		int LastY,
		int StillEvaluations,
		int ProbeIndex,
		int MovingEvaluations,
		int EvaluationsSinceScan,
		int AssignedX,
		int AssignedY,
		int ContestedX,
		int ContestedY,
		bool Retreating,
		bool PreemptiveWithdrawalSpent,
		int LastDangerTick,
		int RetreatX,
		int RetreatY)
	{
		/// <summary>
		/// A harvester we have never seen. The impossible cell counts as "moved", and the scan
		/// counter starts due so the very first evaluation picks a field rather than waiting a
		/// review window to do it.
		/// </summary>
		public static HarvesterWatchdog Start { get; } =
			new(
				int.MinValue, int.MinValue, 0, 0, 0, int.MaxValue,
				int.MinValue, int.MinValue, int.MinValue, int.MinValue,
				false, false, 0, int.MinValue, int.MinValue);

		/// <summary>Whether this harvester has been told which field to work.</summary>
		public bool HasAssignment => AssignedX != int.MinValue;

		/// <summary>Whether this harvester has been shot off a field and remembers which one.</summary>
		public bool HasContested => ContestedX != int.MinValue;

		/// <summary>Whether this harvester has a stable tactical escape destination.</summary>
		public bool HasRetreatTarget => RetreatX != int.MinValue;
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
	/// a harvester that is provably stopped is always restarted.
	/// </para>
	/// <para>
	/// <b>Restarting means naming a field.</b> The engine's own search is radius-capped — twelve
	/// cells from the last cell it cut, or twenty-four from the refinery — and it never widens.
	/// Once the ground inside that bubble is gone the harvester waits, re-searches the same dead
	/// bubble, and waits again for the rest of the match, which is what "the tiberium runs out
	/// and the harvesters sit at home" actually is. Nothing the harvester does by itself escapes
	/// it. So when one stalls we read the resource layer through
	/// <c>ModeContext.FindResourceFields</c>, pick the nearest field that still has tiberium in
	/// it however far away that is, and issue a <see cref="UnitAction.Harvest"/> order at it,
	/// which re-centres the engine's bubble on the new field.
	/// </para>
	/// <para>
	/// The widening probe underneath is now only the shroud case: with fog on we cannot see
	/// tiberium we have never explored, so a harvester that knows of no field at all walks
	/// outward to reveal one. It is a last resort rather than the mechanism.
	/// </para>
	/// <para>
	/// <b>And naming a field has to happen on a clock, not only after a stall.</b> The stall
	/// watchdog can only fire on a harvester that is idle and has not moved, and a harvester
	/// working the last scraps of a dying patch is doing neither — it is driving further and
	/// further for less and less inside the engine's own bubble. On badland-ridges it fired once
	/// in 1,178 seconds while income per harvester fell from 15.9 credits a second at 180s to
	/// 4.8 at 720s, and the fleet earned 27,760 credits all match against a winner who finished
	/// with 40,800 of army and 36 buildings still standing. So the field is now reviewed every
	/// <c>ReviewEvaluations</c>, and a harvester moves when the ground it is on has stopped
	/// being the best ground it can reach.
	/// </para>
	/// This type has ZERO OpenRA dependencies by design and is integer-only, so it is both
	/// lockstep-safe and readable without booting the engine.
	/// </remarks>
	public static class HarvesterLogic
	{
		// An octagon, in tenths, so the ladder fans out without floating point.
		static readonly int[] DirX = [10, 7, 0, -7, -10, -7, 0, 7];
		static readonly int[] DirY = [0, 7, 10, 7, 0, -7, -10, -7];

		/// <summary>
		/// Whether this evaluation should pay for a resource scan. Asked by the mode before
		/// <see cref="Decide"/> and re-derived identically inside it, so the two cannot disagree
		/// about whether <c>fields</c> is meaningful.
		/// </summary>
		/// <remarks>
		/// <c>FindResourceFields</c> walks the map rather than looking something up, so this is
		/// deliberately stingy: a harvester that has provably stopped needs an answer now, and
		/// one that is working needs one every review window.
		/// <para>
		/// <b>It has to test the observation this evaluation is about to make, not the one before
		/// it.</b> That is not a detail, it is the whole rule. This used to read the incoming
		/// watchdog, whose <c>StillEvaluations</c> is one behind: on the evaluation where a
		/// harvester finally crosses <see cref="HarvesterTuning.StallEvaluations"/> the incoming
		/// count is still one short, so the stall term read false — and because every branch that
		/// fires on a stall resets the count to zero, the incoming count could never reach the
		/// threshold on any later evaluation either. The stall term was unreachable code. A
		/// stalled harvester was therefore never scanned for, <c>fields</c> was empty at the one
		/// moment it mattered, and the rule fell past every field branch into the shroud probe
		/// underneath. On badland-ridges that was <b>245 blind move orders against 3 re-cut
		/// orders</b>, all of them after 840s, while income per harvester fell from 10.3 credits
		/// a second to 4.2 with more harvesters standing than during the good window.
		/// </para>
		/// </remarks>
		public static bool ShouldScan(in HarvesterWatchdog watchdog, in HarvesterState state, in HarvesterTuning tuning) =>
			DueForScan(Observe(watchdog, state, tuning), tuning);

		/// <summary>The scan test itself, over an already-advanced watchdog.</summary>
		/// <remarks>
		/// Private and shared so <see cref="ShouldScan"/> and <see cref="Decide"/> cannot drift
		/// apart about whether <c>fields</c> is meaningful — the drift above was exactly that,
		/// expressed as two different watchdogs rather than two different tests.
		/// </remarks>
		static bool DueForScan(in HarvesterWatchdog observed, in HarvesterTuning tuning) =>
			observed.StillEvaluations >= tuning.StallEvaluations
			|| observed.EvaluationsSinceScan >= tuning.ReviewEvaluations;

		public static HarvesterOutcome Decide(in HarvesterState state, in HarvesterWatchdog watchdog, in HarvesterTuning tuning, IReadOnlyList<FieldOption> fields)
		{
			var seen = Observe(watchdog, state, tuning);
			var scanned = DueForScan(seen, tuning);
			seen = seen with
			{
				EvaluationsSinceScan = scanned
					? 0
					: (seen.EvaluationsSinceScan >= tuning.ReviewEvaluations ? tuning.ReviewEvaluations : seen.EvaluationsSinceScan + 1)
			};

			if (state.DangerNearby)
				seen = seen with { LastDangerTick = state.WorldTick };

			// Nothing we order can help a harvester that cannot move.
			if (!state.CanMove)
				return new HarvesterOutcome(UnitDecision.Continue, seen);

			var awayFromRefinery = state.HasRefinery && state.DistanceToRefineryUnits > tuning.SafeDistanceUnits;
			var lowHealthDanger = state.HealthPercent < tuning.FleeBelowHealthPercent;
			var completedBoundedWithdrawal = false;

			// 1. Leave concentrated immediate threats before sustained fire makes escape
			//    impossible. The short panic radius limits this to enemies that can hit the
			//    harvester, while the health threshold keeps the existing response to one
			//    attacker that is already doing serious damage.
			//
			//    Running home was only ever half a rule, and the missing half lost badland-ridges.
			//    A flee order changes where the harvester *is* and leaves where it has been *sent*
			//    exactly as it was, so once the shooting stops the field rule looks at a patch
			//    that is still full of tiberium, still the best thing in reach, and still
			//    assigned — and drives the harvester straight back into the same rockets. That is
			//    not a hypothesis: this bot issued 44 "hurt at" orders across six harvesters and
			//    lost all six to enemy e3, ten of its losses inside one 5-cell circle around a
			//    single field. Income stopped dead at 660s of a 1,041-second match and the last
			//    393 seconds were played with no harvester at all, at 21.6 credits a second
			//    against a prior median of 35.2.
			//
			//    So fleeing now also *forgets the ground*: the assigned field is recorded as
			//    contested, and the scan clock is wound forward so the next evaluation pays for a
			//    scan instead of waiting out a review window. Rule 2 then reassigns away from it.
			//
			//    A harvester that has actually taken damage gets one refinery-screened escape
			//    before the low-health rule takes over. Nearby contacts alone do not prove they
			//    are attacking it: repeatedly fleeing from an undamaged screen cancels every
			//    harvest attempt and can suppress income without costing the harvester a hit.
			//    It shelters only after no attacker remains inside the panic radius. If an
			//    attacker can still hit it at the endpoint, the next leg starts from the
			//    harvester instead of the refinery; re-anchoring each leg to the refinery can
			//    reverse the escape as the threat centre moves.
			if (seen.Retreating)
			{
				var reachedTarget = seen.HasRetreatTarget
					&& IsNear(state.X, state.Y, seen.RetreatX, seen.RetreatY);
				var retreatStillActive = state.DangerNearby
					|| state.WorldTick - seen.LastDangerTick <= tuning.ThreatClearTicks;
				if (seen.HasRetreatTarget && !reachedTarget && retreatStillActive)
					return new HarvesterOutcome(
						UnitDecision.MoveTo(seen.RetreatX, seen.RetreatY, "harvester bounded threat escape en route"),
						seen with { StillEvaluations = 0, EvaluationsSinceScan = tuning.ReviewEvaluations });

				if (reachedTarget && state.DangerNearby)
				{
					var escape = ThreatEscapeCell(state, tuning, preserveRefineryAccess: false);
					return new HarvesterOutcome(
						UnitDecision.MoveTo(escape.X, escape.Y, "harvester extending escape while immediate threat remains"),
						seen with
						{
							StillEvaluations = 0,
							EvaluationsSinceScan = tuning.ReviewEvaluations,
							RetreatX = escape.X,
							RetreatY = escape.Y
						});
				}

				if (reachedTarget && retreatStillActive)
					return new HarvesterOutcome(
						UnitDecision.Hold("harvester sheltering after immediate threat clears"),
						seen with { StillEvaluations = 0, EvaluationsSinceScan = tuning.ReviewEvaluations });

				completedBoundedWithdrawal =
					reachedTarget && seen.PreemptiveWithdrawalSpent;
				seen = seen with { Retreating = false, RetreatX = int.MinValue, RetreatY = int.MinValue };
			}

			// The escape re-arms only after repair; the clear radius created by the retreat
			// itself is not evidence that the next nearby contacts are a new attack.
			if (!seen.Retreating
				&& !state.DangerNearby
				&& state.WorldTick - seen.LastDangerTick > tuning.ThreatClearTicks
				&& state.HealthPercent >= 100
				&& !completedBoundedWithdrawal)
				seen = seen with { PreemptiveWithdrawalSpent = false };

			var damageConfirmedDanger =
				state.HealthPercent < 100
				&& !lowHealthDanger
				&& state.NearbyThreatCount >= tuning.PreemptiveThreatCount
				&& !seen.PreemptiveWithdrawalSpent;
			if (state.DangerNearby
				&& (lowHealthDanger || damageConfirmedDanger))
			{
				var drivenOff = seen.HasAssignment
					? seen with { ContestedX = seen.AssignedX, ContestedY = seen.AssignedY }
					: seen;
				drivenOff = drivenOff with
				{
					PreemptiveWithdrawalSpent =
						drivenOff.PreemptiveWithdrawalSpent || damageConfirmedDanger
				};

				if (state.HasThreatCenter)
				{
					var escape = ThreatEscapeCell(state, tuning);
					return new HarvesterOutcome(
						UnitDecision.MoveTo(
							escape.X,
							escape.Y,
							damageConfirmedDanger
								? escape.PreservesRefineryAccess
									? $"harvester threat-vector withdrawal preserving refinery access, damage-confirmed harvester withdrawal at {state.HealthPercent}%"
									: $"harvester threat-vector withdrawal, damage-confirmed harvester withdrawal at {state.HealthPercent}%"
								: escape.PreservesRefineryAccess
									? $"harvester threat-vector withdrawal preserving refinery access at {state.HealthPercent}%"
									: $"harvester threat-vector withdrawal at {state.HealthPercent}%"),
						drivenOff with
						{
							StillEvaluations = 0,
							EvaluationsSinceScan = tuning.ReviewEvaluations,
							Retreating = true,
							RetreatX = escape.X,
							RetreatY = escape.Y
						});
				}

				if (!state.HasRefinery)
					return new HarvesterOutcome(UnitDecision.Continue, drivenOff);

				var retreating = drivenOff with
				{
					StillEvaluations = 0,
					EvaluationsSinceScan = tuning.ReviewEvaluations,
					Retreating = true,
					RetreatX = state.RefineryX,
					RetreatY = state.RefineryY
				};

				if (awayFromRefinery)
					return new HarvesterOutcome(
						UnitDecision.MoveTo(state.RefineryX, state.RefineryY,
							damageConfirmedDanger
								? $"damage-confirmed harvester withdrawal to refinery at {state.HealthPercent}%"
								: $"hurt at {state.HealthPercent}%, running to the refinery"),
						retreating);

				// Do not fall through to the due field scan while the same raid is still in
				// range. That immediately replaces the flee order with Harvest and sends a
				// critically damaged harvester back out before its escorts clear the threat.
				return new HarvesterOutcome(
					UnitDecision.MoveTo(state.RefineryX, state.RefineryY, "damaged harvester sheltering at refinery"),
					retreating);
			}

			var stalled = seen.StillEvaluations >= tuning.StallEvaluations;
			var count = fields?.Count ?? 0;

			if (completedBoundedWithdrawal && scanned && count > 0)
			{
				var contested = FindContested(fields, seen, tuning);
				var best = SelectFieldAvoiding(fields, -1, contested, tuning);
				var f = fields[best];
				return Assign(
					f,
					seen,
					$"harvester resumed economy after bounded threat withdrawal, harvesting {f.CellCount} cells ({f.TotalDensity} left) {f.DistanceUnits / 1024} cells out");
			}

			// 2. Choosing the ground. Only on a review, because this is the half that costs a scan.
			if (scanned && count > 0)
			{
				var assigned = FindAssigned(fields, watchdog, tuning);
				var contested = FindContested(fields, seen, tuning);
				var best = SelectFieldAvoiding(fields, -1, contested, tuning);

				// Worked out: the patch we were sent to no longer holds MinFieldCells of anything,
				// so the scan does not return it any more and there is nothing left to go back to.
				if (assigned < 0)
				{
					var f = fields[best];
					var why = watchdog.HasAssignment ? "field worked out" : "picking a field";
					return Assign(f, seen, $"{why}, harvesting {f.CellCount} cells ({f.TotalDensity} left) {f.DistanceUnits / 1024} cells out");
				}

				// Still there, but no longer worth staying on: something within reach holds enough
				// more tiberium to pay for the drive twice over.
				//
				// ...or we are simply too far out. A harvester already assigned past
				// MaxHaulCells comes home for any field inside it, whatever the scores say,
				// because the score that sent it there is the score that would keep it there.
				// This can only ever fire in the direction of home — it needs the assigned field
				// outside the ceiling and the candidate inside it — so it cannot dither: once
				// home, the same pair fails the test forever.
				//
				// ...or we were shot off it. That one outranks the scores outright: what a field
				// holds is worth nothing to a harvester that does not survive to carry it, and
				// the score cannot see the infantry standing on it. It cannot dither either,
				// because the exclusion travels with the harvester — after the switch the
				// contested field is still excluded, so the pair can never swap back.
				var evicted = assigned == contested;
				var assignedScore = Score(fields[assigned], tuning);
				var bestScore = Score(fields[best], tuning);
				var comingHome = TooFarToHaul(fields[assigned], tuning) && !TooFarToHaul(fields[best], tuning);
				if (best != assigned && (evicted || comingHome || bestScore >= (long)assignedScore * tuning.SwitchScoreMultiplier))
				{
					var f = fields[best];
					var why = evicted
						? $"driven off that field, working {f.TotalDensity} left {f.DistanceUnits / 1024} cells out instead"
						: comingHome
							? $"field {fields[assigned].DistanceUnits / 1024} cells out is past the {tuning.MaxHaulCells} cell haul limit, coming back to {f.TotalDensity} left {f.DistanceUnits / 1024} cells out"
							: $"field thinning to {fields[assigned].TotalDensity}, crossing to {f.TotalDensity} left {f.DistanceUnits / 1024} cells out";

					return Assign(f, seen, why);
				}

				if (stalled)
				{
					// 3. On the right field and provably stopped: re-aim at its nearest live
					//    cell, which re-centres the engine's own search and restarts the
					//    harvester. Marked so a second stall does not re-issue the same order —
					//    the host suppresses a duplicate, so repeating it cannot restart
					//    anything.
					if (seen.ProbeIndex == 0)
					{
						var f = fields[assigned];
						return Assign(f, seen,
							$"stopped for {seen.StillEvaluations} evaluations, re-cutting {f.TotalDensity} left {f.DistanceUnits / 1024} cells out",
							probeIndex: 1);
					}

					// 3b. Re-cutting did not start it either, so this ground is unreachable or
					//     contested whatever the scan says is still in it. Any other field is a
					//     different order rather than a suppressed duplicate, and walking to
					//     tiberium we can see beats walking into shroud to look for some.
					//     Assign() moves the remembered assignment with it, so the next stall
					//     excludes this field in turn and the harvester works outward through
					//     what is left instead of grinding on one dead patch.
					var other = SelectFieldAvoiding(fields, assigned, contested, tuning);
					if (other >= 0)
					{
						var f = fields[other];
						return Assign(f, seen,
							$"stopped for {seen.StillEvaluations} evaluations, giving that field up for {f.TotalDensity} left {f.DistanceUnits / 1024} cells out",
							probeIndex: 1);
					}
				}
			}

			// 4. Still earning, or not yet provably stopped: leave it alone.
			if (!stalled)
				return new HarvesterOutcome(UnitDecision.Continue, seen);

			// 5. Stopped, and naming a field has not helped — either nothing was scanned this
			//    evaluation, or nothing is known anywhere, or re-cutting the assigned field
			//    already failed. Try the cheap explanation first: a cancelled delivery, with a
			//    full hold and nowhere it thinks it should take it.
			if (seen.ProbeIndex == 0 && awayFromRefinery)
				return new HarvesterOutcome(
					UnitDecision.MoveTo(state.RefineryX, state.RefineryY, $"stopped for {seen.StillEvaluations} evaluations, returning to the refinery"),
					seen with { StillEvaluations = 0, ProbeIndex = 1 });

			// 6. Nothing known anywhere: the scan above ran — a stall always scans now — and came
			//    back with no field at all, so every patch this side has *explored* is mined out.
			//    That is a scouting problem rather than a map problem, and walking outward in
			//    widening steps is the only thing a harvester can do about it alone.
			var probe = seen.ProbeIndex < 1 ? 1 : seen.ProbeIndex;
			var target = ProbeCell(state, probe, tuning, out var cells);

			return new HarvesterOutcome(
				UnitDecision.MoveTo(target.X, target.Y,
					$"stopped for {seen.StillEvaluations} evaluations, no tiberium in sight, searching {cells} cells out"),
				seen with { StillEvaluations = 0, ProbeIndex = probe + 1 });
		}

		static bool IsNear(int x, int y, int targetX, int targetY) =>
			x >= targetX - 1 && x <= targetX + 1
			&& y >= targetY - 1 && y <= targetY + 1;

		/// <summary>
		/// Sends the harvester to a field and remembers which one, so the next review can tell
		/// "still on it" from "it is gone".
		/// </summary>
		/// <remarks>
		/// A <see cref="UnitAction.Harvest"/> order rather than a move: it delivers a full load on
		/// the way and then keeps working the new field on its own, whereas a move order would
		/// park the harvester on the tiberium and stop it. The order names the field's nearest
		/// cell rather than its centre, because the centre drives the harvester through the field
		/// to the far side. The <em>identity</em> we keep is the centre, because that is what
		/// stays put between scans while the nearest cell walks in as the edge is cut away.
		/// </remarks>
		static HarvesterOutcome Assign(in FieldOption field, in HarvesterWatchdog seen, string reason, int probeIndex = 0) =>
			new(UnitDecision.Harvest(field.NearestX, field.NearestY, reason),
				seen with { StillEvaluations = 0, ProbeIndex = probeIndex, AssignedX = field.CenterX, AssignedY = field.CenterY });

		/// <summary>Picks a threat-opposite escape cell without abandoning a live refinery.</summary>
		static (int X, int Y, bool PreservesRefineryAccess) ThreatEscapeCell(
			in HarvesterState state, in HarvesterTuning tuning, bool preserveRefineryAccess = true)
		{
			// Repeated local vectors can ratchet a damaged harvester farther from the only place
			// that can unload it. A live refinery instead anchors a dispersed screen: the visible
			// threat still selects the far side, but every completed leg can resume deliveries.
			var preservesRefineryAccess = preserveRefineryAccess && state.HasRefinery;
			var originX = preservesRefineryAccess ? state.RefineryX : state.X;
			var originY = preservesRefineryAccess ? state.RefineryY : state.Y;
			var cells = preservesRefineryAccess
				? (tuning.SafeDistanceUnits + 1023) / 1024
				: (tuning.PanicRadiusUnits + 1023) / 1024 + 1;
			if (cells < 1)
				cells = 1;

			var bestX = state.X;
			var bestY = state.Y;
			long bestDistance = -1;

			for (var i = 0; i < DirX.Length; i++)
			{
				var x = Clamp(originX + cells * DirX[i] / 10, state.MapMinX, state.MapMaxX);
				var y = Clamp(originY + cells * DirY[i] / 10, state.MapMinY, state.MapMaxY);
				if (x == state.X && y == state.Y)
					continue;

				var dx = x - state.ThreatCenterX;
				var dy = y - state.ThreatCenterY;
				var distance = (long)dx * dx + (long)dy * dy;
				if (distance <= bestDistance)
					continue;

				bestDistance = distance;
				bestX = x;
				bestY = y;
			}

			return (bestX, bestY, preservesRefineryAccess);
		}

		/// <summary>
		/// What a field is worth to a harvester: what is left in it, discounted by how far the
		/// round trip is.
		/// </summary>
		/// <remarks>
		/// Deliberately scale-free in density, because nothing here knows what a full cell is
		/// worth in this mod and a hard "is it empty yet" threshold would be a guess. The
		/// discount is a hyperbola rather than a cliff: a field at
		/// <see cref="HarvesterTuning.DistanceScaleUnits"/> is worth half its tiberium, one at
		/// three times that a quarter — so a field three times as far has to hold four times as
		/// much to win, which is about the trade a unit moving 1.758 cells a second is making.
		/// <para>
		/// A hyperbola discounts distance and never refuses it, which is how this rule once sent
		/// harvesters 62 cells to mine beside the enemy refinery. The refusal lives in
		/// <see cref="SelectFieldExcept"/> and <see cref="HarvesterTuning.MaxHaulCells"/>, not
		/// here, so the score stays a pure statement of what a field is worth.
		/// </para>
		/// </remarks>
		public static int Score(in FieldOption field, in HarvesterTuning tuning)
		{
			if (field.TotalDensity <= 0)
				return 0;

			var scale = tuning.DistanceScaleUnits;
			var distance = field.DistanceUnits < 0 ? 0 : field.DistanceUnits;
			return (int)((long)field.TotalDensity * scale / (scale + distance));
		}

		/// <summary>The best field to be working. Never returns -1 for a non-empty list.</summary>
		public static int SelectField(IReadOnlyList<FieldOption> fields, in HarvesterTuning tuning)
			=> SelectFieldExcept(fields, -1, tuning);

		/// <summary>
		/// The best field other than <paramref name="exclude"/>, or -1 if there is no other one.
		/// </summary>
		/// <remarks>
		/// The escape hatch for a harvester that has been re-aimed at the field it is already on
		/// and still has not moved. Somewhere it can see tiberium is always a better answer than
		/// the shroud probe, and naming a *different* field is the only re-order the host will
		/// not suppress as a duplicate of the one it is already carrying out.
		/// <para>
		/// <b>A preference with a fallback, not a demand.</b> The first pass looks only inside
		/// <see cref="HarvesterTuning.MaxHaulCells"/>, because a field further out than that is a
		/// commute the harvester rarely survives — three of this bot's four died 45 to 56 cells
		/// from home on the way to one 62 cells out. The second pass is the old rule exactly, so
		/// a map whose tiberium is all distant is unchanged: the ceiling can refuse ground, but
		/// it can never refuse the last ground there is.
		/// </para>
		/// </remarks>
		public static int SelectFieldExcept(IReadOnlyList<FieldOption> fields, int exclude, in HarvesterTuning tuning)
			=> SelectFieldAvoiding(fields, exclude, -1, tuning);

		/// <summary>
		/// The best field other than <paramref name="exclude"/>, preferring to leave
		/// <paramref name="avoid"/> alone as well.
		/// </summary>
		/// <remarks>
		/// <paramref name="exclude"/> is a hard exclusion and <paramref name="avoid"/> is a soft
		/// one, and the difference is the whole point. <paramref name="exclude"/> is the field we
		/// have just proved we cannot work; <paramref name="avoid"/> is the field we were shot
		/// off, which is still perfectly good ground once whatever was standing on it moves on or
		/// dies. Tiberium is finite and enemies are not stationary, so a hard ban would eventually
		/// hand the map away one patch at a time, and a harvester that starves because every
		/// field it knows is dangerous has lost exactly as much as one that is shot.
		/// <para>
		/// So the avoidance is tried first and dropped if it leaves nothing. Same shape as the
		/// haul ceiling above, for the same reason: this rule may refuse ground, but it may never
		/// refuse the last ground there is.
		/// </para>
		/// </remarks>
		public static int SelectFieldAvoiding(
			IReadOnlyList<FieldOption> fields, int exclude, int avoid, in HarvesterTuning tuning)
		{
			if (avoid >= 0)
			{
				var elsewhere = SelectWithin(fields, exclude, avoid, tuning);
				if (elsewhere >= 0)
					return elsewhere;
			}

			return SelectWithin(fields, exclude, -1, tuning);
		}

		/// <summary>The haul-ceiling preference, over whatever exclusions it is given.</summary>
		static int SelectWithin(
			IReadOnlyList<FieldOption> fields, int exclude, int avoid, in HarvesterTuning tuning)
		{
			var near = BestWithin(fields, exclude, avoid, tuning, HaulCeilingUnits(tuning));
			return near >= 0 ? near : BestWithin(fields, exclude, avoid, tuning, int.MaxValue);
		}

		/// <summary>How far out a harvester will be sent while anything closer is on offer.</summary>
		public static int HaulCeilingUnits(in HarvesterTuning tuning) => tuning.MaxHaulCells * 1024;

		/// <summary>Whether this field is further out than a harvester should be sent.</summary>
		public static bool TooFarToHaul(in FieldOption field, in HarvesterTuning tuning) =>
			field.DistanceUnits > HaulCeilingUnits(tuning);

		static int BestWithin(
			IReadOnlyList<FieldOption> fields, int exclude, int avoid, in HarvesterTuning tuning, int ceilingUnits)
		{
			var best = -1;
			var bestScore = 0;
			for (var i = 0; i < fields.Count; i++)
			{
				if (i == exclude || i == avoid || fields[i].DistanceUnits > ceilingUnits)
					continue;

				var score = Score(fields[i], tuning);
				if (best < 0 || score > bestScore)
				{
					best = i;
					bestScore = score;
				}
			}

			return best;
		}

		/// <summary>
		/// Where the field this harvester was sent to has got to, or -1 if the scan no longer
		/// returns it — which means it has been ground below <see cref="HarvesterTuning.MinFieldCells"/>
		/// and there is nothing left there to go back to.
		/// </summary>
		public static int FindAssigned(IReadOnlyList<FieldOption> fields, in HarvesterWatchdog watchdog, in HarvesterTuning tuning)
			=> watchdog.HasAssignment
				? FindByCenter(fields, watchdog.AssignedX, watchdog.AssignedY, tuning)
				: -1;

		/// <summary>
		/// Where the field this harvester was shot off has got to, or -1 if it no longer exists
		/// or the harvester has never been driven off one.
		/// </summary>
		/// <remarks>
		/// Matched by centre exactly as <see cref="FindAssigned"/> is, because the two questions
		/// are the same question — "which of these patches is the one I remember" — and answering
		/// them two different ways is how a remembered field turns into a different field.
		/// </remarks>
		public static int FindContested(IReadOnlyList<FieldOption> fields, in HarvesterWatchdog watchdog, in HarvesterTuning tuning)
			=> watchdog.HasContested
				? FindByCenter(fields, watchdog.ContestedX, watchdog.ContestedY, tuning)
				: -1;

		/// <summary>The scanned field whose centre is nearest a remembered one, within tolerance.</summary>
		static int FindByCenter(IReadOnlyList<FieldOption> fields, int x, int y, in HarvesterTuning tuning)
		{
			if (fields == null)
				return -1;

			var tolerance = tuning.FieldMatchCells;
			var bestDistance = int.MaxValue;
			var match = -1;

			for (var i = 0; i < fields.Count; i++)
			{
				var dx = fields[i].CenterX - x;
				var dy = fields[i].CenterY - y;
				if (dx < -tolerance || dx > tolerance || dy < -tolerance || dy > tolerance)
					continue;

				var distance = dx * dx + dy * dy;
				if (distance < bestDistance)
				{
					bestDistance = distance;
					match = i;
				}
			}

			return match;
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
				return watchdog with { LastX = state.X, LastY = state.Y, StillEvaluations = 0, ProbeIndex = probe, MovingEvaluations = moving };
			}

			return watchdog with { LastX = state.X, LastY = state.Y, StillEvaluations = watchdog.StillEvaluations + 1, MovingEvaluations = 0 };
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
