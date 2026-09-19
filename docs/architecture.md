# Architecture

## The problem

Unit behaviour should be **player-authored code** that is easy to write, easy to reason about, and
safe in multiplayer. Those goals pull against each other:

- *Easy to write* wants full engine access.
- *Easy to reason about* wants no engine at all.
- *Safe in multiplayer* wants code that cannot desync a lockstep simulation — and, once players
  are writing it, code an opponent never has to execute.

Everything below is a response to that tension.

---

## Platform and battle bots

AutoC&C is split into a **platform** (infrastructure) and **battle bots** (strategy). The
platform ships no strategy at all: with no bot loaded, nothing deploys, builds or shoots.

A bot owns several **doctrines** — complete, self-contained ways of fighting — and decides which
one the match needs. A doctrine answers *how* to fight one way; the bot answers *which* way, and
when.

```
┌──────────────────────────────────────────────────────────────┐
│  bots/*               battle bots — all strategy             │
│    IBattleBot           which doctrine, and when             │
│      IDoctrine          build plan, production plan,         │
│                         modes, assignments                   │
│    → built to bin/bots, discovered by scanning               │
└──────────────────────────────────────────────────────────────┘
                              │ loaded by reflection
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Platform     THE HOST (references OpenRA)           │
│    ModeExecutor       client-local; runs the loaded bot      │
│    BattleBotLoader    scans folders for bot assemblies       │
│    BattleAssessor     builds BattleState, fog respected      │
│    ProgrammableController, ModeCommands                      │
│    TurboSpeed, MatchTelemetry, BattleLog, DecisionTrace      │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Sdk          WHAT BOTS CODE AGAINST                 │
│    IBattleBot, IBattleBotBuilder, BattleBot                  │
│    IDoctrine, IDoctrineBuilder                               │
│    IUnitMode, UnitMode, ModeContext                          │
│    IUnitState, IModeHost  (so the SDK needn't know the host) │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Core         ZERO DEPENDENCIES                      │
│    UnitDecision, DoctrineDecision, BattleState               │
│    ThreatSnapshot, BuildStep, ProductionStep                 │
│    BaseBuildLogic, UnitProductionLogic, ModeAssignments      │
└──────────────────────────────────────────────────────────────┘
```

Bots reference the SDK and Core **as binaries**, never as projects. That is what lets a bot live
in its own repository: point `AutoCnCPath` at any AutoC&C checkout and it compiles.

Bots are deliberately **not** listed in `mod.yaml`. They are player artifacts discovered by
scanning `bin/bots` and `<SupportDir>/autocnc/bots`, so you can install several, swap between
them with `/bot`, and share one without anybody editing the mod.

An assembly with `IDoctrine` types and no `IBattleBot` is read as one bot per doctrine. That is
the degenerate case of the model rather than a compatibility shim: a bot with one doctrine has
nothing to decide, so it decides nothing.

### What belongs where

The dividing line is *algorithm versus plan*. Walking a build plan and picking the first unmet
step is infrastructure, so `BaseBuildLogic` lives in Core. Deciding that power comes before a
refinery is strategy, so the plan lives in the doctrine. `BuildBaseMode` reads `ctx.BuildPlan`
rather than hardcoding an order, which is why the same mode serves every doctrine.

The same line runs one level up. Sampling the world into a `BattleState` and rate-limiting
switches is infrastructure, so `BattleAssessor` and the dwell time live in the platform. The only
dwell bypass is an explicit urgent decision targeting `Defence` while a visible enemy is currently
near the base. A doctrine name or rolling building loss alone never implies urgency. Deciding that
two refineries is enough to spare a jeep is strategy, so it lives in the bot.

### What a bot is allowed to know

`BattleState` is built by `BattleAssessor`, and its enemy half goes through
`ModeContext.IsVisibleEnemy` — the same predicate `ctx.SenseThreats` filters with. A client
simulates the whole world, so this filtering has to be deliberate: without it a bot could switch
to an attack doctrine on the strength of an army value no unit of yours has ever seen. Your own
economy and forces are read exactly, because they are yours. The assessment carries rolling
income, exact own value lost, observed enemy value killed, visible enemy value/mix, and own versus
enemy value near the base. The kill-value ledger accepts only enemies present in the latest
pre-damage visibility sample. It intentionally omits kills between samples: actor damage handlers
can uncloak a victim before the player notification, so post-damage visibility is not trustworthy.
The enemy mix is stored internally as a fixed value profile, preserving deterministic
`BattleState` equality and hashing while the public property remains an `IReadOnlyList`.

