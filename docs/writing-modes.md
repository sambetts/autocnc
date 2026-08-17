# Writing a mode

A mode is a C# class that decides what one unit should do. This guide walks through building
one properly.

Read [determinism.md](determinism.md) first — it defines the rules that make a mode safe to run
in a networked match. Breaking them desyncs the game.

---

## The shape of a mode

```csharp
public sealed class MyMode : UnitMode
{
    public override void OnTick(Actor self, ModeContext ctx)
    {
        var state    = Sense(ctx);              // read the world
        var decision = MyLogic.Decide(state);   // pure function, no engine
        ctx.Apply(decision);                    // act on the world
    }
}
```

`UnitMode` is a convenience base with no-op defaults, so you only override what you need. The
full interface is `IUnitMode`:

| Member | When it runs |
|---|---|
| `OnEnter` | Once, when the unit switches into this mode |
| `OnTick` | Every `TickInterval` ticks while active |
| `OnDamaged` | Immediately on taking damage, regardless of tick interval |
| `OnExit` | Once, when the unit leaves this mode |

**One instance per unit.** The controller constructs a fresh mode object per unit and discards
it on exit, so instance fields are safe for per-unit memory. `static` mutable fields are not —
they would be shared across every unit and every player.

---

## Why sense / decide / act

The temptation is to write everything inline against `Actor` and `World`. Resist it. That code
is untestable: `Actor` and `World` cannot be constructed outside a running game.

Instead, put the judgement in a pure function over plain integer structs in
`AutoCnC.Modes.Core`, which has no OpenRA reference at all. Then:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests
```

runs your unit's actual combat behaviour in milliseconds, with no engine build and no game.

The split also keeps the two concerns honest. Sensing is about *what the engine can tell you*;
deciding is about *what your unit should do*. They change for different reasons.

---

## Step 1 — Define the state and decision

In `src/AutoCnC.Modes.Core/`:

```csharp
public readonly record struct EscortState(
    int HealthPercent,
    int DistanceToWardUnits,
    bool WardUnderAttack,
    IReadOnlyList<ThreatSnapshot> Threats);

public static class EscortLogic
{
    public static UnitDecision Decide(in EscortState state, in EscortTuning tuning)
    {
        if (state.DistanceToWardUnits > tuning.MaxLeashUnits)
            return UnitDecision.ReturnToAnchor("too far from ward");

        // ... etc
        return UnitDecision.Continue;
    }
}
```

Integers only. No `float`, no `Actor`, no `World`. If you find yourself wanting to reach for the
engine here, that information belongs in the state struct instead.

## Step 2 — Test it

```csharp
[Test]
public void BreaksOffWhenWardDriftsTooFar()
{
    var state = new EscortState(100, 20 * 1024, false, []);
    var decision = EscortLogic.Decide(state, EscortTuning.Default);

    Assert.That(decision.Action, Is.EqualTo(UnitAction.ReturnToAnchor));
}
```

Add a reordering test for anything that picks between candidates:

```csharp
[Test]
public void SelectionIsDeterministic()
{
    Assert.That(EscortLogic.Decide(WithThreats(a, b), t).TargetActorId,
        Is.EqualTo(EscortLogic.Decide(WithThreats(b, a), t).TargetActorId));
}
```

## Step 3 — Wire it to the engine

In `src/AutoCnC.Mod/Library/`:

```csharp
public sealed class HarvesterEscortMode : UnitMode
{
    public override void OnTick(Actor self, ModeContext ctx)
    {
        var state = new EscortState(
            HealthPercent: ctx.HealthPercent,
            DistanceToWardUnits: ctx.DistanceFromAnchorUnits,
            WardUnderAttack: false,
            Threats: ctx.SenseThreats(WDist.FromCells(6)));

        ctx.Apply(EscortLogic.Decide(state, EscortTuning.Default));
    }
}
```

## Step 4 — Enable it in YAML

```yaml
^AutoTargetGround:
	ProgrammableController:
		DefaultMode: DefensiveMode
		AvailableModes: DefensiveMode, AttackBaseMode, HarvesterEscortMode
```

No registration code. `ModeRegistry` discovers implementations through OpenRA's own
`ObjectCreator.GetTypesImplementing<IUnitMode>()` — the same reflection the engine uses to bind
YAML trait names to `TraitInfo` classes. The mode's name is its class name.

Then lint it, which constructs every actor in the mod and will tell you immediately if the
wiring is wrong:

```powershell
./scripts/lint.ps1
```

---

## The ModeContext API

### Sensing

| Member | Notes |
|---|---|
| `HealthPercent` | 0–100 integer |
| `WeaponRangeUnits` | Longest enabled armament range, world units |
| `DistanceFromAnchorUnits` | Distance from the unit's home position |
| `IsIdle`, `CanMove`, `HasWeapon` | Capability checks |
| `GroupId` | Synced control group, 0 if unassigned |
| `Anchor` | Home cell |
| `SenseThreats(radius)` | Visible enemies as `ThreatSnapshot`s, sorted by ActorID |
| `SenseStructures(radius)` | Visible enemy buildings |
| `CanAttack(actor)` | Do our weapons work against it? |
| `FindRepairBay()` | Nearest allied repairer, deterministic tie-break |
| `ResolveActor(id)` | ActorID back to a live actor, or null |
| `Random` | `World.SharedRandom`. The only legal RNG |
| `WorldTick` | Simulation tick counter |

`SenseThreats` returns a **reused buffer**. Consume it within the tick; do not store it.

### Acting

| Member | Notes |
|---|---|
| `Apply(decision)` | Preferred: turns a `UnitDecision` into engine calls |
| `Attack(target)` | Queues an attack activity directly |
| `MoveTo(cell)` | Plain move |
| `AttackMoveTo(cell)` | Advance while engaging targets of opportunity |
| `Stop()` | Cancel current activity |

Prefer `Apply`. Keeping every mode funnelled through one place means activity handling stays
consistent, and decisions remain inspectable data rather than scattered side effects.

---

## Performance

`OnTick` runs for every unit with a controller. In a 200-unit late game at `TickInterval: 8`,
that is roughly 625 evaluations per second.

- Evaluations are staggered by `ActorID`, so the cost spreads across ticks rather than spiking.
- Raise `TickInterval` for units that do not need fast reactions (artillery ships at 15).
- Keep `SenseThreats` radii tight; `FindActorsInCircle` cost scales with area.
- Cache expensive lookups in mode instance fields across ticks where the answer is stable.
- Avoid LINQ and allocation in `OnTick`; it is a hot path.

---

## Common mistakes

| Mistake | Consequence |
|---|---|
| `static` mutable field in a mode | Shared across all units and players |
| Issuing an `Order` from `OnTick` | Order stream flooded; not how traits work |
| Reading `world.LocalPlayer` / `Selection` | Instant desync |
| `float` in decision logic | Potential cross-platform desync |
| Retaining the `SenseThreats` list | Buffer is reused next tick |
| Untied tie-break in target selection | Rare, near-undebuggable desync |
| Calling `ctx.Stop()` unconditionally | Cancels legitimately running activities every tick |
