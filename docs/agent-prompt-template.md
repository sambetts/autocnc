Improve the AutoC&C battle bot in `{workspace}` using evidence from its latest completed fight.

## Mechanics and SDK reference

Everything in this section is injected from version control and generated from the compiled
assemblies. It is authoritative: if a member is not listed, it does not exist. Do not spend the
budget rediscovering it, and do not copy it into the prompt you propose at the end.

{gameMechanics}

## Required context

Read the game and bot guide first: `{gameGuide}`

Query the resolved actor and weapon stats as needed: `{gameRules}`

Treat the guide as authoritative for game mechanics, fair information access, and the SDK.

## Priorities for this phase

Three gaps decide this bot's matches. Choose this round's change from them, say which one you chose
and cite the number that chose it, and write at least one check on the metric it targets. All of
these numbers are precomputed in `{summary}`.

1. **Produce more.** `scale` sets this side's income, spend, harvesters, unit factories and army
   beside the opponent's. In the twelve fights before this section was written, every loss had a
   `scale.spendVsOpponentPercent` of 40 or less and every win had 71 or more. The losses were
   economies that collapsed: a mean of 1.3 to 4.8 harvesters on the field against the opponent's
   6.5 to 12.8, and a peak of 6 to 9 against 13 to 23. Income per harvester
   (`scale.ownEarnedPerHarvesterSecond`) differed far less than harvester count, so the gap is how
   many harvesters there are and how long they live. Then spend everything that comes in: keep
   every queue busy (`production` secondsIdle, `economy.meanIdleCash`), and turn cash that banks
   up into another barracks or war factory (`scale.ownUnitFactoriesPeak`). Work on this first
   while `spendVsOpponentPercent` is below 70.
2. **See what is coming.** `intel.threats` compares when each enemy unit type was first seen with
   when it first hit this side. `leadSeconds` is negative when a type, usually artillery, hit
   before anything saw it. `intel.lateSightingLossPercent` is the share of losses to types that
   arrived unannounced, and it was 55 to 100 in eleven of those twelve fights, wins included.
   `intel.enemyStructuresFirstSeen` dates their tech: a helipad or airfield means aircraft are
   coming. A counter can only be built against something that has been seen, so scouting must go
   on after their base is found. Watch their approach, and look into their base every few
   minutes. Work on this when `lateSightingLossPercent` is 50 or more and production is not the
   larger gap.
3. **Build what beats it.** For each enemy type, the `counters` column of `intel.threats` names the
   three units this faction can build that beat it at equal cost, as measured by the engine in the
   duel lab, and `creditsSpentOnCounters` says how much this side bought of them.
   `intel.mixCounters` answers the whole enemy army seen, and `intel.counterMatchPercent` scores
   what this side actually built against it: 50 is an even trade, 100 a sweep. The full grid,
   blind and with vision, including towers and raids, is `docs/unit-matchups.md` at the repository
   root. To use the margins in code, run `./scripts/export-bot-matchups.ps1` from the repository
   root. It writes `Logic/MatchupTable.cs` into this workspace, with
   `MatchupTable.Margin(unit, opponent)`. The margins come from massed, equal-cost groups on open
   ground. A counter bought one unit at a time into a fight it cannot win alone still loses, and
   a combined army, such as artillery behind riflemen, can beat what each of its parts loses to
   on its own.

Earlier experiments on these themes, so they are not repeated:

- A scout taken from the only light vehicle a Nod opening escorts its harvesters with lost games
  the champion wins. Watch with a cheap body, or build an extra scout.
- Counter rungs placed ahead of the harvester rungs, or swapped in for the siege rung, starved
  the economy and lost.
- Answering aircraft on sight, or on seeing a helipad, with rocket soldiers or APCs won games the
  champion lost.

A won game must also be finished. A benchmark game still running at 2,400 s voids its pair, and
three voided pairs stop training outright. That happened once, with 380 to 700 of our units standing
at home while the enemy had no army and one or two buildings nobody found. When the enemy has
nothing left in sight, hunting down their last buildings and harvesters comes before any of the
three priorities.

## Boundaries

- Edit only files under `{workspace}`.
- Do not edit the AutoC&C platform checkout, engine, training artifacts, or files outside the bot
  workspace.
- Preserve the public SDK boundary and existing project conventions.
- Make evidence-based changes rather than arbitrary tuning.
- Spend the whole budget on battle logic. Do not write unit tests or add a test project: this is a
  game bot, and the next fight is what measures it.
- Build the bot before finishing. Do not stop while the build is failing; fix failures and rebuild
  until it exits successfully. The host will clean generated output and build it again
  independently.
- Do not launch another game. Finish after the code is ready for the next fight.

### Never memorise a match

You are shown one fight, and previous fights are summarised for you. That history is for deciding
what to change. It is not a lookup table, and writing any of it into the bot is cheating that only
wins the rerun:

- No literal map cells. `new CPos(50, 18)` is banned even when it is exactly where the enemy base
  was. Positions must be derived at runtime from `BattleState` and the unit's `ModeContext`.
- No branching on which map, faction or opponent was drawn.
- No constant tuned to one seed, one map or one opponent.

A mechanical scan of the sources reports below. It is advisory and easy to evade, so the rule
matters more than the check.

{botAudit}

## Evidence

Start with the derived artifacts. They are computed by the harness, from the raw records, by tested
code, before you are called.

