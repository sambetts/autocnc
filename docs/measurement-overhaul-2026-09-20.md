# Why 23 Rounds Produced No Progress

Date: September 20, 2026

Baseline audited: `e0f329d`, covering the 23 continuous-training rounds recorded under
`%LOCALAPPDATA%\AutoCnC\TrainingRuns\ReferenceBot`.

## Outcome

The loop was not failing to find improvements. It was unable to tell an improvement from a coin
flip, and it was promoting on the result anyway.

Two defects did the damage. The opponent was never pinned, so a "pinned" benchmark scenario was a
fresh game every time it ran. And the promotion gate accepted any candidate whose median landed a
fraction above zero, which a neutral candidate does half the time.

Both are fixed. The benchmark is now reproducible to the last decimal place, and a candidate that
changes nothing is now refused rather than promoted on a coin flip.

## What the 23 rounds actually contained

| | Count |
| --- | ---: |
| Promoted | 12 |
| Restored | 6 |
| `Undefined` (evaluation discarded) | 5 |
| Valid rounds | 18 |
| Paired scenarios | 144 |

The champion is re-measured every round, because the control arm is always the reigning champion
played over the same eight pinned scenarios. That series is the progress curve, and it is flat:

```
0.37 0.40 0.31 0.23 0.45 0.33 0.34 0.25 0.24 0.23 0.23 0.25 0.38 0.34 0.54 0.38 0.43 0.45
```

Mean `0.34`, SD `0.092`, no trend across 12 promotions. Whatever was promoted did not accumulate.

## Defect 1: the opponent was seeded from the wall clock

`scripts/benchmarks.json` pins a seed per scenario, and `BattleSetup.PinSeed` writes it into
`LobbyInfo.GlobalSettings.RandomSeed`. Its comment claimed this "reaches every consumer of match
randomness". It did not.

`World` built two generators:

```csharp
SharedRandom = new MersenneTwister(orderManager.LobbyInfo.GlobalSettings.RandomSeed);
LocalRandom = new MersenneTwister();          // == new MersenneTwister(Environment.TickCount)
```

Every built-in bot module draws from `LocalRandom`, not `SharedRandom` — `BaseBuilderBotModule`,
`BaseBuilderQueueManager`, `SquadManagerBotModule`, `HarvesterBotModule`, `UnitBuilderBotModule`,
`McvManagerBotModule`, `McvExpansionManagerBotModule`, `SupportPowerBotModule`,
`CaptureManagerBotModule`, `MinelayerBotModule`, `PowerDownBotManager`, `ResourceMapBotModule`
and `Squad` itself. Squad sizes, attack timings, rally points, build order shuffles, production
choices and harvester assignment were all drawn from a clock reading.

So a pinned seed reproduced the map and the factions and nothing the opponent did with them. The
consequence is measurable in the history: the *same champion* on the *same pinned seed* scored

| Seed | Min | Max |
| --- | ---: | ---: |
| 300002 | 0.108 | 1.000 |
| 300003 | 0.128 | 0.975 |
| 300004 | 0.139 | 1.000 |

Per-pair fitness deltas across all 144 scenarios had SD `0.2639`. At eight pairs that is a
standard error near `0.1`, and promotions were being made on medians of `+0.00` to `+0.09`.
Detecting a genuine `+0.05` effect through that much noise needs a few hundred paired matches, not
eight. The benchmark was an anecdote generator with a schema.

### The fix

`engine/OpenRA.Game/World.cs` now derives `LocalRandom` from the lobby seed:

```csharp
LocalRandom = new MersenneTwister(
    unchecked(orderManager.LobbyInfo.GlobalSettings.RandomSeed ^ 0x5f356495));
```

The constant keeps the two streams from running in lockstep. Ordinary play is unaffected, because
an unpinned lobby seed is still `DateTime.Now.ToBinary()`.

### Verification

`hard-16-9` was run A/A: the identical bot assembly in both arms, the existing serial
candidate-then-control order left alone.

| Seed | Candidate | Control | Paired delta |
| --- | --- | --- | ---: |
| 300001 | Lost | Lost | 0.0000 |
| 300002 | Lost | Lost | 0.0000 |
| 300003 | Lost | Lost | 0.0000 |
| 300004 | Won | Won | 0.0000 |
| 300005 | Lost | Lost | 0.0000 |
| 300006 | Lost | Lost | 0.0000 |
| 300007 | Lost | Lost | 0.0000 |
| 300008 | Lost | Lost | 0.0000 |

Both arms: median fitness `0.4053`, 1 win of 8. Every paired delta is exactly zero.

Benchmark noise went from SD `0.2639` to `0`. A nonzero paired delta is now attributable to the
code change and to nothing else, which is what the paired design always claimed to provide.

