# Improve the AutoC&C Reference bot

Improve the battle bot in {workspace} so that it wins games the champion loses. Edit only files under
{workspace}: never the platform checkout, the engine, the tools, `docs/` or training artifacts. You may
read engine source to understand the opponent, but never turn it into map cells, grid constants or
opponent-specific branches.

{gameMechanics}

## This fight

{battle}

{result}

Source revision before the fight: `{sourceRevision}`

{botAudit}

## Evidence

Read {summary} whole first; its tables are computed, so do not re-derive them. Open anything else only
for a question it cannot answer, and name the question: units {units}; map {mapFacts}; checks {checks},
verdict {checkResults}; trend {trend}; guide {gameGuide}; manifest {fightManifest}; rules {gameRules}
(query with a script); battle log {battleLog}; telemetry {telemetry}; trace {decisionTrace} (stream it,
filter on `event`). `docs/unit-matchups.md` is the duel lab: read it before arguing counters. Where each
push lost its units, and to what, is not in the summary: battle-log `lost` rows carry killer and cell.

{checkReport}

{trendReport}

## Experiment ledger

{experimentLedger}

Inconclusive and not-better are not refutations; a change whose ids fired in no game was never tested.

## The pool

The champion's games are in `..\..\gate\hard-16-9-fresh.json` beside the folder holding {summary}. Try
once; the last four rounds were denied it, and the session store holds nothing beyond the facts below.

## What is known (champion `2c79ea9`, 96 fresh games, 68 won)

- By pairing, ours first: GDI-GDI 16/24, GDI-Nod 18/24, Nod-GDI 14/24, Nod-Nod 20/24.
- What precedes the first push decides most games. Raided, Defence won, pushed from Opening at 275-410s:
  35 of 38 won. Straight out of Defence at 4,000: 13 of 22 (3 of the 9 losses launched with enemies in
  sight; no win did). Quiet opening, blind rifle probe at 220-245s: 18 of 32; 8 of 12 against GDI lost.
- Losses end economic (about 58% of HAL's spend), but income at 420s is level: HAL pulls away once our
  army is spent. HAL plants its yard on tiberium within 30 cells of ours in 71 of 96 games; wins kill it
  within a minute. Our home fields are worked out by about 480s.
- `hq` stands near 370s and the first siege piece near 400s; refinery four leads `hq` for a measured
  income reason (`Plans.cs`).
- HAL (`SquadManagerBotModule`, `engine/mods/cnc/rules/ai.yaml`): a squad forms once 15 plus a random
  0-29 units idle at home and attack-moves on our nearest actor; a hit on any of its buildings or
  harvesters pulls its idle units onto the attacker. It runs up to four barracks and four factories; we
  drive one of each, because `QueueFor` returns a group's first queue.
- Pushes are spent at tower lines (duel lab: one `gtwr` or `atwr` wipes out `e1`), yet push tactics
  alone have not moved the gate: holding the first push, tower discipline with a blind siege, the yard
  as objective, muster clocks.

## The candidate judged after this prompt was written

"Siege shells what the side has seen": `Modes/EnemyStructures.cs` remembers every enemy building any
unit sees. In Attack a siege piece shells the nearest remembered ground tower within its reach plus 5
cells, then other remembered buildings, through the fog (`arty`/`msam` carry `TargetFrozenActors`;
see `OpenRA.Mods.Common/TargetExtensions.cs`). Line units stand off remembered towers, and with no
siege in position but one alive, a unit the duel lab says the tower beats waits outside its reach
(`StandOffLogic.Outmatched`, 75s budget). Ids `assault.siege-shells-remembered-defence`/`-structure`,
`assault.stand-off-remembered-*`, `assault.await-siege-*`. Pushes before any siege exists, and home
defence, are unchanged. The ledger says whether it was kept and which ids fired.

## Work in this order

1. If the fight is a loss, find the moment it became unwinnable, the decision that led there, and a
   condition this side could observe before it. If it is a win, say so in one line and work from the
   facts above instead of polishing it.
2. Check the condition concentrates the champion's losses. Estimate flips each way per 100 games; under
   a net of about 5, choose something bigger: one coherent change that fires in most games it targets.
   Untried levers of that size: composition per matchup (the duel lab rates Nod's `ltnk` well below
   `ftnk` and `e1` against HAL-GDI's usual mix), siege before the first push, a second production group.
3. Build, have a code-review agent review the diff (each of the last two found real bugs), fix, build
   with Release last. Do not launch a game; do not add tests or a test project.
4. Say whether the fight was a loss and what came before it, give each failing check and flagged trend
   shift one sentence, and name the touched set and your estimate.

## Reading this bot

`README.md` is a journal, newest last: read its last two sections and add one short one. Doctrine:
`Logic/ReferenceBotLogic.cs`. Pushes: `Modes/AttackBaseMode.cs`, `Logic/AttackBaseLogic.cs`,
`Logic/AssaultStagingLogic.cs`, `Logic/StandOffLogic.cs`. Home: `Modes/DefensiveMode.cs`,
`Logic/DefensiveLogic.cs`. Harvesters: `Modes/HarvesterMode.cs`, `Logic/HarvesterLogic.cs`. Production:
`Modes/BuildBaseMode.cs`, `Modes/TrainUnitsMode.cs`, `Plans.cs`, `Logic/ArmyMixLogic.cs`.

Traps: our losses are battle-log `lost` rows; `killed` rows are theirs. `c17` is Nod's delivery plane.
`attacked` rows whose `otherplayer` is us are friendly fire. `EnemyBaseFound` latches only when a
building is in sight at an assessment, so a push can "probe for their base" after a scout died finding
it. `BattleState` has no composition: a doctrine rule cannot see our siege count, a mode can.
`ctx.ResolveActor` is not visibility-filtered. Shared memory is keyed by `Player`; clocks count
`WorldTick`. A unit told to `Hold` under fire never shoots back.

## checks.json

Write `checks.json` into {workspace} before you finish, with `authoredForRevision` set to
`{sourceRevision}` and a one-sentence `hypothesis` saying what the change should move and which games it
should flip. Give every new code path a `ReasonId` literal nothing else uses, so a `reason-id:` check can
tell "the branch is wrong" from "it never ran". Categorise each check: `activation` (a new path ran;
reads FAIL against this fight), `invariant` (holds in a loss too; always include
`summary.fight.durationSeconds <= 2400`) or `outcome` (a measured result). They run in every candidate
game and their tallies go into the ledger; they are observations, not a gate, and wins on fresh seeds
decide promotion. `units.*` filters only on `type=`, `owner=` and `mode=`. Validate them with
`dotnet tools\AutoCnC.Evidence\bin\Release\net8.0\AutoCnC.Evidence.dll summarise <scratch copy of the evidence> --checks <checks.json>`,
never on the evidence folder itself.

{nextPromptContract}
