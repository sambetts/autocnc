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
			int units = 0,
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
				Units = units,
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
				enemiesNearBase: Tuning.AssaultEnemies - 1));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void APushIsCalledOffWhenItIsTheirWholeArmyAtTheBase()
		{
			// The exemption above has a ceiling. Without one the bot watches a besieging force
			// take the base apart — harvesters first — and only reacts once a building has
			// actually fallen, by which time the economy is gone.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.AssaultEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
			Assert.That(decision.Reason, Does.Contain("at the base"));
		}

		[Test]
		public void ASiegeIsAnAssaultHoweverBigItGets()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue * 3,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.AssaultEnemies * 5));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void ARaidIsStillARaidWhenNotPushing()
		{
			// The ceiling must not have raised the bar for everyone else.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Opening,
				enemiesNearBase: Tuning.RaidEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		// --- Rule 2: a raid must not veto a push that has not started yet ------

		[Test]
		public void ARaidDoesNotVetoAPushThatIsAboutToStart()
		{
			// The failure this exists to prevent: doctrine switches are rate-limited, so the bot
			// wants Attack, is told to wait, and by the time it may switch a handful of raiders
			// have replaced that intent with Defence. Observed at 810-820s, 940-960s and 1280s of
			// the badland-ridges match — every request to attack was overwritten before it landed
			// and the bot never left home again with a 9,650-value army.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Opening,
				armyValue: Tuning.AttackArmyValue,
				units: 50,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.RaidEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void ARaidStillVetoesWhenThereIsNoPushToProtect()
		{
			// The exemption is for a side with an army and a target. Without either, a raid is
			// still a raid and the bodies still come home.
			var noArmy = ReferenceBotLogic.Decide(State(
				armyValue: Tuning.AttackArmyValue - 1,
				units: 50,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.RaidEnemies));

			var noTarget = ReferenceBotLogic.Decide(State(
				armyValue: Tuning.AttackArmyValue * 2,
				units: 50,
				enemyBaseFound: false,
				enemiesNearBase: Tuning.RaidEnemies));

			Assert.That(noArmy.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
			Assert.That(noTarget.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		// --- Rule 2: how big an incursion has to be to end a push ---------------

		[Test]
		public void TheAssaultThresholdNeverDropsBelowTheEarlyGameFloor()
		{
			// A four-unit opening must not decide that four attackers are beneath its notice.
			Assert.That(
				ReferenceBotLogic.AssaultSize(State(units: 4), Tuning),
				Is.EqualTo(Tuning.AssaultEnemies));
		}

		[Test]
		public void TheAssaultThresholdScalesWithOurOwnForce()
		{
			// Six units in the base is a siege at four minutes and a nuisance at fifteen.
			Assert.That(
				ReferenceBotLogic.AssaultSize(State(units: 48), Tuning),
				Is.EqualTo(48 / Tuning.AssaultForceShare));
		}

		[Test]
		public void HarassmentDoesNotCallOffAPushOnceTheArmyIsBig()
		{
			// 835s: six enemies at a base defended by a fifty-unit side. The old absolute ceiling
			// read that as "their army" and turtled; it is a fifth of a raid party.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: 9650,
				units: 50,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.AssaultEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void ARealSiegeStillBringsTheArmyHome()
		{
			// 975s: fifteen enemies in the base of a fifty-unit side. That is their army.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: 9250,
				units: 50,
				enemyBaseFound: true,
				enemiesNearBase: 15));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void AnEarlyRaidStillBringsTheArmyHome()
		{
			// The scaling must not have quietly disarmed rule 2 for a small side.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				units: 8,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.AssaultEnemies));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
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

		[Test]
		public void ARaidIsSmallerThanAnAssault()
		{
			// The two thresholds only mean anything in that order: if a raid were the larger of
			// the two, the exemption for pushes could never fire and rule 2 would collapse into
			// "always come home". Scaling the assault size must not break that either.
			Assert.That(Tuning.AssaultEnemies, Is.GreaterThan(Tuning.RaidEnemies));

			foreach (var units in new[] { 0, 1, 8, 50, 96, 400 })
				Assert.That(ReferenceBotLogic.AssaultSize(State(units: units), Tuning),
					Is.GreaterThan(Tuning.RaidEnemies), $"at {units} units");
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

		[Test]
		public void ASiegeThatLiftsWithAnArmyIntactGoesStraightBackOut()
		{
			// Opening is where the bot waits, and waiting costs a whole rate-limit window in
			// which the next raid re-triggers rule 2. Observed at 805s: the siege lifted with a
			// 9,650-value army, the bot stepped into Opening, and three raiders put it straight
			// back into Defence before the switch to Attack was ever allowed to land.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Defence,
				doctrineSeconds: Tuning.DefenceHoldSeconds,
				armyValue: Tuning.AttackArmyValue,
				units: 50,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void ASiegeThatLiftsWithNothingLeftRebuildsInstead()
		{
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Defence,
				doctrineSeconds: Tuning.DefenceHoldSeconds,
				armyValue: Tuning.AttackArmyValue - 1,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));
		}

		[Test]
		public void AnArmyThatSurvivesASiegeStopsCommutingAndAttacks()
		{
			// The whole failure, replayed. enemiesNearBase is the real sequence recorded every
			// five seconds from 805s to 965s of the badland-ridges match, and the host's
			// thirty-second minimum dwell between doctrine switches is modelled because that
			// rate limit is precisely what the old rules lost the match to: the bot asked for
			// Attack at 810, 815 and 820, was told to wait, and was carrying a Defence request
			// by the time it could act.
			int[] enemiesNearBase =
			[
				0, 0, 0, 0, 3, 5, 6, 8, 7, 6, 6, 7, 6, 5, 5, 4,
				3, 5, 5, 3, 4, 5, 5, 3, 6, 3, 1, 0, 3, 3, 0, 4, 11,
			];

			var doctrine = ReferenceDoctrines.Defence;
			var doctrineSeconds = Tuning.DefenceHoldSeconds;
			var attackTicks = 0;

			foreach (var near in enemiesNearBase)
			{
				var decision = ReferenceBotLogic.Decide(State(
					doctrine: doctrine,
					doctrineSeconds: doctrineSeconds,
					armyValue: 9250,
					units: 50,
					refineries: 4,
					enemyBaseFound: true,
					enemiesNearBase: near));

				// The host applies a switch only once the current doctrine has run its minimum.
				if (decision.WantsChange && decision.Doctrine != doctrine && doctrineSeconds >= MinimumDwellSeconds)
				{
					doctrine = decision.Doctrine;
					doctrineSeconds = 0;
				}

				doctrineSeconds += AssessmentSeconds;

				if (doctrine == ReferenceDoctrines.Attack)
					attackTicks++;
			}

			Assert.That(doctrine, Is.EqualTo(ReferenceDoctrines.Attack),
				"an intact army must end this window pushing, not holding the base");
			Assert.That(attackTicks, Is.GreaterThan(enemiesNearBase.Length * 3 / 4),
				"the push must survive harassment rather than commute");
		}

		/// <summary>How often the host asks the bot to reassess, in game seconds.</summary>
		const int AssessmentSeconds = 5;

		/// <summary>How long the host makes a doctrine run before it will honour a switch.</summary>
		const int MinimumDwellSeconds = 30;

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
					foreach (var units in new[] { 0, 50 })
						foreach (var refineries in new[] { 1, 3 })
							foreach (var found in new[] { false, true })
								foreach (var lost in new[] { 0, 2 })
									foreach (var near in new[] { 0, Tuning.RaidEnemies, Tuning.AssaultEnemies, 20 })
										yield return State(
											doctrine: doctrine,
											doctrineSeconds: 300,
											armyValue: army,
											units: units,
											refineries: refineries,
											buildingsLost: lost,
											enemiesNearBase: near,
											enemyBaseFound: found);
		}
	}
}