This also settles the arm-order question. Candidate arms had won 13 of 144 against control's 6,
and the candidate always ran first; the A/A run reproduces the control exactly, so serial ordering
contributes nothing and interleaving is unnecessary.

## Defect 2: the promotion gate was a coin flip

`PairedBenchmarkEvaluator` ranked lexicographically: more wins promotes; otherwise promote when
`MedianPairedFitnessDelta > 0.00015`.

For a candidate that is genuinely neutral, both branches are symmetric. `P(candidateWins >
controlWins)` equals `P(controlWins > candidateWins)`, and on the remaining ties the median is
above zero half the time. `0.00015` is a floating-point deadband, not an evidence threshold.

```
p = 0.04: P(cand>ctrl) = 0.210, P(tie) = 0.579  ->  0.210 + 0.579/2 = 0.50
p = 0.09: P(cand>ctrl) = 0.312, P(tie) = 0.377  ->  0.312 + 0.377/2 = 0.50
```

**A neutral candidate was promoted 50% of the time, at any win rate.** Across 18 valid rounds that
predicts about 9 promotions from pure chance. Twelve were observed.

Two promotions were self-contradicting in the same sitting — promoted on a single win while the
median paired fitness said the candidate was broadly worse:

| Round | Wins | Median delta | Pairs candidate/control |
| --- | --- | ---: | --- |
| `20260920-085945` | 1–0 | −0.0936 | 2 / 6 |
| `20260920-155514` | 1–0 | −0.0870 | 3 / 5 |

### The fix

Wins still rank first, and the gate now adds two conditions:

1. **A win may not carry a broad regression.** If the candidate leads on wins but the median
   paired fitness delta is negative, the verdict is `Restore`. On a reproducible benchmark a
   negative median is a measured regression, not an unlucky draw.
2. **A fitness promotion needs a margin and breadth.** With wins level, the median must clear
   `MinimumPromotableDelta = 0.0025` *and* the candidate must be ahead in more pairs than the
   control. One scenario carrying the median is how a change fitted to a single pinned seed
   reaches the champion.

Replaying the 18 historical evaluations through the new gate:

| | Promote | Restore |
| --- | ---: | ---: |
| Old rule | 12 | 6 |
| New rule | 8 | 10 |

The four blocked promotions are exactly the pathological ones: the two wins-with-regressions
above, one tied round whose median was `+0.0014`, and one tied round with a `+0.02` median that
helped and harmed four pairs each.

Combined with the seeding fix, a candidate identical to the champion now produces all-zero deltas,
ties on wins, fails the margin, and is restored. The false-promotion rate is no longer 50%; it is
zero.

## Benchmark sets

`hard-16-9` is unchanged, so existing results stay comparable. Two sets are added.

`hard-16-9-wide` — sixteen matches, four seeds per faction matchup. Multiplying seeds was
pointless while a seed did not name an opponent; now that it does, a wider set buys scenario
coverage instead of repeat sampling of one noisy draw.

`hard-holdout` — twelve matches over `chokepoint`, `blue-mountains` and `carters-ridge`, maps no
training set uses. A deterministic benchmark is a fixed list of games and a change *can* be fitted
to it. This set exists to be the thing that was not optimised against. Read it; do not promote on
it.

## What this does not fix

Honest limits, so the next round does not mistake them for solved problems.

- **Determinism is not generality.** The benchmark is now exactly reproducible, which makes
  overfitting *easier*, not harder. `hard-holdout` is the guard, and it only works if it stays
  unoptimised.
- **The champion is only re-validated on `16-9`.** The control arm re-measures it every round on
  the training map alone.
- **The comparator is not transitive.** Promotion compares a candidate to the current champion, so
  a chain of pairwise-justified promotions can still drift. A fixed reference arm would detect it.
- **`checks.json` is not in the gate.** Activation and invariant checks are recorded, not enforced.
  A change whose new branch never executed can still be promoted on the match result.
- **The agent is briefed on one training fight.** It reads a single `Faction=Random` match and is
  judged on eight pinned matchups. Pointing it at the champion's own benchmark evidence — the
  matchups it never wins — is the obvious next change.
- **A single failed match still voids the sitting.** `benchmark-bot.ps1` exits 1 if any match
  fails, and the evaluator maps that to `Undefined`, discarding the other fifteen. Retrying to
  completion is the real fix; 5 of 23 rounds ended `Undefined`.
- **Fitness excludes the win bit by design**, and empirically a win scores ~0.98 against a loss's
  ~0.34. The median of eight pairs therefore mostly compares loss against loss.

## Validation

- Evidence tests: 103 passed (3 added, 1 rewritten).
- Core/SDK/Platform tests: 84 passed.
- A/A benchmark on `hard-16-9`: 16 matches, 8 paired deltas, all exactly zero.
- `scripts/benchmarks.json` parses; 5 sets resolve.
