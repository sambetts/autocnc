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
		int LegUnits,
		int MusterRadiusUnits,
		int PatienceEvaluations,
		int MaxWaitEvaluations,
		int MaxStagingEvaluations,
		bool FightWhileFormingUp)
	{
		public static MusterTuning Default { get; } = new(
			// Shorter than this and the whole approach is one leg: the staging point would be
			// on top of the objective and the gather would buy nothing. Measured home to their
			// base, not from the unit — whether staging is worth it is a property of the march,
			// not of where one unit happens to be standing. The march that lost badland-ridges
			// was 68 cells.
			StageBeyondUnits: 24 * 1024,

			// How far short of their base the *last* leg ends. Outside every reach in the
			// ruleset — msam 11 cells, sam 10, atwr 8 air / 7 ground, gtwr 6 — so the final
			// gather happens where nothing static can shoot it. Also comfortably outside
			// AttackBaseMode's five-cell ArrivedRadius, which matters more than it looks: a
			// unit standing on the remembered cell drops the sighting for the whole side, so a
			// lone fast unit that never reaches it can no longer cancel everyone else's attack.
			StandoffUnits: 14 * 1024,

			// How far the army advances between gathers, measured from home.
			//
			// The bound this must not cross is the patience below. A leg of L cells is crossed
			// in L/0.952 seconds by e3, the slowest unit in the game, and L/3.54 by jeep, the
			// fastest this bot builds, so one leg spreads the force by L * 0.768 seconds. At
			// L = 16 that is 12.3s, or about 9 evaluations — comfortably inside the 16
			// evaluations of patience, so the gather completes rather than timing out with half
			// the army still walking. The ceiling is L <= 22.4 / 0.768 = 29 cells.
			//
			// The floor is the muster radius: at L <= 6 cells a unit released at one boundary
			// is already inside the next one's tolerance, so the leg does nothing. 16 is also
			// longer than the longest reach in the ruleset (msam, 11), so a unit gathering at
			// one boundary is out of reach of anything static sitting on the last one.
			//
			// On the 68-cell badland-ridges approach this gives legs at 16, 32, 48 and then the
			// standoff at 54: four gathers, each costing at most 45s (see MaxWaitEvaluations),
			// against an e3 walk of 57s that previously arrived spread over 46 cells.
			LegUnits: 16 * 1024,

			// Arrival tolerance for the staging cell, and the radius that counts allies as
			// "formed up with us". One number does both jobs deliberately: the crowd that
			// releases the push is the same crowd that will arrive with it.
			MusterRadiusUnits: 6 * 1024,

			// Consecutive evaluations with nobody new joining before the muster is called
			// complete. An evaluation is about 1.4 game seconds, so this is roughly 22s.
			//
			// It has to clear the largest gap between two speed classes arriving over *one leg*,
			// or the fast half leaves before the slow half lands. Over a 16-cell leg the classes
			// arrive at jeep 4.5s, mtnk 9.1s, e2 9.6s, e1 12.1s, e3 16.8s: the widest gap is
			// 12.3s, or 9 evaluations. This used to have to cover the whole 54-cell approach,
			// where the same spread is 41 seconds and no realistic patience could cover it.
			PatienceEvaluations: 16,

			// Hard cap on standing still at *one* leg, about 45 seconds, reset by every release.
			//
			// It has to exceed the cost of a gather that completes normally — 9 evaluations of
			// arrival spread plus 16 of patience is 25 — or it would pre-empt the very thing it
			// exists to backstop. 32 clears that with margin.
			MaxWaitEvaluations: 32,

			// Hard cap on being held by this machine at all — walking to a staging cell or
			// standing on it — across the unit's *whole life*, about 56 game seconds. Never
			// reset: not by a release, and not by a new push. This is the one number that stops
			// the gather from eating the match.
			//
			// Two separate failures on badland-ridges both reduce to "this machine had no
			// timeout on the state it actually spent its time in".
			//
			//  * The walk had no clock of any kind. Observe runs only from the waiting branch, so
			//    a unit that cannot reach its staging cell advances no counter, and MaxWaitEvaluations
			//    cannot fire because it counts waiting. One e1 (actor 355) issued the identical
			//    AttackMoveTo(34,39) 670 times between 460s and 877s — 417 game seconds, against
			//    the 36 seconds an e1 needs to walk the 47.9 cells from the yard at (13,82) — and
			//    reached the leg-3 gather exactly zero times. Across the side, leg 3 drew 19,775
			//    walk orders in the first 1,200 seconds and 780 gathers in the whole match.
			//  * The standing still had a clock, but it was per leg and it was reset by every
			//    re-entry. The doctrine left Attack 27 times, so OnEnter cleared the release latch
			//    27 times and re-imposed gathers the survivors had already been released through.
			//
			// Together that is 39,332 "forming up" decisions, 78% of AttackBaseMode's entire
			// output, against 797 "objective in range", 1.6%.
			//
			// Three bounds pin the number, and it sits inside all three:
			//
			//  * Above one leg done properly, or the machine is a pure tax. For e3 at 0.952 cells
			//    a game second a 16-cell leg is 16.8s of walking, 12 evaluations at 1.4s each;
			//    the gather on top is 9 evaluations of arrival spread (jeep 4.5s to e3 16.8s is
			//    12.3s) plus PatienceEvaluations (16), so 25. One leg is therefore 37.
			//  * Below two legs, 2 x 37 = 74, so the budget buys exactly one gather per unit.
			//  * Below the Attack doctrine's measured mean episode. Its 27 episodes averaged 70.2
			//    game seconds (min 30, max 340), and 40 evaluations is ~56s — inside the episode
			//    that authorised the push, which 4 legs x 45s of the old per-leg cap never was.
			//
			// So 37 <= 40 < 74, and MaxWaitEvaluations (32) stays below it so the per-leg
			// backstop still fires inside a budget that has not run out.
			MaxStagingEvaluations: 40,

			// Whether a unit marching to the staging point stops for what is already inside its
			// weapon range. On by default: the march is the longest leg of the whole push, and
			// the alternative is the one that lost badland-ridges twice over — an army crossing
			// contested ground under a plain move order, shot for free the entire way.
			FightWhileFormingUp: true);
	}

	/// <summary>What the staging rule decided to do with one unit.</summary>
	public enum MusterVerdict
	{
		/// <summary>No staging opinion — the normal assault rule applies.</summary>
		NotRequired,

		/// <summary>Advance to the staging point and form up, fighting through anything in reach.</summary>
		MoveToMuster,

		/// <summary>Formed up; wait for the rest of the army.</summary>
		WaitForTheRest,

		/// <summary>The muster is complete. Go in.</summary>
		Release,
	}

	/// <summary>Everything the staging rule needs, with no engine types in it.</summary>
	/// <remarks>
	/// <see cref="StagingAdvanceUnits"/> is the distance <em>from home</em> of the leg this unit
	/// should gather on, and zero means "no leg left — go in". It is computed by
	/// <see cref="AssaultStagingLogic.StagingAdvanceCells"/> so the mode does no arithmetic of
	/// its own; the mode only needs it back in order to turn it into a cell it can measure
	/// against. This unit's own distance to their base is deliberately <em>not</em> here: it is
	/// an input to that calculation rather than something the rule reads, and a field nothing
	/// reads is a field the next round will believe is load-bearing.
	/// </remarks>
	public readonly record struct MusterState(
		bool HasTarget,
		bool CanMove,
		bool ObjectiveInRange,
		int HomeToTargetUnits,
		int StagingAdvanceUnits,
		int MusterX,
		int MusterY,
		int DistanceToMusterUnits,
		int AlliesNearbyCount);

	/// <summary>
	/// What one unit has seen while forming up. Carried by the mode between evaluations and
	/// advanced here, so the counting is as legible as the rule that reads it.
	/// </summary>
	/// <remarks>
	/// Three of the four counters belong to one leg of one push and are cleared by every release.
	/// <see cref="StagedEvaluations"/> is the exception: it belongs to the unit, it counts every
	/// evaluation this machine has ever held the unit for — walking to a staging cell as well as
	/// standing on one — and nothing clears it. That asymmetry is the point; see
	/// <see cref="MusterTuning.MaxStagingEvaluations"/>.
	/// </remarks>
	public readonly record struct MusterWatchdog(
		int PeakAllies,
		int StaleEvaluations,
		int WaitedEvaluations,
		int ReleasedThroughUnits,
		int StagedEvaluations)
	{
		/// <summary>A unit that has not started forming up yet.</summary>
		/// <remarks>
		/// <see cref="ReleasedThroughUnits"/> is zero rather than a flag because zero is not a
		/// legal staging distance — the first leg is always at least one leg length out — so it
		/// doubles as "never released" without needing a sentinel.
		/// </remarks>
		public static MusterWatchdog Start { get; } = new(0, 0, 0, 0, 0);

		/// <summary>The same unit, setting out on a fresh push.</summary>
		/// <remarks>
		/// A unit that came home and is marching out again really is back on leg one, so the
		/// per-leg clocks and the release latch all clear. What does <b>not</b> clear is the
		/// lifetime staging budget, and that is the whole correction: <c>OnEnter</c> ran 27 times
		/// on badland-ridges because the doctrine left <c>Attack</c> 27 times, and each run handed
		/// the survivors a full fresh staging allowance and cleared a release latch they had
		/// already earned. The army therefore re-gathered at legs it had already passed — leg 3
		/// alone drew 19,775 walk orders in the first 1,200 seconds against 780 gathers in the
		/// whole match.
		/// </remarks>
		public MusterWatchdog ForNewPush() => new(0, 0, 0, 0, StagedEvaluations);
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
	/// <para>
	/// Forming up fixed the arrival and then lost the army a second way. The march to the
	/// staging point was a plain move, so the force crossed 44 cells of contested ground unable
	/// to fight: on badland-ridges 40 units were committed at 660s, met their field army at
	/// 690s, and were down to 3 units by 710s — 37 lost in twenty seconds for 3 kills. Of the
	/// 40, exactly nine ever received an order that permits engaging, and those nine made all
	/// three kills; the rest were still executing <c>MoveTo(46,50)</c> issued at 671s and dealt
	/// no damage at all while <c>e1</c> and <c>e2</c> — whose four-cell reach is shorter than
	/// the <c>e3</c> rockets aimed at nothing — killed them at point-blank range. So the march
	/// is an attack-move, and a unit already in contact stops and fights through.
	/// </para>
	/// <para>
	/// <b>And then it turned out never to have gathered at all.</b> The staging point used to be
	/// measured back from <em>their</em> base, so on badland-ridges it landed at (46,48): 50
	/// cells from the yard at (82,13) and 14 from theirs. Reaching it meant crossing the whole
	/// contested map first, and <see cref="MusterVerdict.WaitForTheRest"/> only fires within
	/// <see cref="MusterTuning.MusterRadiusUnits"/> of it — so in 4,385 decisions the trace holds
	/// <b>415 <c>MoveToMuster</c> and zero <c>mustering</c></b>, the third match running. That is
	/// not a slow gather, it is a dead one: <see cref="Observe"/> is reached only from the
	/// waiting branch, so <c>StaleEvaluations</c> and <c>WaitedEvaluations</c> never advanced
	/// past zero, <see cref="MusterVerdict.Release"/> was unreachable by either route, and the
	/// whole machine reduced to a one-way walk order into the enemy's half of the map. The army
	/// arrived there spread over <b>46.1 cells</b> (25.8 to 71.9 short of their base at 435-515s)
	/// and enemy <c>jeep</c> — 400 credits, 3.54 cells a second, 5,391 damage a second against
	/// the <c>None</c> armour every one of this bot's infantry wears — killed <b>42 units for
	/// 9,640 credits in the 75 seconds from 470s, for 14 kills</b>, 13% of everything the bot
	/// spent all match. At 480s it led on units 43 to 21 and on army 9,940 to 8,400; at 600s it
	/// had 16 units to 37 and never led again.
	/// </para>
	/// <para>
	/// So the legs are measured from <b>home</b> instead. The force gathers every
	/// <see cref="MusterTuning.LegUnits"/> along the line to their base, and each release re-arms
	/// the next gather, because one staging point cannot compress a 3.7x speed spread however
	/// well it is placed — after a single release the army simply re-spreads over whatever leg
	/// remains. Short legs keep the spread inside the patience, the first gather happens in the
	/// bot's own half where its towers are, and the last one still ends
	/// <see cref="MusterTuning.StandoffUnits"/> short of their base so the final approach is made
	/// together.
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
			=> Decide(assault, muster, watchdog, tuning, WeaponRole.Unknown);

		public static MusterOutcome Decide(
			in AssaultState assault,
			in MusterState muster,
			in MusterWatchdog watchdog,
			in MusterTuning tuning,
			WeaponRole role)
		{
			// An unarmed unit has no business in an assault at all. AttackBaseLogic deliberately
			// leaves it where it stands rather than marching it into the enemy base to die, and
			// walking it to the staging point would undo exactly that.
			if (!assault.HasWeapon)
				return new MusterOutcome(null, watchdog);

			var verdict = Judge(muster, watchdog, tuning);
			var leg = LegIndex(muster.StagingAdvanceUnits, tuning.LegUnits);

			switch (verdict)
			{
				case MusterVerdict.MoveToMuster:
				{
					// Every evaluation this machine holds the unit is charged, walking included.
					// The patience clocks below deliberately ignore the walk — a unit still
					// crossing a leg must not spend the army's patience on its own journey — but
					// the lifetime budget is not patience, it is a timeout, and the walk is
					// exactly the state that had none. A staging cell the pathfinder cannot land
					// within MusterRadiusUnits of used to hold a unit here forever: one e1 issued
					// the same order 670 times across 417 game seconds and never arrived.
					var held = Charge(watchdog, tuning);

					// Stop and shoot what is already on top of us. Every leg of the approach may
					// be contested, and the first one starts at home.
					//
					// Bounded by SelectLastStandTarget's weapon-range filter, so this can never
					// become the chase this mode exists to refuse: a unit stops only while
					// something is already inside its own reach, and resumes the moment it is
					// not. That is the same rule the WaitForTheRest branch below has always
					// had; marching simply never got it.
					if (tuning.FightWhileFormingUp)
					{
						var contact = AttackBaseLogic.SelectLastStandTarget(assault, role);
						if (contact.HasValue)
							return new MusterOutcome(
								UnitDecision.Attack(contact.Value.ActorId,
									$"forming up on leg {leg}, fighting through {contact.Value.Kind}"),
								held);
					}

					// Attack-move rather than move, for exactly the reason AttackBaseLogic.Approach
					// already gives about the shorter leg that follows this one: the whole point of
					// forming up is to arrive able to fight. The destination is fixed before the
					// unit sets off, so nothing it meets on the way can redirect it.
					//
					// The reason carries the leg and nothing else. It used to carry the distance
					// to their base, which made 242 of the 415 staging orders in the last trace
					// distinct strings and hid the fact that the branch below never ran at all.
					return new MusterOutcome(
						UnitDecision.AttackMoveTo(muster.MusterX, muster.MusterY,
							$"forming up on leg {leg}"),
						held);
				}

				case MusterVerdict.WaitForTheRest:
				{
					var seen = Observe(Charge(watchdog, tuning), muster, tuning);

					// Holding is not the same as not shooting. A stationary unit gives up no
					// forward progress by firing on whatever is already inside its weapon
					// range, and the last thing this bot needs is another army that stood
					// still under fire because it had not been told to shoot back.
					var target = AttackBaseLogic.SelectLastStandTarget(assault, role);
					if (target.HasValue)
						return new MusterOutcome(
							UnitDecision.Attack(target.Value.ActorId,
								$"mustering on leg {leg}, engaging {target.Value.Kind}"),
							seen);

					return new MusterOutcome(
						UnitDecision.Hold($"mustering on leg {leg} with {muster.AlliesNearbyCount} allies"),
						seen);
				}

				case MusterVerdict.Release:
					// Latch this leg, and only this leg. A released unit that walked past the
					// staging cell must never decide it is out of radius and turn round, or the
					// assault becomes a shuttle — but it must still gather again at the next
					// boundary, because a single release cannot hold a 3.7x speed spread
					// together over the rest of the march.
					//
					// The crowd clocks reset with it: leaving them running would make the next
					// leg's gather instantly stale — the peak from this leg can never be beaten
					// by a crowd that has only just started arriving — and release on arrival,
					// which is the same bug in a new place. WaitedEvaluations resets too, so
					// MaxWaitEvaluations bounds one gather rather than the whole push.
					//
					// StagedEvaluations is carried through rather than rebuilt from zero. It is
					// the only counter here that measures the unit rather than the leg, and a
					// release that cleared it would hand every leg a fresh allowance — which is
					// precisely the unbounded cost MaxStagingEvaluations exists to stop.
					return new MusterOutcome(
						null,
						watchdog with
						{
							PeakAllies = 0,
							StaleEvaluations = 0,
							WaitedEvaluations = 0,
							ReleasedThroughUnits = muster.StagingAdvanceUnits,
						});

				default:
					return new MusterOutcome(null, watchdog);
			}
		}

		/// <summary>Which numbered leg a staging distance belongs to, counting from one.</summary>
		/// <remarks>
		/// Rounded up, so the shortened final leg that ends at the standoff shares a number with
		/// nothing else. Used only for the decision reason, which is what the next round reads.
		/// </remarks>
		public static int LegIndex(int stagingAdvanceUnits, int legUnits)
		{
			if (stagingAdvanceUnits <= 0 || legUnits <= 0)
				return 0;

			return (stagingAdvanceUnits + legUnits - 1) / legUnits;
		}

		/// <summary>The staging verdict for one unit, with no side effects.</summary>
		public static MusterVerdict Judge(in MusterState s, in MusterWatchdog watchdog, in MusterTuning t)
		{
			// Nowhere to stage toward, or no way to get there.
			if (!s.HasTarget || !s.CanMove)
				return MusterVerdict.NotRequired;

			// Already shooting at the thing we were sent to kill. Walking away from a target
			// inside weapon range to go and stand in a field is not caution, it is a bug.
			if (s.ObjectiveInRange)
				return MusterVerdict.NotRequired;

			// A short approach is one leg and does not spread a force out. Judged on the march
			// itself rather than on this unit's distance, because a unit that has already walked
			// most of the way still belongs to a push that started 68 cells out.
			if (s.HomeToTargetUnits <= t.StageBeyondUnits)
				return MusterVerdict.NotRequired;

			// No leg left: inside the standoff, committed. Walking back out to form up would
			// cross the defended ground twice.
			if (s.StagingAdvanceUnits <= 0)
				return MusterVerdict.NotRequired;

			// Already let go through this boundary. Per leg rather than per push: a released
			// unit never turns round, but it does gather again further on.
			if (s.StagingAdvanceUnits <= watchdog.ReleasedThroughUnits)
				return MusterVerdict.NotRequired;

			// This unit has spent its whole staging allowance. Everything below this line is the
			// machine that ate badland-ridges, so once the budget is gone the unit simply goes in
			// and keeps going in for the rest of its life, however many pushes that spans.
			//
			// Deliberately above the per-leg backstop and the walk order rather than beside them:
			// a unit out of budget must not be sent back to a staging cell it has already passed,
			// which is what MoveToMuster below would do. Release rather than NotRequired so the
			// latch advances too, and so both ways out of this machine share one exit.
			if (watchdog.StagedEvaluations >= t.MaxStagingEvaluations)
				return MusterVerdict.Release;

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
		/// Charges one evaluation against the lifetime staging budget.
		/// </summary>
		/// <remarks>
		/// Separate from <see cref="Observe"/> because the two count different things. Observe
		/// runs only while a unit is standing on a staging cell, which is what patience is about;
		/// this runs on every evaluation the machine holds the unit at all, walking included,
		/// which is what a timeout is about. Conflating them is what left the walk with no clock.
		/// <para>Saturating, so a long match cannot overflow a counter nothing ever resets.</para>
		/// </remarks>
		public static MusterWatchdog Charge(in MusterWatchdog watchdog, in MusterTuning tuning)
			=> watchdog.StagedEvaluations >= tuning.MaxStagingEvaluations
				? watchdog
				: watchdog with { StagedEvaluations = watchdog.StagedEvaluations + 1 };

		/// <summary>
		/// Advances the watchdog. The staleness clock resets whenever the crowd reaches a new
		/// high, so reinforcements still arriving always buy more time.
		/// </summary>
		/// <remarks>
		/// The peak is monotonic rather than the live count, so units drifting in and out of the
		/// radius — or dying — cannot keep resetting the clock and pin the army in place.
		/// <para>
		/// Reached only from the <see cref="MusterVerdict.WaitForTheRest"/> branch, so everything
		/// it advances is a statement about standing still. The lifetime budget is charged by
		/// <see cref="Charge"/> instead, precisely because it must also cover walking.
		/// </para>
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
		/// How far from home the next gather should be, or zero if there is no leg left and the
		/// unit should go in.
		/// </summary>
		/// <remarks>
		/// All distances in cells. The last leg is shortened rather than overshot, so the final
		/// gather always sits exactly <paramref name="standoffCells"/> short of their base —
		/// outside every static reach in the ruleset — and the run in from there is made by a
		/// force that is already together.
		/// <para>
		/// Progress is inferred as <c>homeToTarget - distanceToTarget</c> rather than measured,
		/// because a mode can ask the engine how far two things are apart but not where a unit
		/// sits along a line. It is exact on the axis and pessimistic off it, which is the safe
		/// direction: a flanking unit reads as further back and gathers sooner.
		/// </para>
		/// <para>
		/// The staggered-boundary arithmetic is what makes the release re-arm. A unit released at
		/// leg k has progress k*L, so <c>progress / legCells + 1</c> lands on k+1 and never on k
		/// again; combined with <see cref="MusterWatchdog.ReleasedThroughUnits"/> it can neither
		/// re-gather where it just was nor skip the boundary in front of it.
		/// </para>
		/// </remarks>
		public static int StagingAdvanceCells(int homeToTargetCells, int distanceToTargetCells, int legCells, int standoffCells)
		{
			if (legCells <= 0)
				return 0;

			// Where the last gather happens: short of their base by the standoff.
			var march = homeToTargetCells - standoffCells;
			if (march <= 0)
				return 0;

			var progress = homeToTargetCells - distanceToTargetCells;
			if (progress < 0)
				progress = 0;

			// Past the last gather: committed.
			if (progress >= march)
				return 0;

			var next = (progress / legCells + 1) * legCells;
			return next > march ? march : next;
		}

		/// <summary>
		/// Where the army forms up: a cell on the line from our base towards theirs,
		/// <paramref name="advanceCells"/> out from home.
		/// </summary>
		/// <remarks>
		/// Derived from the side's own base rather than from the unit asking, so every unit in
		/// the push computes the same cell and they converge instead of each gathering its own
		/// private crowd.
		/// <para>
		/// Measured from home rather than back from their base, which is the correction this cost
		/// three matches to find. A cell placed relative to their base is 50 cells away across
		/// contested ground however safe the arithmetic around it looks, so the gathering leg was
		/// the dangerous leg and the gather never once completed.
		/// </para>
		/// <para>
		/// Integer-only, matching <see cref="HarvesterLogic"/>: floating point in a decision that
		/// becomes an order is a lockstep hazard.
		/// </para>
		/// </remarks>
		public static (int X, int Y) StagingCell(int homeX, int homeY, int targetX, int targetY, int advanceCells)
		{
			var dx = targetX - homeX;
			var dy = targetY - homeY;

			var distance = IntSqrt(dx * dx + dy * dy);
			if (distance <= 0 || advanceCells >= distance)
				return (targetX, targetY);

			return (homeX + dx * advanceCells / distance, homeY + dy * advanceCells / distance);
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
