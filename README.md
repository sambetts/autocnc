# AutoC&C

**An RTS where you don't play the units — you program them.**

AutoC&C is a mod for [OpenRA](https://github.com/OpenRA/OpenRA) (the open-source Command &
Conquer engine) that removes real-time micromanagement. Instead of clicking units, you write
**C# behavioural modes** and assign them to your army — by unit type, by control group, or
wholesale.

```
/mode all DefensiveMode         everything holds ground
/mode type harv RunHomeMode     harvesters flee instead of dying
/mode group 1 AttackBaseMode    group 1 pushes the enemy base
```

Then the match plays itself. You direct strategy; your code handles tactics.

```csharp
public sealed class RunHomeMode : UnitMode
{
    public override UnitDecision OnTick(Actor self, ModeContext ctx)
    {
        if (!ctx.SenseThreats(new WDist(7 * 1024)).Any(t => t.CanHitUs))
            return UnitDecision.Continue;          // all clear, carry on harvesting

        var home = ctx.FindRefinery()?.Location ?? ctx.Anchor;
        return UnitDecision.MoveTo(home.X, home.Y, "enemy nearby, running home");
    }
}
```

<img width="514" height="400" alt="image" src="https://github.com/user-attachments/assets/b14b14a4-4673-4461-a746-3f66abd7f360" />


---

## Table of contents

