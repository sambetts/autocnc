# Learning-Loop Overhaul: Fresh Seeds, Clean Fights, and a Memory

Date: September 28, 2026

Audited: the 142 training runs recorded under `%LOCALAPPDATA%\AutoCnC\TrainingRuns\ReferenceBot`
between 20 and 27 September, their benchmark and evaluation files, the loop logs, the prompt
archive, and a separate review of the same data by a second model.

## Outcome

From 26 September 16:09 onwards the loop could not promote anything. The champion won all eight
games of the pinned `hard-16-9` gate, so a candidate could at best tie, and a tie was decided by a
fitness score that caps at 1.0 in a win. There were 24 rounds running and no promotions. Every one
of the 8-8 ties had a median fitness change of 0 or below.

The measurements that should have caught this were compromised as well. Many "champion" fights had
actually been played by the rejected candidate. The candidates' own claims about their code were
never tested. And the prompt carried a false account of how noisy the benchmark was.

The loop now:

1. **Fights with the code it names.** Every training fight rebuilds the bot from source and records
   the hash of the assembly that played.
2. **Gates on seeds nothing was selected on.** Each champion gets a fresh pool of seeds. The
   candidate plays them a stage at a time, and a sequential test on the games only one arm won
   decides.
3. **Counts a game stopped at the time limit as a non-win.** The pair is kept instead of dropped.
4. **Plays benchmark games in parallel.** Each game gets its own support directory, and the
   parallel games were confirmed to replay identically to serial ones.
5. **Tests each candidate's checks in its own games** and keeps a harness-owned experiment ledger,
   which the next prompt is given.
6. **Adopts the next prompt only after the verdict**, identifies prompts by their content, and stops
   presenting noise as findings.

## What was wrong

### 1. Fights labelled as the champion's were played by the rejected candidate

`WorkspaceSnapshot.Restore` put a rejected candidate's files back with `File.Copy`, which keeps
the snapshot's older timestamps. The next fight's incremental build therefore saw no source newer
than the candidate's output, and fought with the candidate's DLL. Evidence from 24 September on:

- 11 of 52 decided opening fights logged reason ids that exist only in the preceding rejected
  candidate's source.
- All 11 followed candidates that edited existing files only.
- A candidate that added a file changed the compile list, which forces a rebuild, so it never leaked.

Reproduced on a copy of the bot with real builds:

| Step | Assembly |
| --- | --- |
| Champion A built | `37C052BB9F55` |
| Candidate B (one file edited) built | `D8B9518D7E25` |
| Old restore, then an incremental build | `D8B9518D7E25`: still the candidate |
| `run-bot.ps1`, which now always rebuilds | `37C052BB9F55` |
| New restore (touches the timestamp), then an incremental build | `37C052BB9F55` |

Only 7 of the current champion's 18 opening fights are provably clean, and it won 4 of them. The
rejected candidates that leaked into opening fights won 9 of 11.

### 2. The gate was closed and fitted to itself

The eight pinned seeds were reused for every selection.

- Promotions tended to go to candidates whose re-rolled games happened to come out better.
- So the pinned record rose from 5/8 to 8/8, while the champion's clean random-seed win rate stayed
  near 56%.
- `cdcda7c` went 8/8 on the pinned set and 4/8 on `hard-holdout`.

A candidate changes a median of 7 of the 8 games, so determinism bought little per comparison. It
did bring a correlation between the arms: P(candidate wins | champion won) = 0.70, against 0.13
when the champion lost. That correlation is why the pairing on seeds is kept.

Even on fresh seeds, the old "one more win" rule over 8 pairs is weak. In simulation it would
promote:

- a change that does nothing 42% of the time;
- a +10-point change 61% of the time;
- a +20-point change 81% of the time.

### 3. A timeout erased the other arm's win

`run-bot.ps1` treats a stalemate as a failure. `benchmark-bot.ps1` then retried it, spending a
second full-length game, and marked it `Undefined`. The evaluator dropped the pair from both arms.

In `20260926-075804` the candidate timed out on seed 300004. That removed the champion's win there,
and a raw 7/8 was reported as 6 of 7. This is the source of the prompt's line "identical code has
scored 6 and 7 wins on one day". Every repeat of that champion's control arm had in fact reproduced
7/8 exactly. Eighteen evaluations, five of them promotions, rested on dropped pairs.

