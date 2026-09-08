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
			int windowSeconds = 60,
			int enemiesInSight = 0,
			int secondsSinceContact = 0)
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
				EnemiesInSight = enemiesInSight,
				SecondsSinceContact = secondsSinceContact,
			};

		static ReferenceBotTuning Tuning => ReferenceBotTuning.Default;

		/// <summary>Asserts a decision leaves a push that is already running alone.</summary>
		/// <remarks>
		/// Stricter than "the doctrine is still Attack", and deliberately so. A bot that is
		/// already attacking carries on by having <em>no opinion</em>, because the host reads any
		/// named doctrine as an opinion and the bot's opinion outranks the requests its own modes
		/// make. Naming Attack while attacking is therefore not the harmless no-op it looks like:
		/// it gags AttackBaseMode, which is the only part of this bot that can see whether there
		/// is anything left to attack.
		/// </remarks>
		static void AssertThePushCarriesOn(DoctrineDecision decision)
		{
			Assert.That(decision.WantsChange, Is.False,
				"a running push carries on by staying quiet, so its modes can still be heard");
		}

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

			AssertThePushCarriesOn(decision);
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

			AssertThePushCarriesOn(decision);
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

		// --- Rule 4: a push that has run out of enemy --------------------------

		/// <summary>
		/// The longest a legitimate approach march went without seeing anything on
		/// badland-ridges: the winning push left home at 420s and reached their base at 665s.
		/// </summary>
		const int ObservedApproachMarchSeconds = 245;

		[Test]
		public void APushThatCanNoLongerFindThemGoesLookingAgain()
		{
			// The whole match, in one assertion. The bot levelled their base by 720s, dropped the
			// sighting on arriving to find nothing, and then had no rule that could ever send it
			// looking again: AttackBaseMode issued its last order at 719s, every assessment from
			// 900s to 1500s reported no kills, and a 115-unit army stood still while the other
			// side rebuilt from three buildings to twenty and won.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: 21900,
				units: 115,
				refineries: 3,
				enemyBaseFound: true,
				enemiesInSight: 0,
				secondsSinceContact: Tuning.LostContactSeconds));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Scout));
			Assert.That(decision.Reason, Does.Contain("finding them again"));
		}

		[Test]
		public void AnApproachMarchIsNotMistakenForLosingThem()
		{
			// An army crossing the map sees nothing the whole way. Calling that "we have lost
			// them" would cancel every push before it arrived — including the one that took
			// eleven buildings off them in this match.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				units: 75,
				enemyBaseFound: true,
				secondsSinceContact: ObservedApproachMarchSeconds));

			AssertThePushCarriesOn(decision);
		}

		[Test]
		public void TheContactThresholdClearsARealApproachMarch()
		{
			Assert.That(Tuning.LostContactSeconds, Is.GreaterThan(ObservedApproachMarchSeconds),
				"a threshold under the observed march time cancels pushes instead of rescuing them");
		}

		[Test]
		public void SomethingInSightIsNotLostContactHoweverStaleTheClock()
		{
			// Being able to see them is the definition of not having lost them — so rule 4 stays
			// out of it, and the push is left alone. Note what that costs and why rule 6 no longer
			// re-affirms itself: their counter-attack standing on a stranded army is "something in
			// sight", which pins the clock at zero and disarms this rule for good. The way out has
			// to come from AttackBaseMode, which knows the difference between seeing enemies and
			// having something to attack.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				units: 50,
				enemyBaseFound: true,
				enemiesInSight: 4,
				secondsSinceContact: Tuning.LostContactSeconds * 10));

			Assert.That(ReferenceBotLogic.LostContact(
				State(enemiesInSight: 4, secondsSinceContact: Tuning.LostContactSeconds * 10), Tuning),
				Is.False);
			AssertThePushCarriesOn(decision);
		}

		[Test]
		public void NeverHavingSeenThemIsNotLosingThem()
		{
			// A negative clock means "no contact ever", which is rule 8's job, not rule 4's.
			Assert.That(
				ReferenceBotLogic.LostContact(State(secondsSinceContact: -1), Tuning),
				Is.False);
		}

		[Test]
		public void TheSearchIsNotCalledOffJustBecauseWeOnceFoundThem()
		{
			// Rule 5 ends scouting on EnemyBaseFound, and that flag never goes back to false. If
			// it outranked rule 4 the bot would bounce Scout -> Opening -> Attack -> Scout every
			// rate-limit window instead of actually looking.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				armyValue: 21900,
				units: 115,
				refineries: 3,
				enemyBaseFound: true,
				secondsSinceContact: Tuning.LostContactSeconds * 2));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Scout));
		}

		[Test]
		public void FindingThemAgainEndsTheSearchAndRestartsThePush()
		{
			// The scout sees something, the clock resets, and rule 5 hands back to Opening — from
			// where rule 6 sends the army back out with a fresh sighting to march on.
			var found = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				armyValue: 21900,
				units: 115,
				refineries: 3,
				enemyBaseFound: true,
				enemiesInSight: 2,
				secondsSinceContact: 0));

			Assert.That(found.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));

			var next = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Opening,
				armyValue: 21900,
				units: 115,
				refineries: 3,
				enemyBaseFound: true,
				enemiesInSight: 2,
				secondsSinceContact: 0));

			Assert.That(next.Doctrine, Is.EqualTo(ReferenceDoctrines.Attack));
		}

		[Test]
		public void ADefenceUnderSiegeStillOutranksGoingLooking()
		{
			// Rules 1 to 3 are about the base falling over, and none of them care that we cannot
			// find their main force.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Attack,
				armyValue: Tuning.AttackArmyValue,
				units: 50,
				enemyBaseFound: true,
				buildingsLost: 1,
				secondsSinceContact: Tuning.LostContactSeconds * 2));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void TheDeadlockThatLostBadlandRidgesNowBreaksItself()
		{
			// The real window, replayed. From 875s the bot saw nothing at all: secondsSinceContact
			// climbed by five every assessment for the rest of the match while the army grew from
			// 32 units to 115. The old rules answered "Attack" to every one of those and the army
			// never moved again. The host's minimum dwell is modelled because a rescue rule that
			// cannot survive it is no rescue at all.
			var doctrine = ReferenceDoctrines.Attack;
			var doctrineSeconds = 580;
			var searching = 0;

			for (var sinceContact = 30; sinceContact <= 570; sinceContact += AssessmentSeconds)
			{
				var decision = ReferenceBotLogic.Decide(State(
					doctrine: doctrine,
					doctrineSeconds: doctrineSeconds,
					armyValue: 5800 + sinceContact * 30,
					units: 32 + sinceContact / 6,
					refineries: 3,
					enemyBaseFound: true,
					enemiesInSight: 0,
					secondsSinceContact: sinceContact));

				if (decision.WantsChange && decision.Doctrine != doctrine && doctrineSeconds >= MinimumDwellSeconds)
				{
					doctrine = decision.Doctrine;
					doctrineSeconds = 0;
				}

				doctrineSeconds += AssessmentSeconds;

				if (doctrine == ReferenceDoctrines.Scout)
					searching++;
			}

			Assert.That(doctrine, Is.EqualTo(ReferenceDoctrines.Scout),
				"an army that cannot find anything must end this window looking, not standing");
			Assert.That(searching, Is.GreaterThan(0));
		}

		// --- Rules 5 and 8: scouting -------------------------------------------

		[Test]
		public void ASearchIsGivenLongEnoughToActuallySearch()
		{
			// EnemyBaseFound never goes back to false, so this rule is answered before the search
			// starts. Observed at 710s: AttackBaseMode reported their base was gone, the bot went
			// looking, and this rule ended it 30 seconds later — the host's minimum dwell — having
			// found nothing. It then went straight back to attacking a base that was not there.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				doctrineSeconds: MinimumDwellSeconds,
				refineries: 3,
				enemyBaseFound: true));

			Assert.That(decision.WantsChange, Is.False, "a 30-second search is not a search");
		}

		[Test]
		public void TheSearchHoldClearsTheHostsMinimumDwell()
		{
			Assert.That(Tuning.ScoutHoldSeconds, Is.GreaterThan(MinimumDwellSeconds),
				"a hold inside the dwell window changes nothing at all");
		}

		[Test]
		public void ASearchIsNotYankedStraightBackIntoAPushItCannotAim()
		{
			// Rule 6 is why this has to Continue rather than fall through. An army worth spending
			// is not worth spending on a base we have just established is no longer there, and
			// re-entering Attack only makes AttackBaseMode ask for the search again.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				doctrineSeconds: MinimumDwellSeconds,
				armyValue: 21900,
				units: 115,
				refineries: 3,
				enemyBaseFound: true));

			Assert.That(decision.WantsChange, Is.False);
		}

		[Test]
		public void ASearchThatHasHadItsTimeStillHandsBack()
		{
			// The hold is a floor, not a commitment: with the jeeps out of ideas the economy
			// should stop paying for them.
			var decision = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				doctrineSeconds: Tuning.ScoutHoldSeconds,
				refineries: 3,
				enemyBaseFound: true));

			Assert.That(decision.Doctrine, Is.EqualTo(ReferenceDoctrines.Opening));
		}

		[Test]
		public void ABaseFallingOverStillOutranksAnUnfinishedSearch()
		{
			// The hold must not have quietly disarmed rules 1 and 2 for a searching side.
			var falling = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				doctrineSeconds: 5,
				refineries: 3,
				enemyBaseFound: true,
				buildingsLost: 1));

			Assert.That(falling.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));

			var besieged = ReferenceBotLogic.Decide(State(
				doctrine: ReferenceDoctrines.Scout,
				doctrineSeconds: 5,
				refineries: 3,
				enemyBaseFound: true,
				enemiesNearBase: Tuning.RaidEnemies));

			Assert.That(besieged.Doctrine, Is.EqualTo(ReferenceDoctrines.Defence));
		}

		[Test]
		public void ThePushThatWasFarmedInACornerNowLeavesInsteadOfDying()
		{
			// The whole failure, end to end. AttackBaseMode had nothing left to attack from 430s
			// and said so on every assessment; the bot answered "army worth N and their base is
			// known" to all 56 of them, so all 56 were discarded as already-active. At 595s their
			// counter-attack arrived and killed 71 units for zero kills while the army held.
			//
			// The mode's request is modelled because it is the thing that was being thrown away,
			// and the host's minimum dwell is modelled because a rescue that cannot survive it is
			// no rescue at all.
			var doctrine = ReferenceDoctrines.Attack;
			var doctrineSeconds = 90;
			var leftAt = -1;

			for (var seconds = 430; seconds <= 710; seconds += AssessmentSeconds)
			{
				// From 595s their army is standing on ours, which is exactly why rule 4 cannot
				// help: being shot at is "contact".
				var inSight = seconds >= 595 ? 14 : 0;
				var sinceContact = seconds >= 595 ? 0 : (seconds - 485) / AssessmentSeconds * AssessmentSeconds;

				var decision = ReferenceBotLogic.Decide(State(
					doctrine: doctrine,
					doctrineSeconds: doctrineSeconds,
					armyValue: 12400,
					units: 100,
					refineries: 3,
					enemyBaseFound: true,
					enemiesInSight: inSight,
					secondsSinceContact: sinceContact));

				// The host takes the bot's answer where it has one, and the mode's only where it
				// does not. AttackBaseMode asks for Scout on every one of these ticks.
				var wanted = decision.WantsChange ? decision.Doctrine : ReferenceDoctrines.Scout;

				if (wanted != doctrine && doctrineSeconds >= MinimumDwellSeconds)
				{
					doctrine = wanted;
					doctrineSeconds = 0;
					if (leftAt < 0)
						leftAt = seconds;
				}

				doctrineSeconds += AssessmentSeconds;
			}

			Assert.That(leftAt, Is.InRange(430, 480),
				"the push must end when its own mode reports there is nothing left to attack, "
				+ "not 280 seconds later when the army has been ground down below AttackArmyValue");
		}

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

		// --- Rules 6 and 7: pushing --------------------------------------------

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

			// Having no opinion is how a rule says "carry on" — see AssertThePushCarriesOn.
			AssertThePushCarriesOn(decision);
		}

		[Test]
		public void ARunningPushNeverRepeatsItselfOverItsOwnModes()
		{
			// The bug that lost badland-ridges, stated directly. AttackBaseMode ends its own
			// doctrine by calling ctx.SwitchDoctrine, exactly as ScoutMode does — but the host
			// only honours a mode's request when the bot itself has no opinion. Rule 6 used to
			// answer "army worth N and their base is known" on every single assessment, so the
			// request was discarded with outcome=already-active 82 times between 235s and 955s
			// while the mode was shouting that there was nothing left to attack.
			//
			// Whatever else rule 6 does, it must never be the thing that speaks for a push that
			// is already running.
			foreach (var army in new[] { Tuning.AttackArmyValue, Tuning.AttackArmyValue * 4, 21900 })
				foreach (var units in new[] { 0, 50, 115 })
					foreach (var contact in new[] { -1, 0, 30, Tuning.LostContactSeconds - 1 })
					{
						var decision = ReferenceBotLogic.Decide(State(
							doctrine: ReferenceDoctrines.Attack,
							armyValue: army,
							units: units,
							refineries: 3,
							enemyBaseFound: true,
							enemiesInSight: 2,
							secondsSinceContact: contact));

						Assert.That(decision.WantsChange, Is.False,
							$"army {army}, {units} units, {contact}s since contact");
					}
		}

		[Test]
		public void TheArmyThatWasFarmedWhileHoldingNowGetsToLeave()
		{
			// 595s to 710s, replayed. The push had nothing left to attack, so AttackBaseMode
			// asked for Scout every assessment; the bot answered Attack every assessment; the
			// army stood at (35-39, 69-73) and their counter-attack killed 71 of it for no kills
			// in return. unitsKilled was 0 and enemiesInSight climbed 5 -> 20 the whole way, so
			// rule 4 could not fire either. The only thing that eventually freed it was the army
			// bleeding below AttackArmyValue at 710s.
			//
			// The bot must now be silent through all of it, so the mode's request actually lands.
			int[] enemiesInSight = [1, 5, 8, 12, 13, 14, 14, 14, 14, 14, 13, 9, 9, 10, 10, 15, 17, 18, 19, 20, 14, 17, 19];
			int[] armyValue =
			[
				12400, 13150, 12950, 12850, 12450, 12250, 11950, 11650, 11450, 11250, 10950, 10850,
				10350, 10050, 9550, 9250, 8650, 8350, 8700, 7250, 6750, 6250, 6100,
			];

			for (var i = 0; i < enemiesInSight.Length; i++)
			{
				var decision = ReferenceBotLogic.Decide(State(
					doctrine: ReferenceDoctrines.Attack,
					doctrineSeconds: 255 + i * AssessmentSeconds,
					armyValue: armyValue[i],
					units: 100 - i * 3,
					refineries: 3,
					enemyBaseFound: true,
					enemiesInSight: enemiesInSight[i],
					secondsSinceContact: 0));

				Assert.That(decision.WantsChange, Is.False,
					$"at 595+{i * AssessmentSeconds}s the bot must leave the doctrine to its modes");
			}
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
										foreach (var contact in new[] { -1, 0, Tuning.LostContactSeconds })
											yield return State(
												doctrine: doctrine,
												doctrineSeconds: 300,
												armyValue: army,
												units: units,
												refineries: refineries,
												buildingsLost: lost,
												enemiesNearBase: near,
												enemyBaseFound: found,
												enemiesInSight: near,
												secondsSinceContact: contact);
		}
	}
}
