# AutoCnC.Evidence

Turns one fight's raw evidence into the artifacts an improvement round actually reads.

## Why this exists

The improvement loop works, but it learned slowly for measurable reasons. Rounds spent the larger
part of their budget re-deriving the same aggregates from the raw event stream with bespoke,
fragile scripts — credits-per-kill by unit type, the telemetry crossover, income rate, doctrine
episodes, loss clusters — in a fresh process that paid the decision-trace parse again every time.
One round missed a 46.6 → 25.4 credits-per-second economic regression entirely, because nothing
compared runs.

Everything here is **derived** and **additive**. The raw records are untouched, and the artifacts
below sit beside them. Existing analysis that reads `battle.csv`, `telemetry.csv`,
`decisions.jsonl` or `fight.json` keeps working unchanged.

## Why it is not in `AutoCnC.sln`

That solution is packed into the NuGet packages a battle bot consumes. This assembly reads whole
finished matches, including the opponent's side, and maintains the per-bot run history. A bot that
could reference it could read the answer sheet for a match it is still playing. It lives in the
launcher solution, is `IsPackable=false`, and targets plain `net8.0` so CI can test it on Linux.

## Commands

```
dotnet AutoCnC.Evidence.dll summarise <evidenceDir> [--history <file>] [--checks <file>] [--bot <name>] [--matchups <file>|none]
dotnet AutoCnC.Evidence.dll trend <historyFile> [--out <file>]
dotnet AutoCnC.Evidence.dll audit-bot <botSourceDir>
```

`summarise` is the one the harness runs. It writes `units.csv`, `summary.json`,
`check-results.json` and `trend.json` into the evidence directory, and folds the run into the
per-bot history.

## Artifacts

### `summary.json` — `schemaVersion` 1

Held under **20 KB** (`FightSummaryBuilder.MaxBytes`), because file reads truncate and an artifact
whose purpose is to be read whole must fit in one read. When a very long match would overflow, the
builder trims its longest lists in a fixed order and records exactly what it trimmed in
`truncated`; a short list is never silently short.

| Field | Contents |
| --- | --- |
| `provenance` | Which inputs existed and at what schema. A missing number reads as unknown, not zero. |
| `fight` | Map, difficulty, factions, outcome, duration, seed, benchmark, arm. |
| `headline` | The flat block the cross-run index stores and the trend diffs. |
| `fitness` | Graded score with named, separately reported components. |
| `crossover` | Last second level-or-ahead and first second behind, for units, army and assets. |
| `economy` | Earned/spent rates, idle cash, harvesters, power, and a 60-second series. |
| `map` | Dimensions, spawns, home-to-enemy distance, resource cells, cells explored. |
| `unitTypes` | Per actor type: built, lost, kills, spend, share of spend, damage both ways, credits per kill, mean lifetime. |
| `production` | Per queue: orders, deepest plan step reached, seconds waiting and idle, repeated requests. |
| `doctrineEpisodes` | Each episode with army value at entry and exit, and what it killed and lost. |
| `engagements` | Own actor type × enemy actor type → damage dealt, damage taken, kills, deaths. |
| `lossClusters` | Where this side kept dying: centroid, radius, count, credits, time window. |
| `intel` | When each enemy unit type was first seen against when it first hit this side, what it killed, and the duel lab's measured counters that this side's faction can build. See below. |
| `scale` | This side's income, spend, army, harvesters and buildings beside the opponent's, and its peak number of unit factories. |
| `notes` | Facts a reader would otherwise have to notice, including why a number is missing. |
| `truncated` | What was shortened to fit, and from what. |

The tabular sections and `economy.series` are `{ "columns": [...], "rows": ["a,b,c", ...] }` — a
CSV table inside the JSON, one row per line. Indented JSON puts every array element on its own
line, spending about 120 bytes of brackets and whitespace to carry 20 bytes of fact; at one row per
line the same table is a third of the size. That saving is the difference between carrying the
whole per-unit-type ledger and having to trim it. The first column of every table is its natural
key, which is what keeps `summary.unitTypes[e1].creditsPerKill` resolvable.

**Free actors** are excluded from `creditsSpent` and from the share-of-spend denominator on the
authority of the ruleset's own `FreeActor` trait, exported into `game-rules.json` at schema 2 —
never by the old heuristic of matching a harvester's build second against a refinery's. On an older
run, `provenance.freeActorExclusion` reads `unavailable` and a note says so rather than guessing.

**`intel`** is built from this side's own battle log, meaning its sightings, hits taken and losses,
plus the duel lab's static matchups:

