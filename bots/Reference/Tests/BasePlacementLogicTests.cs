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
using AutoCnC.Reference.Logic;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	/// <summary>
	/// badland-ridges pushed the economy out correctly and left every tower on the construction
	/// yard: four refineries at 7.07, 11.05, 13.42 and 13.42 cells, and the only three defences
	/// the bot ever built at 1.41, 2.24 and 2.83. A <c>gtwr</c> reaches 6 cells, so 1,850 credits
	/// of defence covered none of the economy, and enemy <c>e3</c> killed all four harvesters at
	/// 670s, 686s, 696s and 699s from 8.54 to 10.05 cells out.
	/// <para>
	/// Everything below is a statement about one half of the correction: a covering structure
	/// goes where the economy is, and it never walks so far in that its weapon stops reaching.
	/// </para>
	/// </summary>
	[TestFixture]
	public class BasePlacementLogicTests
	{
		static IReadOnlyList<ExpandingRole> Expanding => ReferencePlans.ExpandingRoles;

		static IReadOnlyList<CoveringRole> Covering => ReferencePlans.CoveringRoles;

		/// <summary>The cheap ground tower's reach, in cells, straight out of game-rules.json.</summary>
		const int GuardTowerReachCells = 6;

		/// <summary>How far the outermost refinery stood from the yard on badland-ridges.</summary>
		const int OuterRefineryCells = 13;

		static Dictionary<string, int> Owned(int refineries, int power, int towers = 0, int antiAir = 0)
		{
			var owned = new Dictionary<string, int>
			{
				["fact"] = 1,
				["proc"] = refineries,
				["nuke"] = power,
				["hand"] = 1,
			};

			if (towers > 0)
				owned["gtwr"] = towers;

			if (antiAir > 0)
				owned["sam"] = antiAir;

			return owned;
		}

		static IReadOnlyList<PlacementRing> LadderFor(string item, IReadOnlyDictionary<string, int> owned)
			=> BasePlacementLogic.LadderFor(item, owned, Expanding, Covering);

		// --- A tower follows the economy out -----------------------------------------------

		/// <summary>
		/// The decisive case. Two refineries are standing, so the frontier has moved; the second
		/// guard tower must be looked for out there rather than at the two cells
		/// <c>FindBuildLocation</c> hands back for the default ring.
		/// </summary>
		[Test]
		public void SecondGuardTowerIsLookedForOnTheEconomysFrontier()
		{
			var ladder = LadderFor("gtwr", Owned(refineries: 2, power: 2, towers: 1));

			Assert.That(ladder, Has.Count.GreaterThan(1), "a covering tower should have a real ladder, not just the default ring");
			Assert.That(ladder[0].MinRangeCells, Is.EqualTo(8));
			Assert.That(ladder[0].MaxRangeCells, Is.EqualTo(20));
		}

		/// <summary>
		/// The number that matters. On badland-ridges the second tower landed 2.24 cells out and
		/// reached 6, so it covered nothing past 8.24; the outer refineries were at 11.05 and
		/// 13.42. The furthest rung plus the tower's reach has to clear the outer refinery.
		/// </summary>
		[Test]
		public void SecondGuardTowerWouldReachBadlandRidgesOuterRefineries()
		{
			var ladder = LadderFor("gtwr", Owned(refineries: 2, power: 2, towers: 1));

			Assert.That(ladder[0].MinRangeCells + GuardTowerReachCells,
				Is.GreaterThanOrEqualTo(OuterRefineryCells),
				"a tower placed on the first rung must cover the refineries it was bought to defend");
		}

		/// <summary>A fully expanded economy pulls the tower ring right out to the clamped frontier.</summary>
		[Test]
		public void GuardTowerFollowsAFullyExpandedEconomy()
		{
			var ladder = LadderFor("gtwr", Owned(refineries: 4, power: 4, towers: 2));

			Assert.That(ladder[0], Is.EqualTo(new PlacementRing(16, 32)));
		}

		// --- ...but never so far in that it stops covering anything -------------------------

		/// <summary>
		/// The floor. With the frontier at 16 cells and a 6-cell weapon, a tower inside 10 cells
		/// has stopped being cover, so no rung between the default ring and 10 is worth a
		/// <c>FindBuildLocation</c> call.
		/// </summary>
		[Test]
		public void GuardTowerLadderNeverFallsInsideItsOwnReachOfTheFrontier()
		{
			var ladder = LadderFor("gtwr", Owned(refineries: 4, power: 4, towers: 2));

			Assert.That(ladder, Has.Count.GreaterThan(1));

			foreach (var rung in ladder.Take(ladder.Count - 1))
				Assert.That(rung.MinRangeCells, Is.GreaterThanOrEqualTo(16 - GuardTowerReachCells),
					$"rung {rung} is closer than the tower's reach allows");

			Assert.That(ladder.Select(r => r.MinRangeCells), Does.Contain(16 - GuardTowerReachCells),
				"the floor is itself the closest rung that still covers the frontier");
		}

		/// <summary>
		/// Reach is the pessimistic member of the pair, so the <c>atwr</c>/<c>sam</c> ladder is
		/// floored at 7 cells of reach rather than the SAM site's 10.
		/// </summary>
		[Test]
		public void AntiAirPairIsFlooredByTheShorterOfTheTwoReaches()
		{
			var ladder = LadderFor("sam", Owned(refineries: 4, power: 4, antiAir: 1));

			Assert.That(ladder.Select(r => r.MinRangeCells), Does.Contain(16 - 7));
			Assert.That(ladder.Select(r => r.MinRangeCells), Does.Not.Contain(16 - 10));
		}

		/// <summary>
		/// A cramped base still gets its tower. The ladder always ends at the default ring, because
		/// 600 credits wedged in the Support queue with nowhere legal to stand is worse than a
		/// tower at home.
		/// </summary>
		[Test]
		public void EveryCoveringLadderStillEndsAtTheDefaultRing()
		{
			foreach (var owned in new[]
			{
				Owned(refineries: 2, power: 2, towers: 1),
				Owned(refineries: 4, power: 4, towers: 2),
				Owned(refineries: 4, power: 5, antiAir: 1),
			})
			{
				var ladder = LadderFor("gtwr", owned);
				Assert.That(ladder[^1], Is.EqualTo(BasePlacementLogic.Default));
			}
		}

		// --- ...and the first of each kind still guards the base itself ---------------------

		/// <summary>The yard, the barracks and the factory keep one tower of each kind over them.</summary>
		[Test]
		public void FirstTowerOfEachKindStaysHome()
		{
			Assert.That(LadderFor("gtwr", Owned(refineries: 4, power: 4)),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));

			Assert.That(LadderFor("sam", Owned(refineries: 4, power: 4, towers: 3)),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
		}

		/// <summary>Nothing has expanded yet, so there is no frontier to cover and home is right.</summary>
		[Test]
		public void TowerStaysHomeUntilTheEconomyHasActuallyExpanded()
		{
			var ladder = LadderFor("gtwr", Owned(refineries: 1, power: 2, towers: 1));

			Assert.That(ladder, Is.EqualTo(new[] { BasePlacementLogic.Default }));
		}

		// --- The frontier is where the economy is, not where the next one is going ----------

		[Test]
		public void FrontierRingTracksTheLastStructureThatLanded()
		{
			Assert.That(BasePlacementLogic.FrontierRing(Owned(1, 2), Expanding), Is.EqualTo(BasePlacementLogic.Default));
			Assert.That(BasePlacementLogic.FrontierRing(Owned(2, 2), Expanding), Is.EqualTo(BasePlacementLogic.RingAt(1)));
			Assert.That(BasePlacementLogic.FrontierRing(Owned(3, 3), Expanding), Is.EqualTo(BasePlacementLogic.RingAt(2)));
			Assert.That(BasePlacementLogic.FrontierRing(Owned(4, 4), Expanding), Is.EqualTo(BasePlacementLogic.RingAt(3)));
		}

		/// <summary>Power plants lead the expansion, so a power frontier beyond the refineries wins.</summary>
		[Test]
		public void FrontierRingTakesTheFurthestRoleNotTheFirst()
		{
			Assert.That(BasePlacementLogic.FrontierRing(Owned(refineries: 2, power: 6), Expanding),
				Is.EqualTo(BasePlacementLogic.RingAt(4)));
		}

		// --- Regression guards for the refactor (these also pass against the old code) ------

		/// <summary>
		/// Documentation, not a discriminator: the economy's own ladder must be exactly what it
		/// was before covering roles existed, or this change moved refineries as a side effect.
		/// </summary>
		[Test]
		public void EconomyPlacementIsUnchangedByCoveringRoles()
		{
			foreach (var item in new[] { "proc", "nuke", "hq", "hand", "afld" })
			foreach (var owned in new[] { Owned(1, 2), Owned(2, 2), Owned(3, 3), Owned(4, 4) })
				Assert.That(LadderFor(item, owned),
					Is.EqualTo(BasePlacementLogic.LadderFor(item, owned, Expanding)),
					$"{item} moved");
		}

		/// <summary>
		/// Documentation, not a discriminator: an unfloored ladder is the original sequence, so
		/// the floor is genuinely additive.
		/// </summary>
		[Test]
		public void UnflooredLadderIsTheOriginalSequence()
		{
			Assert.That(BasePlacementLogic.Ladder(new PlacementRing(8, 20)), Is.EqualTo(new[]
			{
				new PlacementRing(8, 20),
				new PlacementRing(6, 20),
				new PlacementRing(4, 20),
				BasePlacementLogic.Default,
			}));

			Assert.That(BasePlacementLogic.Ladder(BasePlacementLogic.Default),
				Is.EqualTo(new[] { BasePlacementLogic.Default }));
		}

		/// <summary>A floor at or below the default ring cannot change anything.</summary>
		[Test]
		public void AFloorInsideTheDefaultRingIsInert()
		{
			var target = new PlacementRing(16, 32);

			Assert.That(BasePlacementLogic.Ladder(target, BasePlacementLogic.DefaultMinRangeCells),
				Is.EqualTo(BasePlacementLogic.Ladder(target)));
			Assert.That(BasePlacementLogic.Ladder(target, -5), Is.EqualTo(BasePlacementLogic.Ladder(target)));
		}

		/// <summary>A floor above the ambition collapses to the ambition plus the fallback.</summary>
		[Test]
		public void AFloorBeyondTheTargetCollapsesToTheTarget()
		{
			Assert.That(BasePlacementLogic.Ladder(new PlacementRing(16, 32), 40), Is.EqualTo(new[]
			{
				new PlacementRing(16, 32),
				BasePlacementLogic.Default,
			}));
		}
	}
}