### 4. The loop never learned what its experiments showed

- **Candidate checks were never scored.** `checks.json` lives in the workspace, so a restore
  discarded each rejected candidate's checks. Every round from 26 September 16:09 onwards scored the
  same file, the one written by the round that produced the champion. None of the 80 candidate
  games on 27 September had a check result.
- **The next prompt was adopted before the verdict.** Its "where the bot stands" section therefore
  described changes that had already been thrown out.
- **Rejections became a permanent blacklist.** "Tried and not kept" grew to 20 ideas and a verdict
  that "tower tactics have failed the gate three times". Yet about four in ten rejections were
  decided by a single game, and the gate could no longer promote anything.
- **Prompt identity used only the headings.** Revisions 61-70 shared one id, and the trend told
  rounds that this "prompt" had consistently made the bot worse. That verdict came from comparing
  adjacent random fights.
- **The trend flagged artefacts as regressions.** It treated `durationSeconds` as better when
  longer, so every quick win was flagged. `idleUnitSeconds` and `cellsExplored` were flagged in the
  same way, and every round was asked to explain all three before doing anything else.

A smaller bug was found while validating the fixes. `rules-fingerprint.ps1` piped
`git rev-parse` into `Select-Object -First 1`. That stopped git before it exited, which is a race:
under load `$LASTEXITCODE` read as failure, and the same rules got a second fingerprint.

## What changed

### Fights (`scripts/run-bot.ps1`, `WorkspaceSnapshot.cs`, `TrainingLoopRunner.Fight`)

- **Always a full rebuild.** Every project build runs `dotnet build --no-incremental`.
- **The fight records what played.** `bot-assembly.json` beside the battle log holds the path and
  SHA-256 of what played.
- **A restored file is marked as just written.** A restored file's timestamp is set to now, so an
  incremental build elsewhere also rebuilds.
- **A tripwire in the loop.** The loop warns when the same source fingerprint fights with a
  different assembly than it did before.
- **`-SupportDirectory`** runs a match in its own OpenRA support directory.

### The gate (`SequentialGate.cs`, `ContinuousPromotionRunner.cs`, `scripts/benchmarks.json`)

`hard-16-9-fresh` is the loop's default gate. It has no fixed matches.

- **A fresh pool per champion.** When a champion is first evaluated, the gate draws 96 seeds nobody
  has used, from 1,000,001 upwards. They are stratified so that every stage of 16 holds four of each
  faction pairing.
- **Stages.** The candidate plays one stage at a time. The champion plays only the seeds the pool
  has not cached, plus one cached game per evaluation as a canary.
- **The decision.** Wald's sequential probability ratio test runs on the games exactly one arm won:
  - α = 0.05, β = 0.2, with an alternative of three to one;
  - it is checked after each stage;
  - an undecided test at 96 pairs is `inconclusive`;
  - a candidate game that crashes is a loss, and a champion game that crashes drops its pair.
- **Invalidation.**
  - A promotion retires the pool.
  - A change to the harness key (engine binaries, rules fingerprint, difficulty table, time limit,
    fitness scale) clears the cache but keeps the seeds.
  - A canary that does not replay exactly stops the cache being used for that pool.
- **State** lives in `<runs>/<bot>/gate/hard-16-9-fresh.json`.
- **Same output files as before.** Results use the same `benchmark-result.json` and
  `promotion-evaluation.json` shapes, with the test under `sequential`.
- **One step API.** `NextBenchmarkStep` and `CaptureBenchmarkStep` drive both the launcher and the
  loop. A pinned set is two steps, as before.

Simulated operating characteristics, using the measured behaviour (a 60% win rate and 30% of games
decided differently; 20,000 trials each):

| True effect on win rate | P(promote) | Mean pairs played |
| --- | ---: | ---: |
| −10 points | 0.000 | 28 |
| none | 0.024 | 49 |
| +10 points | 0.37 | 70 |
| +15 points | 0.72 | 65 |
| +20 points | 0.95 | 52 |

Effects smaller than about ten points cannot be resolved at this budget, and the verdict says
*inconclusive* rather than calling them failures. Only candidate games are paid for once a pool's
champion games are cached.

