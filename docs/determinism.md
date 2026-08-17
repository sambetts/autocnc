# Where your code runs (and why it can't desync the game)

The short version: **mode code is not part of the simulation.** You can use floats, LINQ,
`System.Random`, `DateTime` — anything. Your opponent never runs your code, and you never run
theirs.

This document explains why that's true, because it is the decision the whole project turns on.

---

## The problem

OpenRA is a **lockstep** engine. Every client runs an identical simulation and only *player
input* crosses the network. If two clients ever compute different results, the match desyncs and
dies.

That is fine while behaviour ships with the mod. It falls apart the moment players write their
own:

> You write `RunHomeMode` and assign it to your harvesters. Your opponent doesn't have that code.
> If harvester behaviour were part of the simulation, their client could not reproduce your
> harvesters' movements — desync on the first tick.

Distributing your code to your opponent would fix the desync and introduce a worse problem:
their machine would be executing arbitrary C# written by a stranger.

---

## The solution: modes emit orders, they don't move units

Mode code runs **outside the simulation**, on the owning player's client only. Its only output is
an `Order` — the same channel a human player's mouse clicks travel down.

```
Your client                          Network            Every client
───────────                          ───────            ────────────
ModeExecutor.Tick
  └─ your mode's OnTick
       └─ returns UnitDecision
            └─ ModeContext.BuildOrder
                 └─ world.IssueOrder(Order)  ──────►  synced  ──────►  simulation
```

The simulation only ever sees orders. It has no idea a mode produced them, and cannot tell them
apart from a human clicking.

This is precisely how OpenRA's own bots work. Bot logic runs only on the host
(`if (IsBot && Game.IsHost)` in `OpenRA.Game/Player.cs`) and issues orders via
`bot.QueueOrder(...)` for exactly the same reason.

### What this buys

| | Result |
|---|---|
| Custom modes online | Work against anyone; opponents need none of your code |
| Security | You only ever execute code you wrote |
| Determinism rules | Don't apply to mode authors |
| Replays | Orders are already recorded, so replays work unchanged |
| A mode that crashes | Dropped for that unit, error printed to chat; the game survives |

### What it costs

**Order latency.** A decision takes a few ticks to arrive, typically ~120ms. That is the same
latency a human player's clicks already have, so it is fair rather than merely tolerable — but
it does rule out frame-perfect micro.

**Order volume.** One order per unit per decision would swamp the network. Two mitigations:

- `ModeExecutor` only emits an order when the decision's **intent changes**
  (`UnitDecision.SameIntent`), or when a unit has gone idle and still wants something. A mode
  returning the same decision every tick costs nothing.
- Evaluations are staggered per unit by `TickInterval`, and capped at `MaxOrdersPerTick`.

---

## What this means when writing a mode

**Allowed, despite what you may expect from OpenRA modding:**

- `float`, `double`, `decimal`
- `System.Random`, `Random.Shared`
- LINQ, allocation, unordered dictionary iteration
- `DateTime.Now`, `Stopwatch`
- `world.LocalPlayer`, `world.Selection` — you *are* the local player

**Still worth avoiding, for ordinary reasons rather than desync ones:**

- `static` mutable fields on a mode. One instance is created per unit, so a static field is
  shared by every unit and every mode instance. Almost never what you want.
- Heavy work in `OnTick`. It runs for every unit you own; keep sense radii tight.
- Cheating with fog. `SenseThreats` already filters through `CanBeViewedByPlayer`, so don't go
  around it via raw world queries unless you mean to.

---

## Where determinism still matters

Two narrow places.

**1. The engine's simulation, which you don't touch.** Traits in `AutoCnC.Mod/Traits/` run on
every client. `ProgrammableController` therefore holds *no* synced state — no `[VerifySync]`
fields — because everything it stores (assigned mode, group, anchor) is client-local policy.
Two clients disagreeing about a unit's mode changes only which orders each player's own client
chooses to emit.

**2. `AutoCnC.Modes.Core`, for testability rather than sync.** The pure decision layer is kept
integer-only and side-effect free so it can be tested without an engine, and so identical inputs
always produce identical outputs. That is a *testing* property now, not a networking one.

The tie-break tests (`TargetSelectionIsDeterministicForIdenticalThreats`) still earn their place:
a mode that picks targets by dictionary iteration order will jitter between targets frame to
frame and fight badly. Same discipline, different reason.

---

## Control groups

Because mode assignment is client-local policy, AutoC&C uses OpenRA's **built-in**
`ControlGroups` trait directly.

That trait is client-local by construction — it reads `world.Selection` and filters on
`world.LocalPlayer`:

```csharp
// engine/OpenRA.Mods.Common/Traits/World/ControlGroups.cs
controlGroups[group].AddRange(world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer));
```

Under an in-simulation design that would have been fatal, and an earlier revision of this project
carried a whole synced `GroupManager` world trait to work around it. Moving modes out of the
simulation deleted that problem outright: local policy may safely read local state.

---

## If you ever move logic back into the simulation

Should a future mode ship as a genuine engine trait for latency reasons, the old rules return in
full: integer-only maths, `World.SharedRandom` only, no wall-clock time, no `LocalPlayer` or
`Selection`, `[VerifySync]` on synced fields, and explicit tie-breaks on `ActorID`.

The safe default is to leave modes where they are.
