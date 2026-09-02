# Writing a battle bot

A **battle bot** is the unit of authorship in AutoC&C, and the thing a battle is played with. A
bot owns several **doctrines** and decides which one the match needs.

| | Answers | Lives in |
|---|---|---|
| **Doctrine** | *How* to fight one way, thoroughly | `Doctrines/*.cs` |
| **Bot** | *Which* way to fight, and when | `Reassess` |

A doctrine declares everything about one way of fighting:

- **what to build** — the base construction plan
- **what to train** — the unit production plan
- **how units behave** — the modes
- **who runs what** — the assignments

The platform ships no strategy at all. Load a bot and it plays; load none and nothing deploys,
builds or shoots. Beating the reference bot is the goal.

**Why the split.** Plans do not survive contact. You can grow one doctrine a special case at a
time until nobody can say what it does, or you can keep an attack doctrine confident, keep a
defence doctrine paranoid, and let the bot change its mind. The second one stays readable.

---

## The shape of a bot

```csharp
using AutoCnC.Core;
using AutoCnC.Sdk;

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

`Reassess` is called every few seconds of game time. Naming the doctrine already running is the
same as continuing, so a rule can state its condition without also checking what is loaded.

### What a bot is allowed to know

`BattleState` is everything your side can tell about the match. Your own economy and army are
exact, because they are yours. **The enemy half is only what you can currently see** — the
platform fills it in through the same visibility rule `ctx.SenseThreats` filters with, so a bot
cannot switch to an attack doctrine on the strength of an army value no unit of yours has laid
eyes on.

| Field | |
|---|---|
| `Seconds`, `Doctrine`, `DoctrineSeconds` | Game time, what is running, and for how long |
| `Cash`, `PowerBalance`, `Harvesters`, `Refineries` | Your economy |
| `Units`, `ArmyValue`, `Buildings`, `BaseValue` | Your forces |
| `UnitsLost`, `BuildingsLost`, `UnitsKilled` | What the last `WindowSeconds` cost you |
| `EnemiesInSight`, `EnemiesNearBase`, `NearestEnemyCells` | What you can see, right now |
| `SecondsSinceContact`, `EnemyBaseFound` | What you have learned and kept |

There are a few conveniences on top — `BaseUnderAttack`, `BlindToEnemy`, `Winning` — and no
engine types anywhere, which is the point: the deciding half of a bot is a pure function you can
test without a game.

```csharp
[Test]
public void LosingBuildingsSwitchesToDefence()
{
    var s = BattleState.Empty with { Doctrine = "Opening", BuildingsLost = 1 };

    Assert.That(MyBotLogic.Decide(s).Doctrine, Is.EqualTo("Defence"));
}
```

A match where the base starts falling over twenty minutes in takes twenty minutes to reproduce,
and three lines to write down. See `bots/Reference/Tests/ReferenceBotLogicTests.cs`.

### Switching is rate-limited for you

A doctrine carries its own build plan, production plan and assignments, so changing it changes
what the whole side is trying to do — and two rules that disagree would otherwise flip the army
back and forth every few seconds. The platform will not act on a switch until the current
doctrine has had `MinimumDoctrineSeconds` (30 by default). Read `DoctrineSeconds` if you want to
be stricter still.

### A lone doctrine is still a bot

An assembly with `IDoctrine` types and no `IBattleBot` is played as one bot per doctrine, each
owning that doctrine and never switching. A bot with one doctrine has nothing to decide, so it
decides nothing.

---

## The shape of a doctrine

```csharp
using AutoCnC.Sdk;

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

That's a complete doctrine. Put it in a bot, build it, drop the DLL in, `/bot Adaptive`, watch it
play.

### Assign to units, not to control groups

A bot cannot put units in a control group — there is nobody to press the button. A doctrine a bot
is meant to run should `ToAll()` or `ToUnitType(...)`; `ToGroup(1)` still works, and is how a
human watching can take a hand.

### Doctrines can end themselves

A doctrine knows its siblings and can ask for one of them. Sometimes the unit on the ground is
the first to know the plan is finished:

```csharp
if (ctx.SenseStructures(new WDist(10 * 1024)).Count > 0)
    ctx.SwitchDoctrine("Opening", "scout found their base");
```

`ctx.Doctrine` is the one running, `ctx.Doctrines` lists them all. It is a request rather than a
command: it goes through the same assessment and the same dwell time, so calling it every tick is
harmless, and asking for the doctrine already running does nothing.

**The bot outranks it.** `Reassess` is asked first and always, and a request only carries when the
bot returns `Continue` — otherwise a mode that asked every tick could starve the bot of its own
judgement, and the first rule to go would be the one watching the base. What that buys you is a
doctrine that ends itself without the bot needing a rule about it at all.

