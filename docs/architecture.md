# Architecture

## The problem

We want unit behaviour to be **user-authored code** that is easy to write, easy to test, and
safe to run inside a lockstep multiplayer simulation. Those three goals pull against each other:

- *Easy to write* wants full access to the engine.
- *Easy to test* wants no engine at all.
- *Safe in lockstep* wants a restricted, deterministic subset of the engine.

The whole design is a response to that tension.

---

## Layers

```
┌──────────────────────────────────────────────────────────────┐
│  mods/autocnc/rules/*.yaml                                   │
│  Wiring only: which actors get a controller, which modes.    │
└──────────────────────────────────────────────────────────────┘
                              │ trait names bound by reflection
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Mod            (references OpenRA)                  │
│                                                              │
│   GroupManager            world trait, SYNCED group state    │
│   ProgrammableController  per-unit trait, runs the loop      │
│   ModeContext             curated deterministic sense/act    │
│   ModeRegistry            reflection-based mode discovery    │
│   Library/*Mode           thin sense→decide→act wrappers     │
└──────────────────────────────────────────────────────────────┘
                              │ plain integer structs
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Modes.Core     (references NOTHING)                 │
│                                                              │
│   DefensiveLogic, AttackBaseLogic   pure Decide() functions  │
│   ThreatSnapshot, UnitDecision      engine-free data         │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│  AutoCnC.Modes.Core.Tests   fast, no engine build required   │
└──────────────────────────────────────────────────────────────┘
```

The bottom two layers are the point. `AutoCnC.Modes.Core` has no OpenRA reference, which is
enforced by the project file rather than by discipline. Because it cannot reach the engine, its
logic is necessarily pure, and pure logic is testable in milliseconds.

---

## Why a separate assembly instead of a folder convention

A folder convention is a comment. A missing assembly reference is a compiler error.

If decision logic lived alongside engine code, the first time someone needed "just the actor's
armour type" they would reach for `Actor` and the testability guarantee would quietly evaporate.
Splitting the assembly makes that reach impossible: to get new information into a decision, you
must add it to the state struct, which keeps the pure/impure boundary intact by construction.

---

## Execution flow

### Per tick

```
World ticks actor
   └─ ProgrammableController.Tick
        ├─ disabled / dead?                     → return
        ├─ (WorldTick + ActorID) % Interval ≠ 0 → return   (staggered, deterministic)
        └─ activeMode.OnTick(self, ctx)
             ├─ ctx.Sense*    → engine → ThreatSnapshot[]
             ├─ Logic.Decide  → UnitDecision        (pure)
             └─ ctx.Apply     → QueueActivity / AttackTarget
```

Evaluations are staggered by `ActorID` so a 200-unit army spreads its cost across ticks instead
of spiking one. `ActorID` is synced, so the stagger is identical on every client.

### On mode change

```
Player hotkey
   └─ ProgrammableController.CreateSetModeOrder(selection, "AttackBaseMode")
        └─ world.IssueOrder                      → network
             └─ engine: Order.FromGroupedOrder   → one per actor
                  └─ ProgrammableController.ResolveOrder
                       ├─ previous.OnExit
                       ├─ CancelActivity
                       └─ next.OnEnter
```

Mode switches use the engine's built-in `GroupedActors` batching, so a whole control group
costs one network order rather than one per unit.

---

## Key decisions

### Modes queue activities; they do not issue orders

Verified against engine source. A per-unit trait's `Tick` runs on every client, so it is already
replicated and needs no network round-trip — the engine's own `AutoTarget` calls
`AttackBase.AttackTarget()` directly and constructs no `Order`. The "always use
`bot.QueueOrder()`" rule applies to bot modules specifically, because those run only on the host
(`if (IsBot && Game.IsHost)` in `Player.cs`).

Full reasoning and citations: [determinism.md](determinism.md).

### Group state is ours, not the engine's

OpenRA's `ControlGroups` trait is client-local: it reads `world.Selection` and filters on
`world.LocalPlayer`. Driving behaviour from it would desync immediately. `GroupManager` holds
synced membership instead; the engine's trait remains for UI only.

### Modes are per-unit instances, not shared singletons

A singleton would be marginally cheaper but would force all per-unit memory into an external
blackboard, making modes harder to write — the primary goal. Allocation happens only on mode
switch, which is rare. Instance fields are the natural place for things like retreat hysteresis.

### AutoTarget is neutered, not deleted

Deleting it breaks actors that add their own `AutoTargetPriority` (which declares
`Requires<AutoTargetInfo>`), and gating it behind a never-granted condition fails the engine's
condition linter. Forcing `HoldFire` is inert — `AutoTarget.Damaged()` returns early below
`ReturnFire`, `TickIdle()` returns early below `Defend` — and needs no conditions.

### Decisions are data

`Decide` returns a `UnitDecision` value rather than performing actions. That makes behaviour
assertable in tests, loggable into replays, and renderable as a debug overlay, with one single
place (`ModeContext.Apply`) translating intent into engine calls.

---

## Testing strategy

| Layer | How it is verified | Cost |
|---|---|---|
| `AutoCnC.Modes.Core` | NUnit unit tests, no engine | milliseconds |
| Trait/YAML wiring | `./scripts/lint.ps1` — constructs every actor in the mod | ~1 min |
| Engine integration | Compile against pinned engine binaries | seconds |

The YAML lint is more valuable than it sounds: it instantiates every actor, so it catches
unsatisfied `Requires<T>` constraints, conditions consumed but never granted, and malformed
trait fields — none of which the C# compiler can see.

---

## Engine integration

The engine is a git submodule pinned to tag `playtest-20260222`, never edited. Our assembly
references `engine/bin/*.dll` as prebuilt binaries rather than by `ProjectReference`, following
the OpenRA Mod SDK convention, so bumping the engine tag does not drag its internal project
layout into our build.

`AutoCnC.Mod.csproj` outputs directly into `engine/bin/`, because OpenRA resolves the assemblies
named in `mod.yaml` relative to that directory.

To upgrade the engine: bump the submodule, rebuild, run the YAML lint, and fix what breaks.

---

## Known gaps

Tracked against the roadmap in the README:

- Mode assignment is driven by chatbox commands (`ModeCommands`); there is no hotkey or panel UI
  yet.
- `GroupManager` does not implement `IGameSaveTraitData`, so groups and per-group modes are lost
  on save/load.
- Infantry/vehicle classification falls back to the `Infantry` target type string, which is
  Tiberian Dawn specific.
- No headless benchmark harness for mode-vs-mode evaluation.
