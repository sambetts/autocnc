# Determinism

> If you read one document in this repository, make it this one. Everything here is verified
> against OpenRA engine source, with file references you can check yourself.

OpenRA is a **lockstep** engine. Every client runs the identical simulation; only player input
travels over the network. If two clients ever compute different results, the match desyncs and
ends. Because AutoC&C moves unit decision-making into user-written code, mode authors are now
directly responsible for keeping the simulation deterministic.

---

## The core distinction: traits vs bots

This is the single most important thing to understand, and it is why AutoC&C's architecture
looks the way it does.

| | Per-unit trait (`ITick`) | Bot module (`IBotTick`) |
|---|---|---|
| Runs on | **Every client** | **Host only** |
| Already replicated? | Yes | No |
| How it acts | Queue activities directly | Must issue `Order`s |

Bot logic is gated on the host in `OpenRA.Game/Player.cs`:

```csharp
// Enable the bot logic on the host
if (IsBot && Game.IsHost)
{
    var logic = PlayerActor.TraitsImplementing<IBot>()
                           .FirstOrDefault(b => b.Info.Type == BotType);
    ...
    logic.Activate(this);
}
```

Because only one machine runs bot code, a bot's decisions are *external input* to the
simulation and must be broadcast as orders. That is where the widely-repeated rule "bots must
use `bot.QueueOrder()`, never `QueueActivity()`" comes from.

**That rule does not apply to us.** `ProgrammableController` is a normal trait, so its `Tick`
already runs on every client. The engine's own `AutoTarget` proves the pattern —
`OpenRA.Mods.Common/Traits/AutoTarget.cs`:

```csharp
void Attack(in Target target, bool allowMove)
{
    foreach (var ab in ActiveAttackBases)
        ab.AttackTarget(target, AttackSource.AutoTarget, false, allowMove);
}
```

No `Order` is constructed anywhere in that path. `AttackBase.AttackTarget` queues an activity
directly on the actor.

So: **modes queue activities; only player intent becomes an order.** Issuing orders from mode
logic would not just be unnecessary, it would flood the order stream with one order per unit
per tick.

---

## Rule 1 — Never use client-local state

This is the trap that will bite hardest, because the engine hands you client-local state that
looks perfectly usable.

OpenRA's built-in `ControlGroups` world trait
(`OpenRA.Mods.Common/Traits/World/ControlGroups.cs`) is **presentation state**:

```csharp
controlGroups[group].AddRange(world.Selection.Actors.Where(a => a.Owner == world.LocalPlayer));
...
cg.RemoveAll(a => a.Disposed || a.Owner != world.LocalPlayer);
```

`world.Selection` and `world.LocalPlayer` differ on every machine. A four-player game has four
different answers to "what is in control group 3". If unit behaviour read that, all four clients
would simulate a different battle from the first tick.

This is why AutoC&C ships its own `GroupManager` holding **synced** membership, mutated only
through orders, and treats the engine's `ControlGroups` as a UI affordance for *authoring* those
orders.

Never touch, from mode or controller tick code:

- `world.LocalPlayer`, `world.RenderPlayer`
- `world.Selection`
- `world.ControlGroups`
- `ScreenMap` (screen-pixel space, renderer only)
- `Game.Settings`, viewport, zoom, camera
- Anything named `Local*`

---

## Rule 2 — One random source only

`OpenRA.Game/World.cs`:

```csharp
SharedRandom = new MersenneTwister(orderManager.LobbyInfo.GlobalSettings.RandomSeed);
LocalRandom  = new MersenneTwister();
```

`SharedRandom` is seeded from the synced lobby seed, so every client produces the same sequence.
It is also folded into the sync hash:

```csharp
// Hash the shared random number generator.
ret += SharedRandom.Last;
```

`LocalRandom` is unseeded and per-machine — cosmetic effects only.

- ✅ `ctx.Random` (wraps `World.SharedRandom`)
- ❌ `World.LocalRandom`, `System.Random`, `Random.Shared`, `Guid.NewGuid()`

Note that consuming `SharedRandom` *advances* it. If one client draws and another does not, they
desync from that point. Never draw randomness inside a conditional that depends on client-local
state — which Rule 1 already forbids.

---

## Rule 3 — No floating point in simulation logic

Floating-point results can vary across architectures and JIT versions. OpenRA sidesteps this
entirely by representing world space in integers: `WDist`, `WPos`, `WVec` and `CPos`, where
`1024 units == 1 cell`, and `WAngle` with lookup tables instead of trigonometry.

`Sync.cs` enforces this for synced fields — only these types are hashable:

```
int2, CPos, CVec, WDist, WPos, WVec, WAngle, WRot, Actor, Player, Target, int, bool
```

anything else throws:

```csharp
else if (type != typeof(int))
    throw new NotImplementedException($"{nameof(VerifySyncAttribute)} on member of unhashable type: ...");
```

This is why `AutoCnC.Modes.Core` is integer-only throughout. Percentages are `int` (0–100),
distances are `int` world units. If you need a ratio, multiply before you divide.

---

## Rule 4 — No wall-clock time

- ❌ `DateTime.Now`, `Environment.TickCount`, `Stopwatch`, `Game.RunTime`
- ✅ `ctx.WorldTick` — the simulation tick counter, identical on every client

---

## Rule 5 — Break ties deterministically

Never let collection enumeration order decide anything observable. Two enemies at exactly the
same distance must resolve to the same target on every machine.

Both shipped decision functions tie-break on `ActorId`:

```csharp
if (score > bestScore || (score == bestScore && best.HasValue && t.ActorId < best.Value.ActorId))
```

`ModeContext.SenseThreats` additionally sorts snapshots by `ActorID` before handing them to
decision logic, so ordering is stable by construction rather than by assumption.

There is a regression test for exactly this
(`DefensiveLogicTests.TargetSelectionIsDeterministicForIdenticalThreats`): it runs the same two
threats in both orders and asserts the same target comes back.

---

## Catching desyncs early

Mark synced state with `[VerifySync]` (note: **not** `[Sync]` — that is a static helper class)
and implement the `ISync` marker interface. `ProgrammableController` does both:

```csharp
public class ProgrammableController : ConditionalTrait<ProgrammableControllerInfo>,
    ITick, INotifyDamage, IResolveOrder, ..., ISync
{
    [VerifySync] public int GroupId { get; private set; }
    [VerifySync] public CPos Anchor { get; private set; }
    [VerifySync] public int ActiveModeHash => StableHash(activeModeName);
}
```

`ActiveModeHash` is FNV-1a rather than `string.GetHashCode()`, because .NET randomises string
hashing per process — `GetHashCode` would report a false desync on every single run.

With these in place, a divergence shows up in OpenRA's sync report naming the exact trait and
field, instead of as an unexplained "out of sync" several minutes later.

To test in practice: run a multiplayer match against yourself on two clients on one machine, or
enable `Server.EnableSyncReports` on a dedicated server.

---

## Checklist before merging a mode

- [ ] No `float`, `double` or `decimal` anywhere in the decision path
- [ ] No `System.Random`; randomness only via `ctx.Random`
- [ ] No `DateTime`, `Stopwatch` or `Game.RunTime`
- [ ] No access to `LocalPlayer`, `Selection`, `RenderPlayer` or `ScreenMap`
- [ ] No `Order` constructed inside mode logic
- [ ] Every selection over a collection has an explicit, total tie-break
- [ ] Mutable per-unit state lives in the mode instance, never in a `static` field
- [ ] A test asserts identical output for reordered inputs
