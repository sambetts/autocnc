Improve the battle bot in {workspace} from the evidence of its latest fight. Edit only files under {workspace}: not the platform checkout, the engine, the tools, or training artifacts.

## Mechanics and SDK reference

{gameMechanics}

## This fight

{battle}

{result}

Source revision before the fight: `{sourceRevision}`

{botAudit}

Read {summary} whole first. Open anything else only for a question it cannot answer, and say which: units {units}, map facts {mapFacts}, checks {checks} and verdict {checkResults}, trend {trend}, guide {gameGuide}, actor and weapon stats {gameRules} (query it, never read it whole). Read `docs/unit-matchups.md` before arguing counters. Raw records: manifest {fightManifest}, battle log {battleLog}, telemetry {telemetry}, decision trace {decisionTrace} (20 MB; filter lines before parsing). Only the trace says which rule a unit, the yard, the bank or the doctrine obeyed, and where a unit was sent (`targetX`/`targetY`); units.csv says where it died.

Facts that have cost rounds:
- Trace events carry `action`, `reason`, `reasonId` and `outcome` at top level; `assessment` holds `BattleState` under `state`; a bank-blocked order has outcome `production-budget-suppressed`. Many push decisions carry prose only.
- Our deaths are `lost` rows, killer in `otheractor`. Friendly fire is `attacked` rows whose `otherplayer` is us; the engagement matrix omits it. `spotted` rows carry `frombase=`, which is how a forward base shows. `c17` is Nod's delivery plane.
- `CanHitUs` ignores distance; a thing shoots us only inside its `WeaponRangeUnits`, its longest weapon (an `atwr` reads its anti-air reach). Sight is `RevealsShroud` in `engine\mods\cnc\rules\*.yaml`.
- Engine: a unit that cannot move drops an `Attack` out of range; an `Attack` on a building this side has seen continues in fog, on a unit it is dropped.
- `EnemiesNearBase` counts visible enemy buildings within 15 cells of our yard. `AttackBaseMode` evaluates about three times a game second, harvesters twice, the yard once.
- The opponent's towers have fired only since 31ee989. Rules written before then may not know a tower exists.
- A scratch net8.0 console outside {workspace} referencing `engine\bin\AutoCnC.Core.dll` and the built `engine\bin\bots\AutoCnC.Reference.dll` replays any `*Logic` class on states rebuilt from the trace; a short script over sibling run folders sharing this fight's `rulesFingerprint` says whether losses share a gap and how often a rule would fire in a win. Keep nothing of either.

## Checks the champion carries, and the trend

{checkReport}

{trendReport}

These checks were written by the round that produced the champion and are evaluated against every fight it plays. A quick win moves `meanArmyValue` and `creditsKilled`; a loss measured against a median of wins moves nearly everything. Say so once if one applies, and give any other listed shift or failing check a sentence: refuted, or never came up.

## Experiment ledger

{experimentLedger}

The ledger is the harness's record of recent rounds, written after each verdict from the candidate's own benchmark games. Read it before choosing: do not repeat a change that measured worse, and treat "inconclusive" or "not better" as untested at this gate's resolution, not as refuted. A new reason id that fired in 0 of a candidate's games means that change never ran.

## How a change is judged

See "How a bot is measured" in the mechanics reference above: the gate plays fresh seeds and keeps a change only if it wins games the champion loses. Never tune match length. Losses share 7-19 harvesters lost, early pushes without siege, and an enemy forward base near ours standing to the end.

## Where the bot stands

The champion is the source in {workspace}. Every candidate from 26 September 16:09 to 27 September 23:22 (twenty-four rounds) was rejected by the old gate, eight pinned seeds the champion already won 8 of 8: a candidate could at best tie, and a tie was decided by a fitness score that is capped in a win. Those rejections say nothing about the ideas. The last of them made harvesters refuse fields whose ground or road a remembered enemy tower reaches (`economy.harvester-avoids-tower-ground`); it is not in the source.

