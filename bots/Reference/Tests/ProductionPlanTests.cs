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
using System.Linq;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	/// <summary>
	/// What the shipped production plans actually produce, walked the way a factory walks them.
	/// </summary>
	/// <remarks>
	/// The failure these exist to prevent is the one that lost badland-ridges. The bot built 125
	/// <c>e1</c> minigunners and not one <c>e3</c>, so it had nothing at all that could target
	/// aircraft: 67 of its 159 losses were to <c>orca</c> and <c>a10</c>, including 21 of the
	/// last 23, and it finished 23 kills to 140 losses.
	/// <para>
	/// Two plan-level mistakes caused it, and both are asserted against here. First,
	/// <c>Until(n)</c> counts <em>every</em> candidate a step lists, so the one rocket step the
	/// bot had — <c>new("Infantry", ["e3", "e1"], 6)</c> — was already satisfied by the ten
	/// riflemen the step above it had just bought, and never fired. Second, an endless infantry
	/// step above an endless vehicle step starves the war factory, because both queues consult
	/// the same plan and only the owner of the chosen queue acts: a barracks is idle at far more
	/// evaluations than a war factory, so it wins nearly every one.
	/// </para>
	/// <para>
	/// The income tests below are the same shape of fault one layer down. A plan that never names
	/// <c>harv</c> caps the bot at the one free harvester each refinery hands out and can never
	/// replace a dead one — which held it at 0 cash for the whole of a later match while the
	/// other side's army value grew tenfold.
	/// </para>
	/// </remarks>
	[TestFixture]
	public class ProductionPlanTests
	{
		/// <summary>What a Tiberian Dawn Infantry queue offers once a barracks and an hq exist.</summary>
		static readonly string[] InfantryItems = ["e1", "e2", "e3"];

		/// <summary>What a Vehicle queue offers once an hq exists.</summary>
		static readonly string[] VehicleItems = ["jeep", "bggy", "mtnk", "ltnk", "harv", "apc"];

		static readonly string[] Harvesters = [.. ReferencePlans.HarvesterUnits];

		static ProductionQueueState Infantry(bool idle = true) => new("Infantry", idle, InfantryItems);

		static ProductionQueueState Vehicle(bool idle = true) => new("Vehicle", idle, VehicleItems);

		static Dictionary<string, int> Owned(params (string Actor, int Count)[] counts)
		{
			var owned = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var (actor, count) in counts)
				owned[actor] = count;

			return owned;
		}

		static ProductionChoice Next(
			IReadOnlyList<ProductionStep> plan,
			IDictionary<string, int> owned,
			bool infantryIdle = true,
			bool vehicleIdle = true,
			int cash = 5000)
			=> UnitProductionLogic.ChooseNext(
				new ArmyPlanState(cash, [Infantry(infantryIdle), Vehicle(vehicleIdle)],
					new Dictionary<string, int>(owned, System.StringComparer.OrdinalIgnoreCase)),
				plan);

		/// <summary>Walks a plan the way two factories would, returning what was ordered.</summary>
		/// <param name="vehicleIdle">
		/// False models a war factory that is busy for the whole run, which is the only way to
		/// read the infantry half of a plan in isolation: both queues consult the same plan and
		/// a single choice comes back, so an idle war factory takes orders the barracks would
		/// otherwise have had.
		/// </param>
		static List<string> Run(
			IReadOnlyList<ProductionStep> plan,
			int orders,
			bool vehicleIdle = true,
			IDictionary<string, int> seed = null)
		{
			var owned = new Dictionary<string, int>(
				seed ?? new Dictionary<string, int>(), System.StringComparer.OrdinalIgnoreCase);

			var built = new List<string>();
			for (var i = 0; i < orders; i++)
			{
				var choice = UnitProductionLogic.ChooseNext(
					new ArmyPlanState(5000, [Infantry(), Vehicle(vehicleIdle)], owned), plan);

				if (!choice.IsValid)
					break;

				built.Add(choice.ActorType);
				owned[choice.ActorType] = owned.TryGetValue(choice.ActorType, out var n) ? n + 1 : 1;
			}

			return built;
		}

		// --- The bug ------------------------------------------------------------

		[Test]
		public void EveryPlanKeepsBuildingRocketsWhenTheRocketsAreGone()
		{
			// The regression. A rifle-heavy army that has lost its rocket screen must buy the
			// screen back before it buys another rifleman, or a single aircraft farms it.
			var noRockets = Owned(("e1", 60), ("e2", 4), ("mtnk", 8), ("jeep", 2));

			foreach (var plan in ReferencePlans.AllProductionPlans)
			{
				var choice = Next(plan, noRockets, vehicleIdle: false);

				Assert.That(choice.IsValid, Is.True);
				Assert.That(choice.ActorType, Is.EqualTo("e3"),
					"an army with no anti-air must replace it before adding more riflemen");
			}
		}

		[Test]
		public void NoAntiAirStepIsSatisfiableByAGroundOnlyUnit()
		{
			// Until(n) counts every candidate in the step, so listing e1 alongside e3 means the
			// riflemen bought two lines earlier already satisfy the rocket step. That is not a
			// fallback, it is a step that can never fire.
			var antiAir = new HashSet<string>(ReferencePlans.AntiAirUnits, System.StringComparer.OrdinalIgnoreCase);

			foreach (var plan in ReferencePlans.AllProductionPlans)
				foreach (var step in plan)
				{
					if (!step.Candidates.Any(antiAir.Contains))
						continue;

					Assert.That(step.Candidates.All(antiAir.Contains), Is.True,
						$"anti-air step [{string.Join(", ", step.Candidates)}] can be satisfied by a ground-only unit");
				}
		}

		[Test]
		public void EveryPlanAsksForSomethingThatCanShootAircraft()
		{
			var antiAir = new HashSet<string>(ReferencePlans.AntiAirUnits, System.StringComparer.OrdinalIgnoreCase);

			foreach (var plan in ReferencePlans.AllProductionPlans)
				Assert.That(plan.Any(s => s.Candidates.Any(antiAir.Contains)), Is.True,
					"nothing else this bot fields can target Air");
		}

		[Test]
		public void TheWarFactoryIsNotStarvedByTheBarracks()
		{
			// Both queues consult the same plan and only the chosen queue's owner acts, so an
			// endless infantry step above an endless vehicle step means the war factory only
			// ever builds on the rare evaluation where the barracks is busy.
			//
			// Harvesters are seeded well past any plan's saturation target because every plan
			// now buys income before the ninth tank; without them this would pass by picking a
			// harvester, which proves nothing about the ordering it exists to check.
			var lateGame = Owned(
				("e1", 60), ("e2", 4), ("e3", 40), ("mtnk", 8), ("jeep", 2), ("harv", 40));

			foreach (var plan in ReferencePlans.AllProductionPlans)
			{
				var choice = Next(plan, lateGame);

				Assert.That(choice.Queue, Is.EqualTo("Vehicle"),
					"with both queues idle and every count met, the expensive queue must get the order");

				Assert.That(choice.ActorType, Is.AnyOf("mtnk", "ltnk"),
					"and the order must be the combat vehicle, not more economy");
			}
		}

		// --- Income --------------------------------------------------------------

		[Test]
		public void EveryPlanReplacesALostHarvester()
		{
			// The regression this exists to prevent. A refinery carries a FreeActor harvester and
			// hands out exactly one, ever, so a plan that never names harv has no way back from
			// losing them. Refineries are seeded at the target and harvesters at none, which is
			// exactly the state badland-ridges ended in: the last harvester died at 978s and the
			// bot spent the rest of the match with refineries standing and no income at all.
			//
			// Everything else a plan can ask for is seeded, including the scouts: this asserts
			// that income is what a satisfied plan buys next, not that it outranks the first jeep.
			var economyDead = Owned(
				("e1", 60), ("e2", 4), ("e3", 40), ("mtnk", 8), ("bggy", 2), ("proc", 4));

			foreach (var plan in ReferencePlans.AllProductionPlans)
			{
				var choice = Next(plan, economyDead);

				Assert.That(choice.IsValid, Is.True);
				Assert.That(choice.ActorType, Is.EqualTo("harv"),
					"an army with no income has nothing to spend on the next tank");
			}
		}

		[Test]
		public void NoHarvesterStepIsSatisfiableByACombatVehicle()
		{
			// Same trap as the anti-air steps: Until(n) counts every candidate a step lists, so
			// a step written ["harv", "ltnk"] is satisfied by the tanks bought two lines earlier
			// and never buys income.
			var harvesters = new HashSet<string>(ReferencePlans.HarvesterUnits, System.StringComparer.OrdinalIgnoreCase);

			foreach (var plan in ReferencePlans.AllProductionPlans)
				foreach (var step in plan)
				{
					if (!step.Candidates.Any(harvesters.Contains))
						continue;

					Assert.That(step.Candidates.All(harvesters.Contains), Is.True,
						$"income step [{string.Join(", ", step.Candidates)}] can be satisfied by something that earns nothing");
				}
		}

		[Test]
		public void EveryPlanWantsMoreHarvestersThanTheRefineriesHandOut()
		{
			// One harvester per refinery is the ceiling this bot keeps running into: on the last
			// badland-ridges the only three harv that ever existed appeared at 51s, 117s and
			// 860s, each in the same second as a proc, so every one was a refinery's free actor.
			// It had two for the whole match and its cash read 0 from 150s to the end.
			//
			// Refineries are now the floor rather than the ceiling, so the test is the other way
			// round from what it looks: a plan whose harvester target does not exceed the
			// refinery target has given the Vehicle queue nothing to add.
			var refineries = new HashSet<string>(ReferencePlans.Refineries, System.StringComparer.OrdinalIgnoreCase);
			var harvesters = new HashSet<string>(ReferencePlans.HarvesterUnits, System.StringComparer.OrdinalIgnoreCase);

			var free = ReferencePlans.AllBuildSteps
				.Where(s => s.Candidates.Any(refineries.Contains))
				.Max(s => s.DesiredCount);

			foreach (var plan in ReferencePlans.AllProductionPlans)
			{
				var wanted = plan
					.Where(s => s.Candidates.Any(harvesters.Contains))
					.Select(s => s.DesiredCount)
					.DefaultIfEmpty(0)
					.Max();

				Assert.That(wanted, Is.GreaterThan(free),
					$"a plan that wants {wanted} harvesters against {free} refineries buys no income at all");
			}
		}

		[Test]
		public void TheOpeningBuysIncomeBeforeItBuysTanks()
		{
			// A harvester costs 1,100 against a light tank's 750 and needs only a refinery, so it
			// is available from the moment the vehicle queue is, and it is the only purchase on
			// the list that pays for the next one.
			var built = Run(ReferencePlans.OpeningTrain, 30);

			var firstHarvester = built.IndexOf("harv");
			var firstTank = built.FindIndex(i => i is "mtnk" or "ltnk");

			Assert.That(firstHarvester, Is.GreaterThanOrEqualTo(0), "the opening must buy income at all");
			Assert.That(firstTank, Is.GreaterThanOrEqualTo(0), "...and must still reach combat vehicles");
			Assert.That(firstHarvester, Is.LessThan(firstTank),
				"income compounds for the rest of the match; a light tank does not");
		}

		[Test]
		public void IncomeDoesNotCrowdOutTheArmy()
		{
			// The other half of the trade. Harvester steps are finite and sit above the endless
			// combat step, so a bot at saturation must go back to buying things that shoot.
			var saturation = EconomyPlanLogic.TargetFor(
				EconomyPlanLogic.Rungs(ReferencePlans.OpeningTrain), Harvesters);

			var built = Run(ReferencePlans.OpeningTrain, 60);

			Assert.That(built.Count(i => i == "harv"), Is.EqualTo(saturation),
				"a long run must stop buying harvesters at the saturation target, not keep going");

			Assert.That(built.Count(i => i is "mtnk" or "ltnk"), Is.GreaterThan(saturation),
				"and past saturation the surplus belongs in things that shoot");
		}

		// --- What the plans actually produce -------------------------------------

		[Test]
		public void TheOpeningStillGetsBodiesOutFirst()
		{
			// Rockets cost three times a rifleman. Buying them before there is anything standing
			// would trade one weakness for a rush loss.
			var built = Run(ReferencePlans.OpeningTrain, 6, vehicleIdle: false);

			Assert.That(built.Take(4), Is.All.EqualTo("e1"),
				"the first orders must still be the cheapest bodies available");

			Assert.That(built.Skip(4).First(), Is.EqualTo("e3"),
				"and rockets must follow immediately, not wait for tech or a doctrine change");
		}

		[Test]
		public void TheOpeningReachesRocketsWithoutTechOfAnyKind()
		{
			// e3 needs a barracks and nothing else, which is the whole reason it is the answer:
			// a plan that waits for an hq has no anti-air for the first several minutes.
			var barracksOnly = new ProductionQueueState("Infantry", true, ["e1", "e3"]);

			var owned = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase) { ["e1"] = 4 };
			var choice = UnitProductionLogic.ChooseNext(
				new ArmyPlanState(5000, [barracksOnly], owned), ReferencePlans.OpeningTrain);

			Assert.That(choice.ActorType, Is.EqualTo("e3"));
		}

		[Test]
		public void ASustainedInfantryArmyIsMostlyRockets()
		{
			// 125 minigunners and nothing else is what losing looks like. Against the armour
			// classes that actually killed this bot the rocket is worth roughly four riflemen
			// per credit, and it is the only one of the two that can fire upwards at all.
			var built = Run(ReferencePlans.OpeningTrain, 60, vehicleIdle: false);

			Assert.That(built.Count(i => i == "e3"), Is.GreaterThan(built.Count / 2),
				"surplus income belongs in rockets, not in a hundred-and-twenty-fifth rifleman");

			Assert.That(built.Count(i => i == "e1"), Is.GreaterThanOrEqualTo(8),
				"keep a cheap core: the rifle is still the best thing in the barracks vs infantry");

			Assert.That(built.Distinct().Count(), Is.GreaterThanOrEqualTo(3),
				"an army of one unit type has a whole target class it cannot touch");
		}

		[Test]
		public void ASustainedArmyStillBuildsVehicles()
		{
			var built = Run(ReferencePlans.OpeningTrain, 60);

			Assert.That(built.Any(i => i is "mtnk" or "ltnk"), Is.True,
				"a war factory that never gets an order is 2,000 credits of nothing");
		}

		[Test]
		public void APlanWithNothingLeftToDoSaysSo()
		{
			Assert.That(UnitProductionLogic.ChooseNext(
				new ArmyPlanState(5000, [Infantry(), Vehicle()], Owned()), []).IsValid, Is.False);
		}
	}
}