| Field | Contents |
| --- | --- |
| `enemyBaseFirstSeenSeconds` | First enemy structure seen more than 20 cells from home. |
| `firstEnemyHitSeconds` | First time the enemy damaged anything of this side's. |
| `lateSightingLossPercent` | Share of the value lost to enemy units that went to types first seen less than 30 s before they first hit, or never seen. |
| `nearBaseSightingLossPercent` | Share of the same losses to types first seen within 20 cells of home. |
| `counterMatchPercent` | How well the credits spent on combat units answer the enemy army seen, by the lab's equal-cost margins: 50 is an even trade, 100 a sweep. |
| `mixCounters` | The three units this faction can build that best answer the whole enemy army seen. |
| `enemyStructuresFirstSeen` | Each enemy structure type and when it was first seen. A helipad means aircraft. |
| `threats` | Per enemy unit type: first seen, cells from home, first hit, `leadSeconds` (negative when it hit before it was seen), actors and value seen, credits killed, counters, and credits spent on them. |

Counters come from `tools/DuelLab/results/matchups.json`, found by walking up from the tool.
`--matchups <file>` names another file and `--matchups none` disables them. A `matchups.json` beside
the evidence takes precedence. `provenance.hasMatchups` says whether any were found. Counters are
limited to units the side's faction can build, from the queues in `game-rules.json`.

**`scale`** takes the opponent's figures from telemetry, the omniscient after-match record, to
diagnose the gap between the two economies. It is never something a bot could have read during
the match.

### `units.csv` — `UnitLedger.SchemaVersion` 1

```
actorId,type,cost,faction,owner,bornSeconds,diedSeconds,lifetimeSeconds,killerActor,killerPlayer,
deathX,deathY,kills,creditsKilled,damageDealt,damageTaken,cellsTravelled,secondsIdle,modesUsed,
decisionCount,free
```

One row per actor, so "which unit type never survived anything", "did the army arrive in waves
sorted by speed" and "what did each type kill per credit" are each one `Group-Object`.

Two columns are approximations, and are named as such in the code:

- `cellsTravelled` — straight-line distance between the places the unit was *observed*. The trace
  carries no position and the battle log only fixes a unit in place when something happens to it,
  so this is a lower bound.
- `secondsIdle` — time spent in gaps longer than `UnitLedger.IdleGapSeconds` (15) between that
  unit's own consecutive decisions. Units only emit a decision when it changes, so short gaps mean
  "still doing the same thing".

Enemy units appear where they were observed. An enemy killed by this side gets a row with an empty
`bornSeconds`, because watching an enemy factory is not something this side could do — empty rather
than a guessed zero.

### `checks.json` and `check-results.json` — `schemaVersion` 1

A round writes `checks.json` into its bot workspace; the harness evaluates it against the *next*
fight and writes `check-results.json`, whose `rendered` field the next prompt injects verbatim.
That is what makes the "already diagnosed — verify each in one line" section generated rather than
hand-maintained.

Query forms: `summary.<dotted.path>`, `summary.unitTypes[<type>].<column>`,
`summary.production[<queue>].<column>`, `units.count(type=x)`, `units.sum(<field>,type=x)`,
`units.mean|max|min(<field>,type=x)`, and `reason:<literal>`.
Operators: `>=`, `>`, `<=`, `<`, `==`, `!=`, `contains`, `present`, `absent`.

`reason:` counts decisions whose reason contains the literal. It is the important one: it proves a
new code path actually ran, which is what separates "the branch is wrong" from "the branch never
executed".

Checks may add a `category` of `activation`, `invariant`, or `outcome`. Reports retain the category
and publish per-category totals; checks written before categories remain valid and appear as
`uncategorized`. Outcome checks are descriptive evidence, not a promotion pass-rate gate.

An unresolvable query produces a failed result carrying an `Error`. It never throws out of
`Checks.Evaluate`, because one bad check must not cost the other nine.

### Paired benchmark promotion evaluation — `schemaVersion` 1

`PairedBenchmarkEvaluator` reads the machine result written by `benchmark-bot.ps1`, including
`expectedMatchesPerArm` and one explicit success/failure row for every planned match. It requires
both arms to fill that count in the same combined batch and cover the same unique repeat/scenario
configurations, then cross-checks the paired rows against the raw match rows. Missing arms,
symmetric omissions from the plan, duplicate or mismatched scenarios, and pairs naming another
benchmark, batch or configuration produce an `Undefined` verdict, which cannot promote.
Failure rows may serialize null duration and metric values. Those fields are required and checked
for finiteness only when `succeeded` is true.

A scenario that did not complete in both arms — a failed status, an outcome other than `Won` or
`Lost`, or non-finite metrics — is *dropped from both arms together* and counted in
`droppedPairs`, so one crashed match no longer discards the rest of the sitting. Wins are counted
over the surviving pairs, and the verdict is `Undefined` only when fewer than three quarters of
`expectedMatchesPerArm` survive. Incompleteness is thereby distinguished from inconsistency: the
first is absent evidence, the second is corrupt evidence and still voids the batch.

Complete evidence is ranked as follows. Candidate wins against control wins first — but a win
alongside a negative median paired fitness delta restores rather than promotes, because on a
reproducible benchmark that is a measured regression the extra win does not pay for. When wins
tie, the candidate must clear `MinimumPromotableDelta` (0.0025) on the median *and* be ahead in
more pairs than the control; a median a fraction above zero is what a neutral change produces half
the time, and one scenario carrying the median is how a change fitted to a single seed reaches the
champion. The result is written as `promotion-evaluation.json` with `Promote`, `Restore`, or
`Undefined`, the basis, reason, aggregate counts, dropped pairs, median paired delta, and every
validated pair. The launcher composes two immutable single-arm runs into this paired batch,
preserving their raw batch ids in the training manifest. It never uses check pass percentage.