---

## The decision everything follows from

**Modes execute outside the lockstep simulation.**

`ModeExecutor` is a client-local world trait. It ticks only `world.LocalPlayer`'s units, and its
sole output is `Order`s — the same channel a human's mouse clicks use.

This is what makes player-authored modes possible at all. If behaviour lived in the simulation,
every client would need every other player's mode code to reproduce their units, which means
either desyncs or executing strangers' C#.

Full reasoning: [determinism.md](determinism.md).

### Consequences

**Determinism rules stop applying to mode authors.** Floats, LINQ, `System.Random`, wall-clock
time — all fine, because none of it touches the simulation.

**Mode assignment is client-local policy.** Nothing about "which mode is unit X running" needs
syncing, so there are no orders for assignment and no synced group state. An earlier revision
carried a synced `GroupManager` world trait; moving modes out of the simulation deleted it, and
let us use OpenRA's built-in (client-local) `ControlGroups` directly.

**Latency is the price.** Decisions land a few ticks later, ~120ms. Identical to human input
latency, so it is fair, but it rules out frame-perfect micro.

---

## Execution flow

### Per tick

```
ModeExecutor.Tick                          (client-local, local player only)
  └─ for each owned ProgrammableController
       ├─ SyncGroup     ← engine ControlGroups (client-local)
       ├─ SyncMode      ← ModeAssignments.Resolve(override, group, actorType)
       ├─ due this tick? (staggered by TickInterval)
       └─ mode.OnTick → UnitDecision
            ├─ resolve queue items and concrete support-power keys
            ├─ SameIntent as last issued, and unit not idle? → skip
            ├─ coalesce player-scoped actions across all controllers
            └─ ModeContext.BuildOrder → world.IssueOrder
```

Two throttles keep the order stream sane: duplicate-intent suppression, and `MaxOrdersPerTick`.
Repair and exact production cancellation use synchronized player-actor requests handled by
`SdkActionResolver`; it revalidates live simulation state before applying the native OpenRA order.
Cancellation requests also enter a persistent client-local in-flight registry keyed by player,
concrete queue, item, count, and a platform-owned synchronized monotonic queue revision. The player
trait observes queue identities and composition on synced ticks, while a world order validator
advances revisions before production mutation orders, so A→B→A transitions cannot reuse a token.
The full 64-bit revision and item use a base64-encoded length-prefixed payload, while the count
travels in `Order.ExtraData`; no packed-cell field participates in the identity.

Repair uses the same pattern with a per-building synchronized revision over hit points,
repair-requested, repair-active, and repairability state. The resolver advances it when an
ensure-repair request resolves, so a fast complete-and-redamage cycle cannot be hidden by the
controller's previous `LastIssued` value. Deferred `PlaceBuilding`, `LineBuild`, and `PlacePlug`
orders all invalidate their concrete production queue revision before resolution.

### Assignment precedence

```
per-unit override  >  control group  >  unit type  >  all
```

Resolved by `ModeAssignments`, which is pure and lives in the engine-free assembly so the rules
are directly verifiable.

---

## Key decisions

### `AutoCnC.Core` references nothing

A folder convention is a comment; a missing assembly reference is a compiler error. Because the
core cannot reach `Actor` or `World`, its logic is necessarily pure — and the platform's own logic
tests run in milliseconds without building the engine. To feed new information into a decision you
must add it to the state struct, which keeps the boundary intact by construction.

Note this is now a *testability* guarantee for the platform, not a networking one.

### `OnTick` returns a decision instead of acting

Decisions are inert data, so they can be asserted against, logged, rendered as a debug overlay,
and — critically — **compared between ticks** so the executor can suppress duplicate orders.
An earlier revision had modes call actuators directly; that made duplicate suppression
impossible.

### Player modes are just another assembly

`player-modes/` builds to `engine/bin/PlayerModes.dll`, which is listed in `mod.yaml`'s
`Assemblies:`. OpenRA's `ObjectCreator` then finds `IUnitMode` implementations by reflection —
the same mechanism that binds YAML trait names to `TraitInfo` classes.

