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
dotnet AutoCnC.Evidence.dll summarise <evidenceDir> [--history <file>] [--checks <file>] [--bot <name>]
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

An unresolvable query produces a failed result carrying an `Error`. It never throws out of
`Checks.Evaluate`, because one bad check must not cost the other nine.

### `history.json` and `trend.json` — `schemaVersion` 1

Per-bot index of every run, and a rolled-up diff of the last `RunIndex.TrendWindow` (10) runs on
the headline metrics. A metric that falls by `RunIndex.RegressionThreshold` (15%) against the
**median of earlier runs** — not against the previous run, which at n=1 is as noisy as the thing it
is measuring — is flagged as a regression and named in the next prompt.

Control-arm runs are excluded from the trend series and counted only in the benchmark comparison,
so a control loss never reads as the candidate regressing. The comparison is scoped to one
**batch** — a single invocation of `benchmark-bot.ps1` — rather than to a benchmark name, because
aggregating every run that ever used the name folds a previous revision's candidates and a stale
control into the current win count, which is the exact confounding a control arm exists to remove.

**History is for the improvement agent, between matches.** It is never readable by a running bot,
never compiled into one, and must never justify a map- or opponent-specific constant in strategy
code. `BotSourceAudit` is a cheap mechanical smell test for exactly that failure, and is advisory
by design: failing a build on a regex would be worse than the problem, and the real defence is that
history never reaches a running bot.

## Regenerating artifacts for an existing run

```powershell
dotnet tools/AutoCnC.Evidence/bin/Release/net8.0/AutoCnC.Evidence.dll summarise `
    "$env:LOCALAPPDATA\AutoCnC\TrainingRuns\<Bot>\<runId>\evidence" `
    --history "$env:LOCALAPPDATA\AutoCnC\TrainingRuns\<Bot>\history.json" --bot <Bot>
```

Safe to re-run: it only writes derived files, and recording a run the index already holds replaces
that entry rather than duplicating it.
