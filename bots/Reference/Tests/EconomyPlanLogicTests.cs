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
	/// The ordering rules a build plan has to obey, checked against the plans that actually ship.
	/// </summary>
	/// <remarks>
	/// The fault these exist to prevent has no symptom until the match is over. Every rung of a
	/// build plan looks reasonable on its own and <c>BaseBuildLogic</c> walks them top down
	/// without complaint, so a refinery in the wrong place is not a bug anyone can see — it is
	/// simply a bot that is poor.
	/// <para>
	/// On badland-ridges the third refinery sat ninth, behind 1,000 credits of <c>hq</c> (stood
	/// 167s) and 2,000 of <c>afld</c> (stood 414s). It was ordered at 498s and stood at 860s of
	/// a 1,023-second match. Only three <c>harv</c> ever existed — 51s, 117s and 860s, each in
	/// the same second as a <c>proc</c>, so every one was a refinery's free actor and the bot
	/// produced none from a queue. Cash read 0 from 150s to the end; total income came to
	/// roughly 12 credits a second against the winner's 88, and its army value grew in a
	/// straight line from 1,300 to 5,700 while the winner's compounded from 0 to 54,100.
	/// </para>
	/// </remarks>
	[TestFixture]
	public class EconomyPlanLogicTests
	{
		/// <summary>
		/// Build costs as resolved by the mod, from <c>game-rules.json</c>.
		/// </summary>
		/// <remarks>
		/// <c>powr</c> is deliberately absent: it does not exist in this mod, so the power rungs
		/// written <c>["powr", "nuke"]</c> are priced at the <c>nuke</c> that actually gets
		/// built. Anything unpriced is free, which is the honest answer for a candidate no
		/// faction can build.
		/// </remarks>
		static readonly Dictionary<string, int> Costs = new(System.StringComparer.OrdinalIgnoreCase)
		{
			["nuke"] = 500, ["nuk2"] = 800,
			["proc"] = 1500, ["silo"] = 100, ["harv"] = 1100,
			["pyle"] = 500, ["hand"] = 500,
			["hq"] = 1000, ["eye"] = 1800, ["tmpl"] = 2000,
			["weap"] = 2000, ["afld"] = 2000,
			["gtwr"] = 600, ["gun"] = 600, ["atwr"] = 1000, ["sam"] = 650,
			["e1"] = 100, ["e2"] = 160, ["e3"] = 300,
			["jeep"] = 400, ["bggy"] = 300, ["mtnk"] = 900, ["ltnk"] = 750,
		};

		/// <summary>What a side has in the bank at second zero, from telemetry.</summary>
		/// <remarks>
		/// Both sides read <c>cash=7500</c> at <c>seconds=0</c>. It is the bot's own cash, so
		/// planning against it breaks no fairness rule — and it is the number that decides how
		/// much economy an opening can buy before it needs any income at all.
		/// </remarks>
		const int StartingCredits = 7500;

		static readonly string[] Refineries = [.. ReferencePlans.Refineries];
		static readonly string[] Factories = [.. ReferencePlans.VehicleFactories];
		static readonly string[] Tech = [.. ReferencePlans.TechStructures];
		static readonly string[] Harvesters = [.. ReferencePlans.HarvesterUnits];

		static IReadOnlyList<PlanRung> Rungs(IReadOnlyList<BuildStep> plan) =>
			EconomyPlanLogic.Rungs(plan);

		/// <summary>The refinery count every shipped build plan works up to.</summary>
		static int RefineryTarget =>
			ReferencePlans.AllBuildPlans.Min(p => EconomyPlanLogic.TargetFor(Rungs(p), Refineries));

		/// <summary>The harvester floor every shipped production plan works up to.</summary>
		static int HarvesterFloor =>
			ReferencePlans.AllProductionPlans.Min(p =>
				p.Where(s => s.Candidates.Any(c => Harvesters.Contains(c, System.StringComparer.OrdinalIgnoreCase)))
					.Select(s => s.DesiredCount)
					.DefaultIfEmpty(0)
					.Min());

		// --- The rule -------------------------------------------------------------

		[Test]
		public void EveryPlanBuysItsIncomeBeforeItBuysAVehicleFactory()
		{
			// The regression, stated exactly. A weap/afld costs 2,000 and earns nothing; a proc
			// costs 1,500 and arrives with a harvester attached. Buying the factory first is how
			// the third refinery ended up 362 seconds in the Building queue, from 498s to 860s.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var rungs = Rungs(plan);

				var income = EconomyPlanLogic.IndexOfRung(rungs, Refineries, RefineryTarget);
				var factory = EconomyPlanLogic.IndexOfRung(rungs, Factories, 1);

				Assert.That(income, Is.Not.EqualTo(EconomyPlanLogic.NotInPlan),
					$"a plan that never reaches {RefineryTarget} refineries has no economy to grow");

				Assert.That(factory, Is.Not.EqualTo(EconomyPlanLogic.NotInPlan),
					"...and one that never builds a vehicle factory can never build a harvester either");

				Assert.That(income, Is.LessThan(factory),
					"income has to come before the building whose only job is to unlock more income");
			}
		}

		[Test]
		public void EveryPlanBuysItsIncomeBeforeItBuysTech()
		{
			// hq earns nothing. It is worth having, but the 1,000 credits it took at 167s were
			// the third refinery, and the bot had 1,231 in the bank at that moment.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var rungs = Rungs(plan);

				var income = EconomyPlanLogic.IndexOfRung(rungs, Refineries, RefineryTarget);
				var tech = EconomyPlanLogic.IndexOfRung(rungs, Tech, 1);

				Assert.That(tech, Is.Not.EqualTo(EconomyPlanLogic.NotInPlan),
					"every plan still has to unlock tanks, grenadiers and the AA tower");

				Assert.That(income, Is.LessThan(tech),
					"tech is what income buys, not the other way round");
			}
		}

		[Test]
		public void TheHarvesterFloorIsReachableWithNoVehicleQueueAtAll()
		{
			// The deadlock the harvester steps walked into. harv needs a Vehicle queue, the
			// Vehicle queue needs 2,000 credits, and 2,000 credits need harvesters: the factory
			// stood at 414s, the queue was given four orders in the whole match, and the one
			// harv it was asked for at 576s was still unpaid when the match ended at 1,023s.
			// Refineries are the way out, because the construction yard owns that queue at
			// second zero.
			Assert.That(HarvesterFloor, Is.GreaterThan(0), "the plans must still ask for harvesters");

			Assert.That(RefineryTarget, Is.GreaterThanOrEqualTo(HarvesterFloor),
				$"{RefineryTarget} refineries hand out {RefineryTarget} free harvesters, and the "
				+ $"plans want {HarvesterFloor} before they will buy a tank");
		}

		[Test]
		public void ARefineryIsTheCheaperHarvesterUntilAFactoryExists()
		{
			// Why the ordering is what it is, in credits rather than prose. If the mod ever
			// changes this, the ladder above should change with it.
			var refinery = Costs["proc"];
			var boughtHarvester = Factories.Max(f => Costs[f]) + Costs["harv"];

			Assert.That(refinery, Is.LessThan(boughtHarvester),
				"a refinery is 1,500 for a harvester and a docking bay; the first bought one is "
				+ "2,000 for the factory plus 1,100 for the harvester");
		}

		[Test]
		public void TheIncomeLadderIsAffordableFromTheOpeningBank()
		{
			// A plan whose economy needs income to pay for itself compounds slowly or not at
			// all. All but the last refinery must come out of the 7,500 a side starts with, so
			// the fleet is already growing before the first credit is earned.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var credits = EconomyPlanLogic.CreditsToReach(
					Rungs(plan), Refineries, RefineryTarget - 1, Costs);

				Assert.That(credits, Is.Not.EqualTo(EconomyPlanLogic.NotInPlan));
				Assert.That(credits, Is.LessThanOrEqualTo(StartingCredits),
					$"{credits} credits to reach {RefineryTarget - 1} refineries, against a "
					+ $"{StartingCredits} opening bank");
			}
		}

		[Test]
		public void TheWholeIncomeLadderIsWithinOneRefineryOfTheOpeningBank()
		{
			// ...and the last one should be a short wait rather than a project. At 860s it was a
			// project.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var credits = EconomyPlanLogic.CreditsToReach(
					Rungs(plan), Refineries, RefineryTarget, Costs);

				Assert.That(credits, Is.LessThanOrEqualTo(StartingCredits + Costs["proc"]),
					$"{credits} credits to reach {RefineryTarget} refineries");
			}
		}

		[Test]
		public void TheShippedLadderIsTheOneTheReadmeDescribes()
		{
			// The numbers, pinned. Power, refinery, power, barracks, refinery, refinery is 6,000
			// of a 7,500 opening bank and puts three harvesters on the field before a credit has
			// to be earned; a power plant and the fourth refinery take it to 8,000, so the last
			// rung is a short wait rather than the 362-second project the third one became.
			var rungs = Rungs(ReferencePlans.OpeningBuild);

			Assert.That(RefineryTarget, Is.EqualTo(4), "four refineries is four free harvesters");
			Assert.That(HarvesterFloor, Is.EqualTo(4), "...which is exactly the harvester floor");

			Assert.That(EconomyPlanLogic.CreditsToReach(rungs, Refineries, 3, Costs), Is.EqualTo(6000));
			Assert.That(EconomyPlanLogic.CreditsToReach(rungs, Refineries, 4, Costs), Is.EqualTo(8000));
		}

		// --- The logic itself -----------------------------------------------------

		static PlanRung Rung(int desired, params string[] candidates) => new(candidates, desired);

		[Test]
		public void TargetForReadsTheHighestRungOfARole()
		{
			IReadOnlyList<PlanRung> plan = [Rung(1, "proc"), Rung(1, "hq"), Rung(3, "proc")];

			Assert.That(EconomyPlanLogic.TargetFor(plan, Refineries), Is.EqualTo(3));
			Assert.That(EconomyPlanLogic.TargetFor(plan, Factories), Is.EqualTo(0),
				"a role no rung names is a role the plan never buys");
		}

		[Test]
		public void IndexOfRungFindsWhereARoleReachesACount()
		{
			IReadOnlyList<PlanRung> plan = [Rung(1, "proc"), Rung(1, "hq"), Rung(3, "proc")];

			Assert.That(EconomyPlanLogic.IndexOfRung(plan, Refineries, 1), Is.EqualTo(0));
			Assert.That(EconomyPlanLogic.IndexOfRung(plan, Refineries, 2), Is.EqualTo(2),
				"the rung that first meets the count, not the first rung of the role");
			Assert.That(EconomyPlanLogic.IndexOfRung(plan, Refineries, 4),
				Is.EqualTo(EconomyPlanLogic.NotInPlan));
		}

		[Test]
		public void CreditsToReachChargesEachRungOnlyForWhatItAddsToTheCount()
		{
			// Until(n) is cumulative, so the second rung of a role buys the difference and not
			// the whole target over again.
			IReadOnlyList<PlanRung> plan = [Rung(1, "proc"), Rung(1, "hq"), Rung(3, "proc")];

			Assert.That(EconomyPlanLogic.CreditsToReach(plan, Refineries, 1, Costs),
				Is.EqualTo(1500));

			Assert.That(EconomyPlanLogic.CreditsToReach(plan, Refineries, 3, Costs),
				Is.EqualTo(1500 + 1000 + 3000), "one refinery, an hq, then two more refineries");
		}

		[Test]
		public void CreditsToReachStopsAtTheTargetRatherThanTheRung()
		{
			// A rung that overshoots is charged only as far as the question asked.
			IReadOnlyList<PlanRung> plan = [Rung(4, "proc")];

			Assert.That(EconomyPlanLogic.CreditsToReach(plan, Refineries, 2, Costs),
				Is.EqualTo(3000));
		}

		[Test]
		public void FactionAlternativesAreChargedAtTheDearestOneAnyoneCanBuild()
		{
			// ["powr", "nuke"] is a dead entry beside a real one — powr does not exist in this
			// mod — so the rung must cost what a nuke costs rather than nothing.
			IReadOnlyList<PlanRung> plan = [Rung(1, "powr", "nuke"), Rung(1, "proc")];

			Assert.That(EconomyPlanLogic.CreditsToReach(plan, Refineries, 1, Costs),
				Is.EqualTo(500 + 1500));
		}

		[Test]
		public void ARungIsSatisfiedByTheSumOfEverythingItLists()
		{
			// The same rule Until(n) follows: ["hq", "eye", "tmpl"] is already met by the hq the
			// Economy fragment bought, so it must not be charged for again.
			IReadOnlyList<PlanRung> plan = [Rung(1, "hq"), Rung(1, "hq", "eye", "tmpl"), Rung(1, "proc")];

			Assert.That(EconomyPlanLogic.CreditsToReach(plan, Refineries, 1, Costs),
				Is.EqualTo(1000 + 1500), "the tech rung is satisfied, so only the hq is paid for");
		}

		[Test]
		public void EmptyAndMissingInputsAnswerRatherThanThrow()
		{
			Assert.That(EconomyPlanLogic.TargetFor(null, Refineries), Is.EqualTo(0));
			Assert.That(EconomyPlanLogic.TargetFor([], Refineries), Is.EqualTo(0));
			Assert.That(EconomyPlanLogic.IndexOfRung([], Refineries, 1),
				Is.EqualTo(EconomyPlanLogic.NotInPlan));
			Assert.That(EconomyPlanLogic.IndexOfRung([Rung(1, "proc")], Refineries, 0),
				Is.EqualTo(EconomyPlanLogic.NotInPlan));
			Assert.That(EconomyPlanLogic.CreditsToReach(null, Refineries, 1, Costs),
				Is.EqualTo(EconomyPlanLogic.NotInPlan));
			Assert.That(EconomyPlanLogic.Rungs((IReadOnlyList<BuildStep>)null), Is.Empty);
			Assert.That(EconomyPlanLogic.Rungs((IReadOnlyList<ProductionStep>)null), Is.Empty);
		}

		[Test]
		public void ProductionPlansConvertToRungsToo()
		{
			var rungs = EconomyPlanLogic.Rungs(ReferencePlans.OpeningTrain);

			Assert.That(rungs, Has.Count.EqualTo(ReferencePlans.OpeningTrain.Count));
			Assert.That(EconomyPlanLogic.TargetFor(rungs, Harvesters),
				Is.GreaterThan(RefineryTarget),
				"the vehicle queue has to add income the refineries do not hand out for free");
		}
	}
}
