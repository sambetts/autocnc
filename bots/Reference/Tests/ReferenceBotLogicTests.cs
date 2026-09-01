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
	/// The bot's whole personality, asserted one situation at a time.
	/// </summary>
	/// <remarks>
	/// This is the payoff for keeping the deciding half of a bot free of engine types: a match
	/// where the base starts falling over twenty minutes in takes twenty minutes to reproduce, and
	/// three lines to write down.
	/// </remarks>
	[TestFixture]
	public class ReferenceBotLogicTests
	{
		static BattleState State(
			string doctrine = ReferenceDoctrines.Opening,
			int doctrineSeconds = 120,
			int armyValue = 0,
			int refineries = 1,
			int buildingsLost = 0,
			int enemiesNearBase = 0,
			bool enemyBaseFound = false,
			int windowSeconds = 60)
			=> BattleState.Empty with
			{
				Doctrine = doctrine,
				DoctrineSeconds = doctrineSeconds,
				ArmyValue = armyValue,
				Refineries = refineries,
				BuildingsLost = buildingsLost,
				EnemiesNearBase = enemiesNearBase,
				EnemyBaseFound = enemyBaseFound,
				WindowSeconds = windowSeconds,
			};

		static ReferenceBotTuning Tuning => ReferenceBotTuning.Default;

		// --- Rule 1: home comes first ------------------------------------------

		[Test]
		public void LosingBuildingsSwitchesToDefence()
		{
			var decision = ReferenceBotLogic.Decide(State(buildingsLost: 1));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
			Assert.That(decision.Reason, Does.Contain("1 building"));
		}

		[Test]
		public void EnemiesAtTheBaseSwitchToDefence()
		{
			var decision = ReferenceBotLogic.Decide(State(enemiesNearBase: Tuning.RaidEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void OneEnemyNearTheBaseIsAScoutAndIsIgnored()
		{
			// Calling the army home over one jeep sightseeing is how a bot never fights at all.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Opening,
				enemiesNearBase: 1));

			Assert.That(decision.WantsChange, Is.False);
		}

		[Test]
		public void APushIsNotCalledOffForRaiders()
		{
			// Their army is at your base because yours is at theirs. A bot that trades its push
			// for every skirmish spends the match commuting instead of fighting.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.RaidEnemies * 3));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void APushIsCalledOffWhenBuildingsStartFalling()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				enemyBaseFound: true,
				buildingsLost: 1));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void DefendingBeatsAttacking()
		{
			// An army worth spending is not a reason to leave while the base is being taken apart.
			var decision = ReferenceBotLogic.Decide(State(
				armyValue: Tuning.AttackArmyValue * 2,
				enemyBaseFound: true,
				buildingsLost: 2));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		// --- Rule 2: coming out of a siege -------------------------------------

		[Test]
		public void DefenceHoldsBrieflyAfterTheShootingStops()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Defence,
				doctrineSeconds: Tuning.DefenceHoldSeconds - 1));

			Assert.That(decision.WantsChange, Is.False);
		}

		[Test]
		public void DefenceEndsOnceItHasHeldLongEnough()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Defence,
				doctrineSeconds: Tuning.DefenceHoldSeconds));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));
		}

		// --- Rules 3 and 6: scouting -------------------------------------------

		[Test]
		public void ScoutsOnceTheEconomyCanAffordIt()
		{
			var decision = ReferenceBotLogic.Decide(State(refineries: Tuning.ScoutRefineries));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Scout));
		}

		[Test]
		public void DoesNotScoutOnOneRefinery()
		{
			var decision = ReferenceBotLogic.Decide(State(refineries: 1));

			Assert.That(decision.WantsChange, Is.False);
		}

		[Test]
		public void StopsScoutingOnceTheBaseIsFound()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				refineries: 3,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));
		}

		[Test]
		public void DoesNotScoutWhenTheBaseIsAlreadyKnown()
		{
			var decision = ReferenceBotLogic.Decide(State(refineries: 5, enemyBaseFound: true));

			Assert.That(decision.WantsChange, Is.False);
		}

		// --- Rules 4 and 5: pushing --------------------------------------------

		[Test]
		public void AttacksWithAnArmyWorthSpendingAndSomewhereToSpendIt()
		{
			var decision = ReferenceBotLogic.Decide(State(
				armyValue: Tuning.AttackArmyValue,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void WillNotAttackABaseItHasNeverSeen()
		{
			var decision = ReferenceBotLogic.Decide(State(
				armyValue: Tuning.AttackArmyValue * 3,
				refineries: 1,
				enemyBaseFound: false));

			Assert.That(decision.Doctrine, Is.Not.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void BreaksOffAPushThatHasRunOutOfArmy()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.RetreatArmyValue - 1,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));
		}

		[Test]
		public void KeepsPushingWhileTheArmyHoldsUp()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				enemyBaseFound: true));

			// Naming the doctrine already running is how a rule says "carry on".
			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void RebuildsBetweenPushes()
		{
			// Army spent, base known, economy up: back to Opening rather than straight out again.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Opening,
				armyValue: Tuning.AttackArmyValue / 2,
				refineries: 3,
				enemyBaseFound: true));

			Assert.That(decision.WantsChange, Is.False);
		}

		// --- The shape of the thing --------------------------------------------

		[Test]
		public void EveryDoctrineItCanNameIsOneTheBotOwns()
		{
			var owned = new[]
			{
				ReferenceDoctrines.Opening,
				ReferenceDoctrines.Scout,
				ReferenceDoctrines.Attack,
				ReferenceDoctrines.Defence,
			};

			// A decision naming a doctrine the bot does not own is silently ignored at runtime,
			// which is the kind of bug that looks like "the bot just never attacks".
			foreach (var state in Situations())
			{
				var decision = ReferenceBotLogic.Decide(state);
				if (decision.WantsChange)
					Assert.That(owned, Does.Contain(decision.Doctrine), $"from {state.Doctrine}");
			}
		}

		[Test]
		public void EveryChangeSaysWhy()
		{
			foreach (var state in Situations())
			{
				var decision = ReferenceBotLogic.Decide(state);
				if (decision.WantsChange)
					Assert.That(decision.Reason, Is.Not.Null.And.Not.Empty, $"from {state.Doctrine}");
			}
		}

		/// <summary>A spread of situations wide enough to reach every rule.</summary>
		static System.Collections.Generic.IEnumerable<BattleState> Situations()
		{
			string[] doctrines =
			[
				ReferenceDoctrines.Opening,
				ReferenceDoctrines.Scout,
				ReferenceDoctrines.Attack,
				ReferenceDoctrines.Defence,
			];

			foreach (var doctrine in doctrines)
				foreach (var army in new[] { 0, Tuning.RetreatArmyValue, Tuning.AttackArmyValue })
					foreach (var refineries in new[] { 1, 3 })
						foreach (var found in new[] { false, true })
							foreach (var lost in new[] { 0, 2 })
								yield return State(
									doctrine: doctrine,
									doctrineSeconds: 300,
									armyValue: army,
									refineries: refineries,
									buildingsLost: lost,
									enemyBaseFound: found);
		}
	}
}
