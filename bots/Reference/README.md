# Reference battle bot

The bot AutoC&C ships as an opponent and as a worked example. **Beating this is the goal.**

A **battle bot** owns several **doctrines** — complete ways of fighting — and decides which one
the match needs. This one has four, and moves between them as the battle turns.

| Doctrine | For | Switches to it when |
|---|---|---|
| `Opening` | An economy, and enough army not to die | Where every match starts, and where the others fall back to |
| `Scout` | Finding out where the enemy lives | Two refineries up and their base still unknown |
| `Defence` | Static defence, cheap bodies, everything home | A building is lost, or enough enemies reach the base — three for a side with nothing to spend, and a quarter of our own unit count for one that has an army and a target |
| `Attack` | Tech, more production, the whole army pushes | Army worth 6000 and their base is known, including straight out of a siege that has lifted |

The rules are in [`Logic/ReferenceBotLogic.cs`](Logic/ReferenceBotLogic.cs) — a pure function of
`BattleState`, so the interesting half of the bot is tested without a game running. The wiring is
in [`ReferenceBot.cs`](ReferenceBot.cs).

`ScoutMode` ends its own doctrine: the moment it sees an enemy structure it calls
`ctx.SwitchDoctrine`, rather than waiting for the bot's next assessment to notice. It also records
*where*, because sensing only ever returns what is visible right now: an army starting a push from
its own base can see nothing at all, so `AttackBaseMode` marches on the last remembered sighting
until a real objective comes into view.

`BuildBaseMode` drives **every** construction queue the yard owns, not just `Building`. In
Tiberian Dawn the economy and the tech come from `Building` while every defensive structure —
guard tower, turret, SAM site, obelisk — comes from `Support`. A yard that only drives `Building`
reads "four guard towers" as "nothing at all", because `BaseBuildLogic` skips any step it cannot
build: no order, no error, no tower. That is worth knowing before adding a step to a plan.

## Layout

```
Reference/
├── ReferenceBot.cs              ← the bot: which doctrines, and Reassess
├── Doctrines/                   ← four ways of fighting, all sharing the modes below
│   ├── ReferenceDoctrineBase.cs ←   the wiring every doctrine needs
│   ├── OpeningDoctrine.cs
│   ├── ScoutDoctrine.cs
│   ├── DefenceDoctrine.cs
│   └── AttackDoctrine.cs
├── Plans.cs                     ← what each doctrine builds and trains, as plain data
├── Modes/                       ← behaviours
│   ├── BuildBaseMode.cs         ←   deploys the MCV, grows the base from ctx.BuildPlan
│   │                                (drives both the Building and Support queues)
│   ├── TrainUnitsMode.cs        ←   trains units from ctx.ProductionPlan
│   ├── DefensiveMode.cs         ←   holds ground, won't be baited, retreats to repair
│   ├── AttackBaseMode.cs        ←   pushes a base, never chases
│   ├── EnemyBaseSightings.cs    ←   where this side last saw their base
│   ├── RunHomeMode.cs           ←   flees to a refinery when threatened
│   ├── HarvesterEscortMode.cs   ←   guards a harvester
│   └── ScoutMode.cs             ←   wanders, runs from anything armed
├── Logic/                       ← pure decision functions, no engine
└── Tests/                       ← fast tests, no game needed
```

## Start your own

```powershell
./scripts/new-bot.ps1 -Name MyBot
cd bots/MyBot
dotnet test .\Tests\MyBot.Tests.csproj
```

Then in game: `/bots` to see it, `/bot MyBot` to load it, `/why` to ask what it is thinking.

A bot builds against AutoC&C **binaries**, so it can live in its own repository:

```powershell
dotnet build /p:AutoCnCPath=C:\games\autocnc
```

## Test without launching the game

```powershell
dotnet test Tests
```

The tests assert against the plans the doctrines actually declare and the rules `Reassess`
actually runs, so they verify the real strategy rather than a copy that can drift out of date.

## Licence

GPL-3.0-or-later, like everything that links against OpenRA. See the repository `LICENSE` and
`NOTICE.md`. Bots you write and distribute inherit the same terms.
