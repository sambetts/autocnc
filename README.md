# AutoC&C

**An RTS where you don't play the units — you program them.**

AutoC&C is a mod for [OpenRA](https://github.com/OpenRA/OpenRA) (the open-source Command & Conquer engine) that removes real-time micromanagement from the game. Instead of clicking units, you write **C# behavioural modes** — `DefensiveMode`, `AttackBaseMode`, `HarvesterEscortMode` — and assign them to unit classes, control groups (1–9), or your whole army.

Once the match starts, your code fights the battle. You direct strategy; your modes handle tactics.

```csharp
public sealed class DefensiveMode : IUnitMode
{
    public void OnTick(Actor self, ModeContext ctx)
    {
        var state = Sense(self, ctx);                  // read the world
        var decision = DefensiveLogic.Decide(state);   // pure, testable, no engine
        Apply(self, ctx, decision);                    // act on the world
    }
}
```

---

## Table of contents

- [Why this exists](#why-this-exists)
- [Architectural philosophy](#architectural-philosophy)
- [The one hard rule: determinism](#the-one-hard-rule-determinism)
- [How it integrates with OpenRA](#how-it-integrates-with-openra)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Writing a mode](#writing-a-mode)
- [Roadmap](#roadmap)
- [Licence](#licence)

---

## Why this exists

Competitive RTS play is gated behind actions-per-minute. The strategic layer — force composition, timing, map control — is often decided by who can click faster. AutoC&C keeps the strategy and deletes the clicking.

It is also, deliberately, a **programming game**. Your army is a codebase. A bad mode loses games in ways you can profile, unit-test, and fix.

---

## Architectural philosophy

### 1. Modes are plain C#, not a scripting language

OpenRA already embeds Lua for map scripting. We deliberately **do not** use it for unit behaviour:

| | Lua scripting | Compiled C# modes (our choice) |
|---|---|---|
| Type safety | Runtime errors | Compile-time |
| IDE support | Minimal | Full IntelliSense, refactoring, debugger |
| Performance | Interpreter + marshalling per unit per tick | Raw engine speed |
| Engine API access | Curated bindings only | Everything |

Modes compile into a normal .NET assembly that OpenRA loads via `Assemblies:` in `mod.yaml`. There is no interpreter and no marshalling layer.

### 2. Sense → Decide → Act

Every mode is structured in three phases, and the middle one is where the intelligence lives:

```
  Sense                    Decide                     Act
  ─────                    ──────                     ───
  Read the world     →     Pure function        →     Queue activities
  via ModeContext          on plain structs           via ModeContext
  (engine-coupled)         (ZERO engine deps)         (engine-coupled)
```

The `Decide` step is a **pure static function** over plain integer structs. It knows nothing about `Actor`, `World`, or OpenRA at all. That means it lives in a separate assembly with no engine reference, and it can be unit-tested in milliseconds:

```csharp
[Fact]
public void RetreatsWhenBadlyHurtAndOutnumbered()
{
    var state = new DefensiveState { HealthPercent = 22, ThreatCount = 3, ... };
    var decision = DefensiveLogic.Decide(state, DefensiveTuning.Default);
    Assert.Equal(DefensiveAction.Retreat, decision.Action);
}
```

No game, no map, no engine build. This is the single most important structural decision in the project: **behaviour is testable because the decisions are pure.**

### 3. The controller owns the loop; modes own the policy

`ProgrammableController` is a per-unit trait that replaces `AutoTarget`. It handles the boring, error-prone parts — tick throttling, mode lifecycle, damage notifications, group membership, activity hygiene — so a mode author only writes decision logic.

### 4. Groups are simulation state, not UI state

See [the determinism section](#the-one-hard-rule-determinism) — this is subtler than it looks and is the most common way a mod like this desyncs.

---

## The one hard rule: determinism

OpenRA is a **lockstep** engine. Every client runs the identical simulation and only *player input* travels over the network. If two clients ever compute different results, the game desyncs and dies.

This drives three non-negotiable rules, all verified against engine source:

### Rule 1 — Mode logic runs on every client, so it must not use orders

A per-unit trait's `Tick()` executes on *all* clients. It is therefore already replicated, and it should queue activities **directly**. This is exactly what the engine's own `AutoTarget` does:

```csharp
// engine/OpenRA.Mods.Common/Traits/AutoTarget.cs
void Attack(in Target target, bool allowMove)
{
    foreach (var ab in ActiveAttackBases)
        ab.AttackTarget(target, AttackSource.AutoTarget, false, allowMove);
}
```

No `Order` is constructed. Contrast with **bot modules** (`IBotTick`), which *must* use `bot.QueueOrder(...)` — because bot logic runs only on the host, so its decisions are genuine external input that has to be broadcast.

> **AutoC&C modes are traits, not bots.** Modes call `ctx.Attack(...)` / `ctx.MoveTo(...)`, which queue activities directly. Never issue an `Order` from inside a mode.

### Rule 2 — Only player intent becomes an Order

Assigning a mode to a group *is* player input, so it travels as an `Order`, resolved identically on every client:

```
Player presses hotkey
        ↓
  Order("SetUnitMode", subject: unit, GroupedActors: selection)
        ↓  (serialised over the network by the engine)
  Engine fans out via Order.FromGroupedOrder → Actor.ResolveOrder
        ↓
  ProgrammableController.ResolveOrder → mode switched on every client
```

This reuses the engine's built-in `GroupedActors` batching, so one order covers a whole control group.

### Rule 3 — OpenRA's built-in `ControlGroups` is client-local and must NOT drive behaviour

The engine's `ControlGroups` world trait is **presentation state**:

```csharp
// engine/OpenRA.Mods.Common/Traits/World/ControlGroups.cs
controlGroups[group].AddRange(world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer));
...
cg.RemoveAll(a => a.Disposed || a.Owner != world.LocalPlayer);
```

It reads `world.Selection` and filters on `world.LocalPlayer` — both of which differ per client. **If unit behaviour depended on it, every client would simulate a different battle.**

So AutoC&C ships its own `GroupManager`: a world trait holding *synced* group membership as real simulation state, mutated only through orders. The engine's `ControlGroups` is used purely as a UI convenience for *authoring* those orders.

### The determinism checklist for mode authors

- ✅ `ctx.Random` (wraps `World.SharedRandom`, seeded from the synced lobby seed and folded into the sync hash)
- ❌ `World.LocalRandom`, `System.Random`, `Guid.NewGuid()`
- ❌ `float` / `double` anywhere in decision logic — use integer `WDist`, `WPos`, `WAngle`
- ❌ `DateTime.Now`, wall-clock timing, real-world time
- ❌ `world.LocalPlayer`, `world.Selection`, `world.RenderPlayer`, anything on `ScreenMap`
- ⚠️ Never rely on enumeration order of spatial queries — break ties deterministically (`ctx` helpers order by `ActorID`)

`docs/determinism.md` covers this in full, including how to catch desyncs early with `[Sync]` fields.

---

## How it integrates with OpenRA

**The engine is a pinned git submodule. We do not fork it.**

```
engine/   →  github.com/OpenRA/OpenRA @ playtest-20260222   (submodule, untouched)
src/      →  our C# assembly, references engine DLLs
mods/     →  our YAML, loads that assembly
```

Upstream stays pristine, so engine upgrades are a submodule bump plus a compile, not a merge war. All our code lives in `src/` and `mods/` and touches the engine only through public APIs.

Loading is a single line in `mods/autocnc/mod.yaml`:

```yaml
Assemblies: OpenRA.Mods.Common.dll, OpenRA.Mods.Cnc.dll, AutoCnC.Mod.dll
```

OpenRA then discovers our traits by reflection — a YAML key `ProgrammableController:` binds to the C# class `ProgrammableControllerInfo`. Modes are discovered the same way, via `ObjectCreator.GetTypesImplementing<IUnitMode>()`, so **adding a mode requires no registration code** — drop in a class, name it in YAML.

AutoC&C derives its manifest from the shipped `cnc` (Tiberian Dawn) mod and layers its own rules on top, so OpenRA will offer to download the freeware C&C assets on first run.

### Engine version

Pinned to tag **`playtest-20260222`** (.NET 8, C# 12). The last *stable* tag (`release-20250330`) is on end-of-life .NET 6 / C# 9; the playtest tag is immutable and current, which is the better foundation for a new project.

---

## Repository layout

```
autocnc/
├── engine/                              # ← git submodule: OpenRA @ playtest-20260222 (never edited)
│
├── src/
│   ├── AutoCnC.Modes.Core/              # ★ ZERO OpenRA dependencies — pure decision logic
│   │   ├── DefensiveLogic.cs            #   pure Decide() functions
│   │   ├── AttackBaseLogic.cs
│   │   ├── Perception.cs                #   plain int structs: ThreatSnapshot, TargetCandidate…
│   │   └── Decisions.cs                 #   ModeDecision / action enums
│   │
│   ├── AutoCnC.Mod/                     # engine-facing assembly → bin/AutoCnC.Mod.dll
│   │   ├── Modes/
│   │   │   ├── IUnitMode.cs             #   the core mode interface
│   │   │   ├── ModeContext.cs           #   curated, deterministic sense+act API
│   │   │   └── ModeRegistry.cs          #   reflection-based mode discovery
│   │   ├── Traits/
│   │   │   ├── ProgrammableController.cs#   per-unit trait: ITick, INotifyCreated, INotifyDamage
│   │   │   └── GroupManager.cs          #   world trait: SYNCED control groups 1–9
│   │   └── Library/                     #   shipped modes
│   │       ├── DefensiveMode.cs
│   │       └── AttackBaseMode.cs
│   │
│   └── AutoCnC.Modes.Core.Tests/        # fast unit tests — no engine build required
│
├── mods/autocnc/                        # the mod itself
│   ├── mod.yaml                         #   manifest, derived from the cnc mod
│   ├── fluent/autocnc.ftl               #   mod title strings
│   └── rules/
│       ├── world.yaml                   #   GroupManager on the World actor
│       └── units.yaml                   #   ProgrammableController on units, AutoTarget neutered
│
├── docs/
│   ├── architecture.md                  # deep dive and rationale
│   ├── writing-modes.md                 # mode author's guide
│   └── determinism.md                   # desync rules, with engine source citations
│
├── scripts/                             # setup.ps1 / build.ps1 / launch.ps1 / lint.ps1
└── AutoCnC.sln
```

**Why `AutoCnC.Modes.Core` is a separate assembly:** it is physically incapable of referencing OpenRA, so the compiler enforces that decision logic stays pure and testable. It's a guardrail, not a convention.

---

## Getting started

### Prerequisites

- .NET 8 SDK or newer
- Git
- An OpenRA-supported OS (Windows / Linux / macOS)

### Setup

```powershell
git clone --recursive https://github.com/sambetts/autocnc.git
cd autocnc
./scripts/setup.ps1      # fetches submodule + engine dependencies
./scripts/build.ps1      # builds engine, then our mod assembly
./scripts/launch.ps1     # launches OpenRA with the autocnc mod
```

Already cloned without `--recursive`?

```powershell
git submodule update --init --depth 1
```

### Fast inner loop

Iterating on decision logic needs no engine at all:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests
```

Validating trait and YAML wiring runs OpenRA's own linter, which constructs every actor in the
mod and catches unsatisfied trait dependencies the C# compiler cannot see:

```powershell
./scripts/lint.ps1
```

---

## Writing a mode

1. Add a pure decision function in `AutoCnC.Modes.Core` and unit-test it.
2. Add a thin `IUnitMode` wrapper in `AutoCnC.Mod/Library/` that senses and acts.
3. Reference it by class name in YAML:

```yaml
ProgrammableController:
    DefaultMode: DefensiveMode
    AvailableModes: DefensiveMode, AttackBaseMode
    TickInterval: 8
```

No registration, no factory, no switch statement — `ModeRegistry` finds it by reflection.

See `docs/writing-modes.md` for the full guide.

---

## Roadmap

| Phase | Scope |
|---|---|
| **0 — Foundation** | Interfaces, controller, group manager, two reference modes ✅ |
| **1 — Playable** | Hotkey/UI for mode assignment, harvester escort, retreat-and-repair |
| **2 — Authoring** | In-repo mode template, headless benchmark harness, mode-vs-mode arena |
| **3 — Ecosystem** | Hot-reload of mode assemblies, replay-driven regression tests, mode sharing |

Phase 0 is verified: the mod assembly compiles against the pinned engine, 23 logic tests pass,
and `--check-yaml` reports 0 errors and 0 warnings across every actor in the mod.

Known gaps are listed at the end of [`docs/architecture.md`](docs/architecture.md).

---

## Licence

**GPL-3.0-or-later**, inherited from OpenRA.

This is not optional. AutoC&C links against and extends GPLv3 engine code, making it a derivative work. Any distributed build must ship its complete corresponding source under GPLv3. See [`LICENSE`](LICENSE).

OpenRA is a project by [the OpenRA developers and contributors](https://github.com/OpenRA/OpenRA/graphs/contributors). AutoC&C is an independent mod and is not affiliated with or endorsed by them, nor by Electronic Arts. Command & Conquer is a trademark of Electronic Arts Inc.; this project ships no EA assets.
