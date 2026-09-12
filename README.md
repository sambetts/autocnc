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
- [Battle bots](#battle-bots)
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

## Battle bots

The platform contains **no strategy**. Everything about how an army fights lives in a **battle
bot** you author. Load one and it plays; load none and nothing deploys, builds or shoots.

A bot owns several **doctrines** — complete, self-contained ways of fighting, each with its own
build plan, production plan and mode assignments — and decides which one the match needs:

```csharp
public sealed class MyBot : BattleBot
{
    public override string Name => "Adaptive";
    public override string Description => "Opens economic, turtles when hit, pushes when ahead.";

    public override void Configure(IBattleBotBuilder b)
    {
        b.Open<OpeningDoctrine>();     // the doctrine the match starts on
        b.Use<DefenceDoctrine>();
        b.Use<AttackDoctrine>();
    }

    public override DoctrineDecision Reassess(in BattleState s)
    {
        if (s.BuildingsLost > 0)
            return DoctrineDecision.SwitchTo("Defence", "losing buildings");

        if (s.ArmyValue > 6000 && s.EnemyBaseFound)
            return DoctrineDecision.SwitchTo("Attack", "army is worth spending");

        return DoctrineDecision.Continue;
    }
}
```

**Why the split.** Plans do not survive contact. You can grow one doctrine a special case at a
time until nobody can say what it does, or you can keep an attack doctrine confident, keep a
defence doctrine paranoid, and let the bot change its mind. Each doctrine is then a plan you can
read in one sitting.

`BattleState` is only what your side can tell. Your own economy and army are exact; **the enemy
half is only what you can currently see**, filtered by the same visibility rule
`ctx.SenseThreats` uses. A bot cannot attack on the strength of an army value no unit of yours
has laid eyes on.

Doctrines know their siblings, so a unit that has learned something can end its own doctrine:

```csharp
ctx.SwitchDoctrine("Opening", "scout found their base");
```

A doctrine itself declares plans and behaviour:

```csharp
public sealed class AttackDoctrine : IDoctrine
{
    public string Name => "Attack";
    public string Description => "Push: tech, more production, everything goes to their base.";

    public void Configure(IDoctrineBuilder b)
    {
        b.Build("powr", "nuke").Until(2);        // base plan
        b.Build("weap", "afld").Until(2);

        b.Train("Vehicle", "mtnk", "ltnk").Forever();   // production plan

        b.Assign<AttackBaseMode>().ToAll();      // behaviour
        b.Assign<BuildBaseMode>().ToUnitType("mcv", "fact");
    }
}
```

Candidates are alternatives for one role — `powr` or `nuke` both mean "a power plant" — so a
plan works as either faction without checking.

```powershell
./scripts/new-bot.ps1 -Name MyBot # create a standalone solution, project, mode and tests
./scripts/launcher.ps1            # select MyBot, deploy it, and fight
```

Bots build against AutoC&C **binaries**, not projects, so one can live in **its own repository**
— point the launcher at its `.csproj` (or at a `.dll` somebody sent you) wherever it happens to
be. An assembly with doctrines but no bot is played as one bot per doctrine, so a lone doctrine
is still a thing you can put in a battle.

**AutoC&C ships one bot, `Reference`** — it opens economic, scouts when it can afford to, turtles
when hit and pushes when ahead. It is both the worked example and the first opponent to beat.

See [`docs/writing-bots.md`](docs/writing-bots.md).

### Your code cannot desync a match

Modes run **outside the lockstep simulation**, on your machine only, and their output is
`Order`s — the same channel your mouse clicks use.

That means `float`, LINQ, `System.Random` and `DateTime` are all fine in your bot, your
opponent never needs your code, and you never execute theirs. A mode that throws is dropped
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
| `/speed` | The speed you asked for, and the one your machine is managing. `/speed 0.5` while watching a replay |

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
dotnet test src/AutoCnC.Core.Tests         # milliseconds, no engine build
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
│   ├── AutoCnC.Core/                #   engine-free: decisions, plans, planners, BattleState
│   ├── AutoCnC.Sdk/                 #   what bots code against: IBattleBot, IDoctrine,
│   │                                #   IUnitMode, ModeContext
│   ├── AutoCnC.Platform/            #   the host: OpenRA traits, bot loader, commands
│   └── AutoCnC.Core.Tests/
│
├── bots/                            # battle bots — all the strategy lives here
│   └── Reference/                   #   ★ own solution + NuGet refs; copy to start your own
│       ├── ReferenceBot.cs          #     which doctrine, and when
│       ├── Doctrines/               #     Opening, Scout, Defence, Attack
│       ├── Modes/                   #     BuildBase, TrainUnits, Defensive, AttackBase…
│       ├── Logic/                   #     pure decision functions
│       └── Tests/                   #     fast, no game needed
│
├── mods/autocnc/                    # mod manifest and rules
├── docs/                            # getting-started / writing-bots / architecture
├── packages/                        # local NuGet feed bots build against
├── tools/AutoCnC.Launcher/          # the battle launcher — a window over scripts/
├── scripts/                         # setup / build / launch / lint / launcher / run-bot
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

**ARM64:** Linux ARM64 and Apple Silicon builds select native ARM64 engine libraries
automatically. Windows ARM64 uses an **x64 .NET 8 runtime** for OpenRA under Windows emulation;
the SDK and graphical launcher can stay ARM64. See [ARM64 setup](docs/getting-started.md#arm64).

### Setup

```powershell
git clone --recursive https://github.com/sambetts/autocnc.git
cd autocnc
./scripts/setup.ps1      # fetch the engine submodule
./scripts/build.ps1      # build engine, mod and the reference bot
./scripts/launcher.ps1   # pick your code, pick an opponent, play
```

Cloned without `--recursive`? `git submodule update --init --depth 1`

### The launcher

`./scripts/launcher.ps1` opens a window over the whole authoring loop. **New bot** creates a
standalone C# solution with the AutoC&C dependencies, a starter doctrine and mode, and tests.
**Open code** hands that solution to your configured editor, **Deploy bot** builds and installs it,
and **Run fight** boots straight into the selected match with the bot loaded — no menus, lobby, or
`/bot` command. The **Execution** selector chooses **Headless** (the default, CPU-speed training
with no game window) or **Rendered** (the existing battle window, up to 40x, for watching and
debugging).

Launching a battle changes the UI, the way an IDE changes when it starts debugging: the launcher
window is only for setting a fight up, so when one starts two more windows open beside it and stay
up afterwards for as long as you want to read them.

- **Results** graphs every player's units, army value, buildings, base value and kills as the
  battle runs, so you can see the moment a bot lost rather than just the fact that it did.
  **History & trends** plots those final KPIs across every iteration and colors battle duration by
  win or loss, so faster wins and slower losses are visible. Select any battle to inspect it.
- **Output** carries the **battle log**: who is playing, and then every event your side could
  actually react to — an enemy coming into view, a hit taken, a unit lost, a kill — each one
  naming the players on both ends of it. It is filtered by the same visibility rule
  `ctx.SenseThreats` uses, so it is a record of what your code *knew*, not of what was true. The
  graph tells you when it went wrong; the log tells you what your bot had to work with at the
  time, which is the half you can do something about.

Every fight is preserved under `%LOCALAPPDATA%\AutoCnC\TrainingRuns`: its setup, source revision,
telemetry, battle log, structured decision trace, result, replay, measured headless throughput,
agent guide, and a generated JSON snapshot of the actors and weapons from OpenRA's resolved
ruleset. The decision trace connects the other records by writing each bot assessment and each mode
decision that actually became an order.

Improvement is optional. Edit normally with **Open code**, or press **Analyze & improve** after a
fight. In manual mode, the selected battle also accepts your assessment of why it won or lost; that
assessment is stored with the run and added to its next agent prompt. The launcher snapshots the bot
source, invokes a configurable local coding-agent command (GitHub Copilot CLI by default), gives it
only the bot and that run's evidence, then independently tests, builds, and deploys the result.
**Agent workspace** opens a dedicated window with live colored terminal progress plus the exact
prompt, game guide, resolved unit/weapon stats, fight manifest, and changed files;
**Restore previous iteration** puts the exact pre-agent source back.

Check **Continuous improvement** before starting to repeat Fight -> analyze and improve -> Fight
until **Stop**. Continuous mode cannot pause for a player assessment; it automatically adopts a
valid next-round prompt from the agent and stops on any failed battle, agent run, test, or build.

Resolved rules and fight JSON use lazy, collapsible trees rather than raw text. At the end of an
improvement the agent drafts an entirely new prompt template in **Next prompt***. In manual mode the
player can edit and approve it; continuous mode accepts a valid template automatically. That
template replaces the previous one next round, with fresh paths, fight, result, and evidence
inserted through required placeholders. Long-running launcher work also shows an indeterminate
progress bar on its Windows taskbar icon.

Agent and independent-verification failures are separate states. A failed verification exposes
**Retry verification**, which cleans generated output before retesting, plus **Fix failed
improvement** for asking the agent to repair genuine source failures from the archived transcript.

It runs the scripts below and shows you their output, so it never does anything you could not
have typed yourself. Windows only; elsewhere use the command it wraps, which takes the same
options:

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Hard -ExecutionMode Headless -Test
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Hard -ExecutionMode Rendered -GameSpeed maximum
```

Difficulty is a bot personality plus a handicap, because C&C's bots are personalities rather
than tiers. The levels live in [`scripts/difficulties.json`](scripts/difficulties.json), which
the launcher and the script both read. Rendered game speed runs from `slowest` to `maximum` —
0.5x to 40x. Headless uses the same `maximum` world configuration but removes wall-clock pacing
and rendered frames, so logic runs as fast as the CPU permits. One measured Reference-vs-Watson
match reached **68.5x** (1,713 ticks/s); `evidence/performance.json` records the result for each
machine and match. Every battle is recorded, so **Watch replay** takes you back over the bit that
mattered at a speed you can see.

### Loops

```powershell
dotnet test src/AutoCnC.Core.Tests   # logic — ~20ms, no engine
./scripts/lint.ps1                   # wiring — constructs every actor in the mod
./scripts/build.ps1 -SkipEngine      # recompile just your code
./scripts/launcher.ps1               # play-test
```

> **Close the game before rebuilding.** The mod assemblies load from `engine/bin`, and a running
> client holds a lock on them.

---

## Roadmap

| Phase | Scope |
|---|---|
| **0 — Foundation** | Interfaces, executor, reference modes ✅ |
| **1 — Authoring** | Assignment scopes, templates, base building ✅ |
| **2 — Doctrines** | Platform/strategy split, doctrine SDK, unit production ✅ |
| **3 — Battle bots** | Several doctrines per bot, switching on what the side can see ✅ |
| **4 — Training** | Bot scaffolding, durable fight evidence, optional reversible coding-agent improvements ✅ |
| **5 — Ecosystem** | Bot vs bot arena, replay regression tests, bot sharing |

Verified: platform and bot build independently, the repository test suites pass, `--check-yaml`
reports 0 errors, and the reference bot plays a full game — deploying, building, scouting,
pushing, and turtling when its base is hit.

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
