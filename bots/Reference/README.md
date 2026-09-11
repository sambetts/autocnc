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

## An army is a rate, and the rate is harvesters

A `proc` carries a `FreeActor` harvester and hands out exactly one, ever. No production plan used
to name `harv` at all, so this bot's entire income was one harvester per refinery — and that was
the whole game.

On badland-ridges it had **two** harvesters for the first seven minutes, three until 870s and four
after; its cash was 0 or 1 at 46 of the 55 assessments after 150s and never once exceeded 244
again. It was not short of judgement, it was short of money: between 1176s and 1485s it issued no
unit order at all, from two barracks and two airstrips, with nothing on the field. Its army value
never exceeded 7,800 in the entire match while the other side's went 7,250 at 900s to **70,200**,
and its buildings 13 to 38. It finished ahead on the trade — 110 kills to 106 losses — and lost by
four to one on production. When all four harvesters died between 1351s and 1365s the bot spent its
last 285 seconds with three refineries, fourteen buildings and no income at all, because nothing
it owned could build another one.

Two harvester steps are now in every plan, and a `harv` costs 1,100 against the refinery's 1,500
while needing only `proc` and a vehicle queue — so it is both the cheaper and the earlier way to
buy income. The airstrip was standing at 247s; the third free harvester did not arrive until 390s
and the fourth until 841s.

- **A floor of four**, above the first combat vehicle in every plan. A Tiberian Dawn refinery has
  one docking bay and comfortably feeds two harvesters, and this bot has two refineries by 117s.
  Income compounds for the rest of the match; a light tank does not.
- **A saturation target of six**, immediately above the endless combat step. Two per refinery for
  the three every plan builds — and because `Until(n)` counts what is *standing*, it doubles as
  the replacement rule the bot never had. A dead harvester now outranks the next tank.

The steps name `harv` and nothing else, for the same reason the anti-air steps name `e3` and
nothing else: `Until(n)` counts every candidate a step lists, so `["harv", "ltnk"]` would be
satisfied by tanks and buy no income at all. A test asserts it.

## A field is finite, and three refineries on one field is one field

The harvester floor held. On the second badland-ridges match the bot had five harvesters and
three refineries by 443s — against two harvesters for the first seven minutes last time — and all
five were alive and working until the first died at 838s.

It made no difference, because **it bought more harvesters for the same patch of tiberium.**

Cash was 0 at 30 of the 35 assessments sampled from 240s to the end, so what the bot spent is what
it earned. Pricing the build log gives the income curve directly:

| Window | Harvesters | Refineries | Income | Per harvester |
|---|---|---|---|---|
| 300–400s | 2 → 4 | 2 → 3 | 5,300 | ~1,900 |
| 400–500s | 4 → 5 | 3 | 4,600 | ~1,000 |
| 500–600s | 5 | 3 | 3,620 | 724 |
| 600–700s | 5 | 3 | 3,660 | 732 |
| 700–800s | 5 | 3 | 2,400 | 480 |

Credits per 100 game seconds. Income *fell by more than half* while the fleet more than doubled.
That is not a harvester shortage and it is not a losses problem — nothing was lost until 838s. It
is one field running out.

The round trip says the same thing from the other side. A `harv` moves 1.758 cells per game second
and carries about 700 credits, so 2,550 credits per 100s each over 200–300s is a load turned
around in roughly 27 seconds, and 640 each over 500–700s is roughly 92. The harvesters were
neither idle nor dead. They were walking.

**Every structure went in the same ring.** `ModeContext.FindBuildLocation(item, minRange = 2,
maxRange = 14)` looks in a 2–14 cell band around the base centre, and `BuildBaseMode` took that
default for everything in every plan. A Tiberian Dawn harvester works the closest tiberium to the
refinery it docks with, so all three refineries — 51s, 117s, 398s, all inside that one band —
shared a single harvesting footprint. The third one bought a docking bay and not one cell of new
ground.

[`Logic/BasePlacementLogic.cs`](Logic/BasePlacementLogic.cs) now decides *where*, not just what.
Refinery *n* is looked for in a ring pushed `RingStepCells` further out than refinery *n−1*: the
first stays home at 2–14, the second goes to 8–20, the third to 14–26. Power plants, production
and defences keep the default ring, because those want to be behind the front rather than beyond
it.

Two things keep it honest rather than clever:

- **The step is narrower than the band.** Six cells against a twelve-cell ring, so consecutive
  rings overlap. A Tiberian Dawn structure has to sit in buildable area, so a refinery that
  cannot reach the last one is a refinery that never gets placed. A test asserts the overlap.