### The runner (`scripts/benchmark-bot.ps1`)

- **`-Parallel N` now runs in parallel.** Each game is a child process with its own copy of the
  support directory. Each is summarised as soon as it ends, while the next games play. A parallel
  A/A run of the champion on `hard-16-9` reproduced the serial results exactly on all 8 seeds, and
  took 203 s at three at a time against about 450 s serially.
- **`-MatchesFile`** plays an explicit list, keeping each match's scenario number.
- **`-ChecksFile`** evaluates a candidate's checks in every one of its games.
- **`TimedOut` is a decided outcome.** It is not retried and not a win. `PairedBenchmarkEvaluation`
  keeps its pair.
- **Game rules are exported once per sitting** instead of once per game.

### Memory (`ExperimentLedger.cs`, `scripts/train-bot.ps1`, `docs/agent-prompt-template.md`)

- **`reason-ids.json`.** Every fight gets it: each reason id the trace logged, and how often.
- **The ledger.** After every verdict, the loop appends a record to `<runs>/<bot>/experiments.jsonl`
  containing:
  - the files the candidate changed;
  - the reason-id literals it added, with how many of its own games logged each;
  - its checks, tallied across those games;
  - its `hypothesis`, a new optional field in `checks.json`;
  - the opening fight it read;
  - the gate's conclusion.
- **`{experimentLedger}` in the prompt.** The next prompt gets the newest 12 records, headed by the
  current champion's record in its random-seed opening fights, with a Wilson interval. The records
  are rendered by `train-bot.ps1` through `AutoCnC.Evidence ledger`. A template that drops the
  placeholder has the ledger appended.
- **The gate's description moved to the fixed half of the prompt.** It now lives in
  `docs/agent-mechanics.md` ("How a bot is measured"), which no round can rewrite. The live template
  lost the false and obsolete claims, and its "tried and not kept" list is reframed as untested
  attempts under the old gate.
- **The next prompt is adopted after the verdict**, and before a promotion's commit, so the committed
  prompt is the one in force.
- **Prompt identity.** A prompt's id is the hash of its unrendered template, which the launcher now
  keeps beside the fight as `agent-prompt-template.md`.
- **The trend is tamer.**
  - `durationSeconds`, `cellsExplored` and `idleUnitSeconds` are shown but never flagged.
  - Large moves are listed as leads rather than "regressions to explain before doing anything else".
  - Per-prompt "effects" are no longer rendered.

### Holdout audit (`TrainingLoopRunner.AuditPromotion`)

After every promotion the loop plays the new champion on `hard-holdout`, whose maps no training set
uses. The first time, it also plays the champion that was replaced, as the baseline.

- The results go to `<runs>/<bot>/audit.jsonl` and the loop output.
- They never decide a promotion and are not shown to the agent: a set that steers selection stops
  being a holdout.
- Holdout games are deterministic, so the baseline is played only once.

This is the out-of-sample reading the week's promotions never had. `-AuditBenchmark ''` turns it
off.

## Validation

- **Evidence tests:** 140 passed, 19 of them new, covering:
  - the pool, stratification and seed allocation;
  - pool retirement, and harness-change invalidation;
  - the test's bounds, and inconclusive truncation;
  - crash handling;
  - composition;
  - canary reproduction;
  - the shipped set's validity and seed separation;
  - the ledger (new reason ids, activations, check tallies, rendering, champion record, Wilson);
  - a timed-out pair;
  - the trend's flags and rendering;
  - content-based prompt identity.
- **Launcher tests:** the loop, promotion and script suites pass, including new tests for:
  - fresh-seed promotion;
  - cache reuse with a single canary;
  - cache abandonment after a failed canary;
  - a canary whose game failed to run, which leaves the cache trusted;
  - pool retirement on promotion;
  - candidate checks in candidate games;
  - adoption after the verdict;
  - restored-file timestamps;
  - the holdout audit, including a baseline played once;
  - the checked-in prompt and gate.
- **Real builds:** the stale-DLL table above.
- **Real games:**
  - the parallel A/A run above;
  - a run with `-MatchesFile`, `-ChecksFile`, `-Parallel 2` and a 120-second limit gave two
    `TimedOut` rows, each played once, with check results, `reason-ids.json` and
    `bot-assembly.json` beside them, and no support directories left behind.