This deliberately avoids a bespoke loader or an embedded Roslyn compiler. Players get a real
project with real IntelliSense and a real debugger, and the game gets no new loading code.

### Modes are authored before the match, not during it

Picking up *new or edited* mode code requires a rebuild and a restart, because .NET cannot
replace an assembly already loaded into a process.

This is a deliberate boundary rather than a limitation to engineer away. The game's premise is
that you compose your army's behaviour up front and then commit to it: the match is the test of
what you wrote, not a live coding session. Being able to patch a losing mode mid-battle would
undermine that.

Note this is **only** about loading new code. Switching between modes that already exist is
fully dynamic — `SyncMode` re-resolves every tick and `ApplyMode` swaps the instance
immediately, so `/mode group 1 AttackBaseMode` takes effect within one tick, mid-battle.

The launcher now opens the bot's real solution for edits between matches and owns the
build/restart loop. It deliberately does not hot-reload a running match.

### Modes are per-unit instances

A shared singleton would be marginally cheaper but would force per-unit memory into an external
blackboard, making modes harder to write — the primary goal. Allocation happens only on mode
switch.

### A mode that throws is contained

Player code is wrapped: the exception is logged and printed to chat, the unit's mode is dropped,
and the game continues. One bad mode must not end the match.

### AutoTarget is neutered, not deleted

Deleting it breaks actors that add their own `AutoTargetPriority` (which declares
`Requires<AutoTargetInfo>`), and gating it behind a never-granted condition fails the engine's
condition linter. Forcing `HoldFire` is inert — `Damaged()` returns early below `ReturnFire`,
`TickIdle()` below `Defend` — and needs no conditions.

---

## Verification strategy

| Layer | How it is verified | Cost |
|---|---|---|
| `AutoCnC.Core` | NUnit tests, no engine | milliseconds |
| Trait/YAML wiring | `./scripts/lint.ps1` — constructs every actor | ~1 min |
| Engine integration | Compile against pinned engine binaries | seconds |
| Battle bots | A recorded fight, not a test suite | one match |

The YAML lint is worth more than it sounds: it instantiates every actor in the mod, catching
unsatisfied `Requires<T>`, conditions consumed but never granted, and malformed trait fields —
none of which the C# compiler can see.

Bots are deliberately outside the test story. A bot is judged by whether it wins, which no
assertion can tell you, so the evidence a fight records is the verification and improvement agents
are told not to spend their budget writing unit tests.

---

## Engine integration

The engine is a git submodule pinned to tag `playtest-20260222`, never edited. Our assemblies
reference `engine/bin/*.dll` as prebuilt binaries rather than by `ProjectReference`, following
the OpenRA Mod SDK convention, so bumping the engine tag never drags its internal project layout
into our build. Both mod assemblies output into `engine/bin/`, because OpenRA resolves the
assemblies named in `mod.yaml` relative to that directory.

To upgrade: bump the submodule, rebuild, run the lint, fix what breaks.

### Launching straight into a battle

The launcher and `run-bot.ps1` boot the game into a fight with no menus in between. That is
built from three pieces, all inside our own assemblies:

| Piece | Does |
|---|---|
| `LaunchOptions` | Re-reads the process command line for `Launch.*` arguments the engine does not know about. `Arguments` ignores keys it has no field for, so a mod can add its own without touching `LaunchArguments`. |
| `Server.BattleSetup` | A `ServerTrait` on `IClientJoined` that seats the requested bots and applies handicaps and factions. The engine's `SkirmishLogic` only seats a bot for `ServerType.Skirmish`, and a `Launch.Map` game is `ServerType.Local`, so without this you get a map with nobody on it. |
| `ModeExecutor.WorldLoaded` | Loads the battle bot named by `Launch.BattleBot`, or the one in the assembly at `Launch.BattleBotPath`. |
| `OpenRA.Platforms.Headless` | Supplies the engine's public platform interfaces with a suspended null window, no-op graphics, blank fonts, and no-op sound. OpenRA still constructs its normal renderer/world objects, but creates no OS window or GPU context. |
| `HeadlessSimulation` | After the normal local server/client start completes, drives the client's ordinary immediate-orders -> network-orders -> order generator -> `World.Tick` -> `World.TickRender` sequence in a tight loop. |