- **The near edge is capped.** Without a ceiling the fourth refinery asks for open ground twenty
  cells out, finds nothing legal, and falls back — which is the old behaviour with extra steps.
  The far edge carries on growing; the near edge stops at sixteen.

It is a preference, not a demand. `BuildBaseMode` falls back to the default ring whenever nothing
in the outer one is legal, so a base hemmed in against a cliff still builds its refinery instead
of stalling with one paid for and nowhere to put it.

## A refinery is the only harvester a poor bot can buy

Both fixes above were real and neither one fired. On the third badland-ridges the bot had **two
harvesters for the entire 1,023-second match** and put a third refinery down at 860s.

Only three `harv` ever existed — at 51s, 117s and 860s — and each appeared *in the same second as
a `proc`*, so every one of them was a refinery's free actor. The bot produced none. Its `Vehicle`
queue received **four orders in the whole match**: `bggy` at 414s, 456s and 532s, then a single
`harv` at 576s that was still unpaid when the game ended 447 seconds later.

The reason is one line of ordering. `harv` costs 1,100 and needs a `weap`/`afld`; that factory
costs 2,000 and earns nothing. `proc` costs **1,500, needs only `anypower`, arrives with a
harvester attached, and comes from the `Building` queue the construction yard owns at second
zero.** The ladder bought the factory first:

| | Ordered | Stood | Cost | Building-queue rate |
|---|---|---|---|---|
| `proc` #1 | 14s | 51s | 1,500 | — |
| `proc` #2 | 80s | 117s | 1,500 | — |
| `hq` | 118s | 167s | 1,000 | ~20/s |
| `afld` | 168s | **414s** | 2,000 | **8.1/s** |
| `nuke` | 415s | 497s | 500 | 6.1/s |
| `proc` #3 | 498s | **860s** | 1,500 | **4.1/s** |

The third refinery was *ninth* in the plan. Cash read 0 from 150s to the end, so what the bot
spent is what it earned: roughly **12 credits a second, against the winner's 88**. Its army value
grew in a straight line — 1,300 at 120s, 2,400 at 180s, 3,600 at 360s, 5,700 at 600s — while
Cabal's compounded from 0 to 4,400 by 300s, 20,550 by 660s and 54,100 by 1,020s. The two curves
cross at about 270s and never come back. Twenty-six units died between 660s and 720s for two
kills, and the base was gone by 1,020s, but that was the funeral rather than the cause.

It also cost the rest of the game plan. The bot sat in `Scout` from 120s to 495s because it had
no vehicle to scout with, and never built a single tank.

So the ladder in [`Plans.cs`](Plans.cs) now buys income first, and buys enough of it:

- **Four refineries before any tech**, because four refineries hand out four harvesters and
  `HarvesterCore` is four. The floor the last round added could only ever be met through a factory
  the bot could not afford; it is now met by the one queue that exists at second zero.
- **Power, refinery, power, barracks, refinery, refinery costs 6,000 of a 7,500 opening bank**, so
  three harvesters are on the field before a credit has to be earned. A power rung and the fourth
  refinery take it to 8,000 — a short wait, not the 362-second project the third one became.
- **`hq` and `weap`/`afld` moved below it, not out of it.** Neither pays for itself, and both
  arrive *sooner in wall-clock* behind four refineries than they did in front of two, because the
  four refineries pay for them. Only the `Building` queue reads this list, so nothing here slows
  infantry production — that is a different plan and a different queue.
- **Saturation is now two per refinery** rather than a fixed six, so it cannot quietly stop being
  saturation as the base grows.

[`Logic/EconomyPlanLogic.cs`](Logic/EconomyPlanLogic.cs) is the rule written down. An ordering
mistake in a plan has no symptom — `BaseBuildLogic` walks the list top down and every rung looks
reasonable on its own — so the tests state it directly over the shipped plans: income reaches its
target before any vehicle factory, before any tech, for less than the opening bank, and a refinery
is cheaper than a factory plus a harvester. Every one of those assertions fails against the plan
this match was fought with.

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
│                                  (income leads tech — see EconomyPlanLogic)
├── Modes/                       ← behaviours
│   ├── BuildBaseMode.cs         ←   deploys the MCV, grows the base from ctx.BuildPlan
│   │                                (drives both the Building and Support queues,
│   │                                 and expands refineries outward as they multiply)
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
