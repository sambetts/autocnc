# Your modes

Every `.cs` file here is compiled into `PlayerModes.dll` and picked up by the game
automatically. There is no registration step — add a class, rebuild, launch.

**Write your modes before the match, then play it.** Your army's behaviour is a loadout you
commit to; the match is the test of what you wrote. Switching between modes you've already
written is live and instant in-game — it's only loading freshly *edited* code that needs a
relaunch.

## Write one

1. Copy a template, rename the class. The class name *is* the mode name in-game.
2. Implement `OnTick`, returning a `UnitDecision`.
3. Check your logic without launching anything:

   ```powershell
   dotnet test src/AutoCnC.Modes.Core.Tests
   ```

4. Build and play:

   ```powershell
   ./scripts/build.ps1
   ./scripts/launch.ps1
   ```

5. In-game, press `Enter` and assign it:

   ```
   /modes                          list what's loaded
   /mode MyMode                    the units you have selected
   /mode all MyMode                everything
   /mode type harv RunHomeMode     every harvester
   /mode group 1 AttackBaseMode    control group 1
   ```

Most specific wins: per-unit selection beats group, which beats unit type, which beats `all`.

## Templates

| File | What it shows |
|---|---|
| `RunHomeMode.cs` | The simplest useful mode. Flees to a refinery when threatened. |
| `ScoutMode.cs` | Per-unit state in instance fields, reacting via `OnDamaged`. |
| `HarvesterEscortMode.cs` | Tracking another actor and defending it. |

## Things worth knowing

**Your code is not in the simulation.** It runs on your machine only and its output is orders,
the same channel your mouse clicks use. So `float`, LINQ, `System.Random` and `DateTime` are all
fine here — none of it can desync a match, and your opponent never runs your code.

**Return `UnitDecision.Continue` to mean "leave the unit alone."** That is what lets
`RunHomeMode` sit on a harvester without stopping it harvesting. Only return an action when you
actually want to override what the unit is doing.

**Orders are only sent when your decision changes.** Returning the same decision every tick is
cheap, so don't try to be clever about rate-limiting yourself.

**One mode instance per unit**, created on entry and dropped on exit. Instance fields are safe
per-unit memory. A `static` mutable field would be shared by every unit — almost never what you
want.

**A mode that throws is dropped** for that unit, with the error printed to chat and `debug.log`.
It won't crash the game.

## Testing without launching the game

Put real judgement in a pure function in `src/AutoCnC.Modes.Core` and call it from `OnTick`.
Then you can test it in milliseconds:

```powershell
dotnet test src/AutoCnC.Modes.Core.Tests
```

`DefensiveLogic` and `AttackBaseLogic` are the worked examples. See `docs/writing-modes.md`.
