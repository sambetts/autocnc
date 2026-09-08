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
	/// A construction yard owns more than one queue, and the match is decided by whether it
	/// drives all of them.
	/// </summary>
	/// <remarks>
	/// The failure these exist to prevent: every defence in Tiberian Dawn is built from the
	/// yard's <c>Support</c> queue, and <see cref="BaseBuildLogic.ChooseNext"/> silently skips a
	/// step it cannot build. A yard driving only <c>Building</c> therefore reads
	/// "four guard towers" as "nothing" — no order, no error, no tower. On badland-ridges the
	/// bot spent roughly 380 seconds in a doctrine whose entire premise is static defence and
	/// finished the match with zero defensive structures built.
	/// </remarks>
	[TestFixture]
	public class BaseConstructionLogicTests
	{
		/// <summary>
		/// What a Tiberian Dawn Building queue offers: economy and tech. No <c>powr</c> — that is
		/// a Red Alert actor, and this mod's power plant is <c>nuke</c> for both factions.
		/// </summary>
		static readonly string[] BuildingItems = ["nuke", "nuk2", "proc", "pyle", "hand", "weap", "afld", "hq"];

		/// <summary>What a Support queue offers: everything that shoots.</summary>
		static readonly string[] SupportItems = ["gtwr", "gun", "atwr", "sam", "sbag", "silo"];

		static ConstructionQueueState Building(bool busy = false, string ready = null, IEnumerable<string> items = null)
			=> new("Building", true, busy, ready, (items ?? BuildingItems).ToArray());

		static ConstructionQueueState Support(bool busy = false, string ready = null, IEnumerable<string> items = null)
			=> new("Support", true, busy, ready, (items ?? SupportItems).ToArray());

		static Dictionary<string, int> Owned(params (string Actor, int Count)[] counts)
		{
			var owned = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var (actor, count) in counts)
				owned[actor] = count;

			return owned;
		}

		static ConstructionOrder Decide(
			IReadOnlyList<ConstructionQueueState> queues,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<BuildStep> plan = null,
			int cash = 5000,
			int power = 150)
			=> BaseConstructionLogic.ChooseNext(
				queues, cash, power, owned, plan ?? ReferencePlans.DefenceBuild,
				[.. ReferencePlans.PowerPlants]);

		/// <summary>An economy far enough along that the Building queue has run out of plan.</summary>
		static Dictionary<string, int> EconomyDone
			=> Owned(("nuke", 4), ("proc", 3), ("pyle", 1), ("weap", 1), ("hq", 1));

		// --- The bug ------------------------------------------------------------

		[Test]
		public void DefencesAreOrderedFromTheSupportQueue()
		{
			// The whole point. With only the Building queue driven this situation produces
			// nothing at all, because no Building item is left in the plan and the tower steps
			// are skipped as unbuildable.
			var order = Decide([Building(), Support()], EconomyDone);

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.Produce));
			Assert.That(order.Queue, Is.EqualTo("Support"));
			Assert.That(order.Item, Is.EqualTo("gtwr"));
		}

		[Test]
		public void ADefencePlanProducesNothingIfOnlyTheBuildingQueueIsDriven()
		{
			// The old behaviour, asserted so nobody quietly restores it.
			var order = Decide([Building()], EconomyDone);

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.None),
				"a Building-only yard cannot build a single defence, which is the bug");
		}

		[Test]
		public void TheEconomyStillComesFirst()
		{
			// Support must not jump the queue: a tower is worth nothing without an economy.
			var order = Decide([Building(), Support()], Owned());

			Assert.That(order.Queue, Is.EqualTo("Building"));
			Assert.That(order.Item, Is.EqualTo("nuke"), "power before anything else");
		}

		[Test]
		public void ABusyBuildingQueueIsAReasonToDriveSupportNotToStop()
		{
			// Queues run in parallel in the engine, so waiting on one wastes the other.
			var order = Decide([Building(busy: true), Support()], EconomyDone);

			Assert.That(order.Queue, Is.EqualTo("Support"));
			Assert.That(order.Item, Is.EqualTo("gtwr"));
		}

		[Test]
		public void AQueueTheYardDoesNotOwnIsSkipped()
		{
			var unowned = new ConstructionQueueState("Support", false, false, null, SupportItems);
			var order = Decide([Building(busy: true), unowned], EconomyDone);

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.None));
		}

		// --- Placement ----------------------------------------------------------

		[Test]
		public void SomethingFinishedIsPlacedBeforeAnythingNewIsOrdered()
		{
			// A finished structure sitting in the queue is money already spent doing nothing,
			// and nothing behind it can start until it is down.
			var order = Decide([Building(ready: "proc"), Support()], EconomyDone);

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.Place));
			Assert.That(order.Queue, Is.EqualTo("Building"));
			Assert.That(order.Item, Is.EqualTo("proc"));
		}

		[Test]
		public void SomethingFinishedInSupportIsPlacedToo()
		{
			var order = Decide([Building(), Support(ready: "gtwr")], EconomyDone);

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.Place));
			Assert.That(order.Queue, Is.EqualTo("Support"));
			Assert.That(order.Item, Is.EqualTo("gtwr"));
		}

		[Test]
		public void PlacingBeatsProducingEvenWhenTheOtherQueueIsIdle()
		{
			var order = Decide([Building(), Support(ready: "gtwr")], Owned());

			Assert.That(order.Action, Is.EqualTo(ConstructionAction.Place),
				"an unplaced structure blocks its own queue; clear it first");
		}

		// --- Housekeeping -------------------------------------------------------

		[Test]
		public void NothingToDoIsSaidClearly()
		{
			var finished = Owned(
				("nuke", 20), ("proc", 20), ("pyle", 20), ("weap", 20),
				("hq", 20), ("gtwr", 20), ("atwr", 20));

			Assert.That(Decide([Building(), Support()], finished).Action,
				Is.EqualTo(ConstructionAction.None));

			Assert.That(BaseConstructionLogic.ChooseNext([], 5000, 150, Owned(), ReferencePlans.DefenceBuild).Action,
				Is.EqualTo(ConstructionAction.None));

			Assert.That(BaseConstructionLogic.ChooseNext(null, 5000, 150, Owned(), ReferencePlans.DefenceBuild).Action,
				Is.EqualTo(ConstructionAction.None));
		}

		[Test]
		public void EveryOrderSaysWhichQueueAndWhy()
		{
			foreach (var queues in new[]
			{
				new[] { Building(), Support() },
				[Building(busy: true), Support()],
				[Building(ready: "proc"), Support()],
				[Building(), Support(ready: "gtwr")],
			})
			{
				var order = Decide(queues, EconomyDone);
				if (order.Action == ConstructionAction.None)
					continue;

				Assert.That(order.Queue, Is.Not.Null.And.Not.Empty);
				Assert.That(order.Item, Is.Not.Null.And.Not.Empty);
				Assert.That(order.Reason, Is.Not.Null.And.Not.Empty);
			}
		}

		[Test]
		public void LowPowerStillJumpsTheQueue()
		{
			// The brownout override must survive being routed through the queue picker.
			var order = Decide([Building(), Support()], EconomyDone, power: -20);

			Assert.That(order.Queue, Is.EqualTo("Building"));
			Assert.That(order.Item, Is.EqualTo("nuke"));
		}

		[Test]
		public void PicksWhicheverFactionVariantTheSupportQueueOffers()
		{
			// Nod's turret rather than GDI's tower, without knowing the faction.
			var nod = Support(items: ["gun", "sam", "sbag"]);
			var order = Decide([Building(busy: true), nod], EconomyDone);

			Assert.That(order.Item, Is.EqualTo("gun"));
		}

		// --- The plans the doctrines actually ship -------------------------------

		[Test]
		public void EveryDefenceStepInEveryPlanIsReachableFromADrivenQueue()
		{
			// A step no driven queue can build neither builds nor blocks: it is dead weight that
			// reads like strategy. Assert against the shipped plans so one cannot be added back.
			var driven = new HashSet<string>(BuildingItems.Concat(SupportItems), System.StringComparer.OrdinalIgnoreCase);

			foreach (var step in ReferencePlans.AllBuildSteps)
				Assert.That(step.Candidates.Any(driven.Contains), Is.True,
					$"no driven queue can build any of [{string.Join(", ", step.Candidates)}]");
		}

		[Test]
		public void TheTurtleActuallyBuildsItsTowers()
		{
			// Walk the shipped Defence plan the way a yard would, and count what comes out.
			var built = Run(ReferencePlans.DefenceBuild, EconomyDone);

			Assert.That(built.Count(i => ReferencePlans.SupportQueueStructures.Contains(i)),
				Is.GreaterThanOrEqualTo(6),
				"a doctrine whose premise is static defence must produce static defence");
		}

		[Test]
		public void TheTurtleBuildsSomethingThatCanShootAircraft()
		{
			// Nothing else this bot fields can target Air, so if the plan does not put up an
			// anti-air structure a single helicopter farms the base for free.
			var built = Run(ReferencePlans.DefenceBuild, EconomyDone);

			Assert.That(built, Does.Contain("atwr").Or.Contain("sam"));
		}

		[Test]
		public void EveryPlanBuildsSomethingThatCanShootAircraft()
		{
			// The turtle having anti-air is not enough, because the bot is rarely turtling. On
			// badland-ridges it entered Defence at 770s of a 995-second match; aircraft had
			// already taken 67 of its 159 units, and took 21 of the last 23.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var built = Run(plan, EconomyDone);

				Assert.That(built.Any(ReferencePlans.AntiAirStructures.Contains), Is.True,
					"a base with no anti-air is free damage for as long as this plan is running");
			}
		}

		[Test]
		public void TheEconomyUnlocksTheUnitsTheProductionPlansAskFor()
		{
			// mtnk, ltnk, e2 and atwr all require anyhq. Without an hq in the plan every
			// doctrine shares, those steps are skipped in silence: on badland-ridges the first
			// hq landed at 445s, so a 2,000-credit war factory built jeeps for five minutes.
			var built = Run(ReferencePlans.Economy, Owned());

			Assert.That(built, Does.Contain("hq"));
			Assert.That(built.IndexOf("hq"), Is.LessThan(built.IndexOf("weap")),
				"unlock the tanks before paying for the factory that builds them");
		}

		[Test]
		public void TheOpeningBuildsItsTowersToo()
		{
			var built = Run(ReferencePlans.OpeningBuild, EconomyDone);

			Assert.That(built.Count(i => i == "gtwr"), Is.EqualTo(2));
		}

		/// <summary>
		/// Runs a plan to completion against two idle queues, returning what was ordered.
		/// </summary>
		static List<string> Run(IReadOnlyList<BuildStep> plan, Dictionary<string, int> owned)
		{
			var built = new List<string>();

			for (var i = 0; i < 64; i++)
			{
				var order = BaseConstructionLogic.ChooseNext(
					[Building(), Support()], 5000, 150, owned, plan, [.. ReferencePlans.PowerPlants]);

				if (order.Action != ConstructionAction.Produce)
					break;

				built.Add(order.Item);
				owned[order.Item] = owned.TryGetValue(order.Item, out var n) ? n + 1 : 1;
			}

			return built;
		}
	}
}
