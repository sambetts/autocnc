Improve the AutoC&C battle bot in `{workspace}` using evidence from its latest completed fight. Edit only files under `{workspace}`.

## Mechanics and SDK reference

Authoritative and generated from the compiled assemblies: if a member is not listed, it does not exist. Do not copy it into the prompt you propose.

{gameMechanics}

## Evidence

Read `{summary}` whole first. It is under 20 KB and already holds the per-type ledger, economy series, production and doctrine tables, engagements, loss clusters and crossover. Then read only what you need: units `{units}`, map facts `{mapFacts}`, last round's checks `{checks}` and verdict `{checkResults}`, trend `{trend}`, guide `{gameGuide}`, and actor and weapon stats `{gameRules}` (query it, do not read it whole).

Open a raw record only for a question the summary cannot answer, and say which: manifest `{fightManifest}`, battle log `{battleLog}`, telemetry `{telemetry}`, decision trace `{decisionTrace}` (tens of MB, so stream it and filter on `event`, `actorId` and `seconds`). Only the trace says:
- why the doctrine changed: `"event":"assessment"` rows carry `state`, `botDecision`, `modeRequest` and `outcome`;
- what a unit was told before it died: its battle-log `lost` row, then its `unit-decision-evaluated` rows, skipping `duplicate-intent`;
- why a queue bought or held: `production-budget` events and `Hold` or `production-budget-suppressed` outcomes.

Fight: {battle}

Result: {result}

Source revision before the fight: `{sourceRevision}`

## Checks carried in

A FAIL is a claim this fight refuted. For a `reason-id:` check it can also mean the situation never came up. Read the description before believing either.

{checkReport}

## Trend

Explain each flagged regression in one sentence and move on. Consecutive runs differ in map and faction, so a loss after a win flags nearly everything; say so once. `idleUnitSeconds` includes buildings and harvesters on autopilot, so it grows with army size and match length.

{trendReport}

## Choose one gap

Name one gap, cite the `{summary}` number that chose it, change one thing, and check the metric it targets.
- A loss: start with `scale`. Losses sit at 26-61 `spendVsOpponentPercent`, wins at 77-149. Ask what our first 600 s bought and lost.
- A win below 1.0: the fitness component furthest below 1.0.
- A win at 1.0: the largest avoidable loss. That is the `doctrineEpisodes` row with the most `creditsLostDuring`, or the heaviest `lossClusters` row. Find the rule that put those units there.

## Where the bot stands

The last round changed what a formed push does when it drops a stale sighting. With an army from 4,000 up to 10,000 it stays in Attack and probes the point mirror of our base (`Logic/StaleSightingLogic.cs`, called from `AttackBaseMode.Approach`). Before, it asked for Scout, which walked a 6,420 push home from their door and lost 5,260 on the way. It ran if `reason-id:assault.stale-sighting-probe` is at least 1. If the file is gone, the benchmark restored the champion.

Tried and not kept:
- A push recall (`doctrine.push-bled-out`) that waited for 1.5 times the bled push's peak.
- Taking a Nod opening's only escort vehicle to scout.
- Counter rungs above the harvester rungs or instead of the siege rung. Both starved the economy.
- Answering aircraft on sight with rocket soldiers or APCs.
- A forward yard at the contested field. It lands at 500-525 s, after theirs.

Open leads. Take one only if your fight shows it:
- Infantry-only pushes into jeeps and APCs. 16:9 losses open with a push that trades even and burns out. Wins have a Defence episode first, where the opponent spends its army on our towers. In the last win the 220 s probe, 4,000 of `e1` and `e3`, lost 3,600 for 1,600, and enemy jeeps took 9,640 of the 16,700 we lost.
- Reinforcement trickle. Units built mid-push arrive in groups of 4-6 and die to the same jeeps or towers.
- After a stale sighting, units already sweeping keep walking the ladder (`sweeping` is sticky) while the rest probe the mirror, so the push can split.
- Holds that deadlock. The harvester bank reserves until 2 per refinery while `ArmyBalanceLogic.Release` treats 3/4 as met, which blocked the `hq` in the loss before last.
- Harvesters sent to fields 1-4 cells from an enemy `gun` (`economy.harvester-works-the-raid`).
- `SelectObjective` ranks a pristine structure (3,000) above a defence (1,000).

## Reading this bot

- `README.md` is a ~200 KB journal, newest last. Read its last two sections and add one short section.
- Doctrine: `Logic/ReferenceBotLogic.cs`. Pushes: `Modes/AttackBaseMode.cs` and its `*Logic`. Every other doctrine gives the army `Modes/DefensiveMode.cs`, which re-anchors on home, so leaving Attack recalls the push. Only Scout with 10,000 of army hunts instead (`Modes/ArmyHunt.cs`).
- Production: `Modes/TrainUnitsMode.cs` and `Plans.cs`. It rewrites the plan every evaluation, and a new rule there should claim a branch only when the baseline would have chosen differently. Construction: `Modes/BuildBaseMode.cs`. Reservations: `ReferenceBot.cs`, `Logic/IncomeFirstLogic.cs`. Harvesters: `Logic/HarvesterLogic.cs` (92 KB, so grep it).
- `units.csv` includes buildings. `fact` and `mcv` each show 3,000 for one MCV, and `c17` is Nod's delivery plane. Quick wins lower `creditsKilled`, `cellsExplored` and `durationSeconds`.

## Boundaries

- Edit only files under `{workspace}`: not the platform checkout, the engine, or training artifacts.
- Keep the SDK boundary and existing conventions. Base changes on evidence, not tuning. No unit tests and no test project.
- Build until it succeeds, Release included. Do not launch a game.
- No literal map cells, no branching on map, faction or opponent, no constant tuned to one seed.

{botAudit}

## Work

1. Read `{summary}` and explain each failing check and flagged regression in a sentence.
2. Name the gap and its decisive weakness, with the artifact and the number.
3. Read only the source that weakness runs through and make the smallest coherent change.
4. Before building, trace it by hand through the moments that motivated it, what else it now enables, and when it would fire in a recent win. Revert what the trace shows going wrong and say so in the README.
5. Write `checks.json`, then build.

## checks.json

Write `checks.json` into `{workspace}` before you finish. The harness evaluates it against the next fight.

```json
{ "schemaVersion": 1, "authoredForRevision": "{sourceRevision}",
  "checks": [ { "id": "new-branch-runs", "category": "activation",
                "description": "the new branch executes",
                "query": "reason-id:assault.example-new-branch", "operator": ">=", "value": "1" } ] }
```

Give every new code path a `ReasonId` and a reason literal nothing else uses. Assert it with a `reason-id:` or `reason:` check, so "the branch is wrong" can be told from "it never ran". Categories: `activation` proves a path ran, `invariant` guards a safety property (keep `summary.fight.durationSeconds <= 2400`), and `outcome` records a measured result. Outcome checks are observations, not a promotion gate; paired benchmark wins and fitness decide promotion. `units.*` accepts `owner=` and `mode=`, and `summary.fitness.components[<name>].score` works. Factions are random, so pair a `type=` check with the other faction's equivalent, and set thresholds that allow the cases your rule means to allow.

{nextPromptContract}
