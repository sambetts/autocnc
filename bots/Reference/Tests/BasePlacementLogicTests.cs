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

		static Dictionary<string, int> Owned(params (string Actor, int Count)[] counts)
		{
			var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var (actor, count) in counts)
				owned[actor] = count;

			return owned;
		}

		static PlacementRing RingFor(string item, Dictionary<string, int> owned)
			=> BasePlacementLogic.RingFor(item, owned, Expanding);

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
			// Power, production and defence all want to be behind the front, not beyond it.
			foreach (var item in new[] { "nuke", "pyle", "hand", "weap", "afld", "hq", "gtwr", "gun", "atwr", "sam" })
				Assert.That(RingFor(item, Owned(("proc", 3), (item, 2))), Is.EqualTo(BasePlacementLogic.Default),
					$"{item} should not wander out of the base");
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
			Assert.That(BasePlacementLogic.RingFor("proc", Owned(), null), Is.EqualTo(BasePlacementLogic.Default));
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
			// BuildBaseMode drives expansion off ReferencePlans.Refineries. If that list ever
			// stops naming the income buildings, expansion silently stops happening.
			Assert.That(ReferencePlans.Refineries, Is.Not.Empty);

			foreach (var refinery in ReferencePlans.Refineries)
				Assert.That(RingFor(refinery, Owned((refinery, 1))), Is.Not.EqualTo(BasePlacementLogic.Default),
					$"{refinery} is income and must expand");
		}
	}
}
