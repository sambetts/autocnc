# Reference battle bot

The bot AutoC&C ships as an opponent and as a worked example. **Beating this is the goal.**

A **battle bot** owns several **doctrines** — complete ways of fighting — and decides which one
the match needs. This one has four, and moves between them as the battle turns.

| Doctrine | For | Switches to it when |
|---|---|---|
| `Opening` | An economy, and enough army not to die | Where every match starts, and where the others fall back to |
| `Scout` | Finding out where the enemy lives | Two refineries up and their base still unknown — or the push itself reporting there is nothing left to attack, or having seen nothing at all for five minutes, which both mean the base it knew is gone |
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

`AttackBaseMode` ends its own doctrine the same way, for the opposite reason. A push that has
levelled everything it remembers has *no* target, and forgetting the stale sighting is not enough
on its own: only a unit that can already see an enemy structure ever records a new one, so an army
left with nowhere to march stops moving, and a stopped army never sees anything to record. Both
halves of that loop are closed — the mode asks for `Scout` on arriving to find nothing, and the
bot's rule 4 reaches the same conclusion from `SecondsSinceContact` as a backstop. The threshold
sits above a real approach march (245s on badland-ridges) so it rescues a stalled push without
cancelling a marching one.

## A bot must not talk over its own modes

The host takes the bot's answer where it has one, and a mode's request only where it does not. So
**naming the doctrine that is already running is not a no-op** — it is the bot claiming an opinion,
and it silences every mode underneath. `Reassess` therefore returns `Continue` for "carry on"
rather than re-affirming the doctrine it is already in.

That distinction decided a match. `AttackBaseMode` asked for `Scout` — *"nothing left to attack"* —
on 82 separate assessments between 235s and 955s, and every one was discarded as `already-active`
because rule 6 kept answering *"army worth N and their base is known"*. The push had flattened
everything it could see by 480s and then stood in the corner of the map at (35–39, 69–73) for 230
seconds. From 595s their counter-attack walked into it and killed 71 units for **zero** kills in
return; the bot only escaped at 710s, once the army had bled below `AttackArmyValue` and rule 6
finally stopped firing. Rule 4 could not help, because its clock measures seeing *anything* rather
than having a target, and being shot at counts as contact.

Rule 5 had the same shape and now has the same fix. `EnemyBaseFound` never goes back to false, so
"stop scouting, we know where they live" was answered before the search began and ended it at the
host's 30-second minimum dwell. A search now runs for `ScoutHoldSeconds` before that rule is
allowed an opinion, which is also what lets `ScoutMode` end the search itself on actually seeing
something.

The other half is what a stranded unit *does*. `AttackBaseMode` deliberately holds its fire —
refusing to be baited off an objective is the whole point of it — but with no objective and
nowhere to march there is nothing left to be baited off, so `AttackBaseLogic` now engages anything
already inside weapon range instead of standing still. It still never leaves weapon range, so this
never becomes a chase.

`BuildBaseMode` drives **every** construction queue the yard owns, not just `Building`. In
Tiberian Dawn the economy and the tech come from `Building` while every defensive structure —
guard tower, turret, SAM site, obelisk — comes from `Support`. A yard that only drives `Building`
reads "four guard towers" as "nothing at all", because `BaseBuildLogic` skips any step it cannot
build: no order, no error, no tower. That is worth knowing before adding a step to a plan.

## An army that cannot touch a target class is not an army

Nothing this bot builds from the `Building` or `Vehicle` queues can shoot upwards. The
minigunner's rifle, the grenadier's grenade, the jeep's machine gun, the medium tank's cannon and
the guard tower's gun all declare `validTargets: Ground, Water`. Exactly two things it can reach
declare `Air`: the rocket soldier `e3`, and the `atwr`/`sam` pair.

It fielded neither, and that decided badland-ridges. It built 125 `e1`, 6 `e2`, 5 `jeep` and 4
`mtnk` — and **zero** `e3`. Aircraft killed 67 of the 159 units it lost, including 21 of the last
23; `msam`, `jeep` and `mtnk` took 81 more. Enemy infantry killed two. It finished 23 kills to 140
losses against a side it had outbuilt 102 units to 14 at 480s.

Three plan-level faults, all now fixed in [`Plans.cs`](Plans.cs):

- **`Until(n)` counts every candidate a step lists.** The only rocket step the bot had was
  `new("Infantry", ["e3", "e1"], 6)` in `DefenceTrain`, sitting under a step that had just bought
  ten `e1`. It was satisfied before it could fire and never bought a rocket. Anti-air steps now
  name `e3` and nothing else, and a test asserts no anti-air step can be satisfied by a
  ground-only unit.
- **Anti-air belonged to one doctrine.** `atwr`/`sam` were only in `DefenceBuild`, and Defence was
  not entered until 770s of a 995-second match. Static anti-air is now in a shared `HomeDefence`
  fragment that every plan includes.
- **The plans asked for units they had not unlocked.** `mtnk`, `ltnk`, `e2` and `atwr` all
  require `anyhq`, and no shared plan built one — the first `hq` landed at 445s under the Attack
  doctrine, so a 2,000-credit war factory built jeeps for five minutes. `hq` is now part of
  `Economy`, ahead of the war factory it unlocks.

The composition that replaces it is a small rifle core and then rockets. `e1` stays because it is
the cheapest body available and the best thing in a barracks against other infantry (150% versus
no armour, where the rocket manages 28%). Everything above the core is `e3`, because against the
armour classes that actually killed this bot the rocket is worth roughly four riflemen per credit
— 140% versus both Light and Heavy against the rifle's 40% and 10% — reaches six cells instead of
four, and needs nothing but the first barracks.

The vehicle steps also moved above the endless infantry step. Both the barracks and the war
factory ask the same plan what to build next and only the owner of the chosen queue acts, so an
endless infantry step on top hands nearly every evaluation to whichever queue is idle most often
— and a barracks turning out a 100-credit rifleman every three seconds is idle far more often
than a war factory. Two war factories produced ten vehicles in 900 seconds because of it.

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
