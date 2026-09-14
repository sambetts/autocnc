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
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	/// <summary>
	/// The rule this replaces had two outcomes, <c>Continue</c> and <c>MoveTo(refinery)</c>, so it
	/// could stop a harvester and could never start one. Everything below is a statement about one
	/// of those two halves: stopping has to be earned, and stopping has to be recoverable.
	/// </summary>
	[TestFixture]
	public class HarvesterLogicTests
	{
		static HarvesterTuning Tuning => HarvesterTuning.Default;

		/// <summary>A healthy harvester cutting tiberium eight cells from its refinery.</summary>
		/// <remarks>
		/// Knows of no field by default, so the tests that predate the resource layer still
		/// exercise the shroud fallback they were written against.
		/// </remarks>
		static HarvesterState Working(int x = 20, int y = 90) => new(
			HealthPercent: 100,
			CanMove: true,
			IsIdle: false,
			DangerNearby: false,
			HasRefinery: true,
			RefineryX: 12,
			RefineryY: 90,
			DistanceToRefineryUnits: 8 * 1024,
			X: x,
			Y: y,
			BaseX: 13,
			BaseY: 82,
			MapMinX: 0,
			MapMinY: 0,
			MapMaxX: 127,
			MapMaxY: 127,
			HasKnownField: false,
			FieldX: 0,
			FieldY: 0,
			FieldDistanceUnits: 0);

		static HarvesterOutcome Run(HarvesterState state, HarvesterWatchdog watchdog)
			=> HarvesterLogic.Decide(state, watchdog, Tuning);

		/// <summary>
		/// Feeds the same state through n evaluations, returning the last outcome. The first
		/// evaluation of a harvester only establishes where it is standing, so a stall takes
		/// <see cref="StallEvaluationsNeeded"/> of them rather than <c>StallEvaluations</c>.
		/// </summary>
		static HarvesterOutcome RunRepeatedly(HarvesterState state, int evaluations, HarvesterWatchdog? from = null)
		{
			var watchdog = from ?? HarvesterWatchdog.Start;
			var outcome = new HarvesterOutcome(UnitDecision.Continue, watchdog);
			for (var i = 0; i < evaluations; i++)
			{
				outcome = Run(state, watchdog);
				watchdog = outcome.Watchdog;
			}

			return outcome;
		}

		static int StallEvaluationsNeeded => Tuning.StallEvaluations + 1;

		/// <summary>
		/// Drives evaluations of an unchanging state until the rule says something other than
		/// "carry on", or the budget runs out. Returns whatever it last decided.
		/// </summary>
		static HarvesterOutcome RunUntilOrdered(HarvesterState state, ref HarvesterWatchdog watchdog, int budget = 16)
		{
			var outcome = new HarvesterOutcome(UnitDecision.Continue, watchdog);
			for (var i = 0; i < budget; i++)
			{
				outcome = Run(state, watchdog);
				watchdog = outcome.Watchdog;
				if (outcome.Decision.Action != UnitAction.Continue)
					break;
			}

			return outcome;
		}

		// --- Stopping has to be earned ---------------------------------------------------

		[Test]
		public void ScratchDamageDoesNotStopAHarvester()
		{
			// badland-ridges 354s: harv 400 took damage=30 against health=99 — one rifle burst,
			// one percent of its hit points — fled, and never harvested again. 561 seconds of the
			// bot's income went with it.
			var state = Working() with { DangerNearby = true, HealthPercent = 99 };

			var outcome = Run(state, HarvesterWatchdog.Start);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue),
				"a harvester at 99% must keep working; cancelling the harvest is the expensive half of this rule");
		}

		[Test]
		public void RealDamageStillSendsAHarvesterHome()
		{
			// badland-ridges 366s: harv 347 took damage=12420 over 40 hits, down to health=80,
			// and kept being shot. That one is worth running from.
			var state = Working() with { DangerNearby = true, HealthPercent = 55 };

			var outcome = Run(state, HarvesterWatchdog.Start);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo));
			Assert.That(outcome.Decision.TargetX, Is.EqualTo(state.RefineryX));
			Assert.That(outcome.Decision.TargetY, Is.EqualTo(state.RefineryY));
		}

		[Test]
		public void AHarvesterAlreadyAtTheRefineryIsNotSentToIt()
		{
			var state = Working() with
			{
				DangerNearby = true,
				HealthPercent = 20,
				DistanceToRefineryUnits = 1024,
			};

			var outcome = Run(state, HarvesterWatchdog.Start);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue));
		}

		[Test]
		public void AWorkingHarvesterIsNeverInterrupted()
		{
			// Moving every evaluation is what harvesting looks like from outside. However long it
			// goes on, the watchdog must never fire.
			var watchdog = HarvesterWatchdog.Start;
			for (var i = 0; i < 50; i++)
			{
				var outcome = Run(Working(20 + i % 5, 90), watchdog);
				watchdog = outcome.Watchdog;

				Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue),
					$"evaluation {i}: a moving harvester must be left alone");
			}
		}

		[Test]
		public void AStationaryHarvesterWithALiveActivityIsNeverInterrupted()
		{
			// Cutting a cell or unloading at the dock keeps a harvester on one cell for seconds at
			// a time, but it is not idle. Only idle AND still counts as stopped.
			var outcome = RunRepeatedly(Working() with { IsIdle = false }, 30);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue));
			Assert.That(outcome.Watchdog.StillEvaluations, Is.Zero);
		}

		// --- Stopping has to be recoverable ----------------------------------------------

		[Test]
		public void AStoppedHarvesterAwayFromHomeIsSentToUnload()
		{
			// The exact shape of the failure: a flee order cancelled the harvest activity, the
			// harvester arrived, went idle, and the old rule answered Continue for ever.
			var stalled = Working() with { IsIdle = true };

			var outcome = RunRepeatedly(stalled, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo),
				"an idle, stationary harvester is earning nothing and must be restarted");
			Assert.That(outcome.Decision.TargetX, Is.EqualTo(stalled.RefineryX));
			Assert.That(outcome.Decision.TargetY, Is.EqualTo(stalled.RefineryY));
		}

		[Test]
		public void AHarvesterStoppedAtTheRefineryIsSentOutToSearch()
		{
			// Stopped where it docks means the ground the refinery was placed on is finished.
			// There is no resource API, so the search is expressed as a move order.
			var stalled = Working() with { IsIdle = true, X = 12, Y = 90, DistanceToRefineryUnits = 512 };

			var outcome = RunRepeatedly(stalled, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo));
			Assert.That((outcome.Decision.TargetX, outcome.Decision.TargetY), Is.Not.EqualTo((12, 90)),
				"sending a stopped harvester to the cell it is already standing on restarts nothing");
			Assert.That(outcome.Decision.Reason, Does.Contain("searching"));
		}

		// --- Restarting means naming a field ----------------------------------------------

		/// <summary>A stalled harvester that can see a field 30 cells away.</summary>
		static HarvesterState StalledWithField(int fieldX = 40, int fieldY = 60, int distanceCells = 30) =>
			Working() with
			{
				IsIdle = true,
				X = 12,
				Y = 90,
				DistanceToRefineryUnits = 512,
				HasKnownField = true,
				FieldX = fieldX,
				FieldY = fieldY,
				FieldDistanceUnits = distanceCells * 1024,
			};

		[Test]
		public void AStalledHarvesterIsSentToTheNearestFieldItCanSee()
		{
			var outcome = RunRepeatedly(StalledWithField(), StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Harvest),
				"a move order parks the harvester on the tiberium and stops; only a harvest order keeps it cutting");
			Assert.That(outcome.Decision.TargetX, Is.EqualTo(40));
			Assert.That(outcome.Decision.TargetY, Is.EqualTo(60));
		}

		[Test]
		public void AKnownFieldOutranksBothTheTripHomeAndTheBlindSearch()
		{
			// The whole bug: every fallback below this one is capped inside the same 24-cell
			// bubble the engine already searched and found empty.
			var awayFromHome = StalledWithField() with { X = 20, Y = 90, DistanceToRefineryUnits = 8 * 1024 };

			var outcome = RunRepeatedly(awayFromHome, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Harvest));
			Assert.That(outcome.Decision.Reason, Does.Contain("harvesting"));
		}

		[Test]
		public void ADistantFieldIsStillWorthCrossingTheMapFor()
		{
			// The engine gives up past 24 cells from the refinery. This rule must not, or the
			// harvester starves next to a mined-out field with tiberium in plain sight.
			var farField = StalledWithField(fieldX: 110, fieldY: 20, distanceCells: 96);

			var outcome = RunRepeatedly(farField, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Harvest));
			Assert.That(outcome.Decision.TargetX, Is.EqualTo(110));
			Assert.That(outcome.Decision.TargetY, Is.EqualTo(20));
			Assert.That(outcome.Decision.Reason, Does.Contain("96 cells out"));
		}

		[Test]
		public void FindingAFieldFoldsTheBlindSearchLadderBack()
		{
			// Drive the ladder out while nothing is visible.
			var blind = Working() with { IsIdle = true, X = 12, Y = 90, DistanceToRefineryUnits = 512 };
			var watchdog = HarvesterWatchdog.Start;
			RunUntilOrdered(blind, ref watchdog);
			RunUntilOrdered(blind, ref watchdog);
			Assert.That(watchdog.ProbeIndex, Is.GreaterThan(1), "precondition: the blind search has widened");

			// Shroud comes off and a field appears.
			var outcome = RunUntilOrdered(StalledWithField(), ref watchdog);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Harvest));
			Assert.That(outcome.Watchdog.ProbeIndex, Is.Zero,
				"a field in sight makes the blind ladder irrelevant; the next stall should start clean");
		}

		[Test]
		public void AWorkingHarvesterIsNotRedirectedToAField()
		{
			var busy = StalledWithField() with { IsIdle = false };

			var outcome = RunRepeatedly(busy, StallEvaluationsNeeded + 4);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue),
				"interrupting a harvester that is already cutting is the expensive mistake this rule exists to avoid");
		}

		[Test]
		public void FleeingOutranksHarvesting()
		{
			var hurt = StalledWithField() with
			{
				DangerNearby = true,
				HealthPercent = 30,
				DistanceToRefineryUnits = 8 * 1024,
			};

			var outcome = RunRepeatedly(hurt, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo));
			Assert.That(outcome.Decision.Reason, Does.Contain("hurt"));
		}

		[Test]
		public void TheSearchWidensWhileTheHarvesterStaysStopped()
		{
			var stalled = Working() with { IsIdle = true, X = 12, Y = 90, DistanceToRefineryUnits = 512 };

			var watchdog = HarvesterWatchdog.Start;
			var reaches = new System.Collections.Generic.List<int>();

			for (var attempt = 0; attempt < 5; attempt++)
			{
				var outcome = RunUntilOrdered(stalled, ref watchdog);

				Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo),
					$"search {attempt}: a harvester that is still stopped must be retried");

				var dx = outcome.Decision.TargetX - stalled.RefineryX;
				var dy = outcome.Decision.TargetY - stalled.RefineryY;
				reaches.Add(dx * dx + dy * dy);
			}

			Assert.That(reaches[^1], Is.GreaterThan(reaches[0]),
				"a search that keeps failing at the same radius is not a search");
		}

		[Test]
		public void TheSearchNeverLeavesTheMap()
		{
			var cornered = Working() with
			{
				IsIdle = true,
				X = 1,
				Y = 1,
				RefineryX = 1,
				RefineryY = 1,
				DistanceToRefineryUnits = 0,
				MapMinX = 0,
				MapMinY = 0,
				MapMaxX = 40,
				MapMaxY = 40,
			};

			var watchdog = HarvesterWatchdog.Start;
			var probes = 0;
			for (var attempt = 0; attempt < 12; attempt++)
			{
				var outcome = RunUntilOrdered(cornered, ref watchdog);

				Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo),
					$"search {attempt}: a harvester stopped in the corner of the map must still be retried");
				probes++;

				Assert.That(outcome.Decision.TargetX, Is.InRange(cornered.MapMinX, cornered.MapMaxX));
				Assert.That(outcome.Decision.TargetY, Is.InRange(cornered.MapMinY, cornered.MapMaxY));
				Assert.That((outcome.Decision.TargetX, outcome.Decision.TargetY), Is.Not.EqualTo((cornered.X, cornered.Y)),
					$"search {attempt}: clamping must not collapse the probe onto the harvester's own cell");
			}

			Assert.That(probes, Is.EqualTo(12), "every stall in the corner must still produce a probe");
		}

		[Test]
		public void AHarvesterBackAtWorkFoldsTheSearchLadderBack()
		{
			var stalled = Working() with { IsIdle = true, X = 12, Y = 90, DistanceToRefineryUnits = 512 };
			var watchdog = RunRepeatedly(stalled, StallEvaluationsNeeded).Watchdog;
			Assert.That(watchdog.ProbeIndex, Is.GreaterThan(0), "precondition: a search is under way");

			// It found something and got on with it.
			for (var i = 0; i < Tuning.ProbeResetEvaluations; i++)
				watchdog = Run(Working(20 + i, 90), watchdog).Watchdog;

			Assert.That(watchdog.ProbeIndex, Is.Zero,
				"the next stall should start from the near ring, not from wherever the last search ended");
		}

		[Test]
		public void TheWatchdogNeverFiresOnAHarvesterThatCannotMove()
		{
			var outcome = RunRepeatedly(Working() with { IsIdle = true, CanMove = false }, 20);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.Continue));
		}

		[Test]
		public void FleeingOutranksTheSearch()
		{
			// A stopped harvester that is also being taken apart should go home, not walk further
			// out into whatever is shooting it.
			var state = Working() with { IsIdle = true, DangerNearby = true, HealthPercent = 30 };

			var outcome = RunRepeatedly(state, StallEvaluationsNeeded + 4);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo));
			Assert.That(outcome.Decision.TargetX, Is.EqualTo(state.RefineryX));
			Assert.That(outcome.Decision.TargetY, Is.EqualTo(state.RefineryY));
			Assert.That(outcome.Decision.Reason, Does.Contain("hurt"));
		}

		[Test]
		public void ProbeRadiusIsMonotonicAndCapped()
		{
			var last = 0;
			for (var probe = 1; probe < 30; probe++)
			{
				var cells = HarvesterLogic.ProbeRadiusCells(probe, Tuning);
				Assert.That(cells, Is.GreaterThanOrEqualTo(last));
				Assert.That(cells, Is.LessThanOrEqualTo(Tuning.MaxProbeCells));
				last = cells;
			}

			Assert.That(HarvesterLogic.ProbeRadiusCells(1, Tuning), Is.EqualTo(Tuning.FirstProbeCells));
		}

		[Test]
		public void WithNoRefineryTheSearchCentresOnTheBase()
		{
			var stranded = Working() with
			{
				IsIdle = true,
				HasRefinery = false,
				DistanceToRefineryUnits = int.MaxValue,
				X = 13,
				Y = 82,
			};

			var outcome = RunRepeatedly(stranded, StallEvaluationsNeeded);

			Assert.That(outcome.Decision.Action, Is.EqualTo(UnitAction.MoveTo),
				"losing every refinery must not also silence the harvester");
			Assert.That(outcome.Decision.Reason, Does.Contain("searching"));
		}
	}
}
