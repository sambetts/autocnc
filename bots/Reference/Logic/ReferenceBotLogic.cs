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
		int RecallBuildings,      // buildings lost in the window that call a push home
		int DefenceHoldSeconds,   // how long to keep turtling after the shooting stops
		int ScoutHoldSeconds,     // how long to let a search actually search
		int LostContactSeconds)   // seeing nothing this long means we have lost them, not that we are marching
	{
		public static ReferenceBotTuning Default { get; } = new(
			// Commit a formed assault rather than repeatedly spending replacement-sized waves.
			// Four thousand is still reachable early with the probe no longer gated on a known
			// base, while leaving enough mass for the staging logic to deliver a mixed force.
			AttackArmyValue: 4000,

			// A push is over when it is genuinely spent, not when it has taken casualties.
			//
			// This has to sit well below AttackArmyValue or the two thresholds straddle a
			// handful of riflemen and the bot commutes: push at the bar, lose three units, go
			// home, rebuild three units, push again. A collapsing engagement can shed most of
			// its value between assessments, so preserve a useful defensive core while leaving
			// a 2,500-credit rebuild gap below the commitment bar.
			RetreatArmyValue: 1500,

			ScoutRefineries: 2,
			RaidEnemies: 3,
			AssaultEnemies: 6,
			AssaultForceShare: 4,

			// How many buildings have to fall inside the window before a push already under way
			// is called home.
			//
			// One used to be enough, and against an opponent that raids continuously that is a
			// permanent veto: twenty buildings were lost on badland-ridges, so a rule that fires
			// on the first of them in every 60-second window is a rule that fires for most of
			// the match. It is the same trap rule 2 already documents — a veto evaluated between
			// the intent to push and the switch being allowed does not delay the push, it
			// cancels it — and the answer here is the same shape: a bounded exemption rather
			// than a suspended rule. Two structures inside one window is a base being taken
			// apart rather than a raid getting lucky, and it still comes home for that.
			RecallBuildings: 2,

			DefenceHoldSeconds: 45,
			ScoutHoldSeconds: 90,
			LostContactSeconds: 300);
	}

	public static class ReferenceBotLogic
	{
		const string UrgentDefenceReasonId = "reference.doctrine.urgent-defence";

		public static DoctrineDecision Decide(in BattleState s) => Decide(s, ReferenceBotTuning.Default);

		public static DoctrineDecision Decide(in BattleState s, ReferenceBotTuning t)
		{
			// An army worth spending, and — since this round — no longer a requirement that
			// somebody has already stood in front of one of their buildings. Computed up front
			// because three separate rules below need it.
			//
			// The EnemyBaseFound conjunct that used to be here is what made Attack unreachable.
			// It is only ever set by a unit that can see an enemy structure, the side built no
			// scout vehicle at all on badland-ridges, and it first read true at 900s against an
			// army that cleared the threshold from 360s to 570s — so the conjunction was false
			// at every one of 238 assessments and the doctrine ran for zero seconds. A push with
			// no sighting now marches on the map's own symmetry instead; see
			// Modes.AttackBaseMode.Probe, which is where the deduction lives.
			var readyToPush = s.ArmyValue >= t.AttackArmyValue;

			// 1. Buildings actually falling over outranks everything else. An army in the field is
			//    worth nothing if the thing it was protecting is gone when it gets back.
			//
			//    Bounded by the same argument rule 2 makes, and for the same reason: against an
			//    opponent that raids continuously, "any building lost in the last 60 seconds"
			//    describes most of the match, and a veto that broad cancels every push rather
			//    than merely postponing it. Twenty buildings were lost on badland-ridges. So a
			//    side that is pushing, or has the army to push, rides out the first one and
			//    comes home for the second — which is a base being dismantled rather than a raid
			//    getting lucky. A side with no army to spend still turtles on the first, because
			//    it has nothing better to do with the next minute.
			if (s.BuildingsLost > 0
				&& (s.BuildingsLost >= t.RecallBuildings
					|| (!Is(s.Doctrine, ReferenceDoctrines.Attack) && !readyToPush)))
				return DoctrineDecision.SwitchUrgentlyTo(
					ReferenceDoctrines.Defence,
					$"lost {s.BuildingsLost} building(s) in {s.WindowSeconds}s",
					UrgentDefenceReasonId);

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
			//
			//    The exemption also ends when the push is no longer formed. Once an active assault
			//    falls below the same value required to start it, preserving the march while a real
			//    raid is already damaging the base only feeds the survivors away from the fight
			//    that now matters.
			if (Is(s.Doctrine, ReferenceDoctrines.Attack)
				&& !readyToPush
				&& s.BaseUnderAttack
				&& s.EnemiesNearBase >= t.RaidEnemies)
				return DoctrineDecision.SwitchUrgentlyTo(
					ReferenceDoctrines.Defence,
					$"spent assault recalled to threatened base: army worth {s.ArmyValue}, {s.EnemiesNearBase} enemy nearby",
					UrgentDefenceReasonId);

			if (s.EnemiesNearBase >= t.RaidEnemies
				&& ((!Is(s.Doctrine, ReferenceDoctrines.Attack) && !readyToPush)
					|| s.EnemiesNearBase >= AssaultSize(s, t)))
				return DoctrineDecision.SwitchUrgentlyTo(
					ReferenceDoctrines.Defence,
					$"{s.EnemiesNearBase} enemy at the base",
					UrgentDefenceReasonId);

			// 3. Turtling with the pressure gone. The minimum doctrine dwell filters the first
			//    wave; leaving also needs enough rebuilt army not to send an empty base straight
			//    into Opening or Scout.
			//
			//    A siege that lifts with the army intact goes straight back out rather than
			//    routing through Opening. Opening is where the bot waits, and waiting costs a
			//    whole rate-limit window in which the next raid re-triggers rule 2 — which is how
			//    a bot ends up holding a full army at home for the rest of the match.
			//
			//    Do not launch while the base is still actively engaged. Doctrine switches are
			//    rate-limited, so leaving under fire can trap the army in Attack when the next
			//    assessment asks it to turn around.
			if (Is(s.Doctrine, ReferenceDoctrines.Defence))
			{
				if (s.DoctrineSeconds < t.DefenceHoldSeconds)
					return DoctrineDecision.Continue;

				if (readyToPush)
				{
					if (s.BaseUnderAttack || s.EnemiesNearBase >= t.RaidEnemies)
						return DoctrineDecision.Continue;

					return DoctrineDecision.SwitchTo(ReferenceDoctrines.Attack,
						$"launching cleared-base assault with army worth {s.ArmyValue}");
				}

				var calmSeconds = s.SecondsSinceContact < 0
					? s.DoctrineSeconds
					: s.SecondsSinceContact;
				if (s.BaseUnderAttack
					|| s.EnemiesNearBase > 0
					|| calmSeconds < t.DefenceHoldSeconds
					|| s.ArmyValue < t.RetreatArmyValue)
					return DoctrineDecision.Continue;

				return DoctrineDecision.SwitchTo(
					ReferenceDoctrines.Opening,
					$"defence recovery completed after {calmSeconds}s calm with army worth {s.ArmyValue}");
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
			//
			//    Rule 8 sitting below this one no longer starves the search, because the two
			//    thresholds are reached in the right order: the refinery gate precedes the
			//    formed-assault bar, so the scout doctrine is entered first and rule 5 holds it
			//    for its full 90 seconds. What has changed is that a
			//    search which comes back empty no longer parks the army at home for the rest of
			//    the match — it falls through to here and the push goes out anyway, looking with
			//    its whole body instead of with one jeep.
			if (readyToPush)
				return Is(s.Doctrine, ReferenceDoctrines.Attack)
					? DoctrineDecision.Continue
					: DoctrineDecision.SwitchTo(ReferenceDoctrines.Attack,
						s.EnemyBaseFound
							? $"assault mass committed: army worth {s.ArmyValue} and their base is known"
							: $"assault mass committed: army worth {s.ArmyValue}, probing for their base");

			// 7. A push that has run out of army. Going home and rebuilding beats feeding the
			//    rest of it in one unit at a time.
			if (Is(s.Doctrine, ReferenceDoctrines.Attack) && s.ArmyValue < t.RetreatArmyValue)
				return DoctrineDecision.SwitchTo(
					ReferenceDoctrines.Opening,
					$"preserving spent assault reserve: army worth {s.ArmyValue} below {t.RetreatArmyValue}");

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