| Argument | Meaning |
|---|---|
| `Launch.BattleBotPath` | A bot assembly, or folder of them, loaded in addition to the usual search paths and preferred at world load |
| `Launch.BattleBot` | Load this bot by its declared `Name`. `Launch.Doctrine` / `Launch.DoctrinePath` are still accepted for both |
| `Launch.Bot` | Bot type for the opponents, e.g. `hal9001`. Absent means no battle is set up |
| `Launch.Opponents` | How many of them, clamped to the map's free bot slots |
| `Launch.BotHandicap` / `Launch.Handicap` | 0-95% penalty on the opponents / on you |
| `Launch.Faction` / `Launch.BotFaction` | Faction for you / for the opponents |
| `Launch.GameSpeed` | Tick rate for the match, e.g. `fastest` |
| `Launch.DecisionTrace` | Optional JSONL path for assessments and issued mode decisions |
| `Launch.Headless` | Select the CPU-speed simulation-client loop |
| `Launch.HeadlessReport` | JSON performance report path |
| `Launch.CancellationFile` | Sentinel path for a clean Stop request |

Naming the assembly rather than the bot means nothing outside the bot has to know the `Name`
declared inside it, and a bot played from its own build output cannot be shadowed by a stale copy
in `engine/bin/bots`.

The headless path deliberately is not the dedicated server. The server owns lobby and order
distribution; the client owns the deterministic world, OpenRA bots, AutoC&C modes, telemetry, and
replay recorder. Headless therefore starts the same loopback server and recording
`NetworkConnection` as Rendered, then removes only client scheduling and rendering:

```
TickImmediate -> TryTick -> OrderGenerator.Tick -> World.Tick -> World.TickRender
```

`World.TickRender` remains because AutoC&C's client-local `ModeExecutor`, decision trace, and
telemetry use render-tick traits to emit orders and evidence. Fog/visibility predicates and normal
order latency are unchanged. When every combatant has a final `WinState`, headless ends immediately
instead of waiting for the rendered results panel's 1.5-second wall-clock notification delay.

OpenRA does not expose `World.OrderManager`, so the adapter resolves that one internal field by
name from the deliberately pinned engine build and fails explicitly if it changes. Everything it
invokes is a public engine API; the submodule remains untouched. This is the compatibility seam to
revalidate when bumping OpenRA. The null platform also assumes a launched local AutoC&C battle:
interactive UI, remote multiplayer, editor, and rendering diagnostics belong on the Rendered path.

The platform is chosen by `Game.Platform`, and that is a **saved setting rather than a command
line flag** — the engine writes settings back to the shared OpenRA profile during startup, before
a battle ends. Left alone, asking for `Headless` once makes it the answer for good: every later
launch that does not name a platform loads the null platform whether it was asked for or not, so
rendered battles and replay playback both start, load the mod, run to completion, and never open a
window — with nothing on screen to say why. Only a build carrying `OpenRA.Platforms.Headless.dll`
is affected; an install without it cannot load the name, and the engine's platform fallback moves
on to `Default` and saves that instead. Two things prevent the trap.
`HeadlessPlatform`'s constructor puts the setting back to `Default` the moment it is built, which
is safe because the engine fixes its candidate list before constructing anything, and which also
erases a value an older build already saved, since only non-default settings are written. And
`run-bot.ps1` and `launch.ps1` name `Game.Platform` on every launch instead of only the headless
one, so nothing is ever inherited from whatever ran last.

### Authoring and training flow

The launcher remains a front end over scripts rather than a second build system:

```
New bot        -> scripts/new-bot.ps1 -> standalone solution + package references
Deploy / Fight -> scripts/run-bot.ps1 -> build, install, optionally launch
Improve        -> scripts/train-bot.ps1 -> local coding agent -> build, install
Chat           -> scripts/chat-bot.ps1 -> same agent session, one message
```

`scripts/authoring-api.version` is the compatibility contract between that UI and those scripts.
A launcher ignores a stale remembered checkout when its authoring API is too old and instead uses
the compatible checkout it was launched from; selecting an incompatible checkout disables
authoring actions with an explicit message.

