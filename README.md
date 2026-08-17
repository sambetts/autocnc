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
- [Doctrines](#doctrines)
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

## Doctrines

The platform contains **no strategy**. Everything about how an army fights lives in a **battle
module** you author: the base build plan, the unit production plan, the modes, and which units
run them. Load one and it plays; load none and nothing deploys, builds or shoots.

```csharp
public sealed class MyDoctrine : IDoctrine
{
    public string Name => "Rush";
    public string Description => "Fast barracks, early pressure.";

    public void Configure(IDoctrineBuilder b)
    {
        b.Build("powr", "nuke").Until(2);        // base plan
        b.Build("proc").Until(2);
        b.Build("pyle", "hand").Until(1);

        b.Train("Infantry", "e1").Until(10);     // production plan
        b.Train("Infantry", "e1", "e2").Forever();

        b.Assign<MyDefensiveMode>().ToAll();     // behaviour
        b.Assign<BuildBaseMode>().ToUnitType("mcv", "fact");
        b.Assign<MyRushMode>().ToGroup(1);
    }
}
```

Candidates are alternatives for one role — `powr` or `nuke` both mean "a power plant" — so a
plan works as either faction without checking.

```powershell
cp -r doctrines/Reference modules/MyModule   # start from the reference module
cd modules/MyModule && dotnet build        # builds into engine/bin/doctrines
```

Then in game: `/modules`, `/module MyModule`.

Modules build against AutoC&C **binaries**, not projects, so a module can live in **its own
repository**: `dotnet build /p:AutoCnCPath=C:\games\autocnc`.

**AutoC&C ships one module, `Reference`** — a balanced opening that defends its base and pushes
with control group 1. It is both the worked example and the first opponent to beat.

See [`docs/writing-doctrines.md`](docs/writing-doctrines.md).

### Your code cannot desync a match

Modes run **outside the lockstep simulation**, on your machine only, and their output is
`Order`s — the same channel your mouse clicks use.

That means `float`, LINQ, `System.Random` and `DateTime` are all fine in your module, your
opponent never needs your code, and you never execute theirs. A module that throws is dropped
for that unit with the error printed to chat; the match continues.

The cost is ~120ms of order latency — the same latency human input already has.
[`docs/determinism.md`](docs/determinism.md) explains the whole thing.

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
Assemblies: OpenRA.Mods.Common.dll, OpenRA.Mods.Cnc.dll, AutoCnC.Core.dll, AutoCnC.Sdk.dll, AutoCnC.Platform.dll
```

AutoC&C derives its manifest from the shipped `cnc` (Tiberian Dawn) mod, so OpenRA offers to
download the freeware C&C assets on first run.

Pinned to tag **`playtest-20260222`** (.NET 8, C# 12). The last *stable* tag is on end-of-life
.NET 6 / C# 9.

---

## Repository layout

```
autocnc/
├── engine/                          # ← git submodule: OpenRA (never edited)
│
├── src/                             # THE PLATFORM — infrastructure, zero strategy
│   ├── AutoCnC.Core/                #   engine-free: decisions, plans, planners
│   ├── AutoCnC.Sdk/                 #   what modules code against: IDoctrine,
│   │                                #   IUnitMode, ModeContext
│   ├── AutoCnC.Platform/            #   the host: OpenRA traits, module loader, commands
│   └── AutoCnC.Core.Tests/
│
├── modules/                         # doctrines — all the strategy lives here
│   └── Reference/                   #   ★ own solution + NuGet refs; copy to start your own
│       ├── ReferenceDoctrine.cs #     plans + assignments
│       ├── Modes/                   #     BuildBase, TrainUnits, Defensive, AttackBase…
│       ├── Logic/                   #     pure decision functions
│       └── Tests/                   #     fast, no game needed
│
├── mods/autocnc/                    # mod manifest and rules
├── docs/                            # getting-started / writing-modules / architecture
├── packages/                        # local NuGet feed doctrines build against
├── scripts/                         # setup / build / launch / lint / run-doctrine
└── AutoCnC.sln                      # the platform only
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
| **1 — Authoring** | Assignment scopes, templates, base building ✅ |
| **2 — Modules** | Platform/module split, doctrine SDK, unit production ✅ |
| **3 — Ecosystem** | Module vs module arena, replay regression tests, module sharing |
Verified: platform and module build independently, 49 tests pass with no engine, `--check-yaml`
reports 0 errors, and the reference module plays a full game — deploy, build, train, fight.

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
