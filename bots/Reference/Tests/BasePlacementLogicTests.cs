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

using System;
using System.Collections.Generic;
using System.Linq;
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	/// <summary>
	/// A base that never expands is an economy with a fixed ceiling.
	/// </summary>
	/// <remarks>
	/// The failure these exist to prevent: every structure went into the SDK's default 2–14 cell
	/// ring around the base centre, refineries included. A Tiberian Dawn harvester works the
	/// closest tiberium to the refinery it docks with, so three refineries in one ring is one
	/// harvesting footprint however many harvesters are bought. On badland-ridges income fell
	/// 5,300 → 3,620 → 2,400 credits per 100s across 300–800s while the fleet grew from two
	/// harvesters to five, and cash sat at 0 from 240s to the end of the match.
	/// </remarks>
	[TestFixture]
	public class BasePlacementLogicTests
	{
		static readonly string[] Expanding = [.. ReferencePlans.Refineries];

		static readonly IReadOnlyList<ExpandingRole> Shipped = ReferencePlans.ExpandingRoles;

		static Dictionary<string, int> Owned(params (string Actor, int Count)[] counts)
		{
			var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var (actor, count) in counts)
				owned[actor] = count;

			return owned;
		}

		static PlacementRing RingFor(string item, Dictionary<string, int> owned)
			=> BasePlacementLogic.RingFor(item, owned, Expanding);

		static PlacementRing ShippedRingFor(string item, Dictionary<string, int> owned)
			=> BasePlacementLogic.RingFor(item, owned, Shipped);

		// --- The bug ------------------------------------------------------------

		[Test]
		public void TheFirstRefineryStaysHome()
		{
			// It is the entire economy, it wants the starting field, and it wants to be inside
			// whatever defences exist.
			Assert.That(RingFor("proc", Owned()), Is.EqualTo(BasePlacementLogic.Default));
		}

		[Test]
		public void EachLaterRefineryIsLookedForFurtherOut()
		{
			// The whole point: refinery two must not land on the patch refinery one is stripping.
			var first = RingFor("proc", Owned());
			var second = RingFor("proc", Owned(("proc", 1)));
			var third = RingFor("proc", Owned(("proc", 2)));

			Assert.That(second.MinRangeCells, Is.GreaterThan(first.MinRangeCells));
			Assert.That(third.MinRangeCells, Is.GreaterThan(second.MinRangeCells));

			Assert.That(second.MaxRangeCells, Is.GreaterThan(first.MaxRangeCells));
			Assert.That(third.MaxRangeCells, Is.GreaterThan(second.MaxRangeCells));
		}

		[Test]
		public void TheThirdRefineryClearsTheFirstRingEntirely()
		{
			// badland-ridges put all three refineries inside 2–14 cells (51s, 117s, 398s), so the
			// third one added a docking bay and no new ground at all. The near edge of the third
			// ring must now start at or beyond the far edge of the default one.
			var third = RingFor("proc", Owned(("proc", 2)));

			Assert.That(third.MinRangeCells, Is.GreaterThanOrEqualTo(BasePlacementLogic.DefaultMaxRangeCells),
				"a third refinery inside the first ring is a third refinery on the same tiberium");
		}

		[Test]
		public void EverythingElseKeepsTheDefaultRing()
		{
			// Production and defence want to be behind the front, not beyond it: a barracks
			// wants its rally point inside the base and a tower on the frontier defends nothing.
			// Asserted against the roles the mode actually ships, so it describes real behaviour.
			foreach (var item in new[] { "pyle", "hand", "weap", "afld", "hq", "gtwr", "gun", "atwr", "sam", "silo" })
				Assert.That(ShippedRingFor(item, Owned(("proc", 3), ("nuke", 3), (item, 4))),
					Is.EqualTo(BasePlacementLogic.Default),
					$"{item} should not wander out of the base");
		}

		[Test]
		public void OnlyNamedRolesExpandUnderTheSingleRoleOverload()
		{
			// The refinery-only overload is still the simple statement of the rule, and under it
			// nothing but a refinery moves.
			foreach (var item in new[] { "nuke", "powr", "pyle", "hand", "weap", "afld", "hq", "gtwr", "gun", "atwr", "sam" })
				Assert.That(RingFor(item, Owned(("proc", 3), (item, 2))), Is.EqualTo(BasePlacementLogic.Default),
					$"{item} is not in the expanding set and must keep the default ring");
		}

		// --- Moving the frontier, rather than wishing for it ---------------------

		[Test]
		public void PowerPlantsStayHomeUntilTheBaseHasItsOwn()
		{
			// The first two go up before there is anywhere to expand to — 13s and 65s on
			// badland-ridges, when the only other structures were the yard and one refinery.
			Assert.That(ShippedRingFor("nuke", Owned()), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(ShippedRingFor("nuke", Owned(("nuke", 1))), Is.EqualTo(BasePlacementLogic.Default));
		}

		[Test]
		public void PowerPlantsThenLeadTheFrontier()
		{
			// Nothing cheap that this bot builds carries GivesBuildableArea except the power
			// plant, so if power plants do not move, nothing the refineries need ever becomes
			// legal. All five stood at 2.00-5.83 cells on badland-ridges, every one of them
			// behind refinery number one at 7.07, and the base never grew for 1,519 seconds.
			var third = ShippedRingFor("nuke", Owned(("nuke", 2)));
			var fourth = ShippedRingFor("nuke", Owned(("nuke", 3)));

			Assert.That(third, Is.Not.EqualTo(BasePlacementLogic.Default),
				"the third power plant must push the buildable area outward");
			Assert.That(fourth.MinRangeCells, Is.GreaterThan(third.MinRangeCells));
			Assert.That(fourth.MaxRangeCells, Is.GreaterThan(third.MaxRangeCells));
		}

		[Test]
		public void EachRoleIsCountedOnItsOwn()
		{
			// A power plant going up must not advance the refinery ring, or the two roles
			// leapfrog each other into ground neither can reach.
			var withoutPower = ShippedRingFor("proc", Owned(("proc", 2)));
			var withPower = ShippedRingFor("proc", Owned(("proc", 2), ("nuke", 5)));

			Assert.That(withPower, Is.EqualTo(withoutPower));

			var withoutRefineries = ShippedRingFor("nuke", Owned(("nuke", 3)));
			var withRefineries = ShippedRingFor("nuke", Owned(("nuke", 3), ("proc", 4)));

			Assert.That(withRefineries, Is.EqualTo(withoutRefineries));
		}

		[Test]
		public void EveryExpandingRoleIsAWellFormedRole()
		{
			Assert.That(Shipped, Is.Not.Empty);

			foreach (var role in Shipped)
			{
				Assert.That(role.Candidates, Is.Not.Null.And.Not.Empty);
				Assert.That(role.HomeCount, Is.GreaterThanOrEqualTo(1),
					"a role with no structure at home expands from nothing");
			}
		}

		[Test]
		public void TheExpandingRolesCoverIncomeAndTheGroundItNeeds()
		{
			// Both halves are load-bearing and neither works alone: refineries that expand into
			// ground no structure has made buildable fall home, and power plants that move the
			// frontier with nothing following them just spread the base out.
			static bool Names(IReadOnlyList<ExpandingRole> roles, IReadOnlyList<string> wanted)
			{
				foreach (var role in roles)
					foreach (var candidate in role.Candidates)
						if (wanted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
							return true;

				return false;
			}

			Assert.That(Names(Shipped, ReferencePlans.Refineries), Is.True,
				"income must expand or every refinery shares one patch of tiberium");
			Assert.That(Names(Shipped, ReferencePlans.PowerPlants), Is.True,
				"something that gives buildable area must lead, or the outer rings stay illegal");
		}

		// --- The ladder ----------------------------------------------------------

		[Test]
		public void ALadderAlwaysEndsAtTheDefaultRing()
		{
			// A structure that has been paid for must always have somewhere to go, so the fix
			// can only ever do better than the behaviour it replaces.
			for (var i = 0; i < 12; i++)
			{
				var ladder = BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(i));

				Assert.That(ladder, Is.Not.Empty);
				Assert.That(ladder[^1], Is.EqualTo(BasePlacementLogic.Default),
					$"ring {i} can strand a finished building");
			}
		}

		[Test]
		public void ALadderStartsAtTheRingItWasAskedFor()
		{
			for (var i = 1; i < 12; i++)
			{
				var ring = BasePlacementLogic.RingAt(i);
				var ladder = BasePlacementLogic.Ladder(ring);

				Assert.That(ladder[0], Is.EqualTo(ring),
					"the ambition is still tried first");
			}
		}

		[Test]
		public void ALadderWalksTheNearEdgeInwardAndLeavesTheFarEdgeAlone()
		{
			// Each rung must contain the one before it, so the first rung that matches anything
			// is the furthest-out band the base can actually build in. Shrinking the far edge
			// too would make the rungs disjoint and the answer arbitrary.
			var ladder = BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(3));

			for (var i = 1; i < ladder.Count - 1; i++)
			{
				Assert.That(ladder[i].MinRangeCells, Is.LessThan(ladder[i - 1].MinRangeCells),
					"the near edge must move inward");
				Assert.That(ladder[i].MaxRangeCells, Is.EqualTo(ladder[i - 1].MaxRangeCells),
					"the far edge is the ambition and should not retreat");
			}
		}

		[Test]
		public void ALadderHasRungsBetweenTheAmbitionAndTheDefault()
		{
			// The whole bug: one far request and then the default is a two-rung ladder with no
			// middle, so a refinery that cannot reach 8 cells lands at 3 rather than at 6. Every
			// ring a shipped plan can ask for must offer somewhere in between.
			for (var i = 1; i < 6; i++)
			{
				var ladder = BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(i));

				Assert.That(ladder.Count, Is.GreaterThanOrEqualTo(3),
					$"ring {i} falls straight home when it misses");
			}
		}

		[Test]
		public void TheLadderStepIsSmallerThanTheRingStep()
		{
			// If it were not, a ladder would descend a whole ring per rung and have nothing in
			// between — which is exactly the behaviour being replaced.
			Assert.That(BasePlacementLogic.LadderStepCells,
				Is.LessThan(BasePlacementLogic.RingStepCells));
			Assert.That(BasePlacementLogic.LadderStepCells, Is.GreaterThan(0));
		}

		[Test]
		public void ALadderIsBounded()
		{
			// Each rung is a FindBuildLocation call inside a mode's tick.
			for (var i = 0; i < 40; i++)
				Assert.That(BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(i)).Count,
					Is.LessThanOrEqualTo(BasePlacementLogic.MaxLadderRungs));
		}

		[Test]
		public void ALadderForTheDefaultRingIsJustTheDefaultRing()
		{
			// Nothing to descend to, and no reason to spend extra searches on a structure that
			// was always going to stay home.
			Assert.That(BasePlacementLogic.Ladder(BasePlacementLogic.Default),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
			Assert.That(BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(0)),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
		}

		[Test]
		public void EveryRungIsAWellFormedBand()
		{
			for (var i = 0; i < 12; i++)
				foreach (var rung in BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(i)))
				{
					Assert.That(rung.MinRangeCells, Is.GreaterThanOrEqualTo(BasePlacementLogic.DefaultMinRangeCells));
					Assert.That(rung.MaxRangeCells, Is.GreaterThan(rung.MinRangeCells),
						"a rung whose edges cross can never match a cell");
				}
		}

		[Test]
		public void TheLadderForARefineryReachesTheFrontierTheBotActuallyHad()
		{
			// badland-ridges: every structure stood within 7.07 cells of the yard, so refinery
			// two's 8-20 request was unanswerable and it fell back to 3.00 cells. A rung between
			// 8 and the default is the difference between landing at the frontier and landing at
			// home, so one must exist inside that range.
			var ladder = BasePlacementLogic.Ladder(BasePlacementLogic.RingAt(1));

			Assert.That(ladder.Any(r => r.MinRangeCells > BasePlacementLogic.DefaultMinRangeCells
					&& r.MinRangeCells <= 7),
				Is.True,
				"no rung asks for ground between the base and the failed ambition");
		}

		[Test]
		public void ALadderFromTheShippedRolesIsUsableForBothRoles()
		{
			foreach (var item in new[] { "proc", "nuke" })
			{
				var owned = Owned(("proc", 3), ("nuke", 4));
				var ladder = BasePlacementLogic.LadderFor(item, owned, Shipped);

				Assert.That(ladder.Count, Is.GreaterThanOrEqualTo(3),
					$"{item} should have somewhere to go between the frontier and home");
				Assert.That(ladder[^1], Is.EqualTo(BasePlacementLogic.Default));
			}
		}

		[Test]
		public void LadderInputsThatMakeNoSenseStillYieldTheDefault()
		{
			Assert.That(BasePlacementLogic.LadderFor(null, Owned(), Shipped),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
			Assert.That(BasePlacementLogic.LadderFor("proc", Owned(), null),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
		}

		// --- Staying answerable -------------------------------------------------

		[Test]
		public void TheNearEdgeIsCappedSoTheRequestStaysAnswerable()
		{
			// An unbounded near edge asks for open ground the buildable area never reaches, gets
			// nothing, and falls back to the default ring every time — the old behaviour with
			// extra steps.
			for (var i = 0; i < 20; i++)
				Assert.That(BasePlacementLogic.RingAt(i).MinRangeCells,
					Is.LessThanOrEqualTo(BasePlacementLogic.MaxMinRangeCells));
		}

		[Test]
		public void EveryRingIsAWellFormedBand()
		{
			for (var i = 0; i < 20; i++)
			{
				var ring = BasePlacementLogic.RingAt(i);

				Assert.That(ring.MinRangeCells, Is.GreaterThanOrEqualTo(BasePlacementLogic.DefaultMinRangeCells));
				Assert.That(ring.MaxRangeCells, Is.GreaterThan(ring.MinRangeCells),
					"a ring whose edges cross can never match a cell");
			}
		}

		[Test]
		public void TheRingStepIsSmallerThanTheRingIsWide()
		{
			// A step wider than the band leaves a gap between one refinery's ring and the next,
			// and a structure has to stay connected to the buildable area.
			Assert.That(BasePlacementLogic.RingStepCells,
				Is.LessThan(BasePlacementLogic.DefaultMaxRangeCells - BasePlacementLogic.DefaultMinRangeCells));
		}

		[Test]
		public void ConsecutiveRingsOverlap()
		{
			// Same rule, asserted on the rings themselves: refinery n+1 must be placeable from
			// ground refinery n has already made buildable.
			for (var i = 0; i < 8; i++)
			{
				var here = BasePlacementLogic.RingAt(i);
				var next = BasePlacementLogic.RingAt(i + 1);

				Assert.That(next.MinRangeCells, Is.LessThan(here.MaxRangeCells),
					$"ring {i + 1} starts beyond where ring {i} can reach");
			}
		}

		// --- Housekeeping -------------------------------------------------------

		[Test]
		public void TheDefaultRingMatchesTheSdkDefault()
		{
			// ModeContext.FindBuildLocation(item, minRange = 2, maxRange = 14). If the SDK moves,
			// the fallback in BuildBaseMode stops being a no-op and this should be revisited.
			Assert.That(BasePlacementLogic.Default,
				Is.EqualTo(new PlacementRing(2, 14)));
		}

		[Test]
		public void UnknownAndMissingInputsFallBackToTheDefault()
		{
			Assert.That(BasePlacementLogic.RingFor(null, Owned(), Expanding), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("", Owned(), Expanding), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", null, Expanding), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", Owned(), (IReadOnlyCollection<string>)null), Is.EqualTo(BasePlacementLogic.Default));

			Assert.That(BasePlacementLogic.RingFor(null, Owned(), Shipped), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("", Owned(), Shipped), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", null, Shipped), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", Owned(), (IReadOnlyList<ExpandingRole>)null),
				Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", Owned(), new ExpandingRole[] { new(null, 1) }),
				Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.RingFor("proc", Owned(("proc", 2)), new ExpandingRole[] { new(ReferencePlans.Refineries, 0) }),
				Is.EqualTo(BasePlacementLogic.RingAt(2)),
				"a nonsensical home count must not shift the ring");
		}

		[Test]
		public void CountingIsCaseInsensitive()
		{
			// OwnedBuildingCounts is the host's dictionary, not ours.
			Assert.That(RingFor("PROC", Owned(("Proc", 2))), Is.EqualTo(BasePlacementLogic.RingAt(2)));
		}

		// --- The plans the doctrines actually ship -------------------------------

		[Test]
		public void EveryRefineryAnyPlanBuildsBeyondTheFirstGetsItsOwnGround()
		{
			// Walk the shipped plans: every plan builds at least three refineries, so every plan
			// must reach a ring clear of the default one or the extra ones buy no new tiberium.
			foreach (var plan in ReferencePlans.AllBuildPlans)
			{
				var refineries = plan
					.Where(s => s.Candidates.Any(c => ReferencePlans.Refineries.Contains(c, StringComparer.OrdinalIgnoreCase)))
					.Select(s => s.DesiredCount)
					.DefaultIfEmpty(0)
					.Max();

				Assert.That(refineries, Is.GreaterThanOrEqualTo(2),
					"a plan with one refinery has nothing to expand with");

				Assert.That(BasePlacementLogic.RingAt(refineries - 1).MinRangeCells,
					Is.GreaterThan(BasePlacementLogic.DefaultMinRangeCells),
					"the last refinery this plan builds still lands in the opening ring");
			}
		}

		[Test]
		public void TheRefineryListIsWhatTheModeExpandsOn()
		{
			// BuildBaseMode drives expansion off ReferencePlans.ExpandingRoles. If that list ever
			// stops naming the income buildings, expansion silently stops happening.
			Assert.That(ReferencePlans.Refineries, Is.Not.Empty);

			foreach (var refinery in ReferencePlans.Refineries)
			{
				Assert.That(RingFor(refinery, Owned((refinery, 1))), Is.Not.EqualTo(BasePlacementLogic.Default),
					$"{refinery} is income and must expand");

				Assert.That(ShippedRingFor(refinery, Owned((refinery, 1))), Is.Not.EqualTo(BasePlacementLogic.Default),
					$"{refinery} must still expand under the roles the mode actually ships");
			}
		}
	}
}