Each fight gets a unique directory outside the source tree. Its manifest records the battle
configuration, execution mode and source revision; the game writes telemetry, the
visibility-filtered battle log, and a decision trace there; the launcher copies the finished replay
and result into the same run. Headless additionally writes `performance.json` (ticks/second,
effective multiplier, wall time and result), which the launcher folds into the manifest.
Completed manifests are reduced to chronological local-versus-opponent KPI samples for the Results
window's iteration charts. Those KPIs are taken from the last sample before the engine settled each
player's fate, with the peak of each figure alongside them, because defeat destroys everything a
beaten player owns and recording continues afterwards — scoring the final sample would make every
loss identically zero. Manifests written before schema 6 are rescored in memory from their own
telemetry on load, leaving the run's evidence on disk untouched. The duration series colors each
point by outcome. This avoids the old fixed-path/one-deep-rotation limit and makes iterations
directly comparable.

The three runtime records have intentionally different trust boundaries:

- telemetry is omniscient and for after-match evaluation only;
- the battle log is restricted to what the side could observe;
- the decision trace records assessments, every unit evaluation and its outcome, and issued
  orders, including stable `ReasonId` values, but is write-only to bot code.

Agent context adds three generated resources. `game-guide.md` is the shared explanation of mechanics,
SDK semantics, and improvement constraints. `mechanics.md` is the **gospel** half of the prompt —
game invariants plus the public bot-facing surface generated from the compiled assemblies by
`scripts/export-agent-api.ps1`. `game-rules.json` is exported by
`--export-agent-rules` directly from OpenRA's `ModData.DefaultRules`, after manifest inheritance
and AutoC&C overrides have resolved. It is therefore a snapshot of the source of truth rather than
a parallel unit-stats model.

The improvement prompt has two halves, and only one of them evolves. The **learned** half is the
template the agent rewrites each round: how to read this bot's evidence, what has been diagnosed,
what to try next. The **gospel** half is injected by the launcher from version control through the
`{gameMechanics}` placeholder, and the agent is told not to restate it.

The split exists because a self-rewriting prompt cannot be trusted to carry facts. A round once
inlined "there is no resource/tiberium sensing API" under the heading "do not rediscover this"; it
was true when written, the API shipped later, and every subsequent round still read it, believed
it, and steered away from the one fix its harvesters needed. Gospel is therefore generated rather
than typed, injected rather than copied, and CI fails when the committed reference no longer
matches the assemblies. A template that arrives without the placeholder — saved before the split,
or proposed by a forgetful round — is repaired rather than refused, because an evolved template can
represent many rounds of work.

AI improvement is explicit and optional. Before invoking a configured local command, the launcher
snapshots the workspace while excluding git metadata and generated output. The command runs from
the bot workspace with an evidence-grounded prompt. The default Copilot configuration grants
access to the run's `evidence/` directory, while the source snapshot and authoritative run state
remain outside its allowed paths. The prompt tells the agent to spend its budget on battle logic
rather than unit tests, because a bot is judged by a recorded fight and not by an assertion. The
wrapper independently runs the deployment build; the launcher streams the raw ANSI terminal output
as colored UTF-8 spans into a dedicated
Improvement window and exposes the exact prompt, guide, resolved rules, fight manifest, and
file-level change set in adjacent tabs. Plain build logs use the same stream with styling removed.
It can restore the pre-agent snapshot. Proving ground and the recorded-session history expose
bounded per-battle feedback, and manual improvement offers to collect missing observations before
preparing the prompt. Regenerating agent context includes them in both `fight.json` and the rendered
`{result}`. Feedback eligibility is enforced by `TrainingRun`, not just the UI: a completed,
recorded headless battle also needs a persisted `ReplayWatchedUtc`. Replay jobs use that run's
captured replay rather than the newest global replay, and only a successful, uncancelled playback
marks it watched. The replay launch script propagates the engine's exit code. Legacy manifests
without the optional watched timestamp remain readable; existing feedback is preserved.

The launcher keeps the latest fought run separate from the selected training run.
`SelectedTrainingRunDirectory` persists the manual choice, and improvement and verification jobs
capture that run in their completion callback so the result cannot land on another battle.
History's train action selects evidence and navigates to AI training; it does not run the agent.
Deleting a completed session validates its archive location and manifest, rejects active runs and
linked directories, and removes only that run's files. The manifest is removed last so an I/O
failure leaves a visible, retryable entry. Last-run and training selections, views, and trend
summaries are refreshed without reintroducing the deleted run.