- **Fight summary: `{summary}`** — read this first, and read it whole; it is held under 20 KB for
  exactly that reason. It holds the per-unit-type ledger (built, lost, kills, credits spent, share
  of spend, damage both ways, credits per kill, mean lifetime), the economy series, per-queue
  production, doctrine episodes, the engagement matrix, loss clusters, map facts, the telemetry
  crossover, a graded fitness score broken into named components, `intel` (each enemy unit type's
  first sighting against its first hit on this side, and its measured counters) and `scale` (this
  side's economy and production against the opponent's).
- **Unit ledger: `{units}`** — one row per unit, whole lifecycle. `Import-Csv` then `Group-Object`
  answers "which type never survived anything", "did the army arrive in waves sorted by speed" and
  "what did each type kill per credit" in one line each.
- Map facts: `{mapFacts}` — dimensions, spawn cells, home-to-enemy distance, resource cells, and
  the random seed this match ran on.
- The checks the previous round wrote, and the harness's verdict on them: `{checks}`,
  `{checkResults}`. The rendered verdict is in the section below; read the files only if you need
  a value the rendering does not show.
- Cross-run trend: `{trend}`.

Do not re-derive anything above from the raw records. That arithmetic is done, and doing it again
by hand is where this loop used to spend most of its budget.

The raw records remain, for the questions the derived artifacts cannot answer:

- Fight manifest: `{fightManifest}`
- Battle log (what this bot could observe): `{battleLog}`
- Match telemetry (both sides' curves, one-second resolution): `{telemetry}`
- Decision trace (assessments and issued orders): `{decisionTrace}`
- Replay, if captured: `{replay}`

Two things about the raw records that have caused wrong analyses before:

- A `killed` row carries the **victim's** owner in `player`; the killer is in `otheractor`. Filter
  on `otherplayer` to find this bot's kills. `{summary}` and `{units}` already account for this.
- `cash` is a stock, so `cash = 0` cannot tell "earning nothing" from "spending it the instant it
  arrives". Use the cumulative `earned` and `spent` flows.

Fight configuration: {battle}

Result and player assessment, when one was provided: {result}

Source revision before the fight: `{sourceRevision}`

## Checks carried in from the previous round

Generated by the harness. A FAIL is a claim the previous round made that this fight refuted, and a
failing `reason-id:` check means a code path never executed at all.

{checkReport}

## How this bot is trending

Generated by the harness across previous runs. A flagged regression is measured against the median
of earlier runs rather than against a single noisy match, and is the first thing to explain.

{trendReport}

## Work

1. Read `{summary}` whole. Note the fitness components: a component that moved is the signal, and
   a match can be lost while a component genuinely improves.
2. Explain every failing check and every flagged regression before proposing anything new.
3. Read the bot source.
4. Choose the priority above that this fight's numbers put furthest behind, and locate the
   decisive weakness within it, citing the artifact and the number that shows it.
5. Implement the smallest coherent improvement to the battle logic.
6. Write the checks for the next round, as below.
7. Build the bot, fixing failures before finishing.

## Write the checks for the next round

Write `checks.json` into `{workspace}` before you finish. Every claim you make about what your
change will do goes in it, so the harness settles it against the next fight rather than a later
round taking your word for it. The harness reads it from there and reports it as the section above.

```json
{
  "schemaVersion": 1,
  "authoredForRevision": "{sourceRevision}",
  "checks": [
    {
      "id": "harvester-escort-runs",
      "category": "activation",
      "description": "The new harvester escort branch actually executes",
      "query": "reason-id:economy.escort-harvester",
      "operator": ">=",
      "value": "1"
    },
    {
      "id": "spend-keeps-up",
      "category": "outcome",
      "description": "This side spends at least 70% of what the opponent spends",
      "query": "summary.scale.spendVsOpponentPercent",
      "operator": ">=",
      "value": "70"
    },
    {
      "id": "light-infantry-earn-their-cost",
      "category": "invariant",
      "description": "e1 stays under 200 credits spent per kill",
      "query": "summary.unitTypes[e1].creditsPerKill",
      "operator": "<=",
      "value": "200"
    }
  ]
}
```

Query forms: `summary.<dotted.path>`, `summary.unitTypes[<type>].<column>`,
`summary.production[<queue>].<column>`, `units.count(type=x)`, `units.sum(<field>,type=x)`,
`units.mean(<field>,type=x)`, `reason-id:<id>` for an exact stable identifier, and
`reason:<literal>` for the backward-compatible prose substring query. `reason:` first uses an
exact `ReasonId` when one exists. Exact-ID counts include matching unit evaluations, assessment
decisions, and doctrine changes. Unit IDs count evaluations rather than only issued orders;
legacy issued-decision indexes remain unchanged. Operators: `>=`, `>`, `<=`, `<`, `==`, `!=`,
`contains`, `present`, `absent`.
Use category `activation` for proof a path ran, `invariant` for a safety property, and `outcome`
for a measured result. Outcome checks are diagnostic; paired benchmark wins and fitness, not the
percentage of outcome checks passing, decide promotion.

If you added or changed a code path, give it a stable `ReasonId` and assert it with a
`reason-id:` check. That is the only thing that distinguishes "the new branch is wrong" from
"the new branch never ran", and the ID remains valid when explanatory prose changes.

{nextPromptContract}