### Candidates cover both factions

`Build("powr", "nuke")` means "a power plant" — GDI's is `powr`, Nod's is `nuke`. The planner
takes whichever is currently buildable, so one plan works as either faction without you checking.

Same for `Train("Infantry", "e1", "e2")` and `Build("pyle", "hand")`.

### Build steps are cumulative

`Until(n)` means "until `n` of these exist", counting what is already standing and what is
queued. So a doctrine whose plan extends another's picks up where that one left off, and
switching back and forth never rebuilds anything.

### `Forever()` keeps production going

`Until(n)` stops once you have `n`. `Forever()` never completes, so put it last: once the army is
up to strength the factory keeps replacing losses instead of going idle.

### Assignment precedence

```
per-unit (player, in game)  >  control group  >  unit type  >  all
```

So `Assign<DefensiveMode>().ToAll()` then `Assign<RunHomeMode>().ToUnitType("harv")` does what
you'd expect. A player can still override anything live with `/mode`.

---

## Getting set up

```powershell
./scripts/new-bot.ps1 -Name MyBot
cd bots/MyBot
dotnet test .\Tests\MyBot.Tests.csproj
```

The launcher exposes the same operation as **New bot…** and selects the generated project
immediately. The starter is intentionally small rather than a copy of `Reference`: one doctrine,
one sense/decide/act mode, pure logic, and tests you can safely evolve.

Build and play in one command:

```powershell
./scripts/run-bot.ps1 -BattleBot MyBot -Test
```

That tests your strategy, builds it, installs it where the platform scans, and launches the game.

### A bot is a normal NuGet consumer

```xml
<PackageReference Include="AutoCnC.Sdk" Version="0.1.0" />
<PackageReference Include="AutoCnC.Core" Version="0.1.0" />
```

The SDK package carries the OpenRA reference assemblies it was built against, so a bot compiles
with no game installed and no path fiddling.

That means **a bot does not have to live in this repository.** Copy the folder anywhere, point
`nuget.config` at a folder holding the `AutoCnC.*` packages, and it builds:

```xml
<packageSources>
  <add key="autocnc-local" value="C:\games\autocnc\packages" />
</packageSources>
```

Set `CopyLocalLockFileAssemblies=false` (the template does) so the SDK and engine DLLs are used
for compilation only. The game already has them loaded, and stray DLLs beside your bot would be
scanned as bots.

### Where bots are installed

Built output goes to `BattleBotInstallDirectory`, which defaults to `engine/bin/bots`. The
platform scans that plus `<SupportDir>/autocnc/bots`, the latter being where a player drops a bot
someone shared with them.

### Training from a fight

The launcher preserves every fight as a unique training run under
`%LOCALAPPDATA%\AutoCnC\TrainingRuns`. A run contains:

| Artifact | Answers |
|---|---|
| `manifest.json` | Authoritative run state, result, and improvement status |
| `evidence/agent-prompt.txt` | The exact instruction sent to the configured agent |
| `evidence/game-guide.md` | Shared mechanics, SDK semantics, fairness constraints, and improvement rules |
| `evidence/game-rules.json` | Actors and weapons exported from OpenRA's resolved runtime ruleset |
| `evidence/fight.json` | The source revision, map, opponent, factions and result shown to an agent |
| `evidence/telemetry.csv` | When the economy or army moved ahead or fell behind |
| `evidence/battle.csv` | What this side could observe and react to |
| `evidence/decisions.jsonl` | What the bot assessed and which mode decisions became orders |
| `evidence/replay.orarep` | What the fight looked like |

That is enough to correlate cause and effect without giving strategy code omniscient information
during the match. `decisions.jsonl` is diagnostic output written by the host; a bot cannot read it.
The Results window can save the player's assessment of why an individual battle won or lost in
`manifest.json` and `fight.json`; `{result}` includes it when that battle is sent to an agent.

`game-rules.json` is generated from `ModData.DefaultRules` after OpenRA has merged the inherited
Tiberian Dawn YAML with AutoC&C overrides. It is a snapshot of the actual engine build, not a
second hand-maintained stats database.

**Open code** is the normal manual path. **Analyze & improve** is optional: it snapshots the
editable bot files, invokes the configured local coding agent (GitHub Copilot CLI by default), and
then runs the bot's tests and deployment build independently. **Agent workspace** opens a
dedicated window that streams the agent's colored terminal progress and exposes the exact prompt
and all shared context before and after the run. Review the changed files before fighting again.
**Restore previous iteration** restores modified and deleted files
and removes files added by that agent run; build output and git metadata are never part of the
snapshot.

