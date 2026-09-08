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
		int DefenceHoldSeconds,   // how long to keep turtling after the shooting stops
		int ScoutHoldSeconds,     // how long to let a search actually search
		int LostContactSeconds)   // seeing nothing this long means we have lost them, not that we are marching
	{
		public static ReferenceBotTuning Default { get; } = new(
			AttackArmyValue: 6000,
			RetreatArmyValue: 1500,
			ScoutRefineries: 2,
			RaidEnemies: 3,
			AssaultEnemies: 6,
			AssaultForceShare: 4,
			DefenceHoldSeconds: 45,
			ScoutHoldSeconds: 90,
			LostContactSeconds: 300);
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

			// 4. Contact lost. We found their base, we went there, and now there is nothing of
			//    theirs anywhere — either we levelled it or they rebuilt somewhere we have never
			//    seen. Either way the answer is to go and look, not to stand in the crater.
			//
			//    Without this rule the bot has no way back at all. AttackBaseMode marches on the
			//    last remembered sighting and drops it once a unit stands on the spot and finds
			//    nothing; only a unit that can already SEE an enemy structure ever records a new
			//    one. So an army with nothing to march on stops moving, and a stopped army never
			//    sees anything again. Rule 8 cannot fire either, because EnemyBaseFound never
			//    goes back to false.
			//
			//    On badland-ridges that deadlocked the match: AttackBaseMode issued its last order
			//    at 719s and none in the remaining 1,495s, every assessment from 900s to 1500s
			//    reported unitsKilled 0 with secondsSinceContact climbing 30 -> 570, and a
			//    115-unit army stood still while the other side rebuilt from three buildings to
			//    twenty and went on to win.
			//
			//    This is a backstop rather than the primary escape, because the clock it reads is
			//    about seeing *anything*, not about having a target. A stranded push that their
			//    counter-attack has walked into can see plenty and still have nothing to attack,
			//    which pins SecondsSinceContact at zero and disarms this rule exactly when it is
			//    needed. AttackBaseMode knows the difference and rule 6 now lets it say so.
			if (s.EnemyBaseFound && LostContact(s, t))
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Scout,
					$"nothing of theirs seen for {s.SecondsSinceContact}s; finding them again");

			// 5. Scouting has answered its question, so stop paying for it.
			//
			//    Held first, for the same reason Defence is, and for a sharper one besides:
			//    EnemyBaseFound never goes back to false, so this rule is answered before a
			//    search has begun. It ends every search the instant the host's minimum dwell
			//    allows — 30 seconds, during which a jeep crosses about a tenth of the map.
			//
			//    That was survivable while nothing ever asked for a second search. Now that
			//    AttackBaseMode can say "their base is not there any more" and be heard, the
			//    search it asks for has to be allowed to happen, or the two rules simply trade
			//    the doctrine back and forth every dwell window and the army commutes instead of
			//    looking. On badland-ridges the one search that worked ran 120s (120s to 240s);
			//    the second got the 30s minimum and found nothing.
			//
			//    Continue rather than fall through, so that during the hold the doctrine belongs
			//    to ScoutMode: it ends its own doctrine the moment it sees a structure, and its
			//    answer is better than this rule's stale flag.
			if (Is(s.Doctrine, ReferenceDoctrines.Scout))
			{
				if (s.DoctrineSeconds < t.ScoutHoldSeconds)
					return DoctrineDecision.Continue;

				if (s.EnemyBaseFound)
					return DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, "their base has been found");
			}

			// 6. An army worth spending, and somewhere to spend it.
			//
			//    Only ever a *switch*. A bot that is already attacking says "carry on" by having
			//    no opinion, not by naming Attack again. Re-affirming the doctrine that is
			//    already running looks like a harmless no-op and is not one: the host reads any
			//    named doctrine as the bot having an opinion, and the bot's opinion outranks the
			//    requests its own modes make. Naming Attack while attacking therefore gags
			//    AttackBaseMode — the one part of this bot that can actually see whether there is
			//    anything left to attack.
			//
			//    That is how badland-ridges was lost. AttackBaseMode asked for Scout ("nothing
			//    left to attack") on 82 separate assessments; every one was discarded with
			//    outcome=already-active because this rule kept answering "army worth N and their
			//    base is known". The push had levelled everything it could see by 480s and then
			//    stood in the far corner of the map at (35-39, 69-73) for 230 seconds. From 595s
			//    their counter-attack walked into it: enemiesInSight climbed 5 -> 20, unitsLost
			//    ran 2 -> 51 and unitsKilled stayed 0 the whole way — 71 units lost for no kills.
			//    The bot only escaped at 710s, when the army had bled below AttackArmyValue and
			//    this rule finally stopped firing. Losing the army is not an acceptable way to
			//    discover the attack is over.
			//
			//    Rules 7 and 8 cannot fire while readyToPush holds — 7 needs an army below the
			//    retreat floor and 8 needs a base we have never found — so continuing here is
			//    exactly what falling through to the bottom would do, minus the gag.
			if (readyToPush)
				return Is(s.Doctrine, ReferenceDoctrines.Attack)
					? DoctrineDecision.Continue
					: DoctrineDecision.SwitchTo(ReferenceDoctrines.Attack,
						$"army worth {s.ArmyValue} and their base is known");

			// 7. A push that has run out of army. Going home and rebuilding beats feeding the
			//    rest of it in one unit at a time.
			if (Is(s.Doctrine, ReferenceDoctrines.Attack) && s.ArmyValue < t.RetreatArmyValue)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Opening, $"army down to {s.ArmyValue}");

			// 8. Economy is up and we still do not know where they live. Worth a jeep to find out,
			//    because rule 6 cannot fire until we do.
			if (!s.EnemyBaseFound && s.Refineries >= t.ScoutRefineries)
				return DoctrineDecision.SwitchTo(ReferenceDoctrines.Scout, "economy is up and their base is unknown");

			return DoctrineDecision.Continue;
		}

		/// <summary>
		/// Whether this side has lost the enemy entirely, rather than merely being between fights.
		/// </summary>
		/// <remarks>
		/// The threshold has to clear a legitimate approach march, because a push crossing the map
		/// sees nothing at all the whole way there and must not be called off for it. On
		/// badland-ridges the winning push ran 245s without contact between leaving home and
		/// arriving at their base, so any threshold below that would have cancelled the attack
		/// that very nearly won the match.
		/// <para>
		/// A negative <c>SecondsSinceContact</c> means "never seen them at all", which is rule 8's
		/// business rather than this one's, and comparing it against a positive threshold already
		/// excludes it.
		/// </para>
		/// </remarks>
		public static bool LostContact(in BattleState s, ReferenceBotTuning t) =>
			s.EnemiesInSight == 0 && s.SecondsSinceContact >= t.LostContactSeconds;

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
