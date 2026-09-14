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
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	/// <summary>
	/// The rule this replaces sent every unit at the enemy base independently, the instant the
	/// doctrine flipped, from wherever it was standing. Across the 68-cell approach on
	/// badland-ridges that produced a 32.2-cell column: the jeep was attacking alone at 487s, the
	/// mtnk alone at 520s, and the 27 e3 that were two-thirds of the army's value were still
	/// 25-32 cells out at 528s. 46 units and 12,140 credits died between 475s and 610s for 18
	/// kills.
	/// <para>
	/// Everything below is a statement about one of the two halves of the replacement: the force
	/// has to gather before it goes in, and gathering must never be able to stop it going in.
	/// </para>
	/// </summary>
	[TestFixture]
	public class AssaultStagingLogicTests
	{
		static MusterTuning Tuning => MusterTuning.Default;

		/// <summary>
		/// Evaluations of a settled crowd before the muster releases. Two more than
		/// <see cref="MusterTuning.PatienceEvaluations"/>: the first evaluation only establishes
		/// the peak, and the second is the first that can find it unchanged.
		/// </summary>
		static int PatienceEvaluationsNeeded => Tuning.PatienceEvaluations + 2;

		const int Cell = 1024;

		/// <summary>An armed unit with nothing in weapon range — the ordinary approach march.</summary>
		static AssaultState Marching(IReadOnlyList<ThreatSnapshot> threats = null) => new(
			HealthPercent: 100,
			IsIdle: false,
			HasWeapon: true,
			CanMove: true,
			HasObjective: false,
			ObjectiveActorId: 0,
			DistanceToObjectiveUnits: int.MaxValue,
			WeaponRangeUnits: 6 * Cell,
			Threats: threats ?? []);

		/// <summary>A unit standing on the staging point, 68 cells out, with company.</summary>
		static MusterState AtMuster(int allies = 0) => new(
			HasTarget: true,
			CanMove: true,
			ObjectiveInRange: false,
			DistanceToTargetUnits: 68 * Cell,
			MusterX: 40,
			MusterY: 40,
			DistanceToMusterUnits: 0,
			AlliesNearbyCount: allies);

		/// <summary>A unit still crossing the map towards the staging point.</summary>
		static MusterState EnRoute() => AtMuster() with { DistanceToMusterUnits = 30 * Cell };

		static MusterOutcome Run(MusterState muster, MusterWatchdog watchdog, AssaultState assault = default)
			=> AssaultStagingLogic.Decide(
				assault == default ? Marching() : assault, muster, watchdog, Tuning);

		/// <summary>Feeds the same situation through n evaluations, returning the last outcome.</summary>
		static MusterOutcome RunRepeatedly(MusterState muster, int evaluations, MusterWatchdog? from = null)
		{
			var watchdog = from ?? MusterWatchdog.Start;
			var outcome = new MusterOutcome(null, watchdog);
			for (var i = 0; i < evaluations; i++)
			{
				outcome = Run(muster, watchdog);
				watchdog = outcome.Watchdog;
			}

			return outcome;
		}

		// --- Going in alone is what has to stop ---------------------------------------------

		[Test]
		public void A_unit_far_from_their_base_walks_to_the_staging_point_instead_of_charging()
		{
			var outcome = Run(EnRoute(), MusterWatchdog.Start);

			Assert.That(outcome.Decision.HasValue, Is.True, "staging must answer for a 68-cell approach");
			Assert.That(outcome.Decision.Value.Action, Is.EqualTo(UnitAction.MoveTo));
			Assert.That(outcome.Decision.Value.TargetX, Is.EqualTo(40));
			Assert.That(outcome.Decision.Value.TargetY, Is.EqualTo(40));
		}

		[Test]
		public void The_first_unit_to_arrive_waits_rather_than_going_in_by_itself()
		{
			// This is the jeep at 487s and the mtnk at 520s. Both reached their base ahead of
			// the infantry mass and both died there.
			var outcome = Run(AtMuster(allies: 0), MusterWatchdog.Start);

			Assert.That(outcome.Decision.HasValue, Is.True);
			Assert.That(outcome.Decision.Value.Action, Is.EqualTo(UnitAction.Hold));
			Assert.That(outcome.Decision.Value.Reason, Does.Contain("mustering"));
		}

		[Test]
		public void Waiting_survives_a_gap_wider_than_the_worst_stagger_between_speed_classes()
		{
			// Over the 54-cell walk to the staging point the classes land at jeep 15s, mtnk 22s,
			// e2 33s, e1 41s, e3 57s. The widest gap is e1 to e3 at 16 seconds, which at about
			// 1.4 game seconds an evaluation is 11 of them. The fast arrival must still be there.
			const int GapEvaluations = 12;

			var outcome = RunRepeatedly(AtMuster(allies: 3), GapEvaluations);

			Assert.That(outcome.Decision.HasValue, Is.True, "released before the slow half could land");
			Assert.That(outcome.Decision.Value.Action, Is.EqualTo(UnitAction.Hold));
		}

		[Test]
		public void Travelling_to_the_staging_point_does_not_spend_the_patience_for_standing_on_it()
		{
			// A slow unit crossing the map must not burn the army's patience on its own walk,
			// or it arrives to find everyone has already left.
			var afterLongWalk = RunRepeatedly(EnRoute(), 60);

			Assert.That(afterLongWalk.Watchdog.WaitedEvaluations, Is.Zero);
			Assert.That(afterLongWalk.Watchdog.StaleEvaluations, Is.Zero);

			var onArrival = Run(AtMuster(allies: 0), afterLongWalk.Watchdog);
			Assert.That(onArrival.Decision.Value.Action, Is.EqualTo(UnitAction.Hold),
				"a unit that has only been walking should still be willing to wait");
		}

		// --- ...without ever being able to stop the attack happening -------------------------

		[Test]
		public void The_muster_releases_once_nobody_new_has_joined_for_a_while()
		{
			var outcome = RunRepeatedly(AtMuster(allies: 8), PatienceEvaluationsNeeded);

			Assert.That(outcome.Decision.HasValue, Is.False, "released pushes fall through to the assault rule");
			Assert.That(outcome.Watchdog.Released, Is.True);
		}

		[Test]
		public void Every_new_arrival_buys_the_muster_more_time()
		{
			var watchdog = MusterWatchdog.Start;
			for (var i = 0; i < Tuning.PatienceEvaluations * 3; i++)
			{
				// A trickle of reinforcements, one per evaluation.
				var outcome = Run(AtMuster(allies: i + 1), watchdog);
				watchdog = outcome.Watchdog;
				Assert.That(watchdog.Released, Is.False, $"released at evaluation {i} while the army was still arriving");
			}
		}

		[Test]
		public void A_lone_unit_whose_reinforcements_never_come_still_attacks()
		{
			// A survivor waiting for an army that no longer exists must not wait forever.
			var outcome = RunRepeatedly(AtMuster(allies: 0), PatienceEvaluationsNeeded);

			Assert.That(outcome.Watchdog.Released, Is.True);
			Assert.That(outcome.Decision.HasValue, Is.False);
		}

		[Test]
		public void The_hard_cap_releases_a_push_that_reinforcements_keep_trickling_into()
		{
			// The anti-deadlock backstop. A crowd that grows on every single evaluation never
			// goes stale, so without the cap this push would form up for the rest of the match.
			// A preference with a fallback, not a demand that can hang.
			var watchdog = MusterWatchdog.Start;
			for (var i = 0; i < Tuning.MaxWaitEvaluations + 1; i++)
				watchdog = Run(AtMuster(allies: i + 1), watchdog).Watchdog;

			Assert.That(watchdog.Released, Is.True);
			Assert.That(watchdog.StaleEvaluations, Is.LessThan(Tuning.PatienceEvaluations),
				"the crowd never went stale, so only the cap can have released this");
		}

		[Test]
		public void Allies_drifting_away_cannot_restart_the_clock()
		{
			// PeakAllies is monotonic on purpose. If the live count reset the clock, units dying
			// or wandering in and out of the radius would pin the army in place indefinitely.
			var watchdog = MusterWatchdog.Start;
			for (var i = 0; i < Tuning.PatienceEvaluations * 2; i++)
			{
				var allies = i % 2 == 0 ? 6 : 2;
				var outcome = Run(AtMuster(allies), watchdog);
				watchdog = outcome.Watchdog;
			}

			Assert.That(watchdog.Released, Is.True);
		}

		[Test]
		public void Release_is_latched_so_a_released_unit_never_turns_round()
		{
			var released = MusterWatchdog.Start with { Released = true };

			// Back outside the muster radius, which before the latch would have read as
			// "not formed up yet" and walked the unit away from the base it was attacking.
			var outcome = Run(EnRoute(), released);

			Assert.That(outcome.Decision.HasValue, Is.False);
			Assert.That(outcome.Watchdog.Released, Is.True);
		}

		// --- Cases where forming up is the wrong answer --------------------------------------

		[Test]
		public void A_short_approach_is_not_worth_staging_for()
		{
			var closeBy = EnRoute() with { DistanceToTargetUnits = Tuning.StageBeyondUnits - 1 };

			Assert.That(Run(closeBy, MusterWatchdog.Start).Decision.HasValue, Is.False);
		}

		[Test]
		public void A_unit_already_inside_their_perimeter_presses_on_instead_of_walking_back_out()
		{
			var committed = AtMuster() with
			{
				DistanceToTargetUnits = Tuning.StandoffUnits - 1,
				DistanceToMusterUnits = 20 * Cell,
			};

			Assert.That(Run(committed, MusterWatchdog.Start).Decision.HasValue, Is.False);
		}

		[Test]
		public void A_unit_with_its_objective_in_range_shoots_it_rather_than_forming_up()
		{
			var inContact = EnRoute() with { ObjectiveInRange = true };

			Assert.That(Run(inContact, MusterWatchdog.Start).Decision.HasValue, Is.False);
		}

		[Test]
		public void Staging_says_nothing_when_this_side_has_never_seen_their_base()
		{
			Assert.That(Run(default, MusterWatchdog.Start).Decision.HasValue, Is.False);
		}

		[Test]
		public void An_unarmed_unit_is_never_marched_to_the_staging_point()
		{
			var unarmed = Marching() with { HasWeapon = false };

			Assert.That(Run(EnRoute(), MusterWatchdog.Start, unarmed).Decision.HasValue, Is.False);
		}

		[Test]
		public void An_immobile_unit_is_not_asked_to_walk_anywhere()
		{
			var stuck = EnRoute() with { CanMove = false };

			Assert.That(Run(stuck, MusterWatchdog.Start).Decision.HasValue, Is.False);
		}

		// --- Holding is not the same as not shooting ----------------------------------------

		[Test]
		public void A_unit_waiting_at_the_staging_point_still_shoots_what_is_in_range()
		{
			// The lesson already paid for once: an army that stood still under fire because
			// nothing had told it to shoot back lost 71 units for zero kills.
			var shootable = new ThreatSnapshot(
				ActorId: 99, DistanceUnits: 3 * Cell, HealthPercent: 100,
				Kind: ThreatKind.Infantry, IsAttackable: true, CanHitUs: true);

			var outcome = Run(AtMuster(allies: 2), MusterWatchdog.Start, Marching([shootable]));

			Assert.That(outcome.Decision.Value.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(outcome.Decision.Value.TargetActorId, Is.EqualTo(99u));
			Assert.That(outcome.Decision.Value.Reason, Does.Contain("mustering"));
		}

		[Test]
		public void Shooting_while_mustering_does_not_stop_the_muster_completing()
		{
			var shootable = new ThreatSnapshot(
				ActorId: 99, DistanceUnits: 3 * Cell, HealthPercent: 100,
				Kind: ThreatKind.Infantry, IsAttackable: true, CanHitUs: true);

			var watchdog = MusterWatchdog.Start;
			for (var i = 0; i < PatienceEvaluationsNeeded; i++)
				watchdog = Run(AtMuster(allies: 4), watchdog, Marching([shootable])).Watchdog;

			Assert.That(watchdog.Released, Is.True);
		}

		[Test]
		public void An_out_of_range_threat_is_not_shot_at_while_mustering()
		{
			var tooFar = new ThreatSnapshot(
				ActorId: 99, DistanceUnits: 20 * Cell, HealthPercent: 100,
				Kind: ThreatKind.Infantry, IsAttackable: true, CanHitUs: true);

			var outcome = Run(AtMuster(allies: 2), MusterWatchdog.Start, Marching([tooFar]));

			Assert.That(outcome.Decision.Value.Action, Is.EqualTo(UnitAction.Hold));
		}

		// --- Where the staging point is ------------------------------------------------------

		[Test]
		public void The_staging_point_sits_a_standoff_short_of_their_base_on_the_way_home()
		{
			// Home at (0,0), their base 68 cells due east.
			var (x, y) = AssaultStagingLogic.MusterCell(0, 0, 68, 0, standoffCells: 14);

			Assert.That(x, Is.EqualTo(54));
			Assert.That(y, Is.Zero);
		}

		[Test]
		public void The_staging_point_is_the_same_cell_for_every_unit_in_the_push()
		{
			// It is derived from the side's own base, not from the unit asking, which is the
			// whole reason the army converges instead of forming a crowd each.
			var first = AssaultStagingLogic.MusterCell(10, 10, 90, 70, 14);
			var second = AssaultStagingLogic.MusterCell(10, 10, 90, 70, 14);

			Assert.That(first, Is.EqualTo(second));
		}

		[Test]
		public void The_staging_point_is_outside_every_static_defence_in_the_ruleset()
		{
			// atwr reaches 8 cells, sam 10, msam 11. Forming up inside any of those would be
			// gathering the army under fire.
			Assert.That(Tuning.StandoffUnits / 1024, Is.GreaterThan(11));

			// ...and outside AttackBaseMode's five-cell ArrivedRadius, so a unit waiting at the
			// staging point can never declare the sighting stale on the whole side's behalf.
			Assert.That(Tuning.StandoffUnits / 1024, Is.GreaterThan(5));
		}

		[Test]
		public void A_target_closer_than_the_standoff_collapses_the_staging_point_onto_home()
		{
			// Degenerate, and reached only if StageBeyondUnits is ever lowered below the
			// standoff. It must still name a real cell rather than overshooting past the base.
			Assert.That(AssaultStagingLogic.MusterCell(10, 10, 14, 10, 14), Is.EqualTo((10, 10)));
		}

		[TestCase(0, 0)]
		[TestCase(1, 1)]
		[TestCase(15, 3)]
		[TestCase(16, 4)]
		[TestCase(4624, 68)]
		public void Integer_square_root_rounds_down(int value, int expected)
		{
			Assert.That(AssaultStagingLogic.IntSqrt(value), Is.EqualTo(expected));
		}
	}
}
