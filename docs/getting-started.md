# Getting started

Start to finish: install, write your first mode, and watch it fight. About 15 minutes, most of
it downloading.

If you just want the API reference, skip to [writing-bots.md](writing-bots.md).

---

## 1. Prerequisites

| | |
|---|---|
| **.NET SDK 8.0 or newer** | [download](https://dotnet.microsoft.com/download) — the **SDK**, not just the runtime, since you're compiling code |
| **Git** | with submodule support (any modern version) |
| **PowerShell** | Windows PowerShell or PowerShell 7 on Windows; PowerShell 7 on Linux/macOS |
| **An IDE** | Visual Studio, Rider, or VS Code with the C# Dev Kit |
| **OS** | Windows, Linux or macOS |

Check the SDK:

```powershell
dotnet --list-sdks
```

You need a line starting `8.` or higher.

### ARM64

Keep your native ARM64 .NET SDK. The build scripts select OpenRA's native libraries to match
the .NET host that will run the engine, rather than assuming x64. Linux ARM64 and Apple Silicon
use their native `linux-arm64` and `osx-arm64` libraries.

On **Windows ARM64**, the pinned OpenRA dependencies do not include ARM64 Windows DLLs.
Install the **Windows x64 .NET 8 Runtime** alongside ARM64 .NET
([download](https://dotnet.microsoft.com/download/dotnet/8.0), choose **.NET Runtime**, **Windows**,
**x64**). An x64 SDK is not needed. Games, headless battles, replays, YAML linting and rule
exports then use that runtime automatically under Windows' x64 emulation. Compilation, bot
tests and the graphical launcher continue using your normal .NET installation.

The scripts find an x64 runtime through `DOTNET_ROOT_X64`, the registered .NET installation,
or the standard `C:\Program Files\dotnet\x64` directory. For a custom installation, set
`DOTNET_ROOT_X64` to its directory; do not replace the ARM64 SDK on `PATH`. If no compatible
runtime is available, the script stops with installation guidance instead of trying to load
x64 libraries into an ARM64 process.

Use the normal build and launch commands below; no architecture flags are required. The
graphical launcher is Windows-only, so Linux/macOS authors use `run-bot.ps1`.

---

## 2. Clone and build

```powershell
git clone --recursive https://github.com/sambetts/autocnc.git
cd autocnc
./scripts/setup.ps1
./scripts/build.ps1
```

`--recursive` matters: it fetches the pinned OpenRA engine. If you forgot it, `setup.ps1` will
fetch the submodule for you.

The first build compiles the whole engine and takes a couple of minutes. Later builds skip it
(`./scripts/build.ps1 -SkipEngine`) and take seconds.

Expect to finish with:

```
Build complete. Next: ./scripts/launch.ps1
```

### If it fails

| Message | Cause |
|---|---|
| `Engine submodule not found` | Run `git submodule update --init --depth 1` |
| `OpenRA engine not built` | Run `./scripts/build.ps1` without `-SkipEngine` |
| `OpenRA on Windows ARM64 requires the .NET 8 x64 runtime` | Install the Windows x64 .NET 8 Runtime alongside ARM64 .NET; see [ARM64](#arm64). |
| `The file is locked by: ".NET Host"` | The game is running. Close it and rebuild. |

---

## 3. First launch

```powershell
./scripts/launcher.ps1
```

That opens **Battle Command**, the battle launcher (Windows). In **Bot bay**, press **New bot…** to create a standalone C#
solution with a starter doctrine, mode, pure logic, and tests. **Open code** opens that solution
for normal manual editing, **Build & deploy** builds and installs it, and **Deploy & fight** starts the game,
seats the AI, and loads your bot before the first tick.

Use **Proving ground** for the battle settings, **AI training** for agent improvement and
continuous training, and **Systems** for the repository, platform rebuild, logs and replays.
The command deck keeps deployment, stop and history available while switching workstations
(keyboard shortcuts: **Alt+1** through **Alt+4**). The bot dossier shows the selected project,
its readiness and its last battle result. See [the design and its C&C95 references](launcher-design.md).

When a battle starts, two more windows
open beside it — the **results** graphs and the **output** log — the way a debugger's windows
appear when you run rather than sitting empty while you edit. They stay up when the game exits,
because everything you want to ask about a match you ask afterwards.

On Linux and macOS, or if you would rather stay in the terminal, the launcher is a front end for
one command that takes exactly the same options:

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Normal
```

Prefer to start at the menu instead?

```powershell
./scripts/launch.ps1
```

AutoC&C is built on Tiberian Dawn, so on first run OpenRA offers to download the **freeware**
C&C assets (~100MB, legally redistributed by EA). Accept and wait.

Your MCV deploys itself and the base starts building — that's `BuildBaseMode`, which MCVs and
construction yards run by default. Combat units run `DefensiveMode`, so they hold ground rather
than chase.

Press `Enter` to open the chatbox and try:

```
/modes
```

You should see the shipped modes plus the templates:

```
Modes from Reference: AttackBaseMode, BuildBaseMode, DefensiveMode, HarvesterEscortMode,
RunHomeMode, ScoutMode, TrainUnitsMode
```

If that list is missing your modes, jump to [Troubleshooting](#troubleshooting).

### Who you are fighting

The C&C bots are personalities rather than difficulty tiers, so a difficulty here is a
personality plus a **handicap** — OpenRA's 0-95% penalty on firepower, durability and build
speed. The levels live in [`scripts/difficulties.json`](../scripts/difficulties.json), which the
launcher and the script both read, so adding one there adds it to both:

| Level | Opponent | Handicap |
|---|---|---|
| Rookie | Watson | 70% on the AI |
| Easy | Watson | 45% on the AI |
| Normal | Cabal | 20% on the AI |
| Hard | HAL 9001 | none |
| Brutal | HAL 9001 | 25% **on you** — the AI cannot be made stronger, so this weakens you |

Override any of it when you need something specific:

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Bot hal9001 -BotHandicap 40 -Opponents 2
```

### How fast it runs

`-GameSpeed` (the **Speed** dropdown in the launcher) sets the tick rate for the match. It is a
real iteration tool rather than a comfort setting, and it changes nothing but the clock: the
simulation at 20x is the same simulation, tick for tick, as the one at 1x. A bot that wins at
`maximum` wins at `default`.

| Speed | Per tick | Multiplier | |
|---|---|---|---|
| `slowest` … `faster` | 80ms … 30ms | 0.5x … 1.3x | watch what a mode is actually deciding |
| `fastest` | 20ms | 2x | the fastest the stock C&C mods offer |
| `turbo` | 8ms | 5x | |
| `ludicrous` | 4ms | 10x | |
| `plaid` | 2ms | 20x | |
| `maximum` | 1ms | 40x | the rendered scheduler's ceiling: timesteps are whole milliseconds |

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -GameSpeed maximum
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Opponents 0 -GameSpeed slowest   # no enemy, watch the build
```

### Headless training or a rendered fight

The launcher's **Execution** selector separates training throughput from watching a match:

| Mode | Use it for | Pace |
|---|---|---|
| **Headless** (default) | continuous improvement and outcome/evidence runs | same `maximum` battle, logic ticks at CPU speed |
| **Rendered** | watching and debugging the bot | normal game window, up to `maximum` (40x) |

Headless is a simulation client, not `OpenRA.Server`: it still loads the AutoC&C world, runs the
local battle bot and OpenRA opponent bots, applies factions and handicaps, evaluates doctrines and
modes through visibility-filtered `ModeContext`, and sends their ordinary lockstep orders through
the loopback server. It skips only wall-clock scheduling, sound output, and drawing.

```powershell
# Fast training match. Headless always uses the same world configuration as rendered maximum.
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Hard `
    -ExecutionMode Headless -PerformanceReport C:\tmp\performance.json

# Watch the equivalent 40x battle.
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Hard `
    -ExecutionMode Rendered -GameSpeed maximum
```

Throughput depends on the map, armies, bot code, and CPU. A Reference-vs-Watson validation match on
`tiberium-rift.oramap` completed at **1,713 ticks/s, or 68.5x** nominal game speed. Launcher runs
write their own measured `evidence/performance.json` and copy the values into `manifest.json`; do
not treat that one-machine number as a guarantee.

Limitations: headless matches have no interactive chat or viewport, support only launched local
battles, and load one match per process. A headless match that remains undecided for 90 nominal game
minutes fails the iteration instead of growing evidence forever; use `-MaxGameSeconds 0` only when
an unlimited match is intentional. **Stop** creates a cancellation sentinel so the client can end
the world and finalize evidence/replay before the launcher resorts to killing it. Use Rendered when
debugging UI/render traits or issuing commands by hand.

**Ask for more than you expect to get.** Past `fastest` the setting is a target, not a promise —
what you actually get is whatever your machine manages. `/speed` in the chatbox reports both
numbers, and `debug.log` records them every ten seconds:

```
Turbo: asked for 40x, getting 32.4x — the machine is the limit here, not the setting.
```

That distinction matters: a bot you think you watched at 40x, but actually watched at 4x, is a
bot you have timed against the wrong clock. Measured on one desktop, a 1v1 with a full
army on both sides:

| Asked for | Got | Why |
|---|---|---|
| 20x, VSync on | 4.3x | the refresh rate, not the setting |
| 10x | 8.6x | sleeping between ticks, and overshooting |
| 20x | 17.1x | same |
| **40x** | **32.4x** | no time left to sleep in, so it simply runs |

Two things were in the way, and neither was the timestep.

**VSync.** The engine draws exactly one frame per logic tick during play, so with VSync on the
tick rate cannot exceed your refresh rate — 4.3x on a 120Hz screen, however fast you set it. The
[`TurboSpeed`](../src/AutoCnC.Platform/Traits/TurboSpeed.cs) trait therefore drops VSync for the
duration of a turbo match and hands it straight back afterwards. Nothing is written to your
settings; the next world to load — including the menu behind it — restores your own preference.

**Sleep granularity.** With VSync gone, a tick costs about 0.9ms of real work (0.4ms of
simulation, 0.5ms of drawing — measured with `Launch.Benchmark`, and a 320x240 window is no
faster than a 1024x768 one, so this is not about pixels). Ask for a 2ms tick and the loop has
1.1ms spare, sleeps for it, and Windows hands control back late — which is the whole of the gap
between 20x asked and 17x got. Ask for 1ms and there is nothing left to sleep in, so it stops
sleeping and just runs. **`maximum` is faster than `plaid` by more than the timestep suggests,**
and on a machine quicker than this one it would land nearer 40x.

### How the match went

Watching at 20x tells you who won. It does not tell you *when* it was lost, and that is usually
the question. So every match records one row per player per second of game time to a CSV, and the
launcher graphs it live in the **results window** it opens when the battle starts: units, army
value, buildings, base value and kills, one line per player in that player's colour, with the
final numbers and the result underneath.

```
seconds,player,faction,bot,colour,units,army,buildings,basevalue,assets,cash,killed,lost,buildingskilled,buildingslost,state
765,Commander,gdi,0,C82020,0,0,0,0,0,0,72,180,0,9,Lost
765,HAL 9001,gdi,1,FF7A22,63,32980,27,29500,74580,332,180,73,9,0,Won
```

The curves say things a match never quite does. An army count that climbs steadily to 60 and then
falls off a cliff at 9:30 is a bot that fought the wrong fight once, not one that
builds badly. A cash column that keeps rising while the unit count sits still is a production plan
that has stopped spending. A base value that flattens while the opponent's keeps climbing is an
economy that stopped expanding three minutes before anyone shot at it. A kill line that stays flat
while your losses mount is an army going somewhere to die.

Buildings and base value are counted from the world rather than read off the engine's own figures,
which have no structures-only equivalent — its "assets" lumps buildings in with harvesters and
everything else. `assets` is recorded too, so the difference is there if you want it.

The command-line default lands in the OpenRA logs folder as `autocnc-telemetry.csv`, with the
previous one kept alongside it as `.csv.1`. The launcher instead gives every fight its own
training-run directory, so no result is overwritten. Both are ordinary CSV with a named header,
so anything that reads a spreadsheet will plot them too.

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Telemetry C:\tmp\run-14.csv   # keep this one
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Telemetry none                # record nothing
```

**What this is, and what it is not.** A client simulates the whole world, so the process running
your match knows everything about everyone — fog hides actors from the *player*, not from the
program. That is why this record is written for a human to read and is not reachable from mode
code: a mode that wants to know about the enemy goes through `ModeContext`, which filters by
visibility. For the same reason, when there is a human opponent only your own side is recorded.
See [determinism](determinism.md).

### What your code knew at the time

The graph says *when* it went wrong. The **battle log** says what your bot had to work with
at that moment, which is the part you can act on — and it is the other half of the launcher's
output window.

It opens by naming everybody in the match, then records one row per event, from your side's point
of view only:

```
seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail
0,player,Commander,,,,,,,,faction=nod bot=0 colour=C82020 side=you
0,player,Watson,,,,,,,,faction=gdi bot=1 colour=18F26F side=enemy
461,spotted,Watson,orca,841,Commander,,,64,52,kind=Aircraft frombase=15
463,attacked,Commander,e1,437,Watson,orca,841,78,54,damage=121 health=97
471,attacked,Commander,e1,437,Watson,orca,841,78,54,damage=547 hits=8 health=86
892,killed,Watson,jeep,806,Commander,e1,437,73,46,
907,lost,Commander,nuke,342,Watson,orca,841,83,48,
956,over,Commander,,,,,,,,result=Lost
```

Every row names both sides: `player` owns `actor`, `otherplayer` owns `otheractor`, and the two
IDs are the engine's own, so "spotted, then hit by, then lost to" is one story about one enemy
rather than three rows that happen to share a unit name.

| Event | Means |
|---|---|
| `player` | Somebody in this match: faction, colour, bot or not, and which one is you |
| `spotted` | An enemy came into view. `frombase` is how many cells from your construction yard |
| `attacked` | Something of yours took damage. Repeat hits on the same unit are counted into `hits` rather than written out one by one |
| `lost` | You lost an actor, and to what |
| `killed` | You destroyed an enemy you could see, and with what |
| `built` | An actor of yours entered the world |
| `over` | The result |

**The constraint is the point.** Everything here is something your code was in a position to react
to, and that is not a second opinion about what "in a position" means: sightings are filtered by
`ModeContext.IsVisibleEnemy`, the same predicate `ctx.SenseThreats` uses, and damage arrives on
the same notification `IUnitMode.OnDamaged` gets. Reading a bot's decisions against an
omniscient feed teaches you nothing, because the decisions were not made with one. If a line is in
this file, a mode could have responded to it.

That makes it the material for the next version: an enemy `spotted` at `frombase=4` two minutes
before the first `attacked` row is a scout your modes ignored; a run of `lost` rows with no
`killed` between them is an attack mode walking into something it should have sensed.

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -BattleLog C:\tmp\battle-14.csv
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -BattleLog none
```

### Why it made those decisions

The battle log says what the bot knew, but not what it decided. Launcher fights therefore also
write `evidence/decisions.jsonl` in their run directory, a structured trace with:

- every `BattleState` assessment;
- the bot's doctrine decision and any mode-requested switch;
- whether a switch continued, was rate-limited, failed validation, or applied;
- every unit decision that actually became an engine order.

It is the bridge between evidence and result. If `battle.csv` says an enemy was spotted at the
base, `evidence/decisions.jsonl` says which doctrine assessment followed and whether individual units
issued attacks, held, or went somewhere else. This is after-match diagnostic output only; bot
code cannot read it.

From the command line, opt in with:

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap -DecisionTrace C:\tmp\decisions-14.jsonl
```

### Watching it back

Running at 40x costs you the ability to see anything, which is fine, because **every battle is
recorded whether you ask or not**. When the graph shows the match turning at 9:30, the replay is
how you find out what actually happened there.

Press **Watch replay** in the launcher for the match you just ran, or name one:

```powershell
./scripts/launch.ps1 -Replay "$env:APPDATA\OpenRA\Replays\autocnc\<version>\<timestamp>.orarep"
```

Replays are not bound to the one-frame-per-tick rule that live play is — they render on their own
schedule, which is why a replay fast-forwards even faster than the live match did (39.8x here,
against 32.4x live). The usual loop is therefore: run it at `maximum`, read the graph, and only
then watch the ninety seconds that decided it.

**A turbo match opens its replay at 1x, on purpose.** The engine's replay bar is *relative* to the
speed the match was recorded at, so on a 40x recording its "slow" button still leaves you at 20x
and there is no way down from there. AutoC&C resets playback to 1x when the replay loads, and
`/speed` sets it in absolute terms:

```
/speed          what it is playing at now
/speed 1        real time
/speed 0.5      half speed, for a fight you keep missing
/speed 8        skip forward through the boring part
/speed max      as fast as it will go
```

**Quit the game rather than killing it.** The recorder writes the index a replay needs during a
clean shutdown, so a force-killed game leaves a file the engine will refuse to open — and the
match you most wanted to review is exactly the one you are most likely to be impatient with. The
launcher's **Stop** button asks the game to close before it resorts to anything harsher, so the
replay survives pressing it.

---

## 4. Assign modes

Everything happens through the chatbox. Select some units, then:

```
/mode AttackBaseMode            just the units you selected
/mode all DefensiveMode         your whole army
/mode type harv RunHomeMode     every harvester
/mode group 1 AttackBaseMode    control group 1 (make one with Ctrl+1 first)
```

Inspect what's going on:

```
/bots            installed battle bots, with the loaded one marked
/bot <name>      load one
/doctrines       the doctrines the loaded bot owns, with the running one marked
/why             what the bot is running, and what it is looking at
/whatmode        what the current selection is running
/modelog         log every decision to debug.log
/speed [n]       the speed you are getting; in a replay, /speed 0.5 sets it
/assignments     every assignment currently in force
/mode clear      drop per-unit overrides
```

**Precedence: most specific wins.**

```
per-unit selection  >  control group  >  unit type  >  all
```

So this does what you'd hope — the second command doesn't undo the first:

```
/mode all DefensiveMode
/mode type harv RunHomeMode
```

Tanks defend; harvesters run home.

Switching modes is **instant and live** — it lands within one game tick, mid-battle. No restart.

---

## 5. Write your first battle bot

The platform has no strategy of its own. All behaviour comes from a **battle bot**, and AutoC&C
ships one — `Reference` — as both the example and the opponent to beat.

A bot owns several **doctrines** and decides between them as the match turns. The reference bot
has four: `Opening`, `Scout`, `Defence` and `Attack`.

Create your own from the small starter template:

```powershell
./scripts/new-bot.ps1 -Name MyRush
cd bots/MyRush
dotnet test .\Tests\MyRush.Tests.csproj
```

The launcher's **New bot…** button performs the same operation and selects the new project.
Unlike copying `Reference`, the generated bot does not inherit an already-developed strategy.

Open `MyRush/Doctrines/` — everything about how your army fights one way is declared there:

```csharp
b.Build("powr", "nuke").Until(2);      // what to construct
b.Train("Infantry", "e1").Until(10);   // what to train
b.Assign<DefensiveMode>().ToAll();     // how units behave
```

And `MyRush/Logic/StarterLogic.cs` keeps combat decisions pure and testable:

```csharp
if (!state.HasWeapon)
    return UnitDecision.Continue;
```

That rule is a pure function of what your side can see, so you can test it without a game:

```powershell
dotnet test bots/MyRush/Tests
```

Point the launcher at `bots/MyRush/MyRush.csproj` and press **Deploy & fight**. It is loaded
before the first tick, so there is nothing to type — but if you want to check, or to take a hand:

```
/bots                   see what's installed, with the loaded one marked
/bot MyRush             load a different one
/doctrines              the doctrines your bot owns, with the running one marked
/why                    what it is running, and what it is looking at
/doctrine Attack        run one by hand; the bot may still change its mind
```

Full guide: [writing-bots.md](writing-bots.md).

## 6. The iteration loop

**Write your modes before the match, then commit to them.** The battle is the test of what you
wrote, not a live coding session — so there is no mid-match code editing by design.

That makes the fast feedback loop the tests, not the game:

```powershell
dotnet test src/AutoCnC.Core.Tests           # milliseconds, no game, no engine build
```

To make your own logic testable that way, put the judgement in a pure function under your bot's
`Logic/` folder and call it from `OnTick`. `StarterLogic` and the reference bot's logic classes
are worked examples. Details are in [writing-bots.md](writing-bots.md).

Full loop:

```powershell
# 1. edit your bot's *.cs in your IDE
dotnet test bots/MyRush/Tests              # 2. check the logic
./scripts/launcher.ps1                     # 3. Deploy & fight from the command deck
```

**Deploy & fight** builds your bot and starts the game in one step, and runs its tests on the way past if
you tick **Run its tests first**. From a terminal that whole loop is one line:

```powershell
./scripts/run-bot.ps1 -Test -Map tiberium-rift.oramap -Difficulty Hard
```

Every launcher fight is durable under `%LOCALAPPDATA%\AutoCnC\TrainingRuns`: manifest, telemetry,
battle log, decision trace, replay, result, and (for headless runs) `performance.json`.
**History & trends** opens a newest-first list of recorded sessions, showing whether you have
provided feedback, the result, duration, and your bot's final units, army, buildings, base value,
kills, losses, and cash. Select any battle to read or edit its feedback, watch its own recorded
replay, or inspect its charts. The **Trends** tab compares final stats and outcome-colored duration
across iterations. After a fight ends there are two
equally supported paths:

1. **Open code** — make the next change yourself, deploy, and fight again.
2. **Analyze & improve** — let a configured local coding agent inspect that evidence and edit the
   bot. GitHub Copilot CLI is the default, but **Agent settings** accepts any executable and
   argument list using `{prompt}` or `{promptFile}`.

In **AI training**, **Train from battle** explicitly selects the evidence to use. The picker shows
the battle number, recorded time, outcome, and duration; the details below show the map, execution
mode, feedback status, and session ID. **Watch replay** plays that selected battle. You can also
right-click a row in **History & trends** and choose **Train from this battle** to select it and
open AI training without starting an agent. Feedback, improvement, verification retry, the agent
workspace, restore, and **Open run** all use that selection. The latest-battle readout in Proving
ground stays unchanged. The launcher remembers your training selection separately.

To remove a completed recording, select it in **History & trends** and choose **Delete session**
(also available in the row's context menu). Confirming permanently removes that session's feedback,
stats, saved logs and replay copy, agent history, and restore snapshots, and updates the trends.
The current bot source, other sessions, and original OpenRA replay are untouched. Deletion and
training-battle selection are disabled while an operation is running.

**Your battle feedback** is at the top of **Proving ground**, with a matching action in **AI
training**. After a manual rendered battle, the launcher asks what you noticed about the win or
loss; choose **Save feedback** or **Not now**. For a headless battle, choose **Watch replay & add
feedback** first. Feedback unlocks for that battle after its recorded replay exits successfully;
a failed or stopped replay does not unlock it, and the watched state survives launcher restarts.

Manual **Analyze & improve** also offers a chance to add missing feedback before the agent starts.
You can save and improve, watch the required replay first, improve using the logs and stats without
feedback, or cancel. Feedback stays with the selected battle in its manifest and agent prompt;
editing an older battle does not attach its observations to the newest fight. Recorded battles
from prebuilt bots can also be annotated, although those bots cannot be AI-trained.

The launcher snapshots source first, the wrapper reruns tests and deploys after the agent exits,
and **Agent workspace** opens a dedicated Improvement window that streams its colored terminal
progress. The same window exposes the exact prompt, shared game guide, fight manifest, changed
files, and `game-rules.json`—a generated snapshot of units, health, armor, movement, build data,
armaments, range, reload, projectile, damage, and armor modifiers from OpenRA's resolved runtime
rules. Use **Restore previous iteration** to put the exact pre-agent source back.

For an unattended loop, keep **Execution** on its default **Headless** in Proving ground.
In **AI training**, check **Repeat: fight > analyze > improve > fight**, then press
**Start AI training**. It repeats the cycle until **Stop operation**,
always improving from the battle just fought and automatically accepting each valid next-round
prompt, regardless of an older manual selection. Switch Execution to **Rendered** when you
want to watch the same loop. It never pauses for feedback; review those battles from **History &
trends** after stopping. Feedback actions are locked only while an operation is running, not merely
because the repeat checkbox is selected. A failed game, agent command, test, or build stops the loop instead of advancing
with an unverified bot.

Fight and rules JSON are shown as collapsible trees. When the run finishes, the agent drafts an
entire replacement prompt template in **Next prompt***. Edit and approve it in manual mode, or let
continuous mode accept a valid template automatically. The launcher inserts that round's paths,
evidence, result, and source revision through required placeholders. This replaces rather than
appends, so the prompt can get more focused without growing indefinitely. While a build, fight, or
improvement is running, the launcher taskbar icon shows indeterminate progress.

If improvement stops with a build or test error, it no longer dead-ends. The Improvement window
states whether the coding agent or independent verification failed. **Retry verification** cleans
generated output and retests without invoking AI. If the source genuinely needs work, **Fix failed
improvement** starts a recovery attempt with the previous transcript and current edits; **Restore
previous iteration** discards the attempt.

And if you touch the mod's YAML or traits, validate the wiring:

```powershell
./scripts/lint.ps1     # constructs every actor in the mod; catches what the compiler can't
```

---

## Troubleshooting

**`/modes` doesn't list my mode**

- Did the build succeed? Check for `PlayerModes -> ...\engine\bin\PlayerModes.dll`.
- Is the class `public`, non-abstract, and does it derive from `UnitMode` (or implement
  `IUnitMode`)?
- Does it have a public parameterless constructor? A constructor taking arguments is skipped.
- Run `/modes` again — load problems are reported there, e.g. duplicate mode names.

**My mode does nothing**

- `/whatmode` with units selected — confirm it's actually assigned.
- Are you returning `UnitDecision.Continue` everywhere? That means "leave the unit alone".
- Something more specific may be winning: `/assignments`, then `/mode clear` to drop per-unit
  overrides.

**"The file is locked by: .NET Host"**

The game is running and holding `PlayerModes.dll`. Close it, then rebuild.

**My mode threw an exception**

It's printed to chat and written to `debug.log` (in `Documents/OpenRA/Logs` or
`%APPDATA%/OpenRA/Logs`). The unit's mode is dropped, but the match carries on.

**Units feel sluggish to react**

Decisions become orders, which take a few ticks (~120ms) to arrive — the same latency your own
clicks have. You can also lower `TickInterval` for an actor in
`mods/autocnc/rules/units.yaml`.

---

## Where next

| Doc | For |
|---|---|
| [writing-bots.md](writing-bots.md) | Authoring bots and doctrines: switching, plans, modes, the full API |
| [architecture.md](architecture.md) | How the pieces fit and why |
| [determinism.md](determinism.md) | Why your code can't desync a multiplayer match |
| [../bots/Reference/README.md](../bots/Reference/README.md) | The reference bot, next to its code |