Continuous mode is a small explicit state machine over the existing script queue: Fighting ->
Improving -> Fighting. Headless is the default execution mode, with Rendered selectable for
watching/debugging. It creates a fresh durable run and source snapshot on every pass, automatically
accepts only a valid complete next-round prompt, and stops on user request or any non-zero game,
agent, or build exit. Headless also treats 90 nominal game minutes without a result as a
failed stalemate (configurable with `-MaxGameSeconds`). Stop writes the run's cancellation sentinel
first, allowing the world, evidence writers and replay recorder to close cleanly before process-tree
termination is used as a fallback. It deliberately has no player-assessment pause.

The JSON views parse lazily into collapsible trees, so the resolved rules snapshot is not expanded
into thousands of controls up front. After coding, the agent returns a complete replacement prompt
template between machine-readable marker lines. In manual mode the round ends on **Next prompt***,
which opens by itself and shows the proposal as a line-by-line difference against the template in
force — unchanged stretches elided — above the editable draft, so the player approves or rejects a
change rather than comparing two walls of text. Editing the draft updates the difference. Rejecting
leaves the saved template untouched and records the decision on the round, so reopening the session
does not present a settled question as outstanding. Continuous mode applies the same validation
before accepting automatically, and does not steal the progress view to display a difference nobody
is reading. Approval replaces the saved template; required placeholders preserve fresh
workspace, evidence, result, and recursive next-template contract values without accumulating
additive guidance. `docs/agent-prompt-template.md` is the repository default, while an approved
replacement is user state. Since approval overwrites that state, each adopted template is also
appended to `%LOCALAPPDATA%\AutoCnC\PromptHistory` as a numbered file plus an `index.json` of
provenance, which is the only record of how the prompt evolved across a long continuous loop.
Script queue activity is mirrored to Windows taskbar indeterminate
progress and cleared on every terminal state.

`train-bot.ps1` records agent and verification phases in `agent-status.json`.
`verify-bot.ps1` owns the shared clean/build/deploy check, so initial verification and the
no-agent retry path cannot drift. A failed agent attempt is archived under `evidence/attempts`;
recovery keeps the current source edits and prepends the archived failure context to the next
agent prompt. The original source snapshot remains outside agent-visible evidence.

A prompt-mode agent answers and exits, so the launcher's conversation continuity is not a process
that stays up but a session id pinned per fight in `manifest.json` and bound to `{sessionId}` in
the provider-neutral agent command. Every turn resumes it: the improvement round, the repair that
follows a failed one, and each message sent through `chat-bot.ps1`. The agent that is asked what it
changed is therefore the agent that changed it, with the round still in its context. Turns share the
single script queue, which is what makes typing during a round safe — two processes resuming one
session would interleave into it, so a message typed while the agent is busy is recorded, held, and
delivered in order when it is free. Queued messages are delivered ahead of the next continuous
training stage, and whether a battle had just finished is carried across that turn so delivering one
cannot cost the loop its next step. What was said is appended to `agent-chat.jsonl` beside the run,
so a conversation outlives the launcher session. Stopping discards what is queued: those questions
were written for a situation the player has just called off.

`BattleSetup` is listed *ahead of* `LobbyCommands` in `mod.yaml`, which matters for exactly one
reason: the engine starts a launched map by issuing a hardcoded `option gamespeed default` from
the client, and it arrives after `IClientJoined` has run. The server stops at the first trait
that claims a command, so being first is what lets `BattleSetup` swallow that one order and
re-issue the speed that was actually asked for. Everything else it sees, it ignores.

---

## Known gaps

- Assignment is chatbox-driven; there is no hotkey or panel UI.
- Assignments are not persisted between matches, so a loadout must be re-entered each game.
- Infantry/vehicle classification falls back to the `Infantry` target type string, which is
  Tiberian Dawn specific.
- No headless benchmark harness for mode-vs-mode evaluation.
- The launcher is Windows Forms, so Windows only. Everything it does is available from
  `run-bot.ps1` on any platform.
- Difficulty tops out at "the strongest bot, with you handicapped": OpenRA's handicap can only
  weaken a player, so there is no way to make the AI itself stronger than its rules.

Deliberately *not* gaps: needing a rebuild and restart to load edited mode code (see
[Modes are authored before the match](#modes-are-authored-before-the-match-not-during-it)).
