# Architecture

## The problem

Unit behaviour should be **player-authored code** that is easy to write, easy to test, and safe
in multiplayer. Those goals pull against each other:

- *Easy to write* wants full engine access.
- *Easy to test* wants no engine at all.
- *Safe in multiplayer* wants code that cannot desync a lockstep simulation — and, once players
  are writing it, code an opponent never has to execute.

Everything below is a response to that tension.

---

## Platform and modules

AutoC&C is split into a **platform** (infrastructure) and **doctrines** (strategy). The
platform ships no strategy at all: with no module loaded, nothing deploys, builds or shoots.

```
┌──────────────────────────────────────────────────────────────┐
│  modules/*            doctrines — all strategy          │
│    IDoctrine      build plan, production plan,           │
│                       modes, assignments                     │
│    → built to bin/doctrines, discovered by scanning            │
└──────────────────────────────────────────────────────────────┘
                              │ loaded by reflection
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Platform     THE HOST (references OpenRA)           │
│    ModeExecutor       client-local; runs the loaded module   │
│    DoctrineLoader       scans folders for module assemblies    │
│    ProgrammableController, ModeCommands                      │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Sdk          WHAT MODULES CODE AGAINST              │
│    IDoctrine, IDoctrineBuilder                       │
│    IUnitMode, UnitMode, ModeContext                          │
│    IUnitState, IModeHost  (so the SDK needn't know the host) │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Core         ZERO DEPENDENCIES                      │
│    UnitDecision, ThreatSnapshot, BuildStep, ProductionStep   │
│    BaseBuildLogic, UnitProductionLogic, ModeAssignments      │
└──────────────────────────────────────────────────────────────┘
```

Modules reference the SDK and Core **as binaries**, never as projects. That is what lets a module
live in its own repository: point `AutoCnCPath` at any AutoC&C checkout and it compiles.

Modules are deliberately **not** listed in `mod.yaml`. They are player artifacts discovered by
scanning `bin/doctrines` and `<SupportDir>/autocnc/doctrines`, so you can install several, swap
between them with `/module`, and share one without anybody editing the mod.

### What belongs where

The dividing line is *algorithm versus plan*. Walking a build plan and picking the first unmet
step is infrastructure, so `BaseBuildLogic` lives in Core. Deciding that power comes before a
refinery is strategy, so the plan lives in the module. `BuildBaseMode` reads `ctx.BuildPlan`
rather than hardcoding an order, which is why the same mode serves every module.

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
            ├─ SameIntent as last issued, and unit not idle? → skip
            └─ ModeContext.BuildOrder → world.IssueOrder
```

Two throttles keep the order stream sane: duplicate-intent suppression, and `MaxOrdersPerTick`.

### Assignment precedence

```
per-unit override  >  control group  >  unit type  >  all
```

Resolved by `ModeAssignments`, which is pure and lives in the engine-free assembly so the rules
are directly testable.

---

## Key decisions

### `AutoCnC.Modes.Core` references nothing

A folder convention is a comment; a missing assembly reference is a compiler error. Because the
core cannot reach `Actor` or `World`, its logic is necessarily pure — and pure logic tests in
milliseconds without building the engine. To feed new information into a decision you must add
it to the state struct, which keeps the boundary intact by construction.

Note this is now a *testability* guarantee, not a networking one.

### `OnTick` returns a decision instead of acting

Decisions are inert data, so they can be asserted in tests, logged, rendered as a debug overlay,
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

A future standalone mode editor could reasonably support hot-reload for authoring, since that
sits outside a real match. See the roadmap.

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

## Testing strategy

| Layer | How it is verified | Cost |
|---|---|---|
| `AutoCnC.Modes.Core` | 36 NUnit tests, no engine | ~30ms |
| Trait/YAML wiring | `./scripts/lint.ps1` — constructs every actor | ~1 min |
| Engine integration | Compile against pinned engine binaries | seconds |

The YAML lint is worth more than it sounds: it instantiates every actor in the mod, catching
unsatisfied `Requires<T>`, conditions consumed but never granted, and malformed trait fields —
none of which the C# compiler can see.

---

## Engine integration

The engine is a git submodule pinned to tag `playtest-20260222`, never edited. Our assemblies
reference `engine/bin/*.dll` as prebuilt binaries rather than by `ProjectReference`, following
the OpenRA Mod SDK convention, so bumping the engine tag never drags its internal project layout
into our build. Both mod assemblies output into `engine/bin/`, because OpenRA resolves the
assemblies named in `mod.yaml` relative to that directory.

To upgrade: bump the submodule, rebuild, run the lint, fix what breaks.

---

## Known gaps

- Assignment is chatbox-driven; there is no hotkey or panel UI.
- Assignments are not persisted between matches, so a loadout must be re-entered each game.
- Infantry/vehicle classification falls back to the `Infantry` target type string, which is
  Tiberian Dawn specific.
- No headless benchmark harness for mode-vs-mode evaluation.

Deliberately *not* gaps: needing a rebuild and restart to load edited mode code (see
[Modes are authored before the match](#modes-are-authored-before-the-match-not-during-it)).
