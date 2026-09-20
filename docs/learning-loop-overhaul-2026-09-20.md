# Reference Bot Learning-Loop Overhaul

Date: September 20, 2026

Branch: `learning-loop-overhaul`

Implementation range: `f28d05e` through `5379268`

Baseline before this work: `ca3df16`

## Outcome

The Reference bot's training process is now a promotion-gated experimentation loop rather than an
automatic sequence of source mutations.

The loop now:

1. Preserves an immutable champion snapshot.
2. Lets the agent create one candidate.
3. Builds candidate and control into separate immutable artifact directories.
4. Runs both arms against the same benchmark definition.
5. Validates the complete paired scenario plan.
6. Promotes only when the paired evaluation says `Promote`.
7. Restores the champion when the candidate loses or evaluation is incomplete.
8. Stops safely on an `Undefined` result instead of treating missing evidence as progress.

The current Reference bot source remains the champion. Four attempted behavior changes were
measured and rejected rather than accumulated:

- urgent Defence transitions;
- critical building repair;
- support-power targeting;
- centralized harvester-recovery cash reservation.

The SDK capabilities behind those experiments remain available for future candidates.

## Why the loop needed changing

The audit covered 47 completed historical training records:

- 46 losses;
- 1 `Undefined` result;
- 0 wins;
- median fitness `0.2696`;
- mean fitness `0.2952`;
- first-10 mean fitness `0.4175`;
- last-10 mean fitness `0.2955`.

The same source revision scored `0.3028` as GDI versus Nod and `0.1129` as Nod versus Nod in
consecutive fights. That spread was larger than most claimed improvement deltas and demonstrated
that a single random fight was not a reliable promotion decision.

The old continuous loop deployed every successfully built agent edit and automatically accepted
each valid replacement prompt. A build success therefore acted as a promotion even when the next
fight was worse.

## Historical champion selection

The historical `source-before-agent` snapshots were preserved as benchmarkable Git tags:

- `reference-contender-current-20260919`
- `reference-contender-early-20260919`
- `reference-contender-mid-high-20260919`
- `reference-contender-mid-control-20260919`
- `reference-contender-late-high-20260919`
- `reference-champion-20260919`

The new `hard-16-9` benchmark uses eight Hard scenarios:

- two GDI versus Nod;
- two Nod versus GDI;
- two GDI mirrors;
- two Nod mirrors.

Each contender was run twice over the set because OpenRA's local AI random stream is not pinned by
the lobby seed. Identical source, map, factions, and lobby seed can still produce materially
different outcomes.

The early snapshot led the trained-map aggregate:

| Contender | Wins | Matches | Median fitness |
| --- | ---: | ---: | ---: |
| Early snapshot | 2 | 16 | 0.3930 |
| Current bot | 1 | 16 | 0.3343 |
| Mid-high snapshot | 1 | 16 | 0.3292 |
| Late-high snapshot | 0 | 16 | 0.2581 |
| Mid-control snapshot | 0 | 15 valid, 1 undefined | 0.1851 |

The early snapshot did not pass the separate Hard multi-map holdout. Against the current bot it
improved 5 of 12 paired scenarios and lost 7, with median paired fitness delta `-0.0552`.
The incumbent therefore remained champion.

## Benchmark and evidence changes

### Controlled benchmarks

`scripts/benchmarks.json` now contains `hard-16-9`, a pinned Hard set covering all four faction
matchups. Existing benchmark sets were not rewritten.

`scripts/benchmark-bot.ps1` now provides:

- isolated candidate and control build directories;
- a machine-readable `benchmark-result.json`;
- explicit expected match counts;
- repeat and scenario identities;
- complete, failed, and `Undefined` match rows;
- candidate/control paired deltas;
- retry handling for individual failed matches;
- configurable match time limits;
- PowerShell 5.1-compatible syntax;
- verdict text based on the same wins-first and paired-fitness rule used by promotion.

Parallel game processes are intentionally serialized because separate OpenRA processes share
local profile/random state on the supported desktop setup. Reliability is more important than
nominal parallelism.

### Paired evaluation

`AutoCnC.Evidence` now validates:

- schema version;
- benchmark identity;
- difficulty;
- match time limit;
- exact expected scenario count;
- repeat and scenario keys;
- maps, factions, and nonzero seeds;
- complete candidate and control rows;
- complete paired rows.

Promotion is lexicographic:

1. More wins.
2. If wins tie, positive median paired fitness delta.
3. Exact ties retain the control.
4. Missing, failed, mismatched, or `Undefined` evidence cannot promote.

Check pass percentage is not a promotion input.

### Trend hygiene

Failed and `Undefined` fights are excluded from trend and prompt-effect calculations. Control-arm
runs remain excluded from candidate trends and are compared only inside their benchmark batch.

## Continuous-training redesign

Continuous training now follows:

`Fighting -> Improving -> Evaluating -> Promoting`

or:

`Fighting -> Improving -> Evaluating -> Restoring`

Important safeguards include:

- immutable pre-agent champion snapshots;
- immutable candidate snapshots;
- distinct candidate/control assembly paths and hash checks;
- exact live-workspace fingerprint checks before applying a result;
- safe restore preflight and post-copy verification;
- explicit recovery for interrupted evaluations;
- explicit discard/accept-current resolution for unrecoverable snapshots;
- workspace-wide and run-specific mutation locks;
- durable worker identity;
- Windows kill-on-close job objects;
- no-op agent runs recorded as settled champion-preserving evaluations;
- prompt rewriting frozen during continuous mode;
- stale modeless UI objects reloaded before manifest mutation;
- edit-capable chat invalidating a result only when source actually changed.