### `history.json` and `trend.json` — `schemaVersion` 1

Per-bot index of every run, and a rolled-up diff of the last `RunIndex.TrendWindow` (10) runs on
the headline metrics. A metric that falls by `RunIndex.RegressionThreshold` (15%) against the
**median of earlier runs** — not against the previous run, which at n=1 is as noisy as the thing it
is measuring — is flagged as a regression and named in the next prompt.

Three of the tracked metrics come from `intel` and `scale`: `lateSightingLossPercent` (lower is
better), `counterMatchPercent` and `spendVsOpponentPercent`. Runs recorded before they existed
simply lack them, and the trend says how many runs record each.

Control-arm runs are excluded from the trend series and counted only in the benchmark comparison,
so a control loss never reads as the candidate regressing. The comparison is scoped to one
**batch** — a single invocation of `benchmark-bot.ps1` — rather than to a benchmark name, because
aggregating every run that ever used the name folds a previous revision's candidates and a stale
control into the current win count, which is the exact confounding a control arm exists to remove.

The trend is also scoped to one **difficulty**. Difficulty is not a dial on a single opponent: it
selects a different bot personality and a different handicap together — in this mod Normal is
`cabal` on a 20% handicap and Hard is `hal9001` on none — so a fight above the change and one below
it are different experiments. The first time the ladder was climbed for real, an unsegmented trend
reported nine simultaneous regressions (economy, exchange, army value, buildings destroyed,
exploration), every one of them the new opponent rather than the bot. Runs before the change are
excluded and the report says so, and prompt attribution discards any fitness delta that straddles
the boundary for the same reason.

A fitness score is therefore only comparable within a difficulty. Nothing here rescales it to make
rungs comparable: a multiplier chosen to equate Normal with Hard would be invented, and an invented
number that looks like a measurement is worse than an honest gap.

Runs whose outcome is failed, unknown, or `Undefined` remain in `history.json` as durable evidence
but are excluded from rolling trends and prompt effects. Prompt attribution also refuses to bridge
across an invalid run to a later valid one.

**History is for the improvement agent, between matches.** It is never readable by a running bot,
never compiled into one, and must never justify a map- or opponent-specific constant in strategy
code. `BotSourceAudit` is a cheap mechanical smell test for exactly that failure, and is advisory
by design: failing a build on a regex would be worse than the problem, and the real defence is that
history never reaches a running bot.

### Measuring the prompt itself

The prompt can be revised after manual review and was once rewritten unattended every round, while
remaining the one artifact in the loop with no fitness function at all. Bot code faces a match;
the prompt faced nothing. That asymmetry is why a saved template grew to 27,250 characters of
triage recipes, why one claimed an API did not exist
for many rounds after it shipped, and why a rule stated only in the mutable half was dropped — and
the next round promptly undid the work it protected.

Each run therefore records a `promptId`, and `trend.json` reports what each prompt revision did:

```
- bac2a7fec442 (39,138 chars, 18 sections): +0.085 mean over 4 round(s), 3 better / 1 worse
- 6f6c15be9675 (42,408 chars, 18 sections): -0.077 mean over 3 round(s), 1 better / 2 worse
```

The causal chain runs forwards and is one step long: a round reads its prompt, edits the bot, and
the **next** fight measures that edit. So a prompt's effect is the fitness change across that
boundary, not the fitness of the fight it was handed — that one its predecessor produced.

Identity comes from the learned half's section headings, not from the file's bytes. A rendered
prompt embeds paths, scores and the injected gospel, all of which differ between two rounds given
the same template; hashing the file would make every round unique and measure nothing. The gospel's
own headings are removed first, so a version-controlled gospel edit does not read as the agent
having rewritten its template.

It is weak evidence at small counts and says so — a prompt seen once reports "says nothing yet".
Four rounds of consistently negative effect is a reason to revert to the previous template.

## Regenerating artifacts for an existing run

```powershell
dotnet tools/AutoCnC.Evidence/bin/Release/net8.0/AutoCnC.Evidence.dll summarise `
    "$env:LOCALAPPDATA\AutoCnC\TrainingRuns\<Bot>\<runId>\evidence" `
    --history "$env:LOCALAPPDATA\AutoCnC\TrainingRuns\<Bot>\history.json" --bot <Bot>
```

Safe to re-run: it only writes derived files, and recording a run the index already holds replaces
that entry rather than duplicating it.

**Do not rebuild `history.json` by scanning run directories.** It is the only durable record of a
run once its evidence folder has been deleted, which the launcher's history view lets a player do
at any time, so a rebuild-by-scan silently erases every run whose folder is gone. Normal operation
only ever appends. `WriteHistory` keeps one previous version alongside as `history.json.bak`.
