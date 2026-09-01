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
		int DefenceHoldSeconds)   // how long to keep turtling after the shooting stops
	{
		public static ReferenceBotTuning Default { get; } = new(
			AttackArmyValue: 6000,
			RetreatArmyValue: 1500,
			ScoutRefineries: 2,
			RaidEnemies: 3,
			DefenceHoldSeconds: 45);
	}

	public static class ReferenceBotLogic
	{
		public static DoctrineDecision Decide(in BattleState s) => Decide(s, ReferenceBotTuning.Default);

		public static DoctrineDecision Decide(in BattleState s, ReferenceBotTuning t)
		{
			// 1. Buildings actually falling over outranks everything else. An army in the field is
			//    worth nothing if the thing it was protecting is gone when it gets back.
			if (s.BuildingsLost > 0)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Defence,
					$"lost {s.BuildingsLost} building(s) in {s.WindowSeconds}s");

			// 2. A raid at the base — but not while pushing. Their army is at your base because
			//    yours is at theirs, and a bot that trades its push for every skirmish spends the
			//    match commuting: it attacks, gets recalled, finds the raiders gone, attacks
			//    again. A push is a deliberate trade, so only rule 1 calls one off.
			if (!Is(s.Doctrine, ReferenceDoctrines.Attack) && s.EnemiesNearBase >= t.RaidEnemies)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Defence,
					$"{s.EnemiesNearBase} enemy at the base");

			// 3. Turtling with the pressure gone. Held for a while first, because an attack that
			//    has paused to regroup is not an attack that has finished.
			if (Is(s.Doctrine, ReferenceDoctrines.Defence))
				return s.DoctrineSeconds >= t.DefenceHoldSeconds
					? DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, "the attack is over")
					: DoctrineDecision.Continue;

			// 4. Scouting has answered its question, so stop paying for it.
			if (Is(s.Doctrine, ReferenceDoctrines.Scout) && s.EnemyBaseFound)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, "their base has been found");

			// 5. An army worth spending, and somewhere to spend it. Naming the doctrine already
			//    running is the same as continuing, so this also means "keep attacking".
			if (s.ArmyValue >= t.AttackArmyValue && s.EnemyBaseFound)
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

		static bool Is(string doctrine, string name) =>
			string.Equals(doctrine, name, StringComparison.OrdinalIgnoreCase);
	}
}