- **Rules fingerprint:** `44db50ef17715b00` in child processes and in-process alike, after the fix.
- **The first real round on the new gate** (`20260928-122503`) ran four stages, 64 fresh pairs.
  - The champion won 46 of its 64 fresh games (72%): well short of the 8 of 8 it managed on the
    pinned seeds, so there is room to measure.
  - The candidate held the first push until two siege pieces stood. It won 8 games the champion
    lost and lost 8 the champion won, so the test crossed its lower bound (LLR −2.30) and restored
    it as "not better".
  - Its ledger entry shows the change really ran: its new reason ids fired in 57 and 58 of its 64
    games. It also carries the candidate's own `hypothesis` and its check tallies.
  - The next round's prompt was given that entry.
  - The fought-assembly tripwire then fired on a valid fight. The SDK stamps the checkout's HEAD
    into the assembly's informational version, and a commit had moved HEAD between rounds. It now
    compares assemblies only within one source and one commit.

## What this does not fix

- **One map, one opponent.** The gate and the training fights still use `16-9` against Hard HAL
  9001. The holdout audit reports on two other maps after each promotion but decides nothing, and
  nothing rolls back a champion automatically when the audit shows no gain.
- **Small effects stay invisible.** A +10-point change passes about a third of the time. Bigger,
  coherent changes are what this gate can see, and the prompt now says so.
- **Fitness is a diagnostic, not a gate.** Its capped components are unchanged.
- **The launcher's continuous mode still defaults to `hard-16-9`.** It can select
  `hard-16-9-fresh` in settings.
- **The no-unit-tests rule stands.** A reviewer suggested allowing small pure-logic tests, but that
  would reverse a deliberate rule.
- **History is not rewritten.** Opening fights before today are source-attributed rather than
  proven, and the champion's earlier records should be read with that in mind.

## 29 September: learning from losses

**The first 20 rounds under the new gate kept nothing.**

- 9 measured worse, 8 of them stopped before 96 pairs.
- 1 was "not better", also stopped early.
- 10 were inconclusive after all 96 pairs.

The gate and the cache behaved as designed: every replayed champion game reproduced exactly, and
clearly worse changes were rejected after as few as 16 games.

**The changes rarely decided a game.** Across 1,456 fresh paired games, the candidates changed
the winner in 136 (9.3%): 61 in their favour and 75 against. So the gate had very little to measure.

**Most rounds had studied a game the bot won.** The random-seed training fight is won about 70% of
the time, and 14 of the 20 rounds studied a win:

| Round studied | Rounds | Test games | Winner changed |
| --- | ---: | ---: | ---: |
| A win | 14 | 1,104 | 7.5% |
| A loss | 6 | 352 | 15.1% |

Changes made from a loss moved twice as many results. They were no better on balance (23 for, 30
against), but a change the gate can measure is a precondition for one it can keep.

**What changed:**

- **`-MaxFightsPerRound` (default 4).** The loop (`TrainingLoopRunner.FightUntilALoss`) keeps
  playing fresh random seeds while the bot wins, and the round studies the first loss, or the last
  win if every fight was won. At a 70% win rate this is about 2.5 fights a round, roughly a minute
  and a half more.
- **Each fight is its own run, announced as it starts,** so the nightly pause still cancels
  whichever fight is in progress.
- **The wins passed over are kept.** They are listed in the studied run's manifest
  (`EarlierFights`) and in its ledger record, and the champion's record counts them.
  - Stopping at the first loss would otherwise make the bot look worse than it is.
  - Counting every fight keeps the pooled win rate an unbiased estimate: by Wald's identity, the
    expected wins over the expected fights is still the true win rate.
- **The prompt says what the fight is:** the first loss after how many wins, or that every fight
  was won.
- **`docs/agent-mechanics.md`**, the fixed half of every prompt, now:
  - gives these numbers;
  - asks for the moment the lost game became unwinnable;
  - asks for an estimate, recorded in `hypothesis`, of how many of 100 games the change would turn
    each way;
  - asks for something bigger when the net is under about 5, where a coherent set of edits serving
    one plan counts as one change.
- **The next-prompt contract** tells each round to keep the next one aimed at changes that decide
  games.
