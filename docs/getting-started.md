# Getting started

Start to finish: install, write your first mode, and watch it fight. About 15 minutes, most of
it downloading.

If you just want the API reference, skip to [writing-modules.md](writing-modules.md).

---

## 1. Prerequisites

| | |
|---|---|
| **.NET SDK 8.0 or newer** | [download](https://dotnet.microsoft.com/download) — the **SDK**, not just the runtime, since you're compiling code |
| **Git** | with submodule support (any modern version) |
| **An IDE** | Visual Studio, Rider, or VS Code with the C# Dev Kit |
| **OS** | Windows, Linux or macOS |

Check the SDK:

```powershell
dotnet --list-sdks
```

You need a line starting `8.` or higher.

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
| `The file is locked by: ".NET Host"` | The game is running. Close it and rebuild. |

---

## 3. First launch

```powershell
./scripts/launch.ps1
```

AutoC&C is built on Tiberian Dawn, so on first run OpenRA offers to download the **freeware**
C&C assets (~100MB, legally redistributed by EA). Accept, wait, and you land on the main menu.

Start a skirmish. Your MCV deploys itself and the base starts building — that's `BuildBaseMode`,
which MCVs and construction yards run by default. Combat units run `DefensiveMode`, so they hold
ground rather than chase.

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
/modules         installed battle modules
/module <name>   load one
/whatmode        what the current selection is running
/modelog         log every decision to debug.log
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

## 5. Write your first battle module

The platform has no strategy of its own. All behaviour comes from a **battle module**, and
AutoC&C ships one — `Reference` — as both the example and the opponent to beat.

Start your own by copying it:

```powershell
cp -r modules/Reference modules/MyModule
cd modules/MyModule
# rename ReferenceModule.csproj / .sln, and the class + Name in ReferenceBattleModule.cs
dotnet build
```

Open `MyModule/ReferenceBattleModule.cs` — everything about how your army fights is declared
there:

```csharp
b.Build("powr", "nuke").Until(2);      // what to construct
b.Train("Infantry", "e1").Until(10);   // what to train
b.Assign<DefensiveMode>().ToAll();     // how units behave
b.Assign<AttackBaseMode>().ToGroup(1);
```

Rebuild, launch, then in game:

```
/modules              see both modules listed
/module MyModule      load yours
```

Full guide: [writing-modules.md](writing-modules.md).

## 6. The iteration loop

**Write your modes before the match, then commit to them.** The battle is the test of what you
wrote, not a live coding session — so there's no mid-match code editing by design.

That makes the fast feedback loop the tests, not the game:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests     # ~20ms, no game, no engine build
```

To make your own logic testable that way, put the judgement in a pure function in
`src/AutoCnC.Modes.Core` and call it from `OnTick`. `DefensiveLogic` is the worked example.
Details in [writing-modules.md](writing-modules.md).

Full loop:

```powershell
# 1. edit player-modes/*.cs in your IDE
dotnet test src/AutoCnC.Modes.Core.Tests   # 2. check the logic
./scripts/build.ps1 -SkipEngine            # 3. compile  (close the game first!)
./scripts/launch.ps1                       # 4. play
```

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
| [writing-modules.md](writing-modules.md) | Authoring battle modules: plans, modes, the full API |
| [architecture.md](architecture.md) | How the pieces fit and why |
| [determinism.md](determinism.md) | Why your code can't desync a multiplayer match |
| [../modules/Reference/README.md](../modules/Reference/README.md) | The reference module, next to its code |
