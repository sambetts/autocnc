# Reference doctrine

The module AutoC&C ships as an opponent and as a worked example. **Beating this is the goal.**

It declares everything about how its army fights, in
[`ReferenceDoctrine.cs`](ReferenceDoctrine.cs):

| | |
|---|---|
| Base plan | Power, refinery, barracks, war factory, defence, tech |
| Production | Early infantry and scouts, then tanks, then replacements forever |
| Behaviour | Defend by default, build with the MCV, train from production buildings, harvesters flee |
| Group 1 | Switches to attacking the enemy base |
| Group 2 | Escorts harvesters |

## Layout

```
Reference/
├── ReferenceDoctrine.cs   ← the entry point: plans and assignments
├── Modes/                     ← behaviours
│   ├── BuildBaseMode.cs       ← deploys the MCV, grows the base from ctx.BuildPlan
│   ├── TrainUnitsMode.cs      ← trains units from ctx.ProductionPlan
│   ├── DefensiveMode.cs       ← holds ground, won't be baited, retreats to repair
│   ├── AttackBaseMode.cs      ← pushes a base, never chases
│   ├── RunHomeMode.cs         ← flees to a refinery when threatened
│   ├── HarvesterEscortMode.cs ← guards a harvester
│   └── ScoutMode.cs           ← wanders, runs from anything armed
├── Logic/                     ← pure decision functions, no engine
└── Tests/                     ← fast tests, no game needed
```

## Start your own

```powershell
cp -r doctrines/Reference modules/MyModule
cd modules/MyModule
# rename the .csproj, .sln, and the IDoctrine class + its Name
dotnet build
```

Then in game: `/modules` to see it, `/module MyModule` to load it.

A module builds against AutoC&C **binaries**, so it can live in its own repository:

```powershell
dotnet build /p:AutoCnCPath=C:\games\autocnc
```

## Test without launching the game

```powershell
dotnet test Tests
```

The tests assert against the plan `ReferenceDoctrine` actually declares, so they verify the
real strategy rather than a copy that can drift out of date.

## Licence

GPL-3.0-or-later, like everything that links against OpenRA. See the repository `LICENSE` and
`NOTICE.md`. Modules you write and distribute inherit the same terms.