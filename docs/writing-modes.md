# Writing a mode

A mode is a C# class that decides what one unit should do. Modes live in `player-modes/`, which
is a normal C# project — open it in your IDE and you get IntelliSense, refactoring and a
debugger.

Before anything else, the one rule that matters: **your code runs on your machine only and its
output is orders.** Floats, LINQ, `System.Random` are all fine. See
[determinism.md](determinism.md).

---

## The smallest possible mode

```csharp
using AutoCnC.Mod.Modes;
using AutoCnC.Modes.Core;
using OpenRA;

namespace PlayerModes
{
	public sealed class StandStillMode : UnitMode
	{
		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			return UnitDecision.Hold("staying put");
		}
	}
}
```

Drop that in `player-modes/`, run `./scripts/build.ps1`, restart, and `/mode StandStillMode`
works. The class name *is* the mode name. There is no registration step: discovery uses OpenRA's
own `ObjectCreator`, the same reflection that binds YAML trait names to `TraitInfo` classes.

---

## The lifecycle

| Member | When it runs |
|---|---|
| `OnEnter` | Once, when a unit switches into this mode |
| `OnTick` | Every `TickInterval` ticks; returns a `UnitDecision` |
| `OnDamaged` | Immediately on taking damage, between evaluations |
| `OnExit` | Once, when the unit leaves this mode |

**One instance per unit.** A fresh object is constructed on entry and dropped on exit, so
instance fields are safe per-unit memory. `static` mutable fields are shared across every unit —
almost never what you want.

---

## Returning decisions

`OnTick` returns data, it does not act. The executor turns the decision into an order, and
**only when the intent changes**:

```csharp
UnitDecision.Continue                      // leave the unit alone
UnitDecision.Hold(reason)                  // stop
UnitDecision.Attack(actorId, reason)
UnitDecision.MoveTo(x, y, reason)
UnitDecision.AttackMoveTo(x, y, reason)
UnitDecision.ReturnToAnchor(reason)
UnitDecision.Retreat(reason)               // nearest repair bay
UnitDecision.AdvanceToObjective(id, reason)
```

Two consequences worth internalising:

**`Continue` means "don't interfere".** This is what lets `RunHomeMode` sit on a harvester
without stopping it harvesting. Only return an action when you actually want to override what
the unit is doing.

**Returning the same decision every tick is free.** Don't hand-roll rate limiting; the executor
already suppresses duplicates.

---

## Sense → decide → act

For anything beyond a few lines, put the judgement in a **pure function** in
`src/AutoCnC.Modes.Core`, which has no OpenRA reference at all:

```csharp
public override UnitDecision OnTick(Actor self, ModeContext ctx)
{
	var state = new DefensiveState(               // sense
		HealthPercent: ctx.HealthPercent,
		Threats: ctx.SenseThreats(radius),
		/* ... */);

	return DefensiveLogic.Decide(state, tuning);  // decide (pure, testable)
}
```

Then test the behaviour in milliseconds, with no engine and no game:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests
```

```csharp
[Test]
public void RetreatsWhenBadlyHurt()
{
	var state = new DefensiveState(20, 0, 4096, true, true, true, true, []);
	Assert.That(DefensiveLogic.Decide(state, DefensiveTuning.Default).Action,
		Is.EqualTo(UnitAction.Retreat));
}
```

`DefensiveLogic` and `AttackBaseLogic` are the worked examples. This split is why the project
keeps a separate assembly with no engine reference — a folder convention is a comment, a missing
assembly reference is a compiler error.

---

## The ModeContext API

### Sensing

| Member | Notes |
|---|---|
| `HealthPercent` | 0–100 |
| `WeaponRangeUnits` | Longest enabled armament range, world units (1024 = 1 cell) |
| `DistanceFromAnchorUnits` | Distance from home |
| `DistanceTo(actor)` / `DistanceTo(cell)` | Convenience |
| `IsIdle`, `CanMove`, `HasWeapon` | Capability checks |
| `GroupId` | Control group 1–9, or 0 |
| `Anchor` | Home cell; assignable |
| `SenseThreats(radius)` | Visible enemies as `ThreatSnapshot`s |
| `SenseStructures(radius)` | Visible enemy buildings |
| `SenseAllies(radius, type)` | Friendly actors, optionally by actor type |
| `CanAttack(actor)` | Do our weapons work against it? |
| `FindRepairBay()` / `FindRefinery()` | Nearest allied |
| `FindNearestAllied<T>()` | Nearest allied actor with trait `T` |
| `ResolveActor(id)` | ActorID back to a live actor, or null |
| `Self`, `World`, `Owner`, `WorldTick`, `Random` | Raw access when you need it |

`SenseThreats` returns a **reused buffer** — consume it within the tick, don't store it.
Sensing respects fog: cloaked and shrouded actors are filtered out.

### Acting

You don't call actuators; you return a decision. To nudge timing:

| Member | Notes |
|---|---|
| `RequestReevaluation()` | Evaluate next tick and re-send even if unchanged. Useful from `OnDamaged`. |

---

## Assigning modes in-game

```
/modes                          what's loaded
/mode ScoutMode                 current selection
/mode all DefensiveMode         everything
/mode type harv RunHomeMode     every harvester
/mode group 1 AttackBaseMode    control group 1
/mode clear                     drop per-unit overrides
/assignments                    what's currently assigned
/whatmode                       what the selection is running
```

Precedence is most-specific-wins: **unit selection > group > unit type > all**. So
`/mode all DefensiveMode` then `/mode type harv RunHomeMode` does what you'd expect, and neither
clobbers the other. The rules are tested in `ModeAssignmentsTests`.

---

## Performance

`OnTick` runs for every unit you own. At `TickInterval: 8` with 200 units that's ~625
evaluations a second.

- Evaluations are staggered per unit, so cost spreads across ticks.
- Raise `TickInterval` in `mods/autocnc/rules/units.yaml` for units that needn't react fast.
- Keep `SenseThreats` radii tight — `FindActorsInCircle` cost scales with area.
- Cache stable lookups in instance fields between ticks.

---

## Common mistakes

| Mistake | Consequence |
|---|---|
| `static` mutable field | Shared across every unit |
| Returning `Hold` when you meant `Continue` | Stops the unit doing anything useful, e.g. harvesting |
| Retaining the `SenseThreats` list | Buffer is reused next tick |
| Re-picking a target every tick | Unit dithers; make selections sticky |
| Expensive work with a huge sense radius | Frame drops in late game |
| Assuming an order applied instantly | Orders take a few ticks; check state, don't assume |