The Fight and Units & weapons views are lazy JSON trees: expand only the objects or actors you
need. Every agent is also asked to draft a complete replacement prompt template for the next
round—not an extra hint. It appears in **Next prompt***, where the player can edit and approve it.
The approved template replaces the previous one and is rendered with fresh run values through
required placeholders such as `{workspace}`, `{telemetry}`, `{result}`, and
`{nextPromptContract}`. The initial template lives at `docs/agent-prompt-template.md`; an approved
replacement is saved in the player's launcher settings.

**History & trends** reads every compatible run for the selected bot. It compares the final units,
army value, buildings, base value, and kills for the local side against the opponents' total, plus
an outcome-colored duration line that makes faster wins and slower losses visible.

Checking **Continuous improvement** starts a stateful Fight -> improve -> Fight loop. Every cycle
still has its own source revision, evidence, reversible snapshot, independent verification, and
result. A valid agent-authored next prompt is accepted automatically; any battle, agent, test, or
build failure stops the loop. Player assessments are disabled because continuous mode does not
pause after a battle.

An agent exit and a host verification failure are recorded separately. Verification always cleans
the bot's generated `bin`/`obj` output before testing. If it still fails, **Retry verification**
repeats that cheap check without spending another agent run; **Fix failed improvement** archives
the failed transcript and asks the agent to repair the current changes. **Restore previous
iteration** remains the escape hatch back to the pre-agent snapshot.

The agent command is provider-neutral and configurable as one argument per line. It supports
`{prompt}`, `{promptFile}`, `{project}`, `{workspace}`, `{evidence}`, and `{run}` placeholders.
The default Copilot command grants file access only to the bot workspace and `{evidence}`, keeping
the restore snapshot outside the agent's allowed paths. The equivalent terminal entry point is
`scripts/train-bot.ps1`.

---

## Writing a mode

A mode decides what one unit should do.

```csharp
public sealed class StandStillMode : UnitMode
{
    public override UnitDecision OnTick(Actor self, ModeContext ctx)
    {
        return UnitDecision.Hold("staying put");
    }
}
```

| Member | When it runs |
|---|---|
| `OnEnter` | Once, when a unit switches into this mode |
| `OnTick` | Every `TickInterval` ticks; returns a `UnitDecision` |
| `OnDamaged` | Immediately on taking damage, between evaluations |
| `OnExit` | Once, when the unit leaves this mode |

**One instance per unit**, created on entry and dropped on exit, so instance fields are safe
per-unit memory. `static` mutable fields are shared by every unit — almost never what you want.

### Decisions

`OnTick` returns data, it does not act. The platform turns the decision into an order, and
**only when the intent changes**:

```csharp
UnitDecision.Continue                          // leave the unit alone
UnitDecision.Hold(reason)
UnitDecision.Attack(actorId, reason)
UnitDecision.MoveTo(x, y, reason)
UnitDecision.AttackMoveTo(x, y, reason)
UnitDecision.ReturnToAnchor(reason)
UnitDecision.Retreat(reason)                   // nearest repair bay
UnitDecision.AdvanceToObjective(id, reason)
UnitDecision.Deploy(reason)                    // e.g. MCV -> construction yard
UnitDecision.Produce(queue, item, reason)
UnitDecision.PlaceBuilding(queue, item, x, y, reason)
```

Two things to internalise:

**`Continue` means "don't interfere".** That's what lets `RunHomeMode` sit on a harvester without
stopping it harvesting. Only return an action when you want to override what the unit is doing.

**Returning the same decision every tick is free.** Don't hand-roll rate limiting.

### Sense → decide → act

For anything beyond a few lines, put the judgement in a **pure function** and call it from
`OnTick`:

```csharp
public override UnitDecision OnTick(Actor self, ModeContext ctx)
{
    var state = new DefensiveState(                // sense
        HealthPercent: ctx.HealthPercent,
        Threats: ctx.SenseThreats(radius),
        /* ... */);

    return DefensiveLogic.Decide(state, tuning);   // decide (pure, testable)
}
```

Then test your strategy in milliseconds, with no engine and no game:

```powershell
dotnet test bots/MyBot/Tests
```

The reference bot's tests assert against the plans its doctrines actually declare, and against
the rules its `Reassess` actually runs, so they verify the real shipped strategy rather than a
copy that can drift.

---

## The ModeContext API

### Sensing