- [Why this exists](#why-this-exists)
- [Writing your own modes](#writing-your-own-modes)
- [Assigning modes](#assigning-modes)
- [Architectural philosophy](#architectural-philosophy)
- [How it integrates with OpenRA](#how-it-integrates-with-openra)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Roadmap](#roadmap)
- [Licence and attribution](#licence-and-attribution)

---

## Why this exists

Competitive RTS play is gated behind actions-per-minute. The strategic layer — force
composition, timing, map control — is often decided by who clicks faster. AutoC&C keeps the
strategy and deletes the clicking.

It is also, deliberately, a **programming game**. Your army is a codebase. A bad mode loses
games in ways you can profile, unit-test and fix.

---

## Writing your own modes

Modes live in [`player-modes/`](player-modes/), a normal C# project. Open it in your IDE and you
get IntelliSense, refactoring and a debugger — no scripting language, no interpreter.

**You write your modes before the match, then commit to them.** The game is the test of what you
wrote, not a live coding session:

1. Copy a template and rename the class. **The class name is the mode name in-game.**
2. `dotnet test src/AutoCnC.Modes.Core.Tests` — check your logic in milliseconds, no game needed
3. `./scripts/build.ps1`
4. `./scripts/launch.ps1`, then assign with `/mode ...` and watch it play out

Switching *between* your modes is fully live — `/mode group 1 AttackBaseMode` takes effect
within a tick, mid-battle. It's only loading newly *edited* code that needs a restart.

There is no registration step. Discovery uses OpenRA's own `ObjectCreator` reflection, the same
mechanism that binds YAML trait names to engine traits.

Three templates ship to start from:

| Template | Shows |
|---|---|
| `RunHomeMode` | The simplest useful mode — flee to a refinery when threatened |
| `ScoutMode` | Per-unit state, reacting to damage between evaluations |
| `HarvesterEscortMode` | Tracking another actor and defending it |

Plus three reference modes built into the mod:

- **`BuildBaseMode`** — deploys your MCV and grows the base from a build plan. MCVs and
  construction yards run this by default, so a skirmish actually starts.
- **`DefensiveMode`** — holds ground, won't be baited past its leash, retreats to repair.
- **`AttackBaseMode`** — pushes a base, only fires on what is already in range.

Combat modes no-op on unarmed units, so `/mode all DefensiveMode` won't stop your harvesters
mining or your construction yard building.

See [`docs/getting-started.md`](docs/getting-started.md) for the full walkthrough, or
[`docs/writing-modes.md`](docs/writing-modes.md) for the API reference.

### Your code cannot desync the game

Mode code runs **outside the lockstep simulation**, on your machine only, and its output is
`Order`s — the same channel your mouse clicks use.

That means `float`, LINQ, `System.Random` and `DateTime` are all fine in your modes, your
opponent never needs your code, and you never execute theirs. A mode that throws is dropped for
that unit with the error printed to chat; the match continues.

The cost is ~120ms of order latency on decisions — the same latency human input already has.
[`docs/determinism.md`](docs/determinism.md) explains the whole thing.

---

## Assigning modes

Press `Enter` in-game for the chatbox:

| Command | Effect |
|---|---|
| `/modes` | List loaded modes |
| `/mode ScoutMode` | Current selection |
| `/mode all DefensiveMode` | Every unit |
| `/mode type harv RunHomeMode` | Every harvester |
| `/mode group 1 AttackBaseMode` | Control group 1 |
| `/mode clear` | Drop per-unit overrides |
| `/assignments` | What's currently assigned |
| `/whatmode` | What the selection is running |
| `/modelog` | Toggle decision logging to `debug.log` — your main debugging tool |

Precedence is **most specific wins**: selection > group > unit type > actor default > all. So
`/mode all DefensiveMode` followed by `/mode type harv RunHomeMode` does what you'd expect, and
neither clobbers the other. The actor default is declared in YAML (it's what makes an MCV run
`BuildBaseMode`), so `/mode all` won't accidentally stop your base building itself.

---

## Architectural philosophy

### 1. Compiled C#, not a scripting language

OpenRA embeds Lua for map scripting. We deliberately don't use it for unit behaviour:

| | Lua | Compiled C# (our choice) |
|---|---|---|
| Type safety | Runtime errors | Compile-time |
| IDE support | Minimal | Full IntelliSense, refactoring, debugger |
| Performance | Interpreter + marshalling | Native .NET |
| Engine API access | Curated bindings | Everything |

### 2. Sense → decide → act

```
  Sense                    Decide                     Act
  ─────                    ──────                     ───
  Read the world     →     Pure function        →     Return a UnitDecision;
  via ModeContext          on plain structs           the executor emits an Order
  (engine-coupled)         (ZERO engine deps)
```

The decide step is a pure function over integer structs, living in an assembly that has **no
OpenRA reference at all** — enforced by the project file, not by convention. So combat behaviour
is testable in milliseconds:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests   # 36 tests, ~30ms, no engine build
```

A folder convention is a comment. A missing assembly reference is a compiler error.

### 3. Decisions are data

`OnTick` returns a `UnitDecision` rather than acting. That makes behaviour assertable in tests,
loggable, renderable as a debug overlay — and comparable between ticks, so the executor only
sends an order when your intent actually changes. Returning the same decision every tick is free.

---

## How it integrates with OpenRA

**The engine is a pinned git submodule. We do not fork it.**

```
engine/        →  github.com/OpenRA/OpenRA @ playtest-20260222   (submodule, untouched)
src/           →  our assemblies
player-modes/  →  your assembly
mods/          →  YAML wiring
```

All three assemblies build into `engine/bin/` and are named in `mods/autocnc/mod.yaml`:

```yaml
Assemblies: OpenRA.Mods.Common.dll, OpenRA.Mods.Cnc.dll, AutoCnC.Mod.dll, PlayerModes.dll
```

AutoC&C derives its manifest from the shipped `cnc` (Tiberian Dawn) mod, so OpenRA offers to
download the freeware C&C assets on first run.

Pinned to tag **`playtest-20260222`** (.NET 8, C# 12). The last *stable* tag is on end-of-life
.NET 6 / C# 9.

---

## Repository layout

```
autocnc/
├── engine/                              # ← git submodule: OpenRA (never edited)
│
├── player-modes/                        # ★ YOUR MODES — a normal C# project
│   ├── PlayerModes.csproj               #   every .cs here is compiled automatically
│   ├── RunHomeMode.cs                   #   templates to copy
│   ├── ScoutMode.cs
│   └── HarvesterEscortMode.cs
│
├── src/
│   ├── AutoCnC.Modes.Core/              # ★ ZERO OpenRA deps — pure, testable logic
│   │   ├── DefensiveLogic.cs            #   pure Decide() functions
│   │   ├── AttackBaseLogic.cs
│   │   ├── ModeAssignments.cs           #   assignment precedence resolver
│   │   ├── Perception.cs                #   plain int structs
│   │   └── Decisions.cs                 #   UnitDecision / UnitAction
│   │
│   ├── AutoCnC.Mod/                     # engine-facing assembly
│   │   ├── Modes/
│   │   │   ├── IUnitMode.cs             #   the mode interface
│   │   │   ├── ModeContext.cs           #   sensing + order construction
│   │   │   └── ModeRegistry.cs          #   reflection-based discovery
│   │   ├── Traits/
│   │   │   ├── ModeExecutor.cs          #   CLIENT-LOCAL: runs modes, emits Orders
│   │   │   ├── ProgrammableController.cs#   per-unit marker + mode state
│   │   │   └── ModeCommands.cs          #   chatbox assignment UI
│   │   └── Library/                     #   shipped reference modes
│   │       ├── DefensiveMode.cs
│   │       └── AttackBaseMode.cs
│   │
│   └── AutoCnC.Modes.Core.Tests/        # fast tests — no engine build required
│
├── mods/autocnc/                        # mod manifest and rules
├── docs/                                # architecture / writing-modes / determinism
├── scripts/                             # setup / build / launch / lint
└── AutoCnC.sln
```

---

## Getting started

**→ [`docs/getting-started.md`](docs/getting-started.md) is the full walkthrough**: install,
write your first mode, assign it, iterate. Start there.

The short version:

### Prerequisites

- .NET 8 SDK or newer (the **SDK**, not just the runtime — you're compiling code)
- Git
- An OpenRA-supported OS (Windows / Linux / macOS)

### Setup

```powershell
git clone --recursive https://github.com/sambetts/autocnc.git
cd autocnc
./scripts/setup.ps1      # fetch the engine submodule
./scripts/build.ps1      # build engine, mod and your modes
./scripts/launch.ps1     # play
```

Cloned without `--recursive`? `git submodule update --init --depth 1`

### Loops

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests   # logic — ~20ms, no engine
./scripts/lint.ps1                         # wiring — constructs every actor in the mod
./scripts/build.ps1 -SkipEngine            # recompile just your code
./scripts/launch.ps1                       # play-test
```

> **Close the game before rebuilding.** The mod assemblies load from `engine/bin`, and a running
> client holds a lock on them.

---

## Roadmap

| Phase | Scope |
|---|---|
| **0 — Foundation** | Interfaces, executor, reference modes ✅ |
| **1 — Authoring** | Player mode project, assignment scopes, templates ✅ |
| **2 — Loadouts** | Save/load named mode loadouts, pre-match assignment screen, debug overlay |
| **3 — Ecosystem** | Standalone mode editor with hot-reload, headless mode-vs-mode arena, mode sharing |

Verified today: all assemblies compile against the pinned engine, 36 logic tests pass in ~30ms,
and `--check-yaml` reports 0 errors and 0 warnings across every actor in the mod.

Known gaps are listed at the end of [`docs/architecture.md`](docs/architecture.md).

---

## Licence and attribution

**GPL-3.0-or-later**, inherited from OpenRA. See [`LICENSE`](LICENSE) for the full text and
[`NOTICE.md`](NOTICE.md) for full attributions.

This is not optional: AutoC&C links against and extends GPLv3 engine code, making it a
derivative work. Any distributed build must ship its complete corresponding source under GPLv3.

### OpenRA

Built on [OpenRA](https://github.com/OpenRA/OpenRA) — copyright (c) OpenRA Developers and
Contributors, licensed [GPL-3.0-or-later](https://github.com/OpenRA/OpenRA/blob/bleed/COPYING).
See their [AUTHORS](https://github.com/OpenRA/OpenRA/blob/bleed/AUTHORS) for the people who made
it possible.

The engine is a pinned submodule, unmodified and not copied into this repository.
`mods/autocnc/mod.yaml` is derived from OpenRA's `mods/cnc/mod.yaml` and carries an attribution
header.

AutoC&C is **not affiliated with or endorsed by the OpenRA project**. Please don't report
AutoC&C issues to them.

### Command & Conquer

**Command & Conquer**, **C&C**, **Tiberian Dawn**, **GDI** and **Nod** are trademarks of
[Electronic Arts Inc.](https://www.ea.com) Their use here is descriptive, to identify the game
this project mods. AutoC&C is an unofficial fan project with no affiliation with, sponsorship
by, or endorsement from EA.

**No game assets are distributed in this repository.** On first launch OpenRA's own content
installer offers to download them from a mirror of the 2007 Command & Conquer Gold freeware
release published by EA, or to copy them from an original disc or digital install you own.
Assets land in your local OpenRA support directory and are never committed here.