Earlier attempts, all rejected by that old gate, so none is refuted; retry one only with a reason to expect it to flip games. Harvesters: a one-credit bank for refinery four; recovery hold funding the factory; refinery five from the bank; a floor until one dies; the bank stopping at the released floor; no field charge for a harvester shot in transit; escorts standing off tiberium; a drive-off outliving a fleet report; radius raided-ground reports; refusing tower-covered fields. Pushes: the first push held to 10,000 or a raid; recalling a bled push; probing our mirror on a stale sighting; infantry waiting at towers; line units shooting their armour's tower first or standing off an unseen defence; siege re-aiming off targets beside our units, stepping back from closing infantry, or shelling the tower in its reach until it falls; counter-battery attack-moving at a fogged hit's cell. Other: a Nod opening's only escort scouting; rockets or APCs on first sight of aircraft; counter rungs above harvester or siege rungs; `ftnk` as Nod armour; a forward yard at the contested field; the screen or Defence attacking their forward base; a covering tower sited toward where the base is hurt.

Open leads; take one only if your fight shows it:
- Only harvester field choice reads `EnemyDefences`; refinery siting, the survey and the engine's own harvester search (which can wander from a safe field into a tower's reach) do not.
- Enemy `orca` did about 870,000 damage to our buildings last fight, a quarter from crashing husks; three `atwr` killed 19 and all died. Do not cut anti-air on one quiet fight.
- `DefensiveLogic.CanEngage` gives towers a mover's leash; our `gtwr`'s blockable bullets hit our own buildings.

## Choose one gap

Name one gap, cite the {summary} number that chose it, price it in credits and seconds, name its decisive moment, and say whether the losses share it. The gate keeps only a change that flips games on fresh seeds, so prefer a gap that decides games and a change that fires in most games of the matchup it targets; moving 600 credits nine seconds sooner flips nothing. Change one thing that works for either faction; a few coordinated edits that only work together count as one thing. In a loss start from `scale`, the costliest `doctrineEpisodes` row and the largest `unitTypes` share of spend; in a win rank `unitTypes` by cost times lost beside `lossClusters`, and look for spend that did nothing. A rule whose comment and orders disagree is the cheapest fix. A real bug that had nothing to act on in this fight is a lead, not a gap.

## Reading this bot

`README.md` is a 200 KB journal, newest last: read its last two sections and add one short one. Doctrine `Logic/ReferenceBotLogic.cs`; assignments `Doctrines/*.cs`; pushes `Modes/AttackBaseMode.cs` with `StandOffLogic`; home `Modes/DefensiveMode.cs`; harvesters `Modes/HarvesterMode.cs` and `Logic/HarvesterLogic.cs` (95 KB, grep it); production `Modes/TrainUnitsMode.cs` and `Plans.cs` (a rung counts what is standing; `ArmyBalanceLogic` releases a floor at 3/4); banks `Logic/IncomeFirstLogic.cs`.

## Work

Edit only files under {workspace}. Keep the SDK boundary and existing conventions; base changes on evidence, not tuning. No unit tests and no test project. No literal map cells, no branching on map, faction or opponent, and no constant tuned to one seed.

1. Read the ledger, and account for every failing check and listed shift.
2. Choose the gap. Read only the source its decisive moment runs through, make the smallest coherent change, and put its evidence in prose beside the code.
3. Before building, trace the change on this fight's numbers: when it fires, what else it moves, how often it would fire in wins, and what changes as the other faction. Revert anything the trace shows going wrong, and say so in the README.
4. Write `checks.json`, then build until it succeeds, Release last. Do not launch a game.

## checks.json

Write `checks.json` into {workspace} before you finish; the harness evaluates it in every one of your candidate's benchmark games and records the tallies in the ledger. Set `authoredForRevision` to `{sourceRevision}`, set `hypothesis` to one sentence saying what the change does and which games it should flip, and give every check a `category`:
- `activation` proves a path ran. Give every new code path a `ReasonId` and a reason literal nothing else uses, and assert it with `reason-id:` or `reason:`, so "the branch is wrong" can be told from "it never ran". A path that only stops something leaves no ID, so put the ID on what it still does.
- `invariant` guards a safety property that must also hold in a loss. Always include `summary.fight.durationSeconds <= 2400`.
- `outcome` records a measured result. Checks are observations, not a gate: wins on fresh seeds decide promotion.

`units.*` filters only on `type=`, `owner=` and `mode=`, and an empty match reads 0, so a one-faction check must hold in the other faction's game. A table's first column is its key; a key nobody produced errors. Confirm queries resolve with `dotnet tools\AutoCnC.Evidence\bin\Release\net8.0\AutoCnC.Evidence.dll summarise <dir> --checks <your checks.json>` on a scratch copy of the evidence, never the evidence directory, which it rewrites. Against this fight, a new branch's activation check should read FAIL.

{nextPromptContract}
