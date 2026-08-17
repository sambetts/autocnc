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
using AutoCnC.Reference;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using NUnit.Framework;

namespace AutoCnC.Reference.Tests
{
	[TestFixture]
	public class BaseBuildLogicTests
	{
		/// <summary>
		/// The plan the shipped module actually declares, so these tests verify the real
		/// strategy rather than a copy of it that can drift.
		/// </summary>
		static readonly string[] PowerCandidates = ["powr", "nuke"];

		static readonly IReadOnlyList<BuildStep> Plan =
			BattleModuleBuilder.Build(new ReferenceBattleModule()).BuildPlan;

		static BasePlanState State(
			int cash = 5000,
			int power = 100,
			string[] buildable = null,
			Dictionary<string, int> owned = null)
			=> new(cash, power, buildable ?? ["powr", "proc", "pyle", "weap"], owned ?? []);

		[Test]
		public void StartsWithPower()
		{
			Assert.That(BaseBuildLogic.ChooseNext(State(), Plan), Is.EqualTo("powr"));
		}

		[Test]
		public void MovesToTheNextStepOnceSatisfied()
		{
			var owned = new Dictionary<string, int> { ["powr"] = 1 };
			Assert.That(BaseBuildLogic.ChooseNext(State(owned: owned), Plan),
				Is.EqualTo("proc"), "power satisfied, economy next");
		}

		[Test]
		public void PrioritisesPowerDuringABrownout()
		{
			// Deep into the plan, but the lights are going out.
			var owned = new Dictionary<string, int> { ["powr"] = 3, ["proc"] = 2, ["pyle"] = 1 };

			// Power candidates are passed explicitly, exactly as BuildBaseMode does — the
			// override is opt-in rather than a hidden default.
			Assert.That(BaseBuildLogic.ChooseNext(State(power: -20, owned: owned), Plan, PowerCandidates),
				Is.EqualTo("powr"), "low power must jump the queue");
		}

		[Test]
		public void PicksWhicheverFactionVariantIsActuallyBuildable()
		{
			// Nod: hand instead of pyle, nuke instead of powr.
			var nod = State(buildable: ["nuke", "proc", "hand"], owned: new Dictionary<string, int> { ["nuke"] = 1 });

			Assert.That(BaseBuildLogic.ChooseNext(nod, Plan), Is.EqualTo("proc"));

			var withRefinery = State(buildable: ["nuke", "proc", "hand"],
				owned: new Dictionary<string, int> { ["nuke"] = 2, ["proc"] = 1 });

			Assert.That(BaseBuildLogic.ChooseNext(withRefinery, Plan), Is.EqualTo("hand"),
				"should pick Nod's barracks without knowing the faction");
		}

		[Test]
		public void SkipsStepsItCannotBuildYet()
		{
			// Barracks not yet available (no prerequisite), so it should not stall the plan.
			var state = State(
				buildable: ["powr", "proc"],
				owned: new Dictionary<string, int> { ["powr"] = 2, ["proc"] = 1 });

			Assert.That(BaseBuildLogic.ChooseNext(state, Plan), Is.EqualTo("proc"),
				"falls through to the next satisfiable step");
		}

		[Test]
		public void ReturnsNullWhenThePlanIsComplete()
		{
			var owned = new Dictionary<string, int>
			{
				["powr"] = 20, ["proc"] = 20, ["pyle"] = 20, ["weap"] = 20, ["gtwr"] = 20, ["hq"] = 20
			};

			Assert.That(BaseBuildLogic.ChooseNext(State(owned: owned), Plan), Is.Null);
		}

		[Test]
		public void ReturnsNullWhenNothingIsBuildable()
		{
			Assert.That(BaseBuildLogic.ChooseNext(State(buildable: []), Plan), Is.Null);
			Assert.That(BaseBuildLogic.ChooseNext(State(), []), Is.Null);
			Assert.That(BaseBuildLogic.ChooseNext(State(), null), Is.Null);
		}

		[Test]
		public void CountsAreCaseInsensitive()
		{
			var owned = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase) { ["POWR"] = 1 };
			Assert.That(BaseBuildLogic.ChooseNext(State(owned: owned), Plan), Is.EqualTo("proc"));
		}

		[Test]
		public void FollowsACustomPlan()
		{
			var plan = new[] { new BuildStep("weap", 2) };
			var state = State(buildable: ["weap"], owned: new Dictionary<string, int> { ["weap"] = 1 });

			Assert.That(BaseBuildLogic.ChooseNext(state, plan), Is.EqualTo("weap"));

			var satisfied = State(buildable: ["weap"], owned: new Dictionary<string, int> { ["weap"] = 2 });
			Assert.That(BaseBuildLogic.ChooseNext(satisfied, plan), Is.Null);
		}
	}

	[TestFixture]
	public class UnarmedUnitTests
	{
		// Regression cover. DefensiveLogic used to issue ReturnToAnchor for any mobile unit,
		// which dragged harvesters off tiberium and killed the economy as soon as a player
		// ran "/mode all DefensiveMode".

		[Test]
		public void DefensiveModeLeavesUnarmedUnitsAlone()
		{
			var harvesterMilesFromHome = new DefensiveState(
				HealthPercent: 100,
				DistanceFromAnchorUnits: 40 * 1024,
				WeaponRangeUnits: 0,
				IsIdle: false,
				HasWeapon: false,
				CanMove: true,
				RepairAvailable: true,
				Threats: []);

			Assert.That(DefensiveLogic.Decide(harvesterMilesFromHome, DefensiveTuning.Default).Action,
				Is.EqualTo(UnitAction.Continue),
				"an unarmed unit must never be dragged back to its anchor");
		}

		[Test]
		public void DefensiveModeDoesNotEvenRetreatAnUnarmedUnit()
		{
			var hurtHarvester = new DefensiveState(100, 0, 0, true, false, true, true, [])
			{
				HealthPercent = 5
			};

			Assert.That(DefensiveLogic.Decide(hurtHarvester, DefensiveTuning.Default).Action,
				Is.EqualTo(UnitAction.Continue));
		}

		[Test]
		public void AttackBaseModeLeavesUnarmedUnitsAlone()
		{
			var unarmed = new AssaultState(
				HealthPercent: 100,
				IsIdle: true,
				HasWeapon: false,
				CanMove: true,
				HasObjective: true,
				ObjectiveActorId: 99,
				DistanceToObjectiveUnits: 30 * 1024,
				WeaponRangeUnits: 0,
				Threats: []);

			Assert.That(AttackBaseLogic.Decide(unarmed, AssaultTuning.Default).Action,
				Is.EqualTo(UnitAction.Continue),
				"don't march unarmed units into the enemy base");
		}

		[Test]
		public void ArmedUnitsAreStillDriven()
		{
			var tank = new DefensiveState(100, 40 * 1024, 4 * 1024, true, true, true, false, []);

			Assert.That(DefensiveLogic.Decide(tank, DefensiveTuning.Default).Action,
				Is.EqualTo(UnitAction.ReturnToAnchor), "the guard must not disable normal behaviour");
		}
	}
}
