# Writing a battle module

A **battle module** is the unit of authorship in AutoC&C. It declares everything about how an
army fights:

- **what to build** — the base construction plan
- **what to train** — the unit production plan
- **how units behave** — the modes
- **who runs what** — the assignments

The platform ships no strategy at all. Load a module and it plays; load none and nothing
deploys, builds or shoots. Beating the reference module is the goal.

---

## The shape of a module

```csharp
using AutoCnC.Sdk;

public sealed class MyModule : IBattleModule
{
    public string Name => "Rush";
    public string Description => "Fast barracks, early pressure.";

    public void Configure(IBattleModuleBuilder b)
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

That's a complete module. Build it, drop the DLL in, `/module Rush`, watch it play.

### Candidates cover both factions

`Build("powr", "nuke")` means "a power plant" — GDI's is `powr`, Nod's is `nuke`. The planner
takes whichever is currently buildable, so one plan works as either faction without you checking.

Same for `Train("Infantry", "e1", "e2")` and `Build("pyle", "hand")`.

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
cp -r modules/Reference modules/MyModule       # copy the reference module
cd modules/MyModule
# rename the .csproj, .sln, the IBattleModule class and its Name
dotnet build
```

A module builds against AutoC&C **binaries**, not projects — see `Directory.Build.props`. That
means it can live in **its own repository**: set `AutoCnCPath` to any AutoC&C checkout.

```powershell
dotnet build /p:AutoCnCPath=C:\games\autocnc
```

Output goes to `<AutoCnCPath>/engine/bin/modules/`, which is one of the folders the platform
scans. The other is `<SupportDir>/autocnc/modules/`, for modules you install rather than build.

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
dotnet test modules/MyModule/Tests
```

The reference module's tests assert against the plan its `IBattleModule` actually declares, so
they verify the real shipped strategy rather than a copy that can drift.

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
| `BuildPlan`, `ProductionPlan` | **Your module's plans** — read these rather than hardcoding |

`BuildBaseMode` and `TrainUnitsMode` read `ctx.BuildPlan` / `ctx.ProductionPlan`, which is why
they work unchanged for any module: change the plan in your `IBattleModule`, not the mode.

---

## In-game commands

```
/modules                        installed modules; * marks the loaded one
/module <name>                  load one (takes effect on the next tick)
/modes                          modes the loaded module provides
/mode <ModeName>                override for the current selection
/mode all|type|group <...>      override more broadly
/mode clear                     drop per-unit overrides
/assignments                    what's assigned right now
/whatmode                       what the selection is running
/modelog                        log every decision to debug.log
```

`/modelog` is your main debugging tool:

```
[mode] fact#23 BuildBaseMode: Produce nuke -> StartProduction (building nuke)
[mode] pyle#30 TrainUnitsMode: Produce e1 -> StartProduction (training e1)
```

You get the decision, the order it became, and your own stated reason — which is why filling in
the `reason` argument properly pays off.

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