Manual training, prompt review, replay viewing, feedback, and restoration remain available.

## Check-system changes

Checks now support three categories:

- `activation`: proves a changed path executed;
- `invariant`: protects stable behavior;
- `outcome`: records a measurable prediction without deciding promotion.

`UnitDecision` and `DoctrineDecision` now carry stable `ReasonId` values in addition to explanatory
prose. `reason-id:` checks match the stable identifier exactly. Legacy `reason:` substring checks
remain supported.

Decision traces record evaluated decisions even when order emission is suppressed because the
intent was duplicate, already satisfied, invalid, or over the order limit.

## SDK and platform additions

### Value-aware strategic state

`BattleState` now exposes additive, visibility-safe strategic data:

- exact own credits lost in the assessment window;
- observed enemy credits killed;
- income earned in the assessment window;
- visible enemy value;
- visible enemy value near the base;
- own army value near the base;
- visible enemy count/value grouped by `ThreatKind`.

`Winning` no longer reports success during an active base emergency or a losing known value trade.

`ThreatSnapshot` now includes:

- actor type;
- map cell;
- build value;
- enabled weapon range.

Historical positional constructors remain valid.

### Doctrine transitions

`DoctrineDecision` supports explicit urgent transitions. The platform permits an urgent Defence
request to bypass minimum dwell only while immediate visible pressure is at the base. Ordinary
transitions remain rate-limited.

### Player actions

The SDK now supports:

- ensure-start building repair;
- exact production cancellation;
- support-power discovery and activation;
- owned building repair state;
- production queue details;
- support-power readiness.

Player-scoped actions are globally coalesced. Production cancellation uses synchronized queue
revisions and exact state validation. Repair requests are idempotent and track semantic repair
state rather than raw hit-point changes.

### Production cash arbitration

Bots may optionally implement `IProductionBudgetBot`. The policy returns one `ProductionBudget`
for a queue Group/Type at each strategic assessment.

The platform:

- resolves Group before Type;
- uses actual queue production cost after modifiers;
- accounts for pending and synchronized unpaid production;
- preserves commitments across network latency;
- tracks ordered per-queue acknowledgements;
- enforces the reservation independently of controller evaluation order;
- uses identity-based fair admission under the order cap;
- traces active, inactive, invalid, unmatched, and suppressed budget states.

Existing direct `IBattleBot` implementations remain compatible because budgeting is an optional
companion interface. `BattleBot` implements it with a no-reservation default.

## Measured bot experiments

### Urgent Defence

The path executed broadly, but the candidate did not pass:

- candidate wins: 0;
- champion wins: 1;
- one candidate scenario was `Undefined`;
- median paired fitness delta: `-0.0279`;
- exchange and building destruction regressed.

Result: rejected and reverted.

### Critical building repair

The repair branch executed in four matches. Results:

- candidate wins: 0;
- champion wins: 1;
- median paired fitness delta: `+0.0091`;
- mean fitness and income were lower.

Result: rejected and reverted.

### Support powers

On `hard-16-9`, support powers initially looked strong:

- candidate wins: 1;
- champion wins: 0;
- median paired fitness delta: `+0.0646`;
- candidate improved 12 of 16 pairs.

The separate Hard multi-map holdout reversed the finding:

- wins tied 0-0;
- median paired fitness delta: `-0.048`;
- candidate improved 5 of 12 pairs.

Result: rejected and reverted.

### Harvester production budget

The policy executed in all 16 candidate fights. Results:

- candidate wins: 0;
- champion wins: 2;
- candidate median fitness: `0.3474`;
- champion median fitness: `0.3328`;
- median paired fitness delta: `+0.0595`;
- mean paired fitness delta: `-0.0403`;
- candidate destroyed materially fewer buildings.

The wins-first promotion rule rejected the candidate despite improvements in several median
economic metrics.

Result: rejected and reverted.

## Final validation

Final exact-tree validation at `5379268`:

- Core/SDK/Platform tests: 84 passed;
- Evidence tests: 100 passed;
- targeted launcher/promotion tests: 117 passed;
- combined validated tests: 301 passed;
- Release platform/package/Reference bot build: 0 warnings, 0 errors;
- generated SDK API documentation: current;
- AutoC&C YAML, Fluent, sequence, and map lint: passed;
- `git diff --check`: passed;
- worktree: clean.

The broader launcher suite also contains environment-dependent command-host fixtures. Those require
their expected fake commands and engine-runtime environment; they are not used as the promotion
release gate. The promotion, snapshot, worker, ScriptRunner, chat, and recovery test groups pass.

## Final repository state

- Current branch: `learning-loop-overhaul`
- Final implementation commit: `5379268`
- Current Reference bot: preserved champion behavior
- Champion tag: `reference-champion-20260919`
- Controlled benchmark evidence:
  `%LOCALAPPDATA%\AutoCnC\BenchmarkRuns\ReferenceBot\20260919-learning-loop`

The principal outcome is not a claim that one new tactic wins Hard. It is that future tactics now
have to demonstrate improvement against an immutable champion before they are allowed to
accumulate.
