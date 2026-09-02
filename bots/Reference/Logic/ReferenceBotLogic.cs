// ============================================================================
//  ReferenceBotLogic — when to change doctrine.
//
//  A pure function of BattleState, with no engine types anywhere near it, for
//  the same reason DefensiveLogic and AttackBaseLogic are: this is the half of
//  a bot that is actually hard, and it is worth being able to test it by
//  writing down a situation and asserting the answer.
//
//  The rules are ordered, and the order is the strategy. Read them top down and
//  the bot's whole personality is in twenty lines.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>The numbers ReferenceBot changes its mind at.</summary>
	/// <remarks>
	/// Separated from the rules so a test can push the bot over a threshold without having to
	/// simulate an economy that reaches it, and so tuning is one line rather than a hunt.
	/// </remarks>
	public readonly record struct ReferenceBotTuning(
		int AttackArmyValue,      // army worth this much is worth spending
		int RetreatArmyValue,     // an attack that has run this low is over
		int ScoutRefineries,      // economy big enough to spare a jeep
		int RaidEnemies,          // this many at the base is a raid; fewer is a scout
		int AssaultEnemies,       // floor for "not a raid, their army" — the early-game number
		int AssaultForceShare,    // ...and past that, a share of our own force: Units / this
		int DefenceHoldSeconds)   // how long to keep turtling after the shooting stops
	{
		public static ReferenceBotTuning Default { get; } = new(
			AttackArmyValue: 6000,
			RetreatArmyValue: 1500,
			ScoutRefineries: 2,
			RaidEnemies: 3,
			AssaultEnemies: 6,
			AssaultForceShare: 4,
			DefenceHoldSeconds: 45);
	}

	public static class ReferenceBotLogic
	{
		public static DoctrineDecision Decide(in BattleState s) => Decide(s, ReferenceBotTuning.Default);

		public static DoctrineDecision Decide(in BattleState s, ReferenceBotTuning t)
		{
			// An army worth spending with somewhere to spend it. Computed up front because two
			// separate rules below need it: one to decide whether a skirmish is worth coming
			// home for, and one to decide where a finished siege goes next.
			var readyToPush = s.ArmyValue >= t.AttackArmyValue && s.EnemyBaseFound;

			// 1. Buildings actually falling over outranks everything else. An army in the field is
			//    worth nothing if the thing it was protecting is gone when it gets back.
			if (s.BuildingsLost > 0)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Defence,
					$"lost {s.BuildingsLost} building(s) in {s.WindowSeconds}s");

			// 2. A raid at the base — but not for a side that has an army and a target. Their army
			//    is at your base because yours is at theirs, and a bot that trades its push for
			//    every skirmish spends the match commuting: it attacks, gets recalled, finds the
			//    raiders gone, attacks again.
			//
			//    The exemption covers being *ready* to push as well as already pushing, because a
			//    veto that only lifts once the push has started can never let one start. Doctrine
			//    switches are rate-limited, so a rule that fires between the intent to attack and
			//    the switch being allowed does not delay the push — it cancels it.
			//
			//    The exemption still has a ceiling, and the ceiling has to scale. Six units in the
			//    base is a siege at four minutes and a nuisance at fifteen, so past the early-game
			//    floor it is a share of our own standing force instead.
			if (s.EnemiesNearBase >= t.RaidEnemies
				&& ((!Is(s.Doctrine, ReferenceDoctrines.Attack) && !readyToPush)
					|| s.EnemiesNearBase >= AssaultSize(s, t)))
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Defence,
					$"{s.EnemiesNearBase} enemy at the base");

			// 3. Turtling with the pressure gone. Held for a while first, because an attack that
			//    has paused to regroup is not an attack that has finished.
			//
			//    A siege that lifts with the army intact goes straight back out rather than
			//    routing through Opening. Opening is where the bot waits, and waiting costs a
			//    whole rate-limit window in which the next raid re-triggers rule 2 — which is how
			//    a bot ends up holding a full army at home for the rest of the match.
			if (Is(s.Doctrine, ReferenceDoctrines.Defence))
			{
				if (s.DoctrineSeconds < t.DefenceHoldSeconds)
					return DoctrineDecision.Continue;

				return readyToPush
					? DoctrineDecision.SwitchTo(ReferenceDoctrines.Attack,
						$"siege lifted with army worth {s.ArmyValue}")
					: DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, "the attack is over");
			}

			// 4. Scouting has answered its question, so stop paying for it.
			if (Is(s.Doctrine, ReferenceDoctrines.Scout) && s.EnemyBaseFound)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, "their base has been found");

			// 5. An army worth spending, and somewhere to spend it. Naming the doctrine already
			//    running is the same as continuing, so this also means "keep attacking".
			if (readyToPush)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Attack,
					$"army worth {s.ArmyValue} and their base is known");

			// 6. A push that has run out of army. Going home and rebuilding beats feeding the
			//    rest of it in one unit at a time.
			if (Is(s.Doctrine, ReferenceDoctrines.Attack) && s.ArmyValue < t.RetreatArmyValue)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, $"army down to {s.ArmyValue}");

			// 7. Economy is up and we still do not know where they live. Worth a jeep to find out,
			//    because rule 5 cannot fire until we do.
			if (!s.EnemyBaseFound && s.Refineries >= t.ScoutRefineries)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Scout, "economy is up and their base is unknown");

			return DoctrineDecision.Continue;
		}

		/// <summary>
		/// How many enemies in the base stop being a raid and start being their army.
		/// </summary>
		/// <remarks>
		/// <see cref="ReferenceBotTuning.AssaultEnemies"/> is the floor, so an opening with a
		/// handful of units still comes home for six. Beyond that it is measured against our own
		/// force, because an absolute count set for the fourth minute vetoes every push from the
		/// tenth minute onwards, and a bot that never pushes loses to one that out-expands it.
		/// </remarks>
		public static int AssaultSize(in BattleState s, ReferenceBotTuning t)
		{
			var share = t.AssaultForceShare > 0 ? s.Units / t.AssaultForceShare : 0;
			return Math.Max(t.AssaultEnemies, share);
		}

		static bool Is(string doctrine, string name) =>
			string.Equals(doctrine, name, StringComparison.OrdinalIgnoreCase);
	}
}