| Member | Notes |
|---|---|
| `HealthPercent` | 0–100 |
| `WeaponRangeUnits` | Longest enabled armament range, world units (1024 = 1 cell) |
| `DistanceFromAnchorUnits`, `DistanceTo(...)` | Distances |
| `IsIdle`, `CanMove`, `HasWeapon`, `IsBuilding` | Capability checks |
| `GroupId`, `Anchor`, `ModeName` | Unit state |
| `SenseThreats(radius)` | Visible enemies (reused buffer — don't retain) |
| `SenseStructures(radius)` | Visible enemy buildings |
| `SenseAllies(radius, type)` | Friendly actors |
| `CanAttack(actor)` | Do our weapons work against it? |
| `FindRepairBay()`, `FindRefinery()`, `FindNearestAllied<T>()` | Nearest allied |
| `ResolveActor(id)` | ActorID back to a live actor |

### Construction and production

| Member | Notes |
|---|---|
| `CanDeploy` / `DeploysIntoBuilding` | **Use both.** A construction yard can transform back into an MCV, so `CanDeploy` alone loops forever |
| `Cash`, `PowerBalance` | Economy |
| `QueueFor(category)` / `OwnsQueue(category)` | Production queues. Only the owning actor should drive one |
| `BuildableItems(category)`, `ProducingItem`, `ItemReadyToPlace` | Queue state |
| `QueueStates()` | All queues, for the production planner |
| `OwnedBuildingCounts()`, `OwnedUnitCounts()` | Counts, including queued |
| `FindBuildLocation(actorType)` | A valid placement cell near the base |
| `BuildPlan`, `ProductionPlan` | **The running doctrine's plans** — read these rather than hardcoding |
| `Doctrine`, `Doctrines`, `SwitchDoctrine(name, why)` | The doctrine you are part of, its siblings, and asking for one |

`BuildBaseMode` and `TrainUnitsMode` read `ctx.BuildPlan` / `ctx.ProductionPlan`, which is why
they work unchanged for any doctrine: change the plan in your `IDoctrine`, not the mode.

---

## In-game commands

```
/bots                           installed battle bots; * marks the loaded one
/bot <name>                     load one
/doctrines                      the doctrines the loaded bot owns; * marks the running one
/doctrine <name>                run one by hand; the bot may still change its mind
/why                            what the bot is running, and what it is looking at
/modes                          modes the running doctrine provides
/mode <ModeName>                override for the current selection
/mode all|type|group <...>      override more broadly
/mode clear                     drop per-unit overrides
/assignments                    what's assigned right now
/whatmode                       what the selection is running
/modelog                        log every decision to debug.log
/speed [n]                      game speed: reports it, and sets it in a replay
```

`/modelog` is your main debugging tool:

```
[mode] fact#23 BuildBaseMode: Produce nuke -> StartProduction (building nuke)
[mode] pyle#30 TrainUnitsMode: Produce e1 -> StartProduction (training e1)
```

You get the decision, the order it became, and your own stated reason — which is why filling in
the `reason` argument properly pays off.

`/modelog` says what your code *did*. The **battle log** says what it had to go on: the launcher's
output window records every event your side could react to — an enemy coming into view, a hit
taken, a unit lost, a kill — each one naming the players on both ends of it, and filtered by the
same visibility rule `ctx.SenseThreats` uses.

```
461,spotted,Watson,orca,841,Commander,,,64,52,kind=Aircraft frombase=15
463,attacked,Commander,e1,437,Watson,orca,841,78,54,damage=121 health=97
907,lost,Commander,nuke,342,Watson,orca,841,83,48,
```

Read the two together and a bad mode gives itself away: a `spotted` row at `frombase=4` two
minutes before the first `attacked` row, with no `/modelog` line in between, is a scout your
modes had every chance to react to and did not. See
[getting-started.md](getting-started.md#what-your-code-knew-at-the-time).

---

## Your code cannot desync a match

Modes run **outside the lockstep simulation**, on your machine only, and their output is orders —
the same channel your mouse clicks use. So `float`, LINQ, `System.Random` and `DateTime` are all
fine, your opponent never runs your code, and a mode that throws is dropped for that unit with
the error printed to chat.

Details in [determinism.md](determinism.md).

---

## Common mistakes

| Mistake | Consequence |
|---|---|
| `static` mutable field on a mode | Shared across every unit |
| `Hold` when you meant `Continue` | Stops the unit doing anything useful, e.g. harvesting |
| Testing `CanDeploy` without `DeploysIntoBuilding` | Construction yard deploys and packs forever |
| Driving a queue without `OwnsQueue` | Every barracks races to order the same unit |
| Retaining the `SenseThreats` list | Buffer is reused next tick |
| Re-picking a target every tick | Unit dithers; make selections sticky |
| Assuming an order applied instantly | Orders take a few ticks; check state, don't assume |
