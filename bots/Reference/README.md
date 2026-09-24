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
`BattleState`, so the interesting half of the bot can be read and reasoned about without a game
running. The wiring is in [`ReferenceBot.cs`](ReferenceBot.cs).

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

A push also **forms up before it goes in**. Units in this mod run from 0.952 cells a second to
4.15, so sending each one at the enemy the instant the doctrine flips is not one attack but one per
speed class. `AttackBaseMode` gathers the army 14 cells short of the remembered sighting — outside
every static defence in the ruleset — and releases it when the crowd around it stops growing, or
when a hard cap says the rest are not coming. It walks there under an **attack-move**, and stops to
fight anything that comes inside its own weapon range on the way, because the march to the staging
point is the longest leg of the push and the ground it crosses is not safe.
See [`Logic/AssaultStagingLogic.cs`](Logic/AssaultStagingLogic.cs).

## A won game has to be finished

A match only ends when the loser has nothing left, and this bot could not finish one. Once
their main base fell and nothing more was in sight, rule 4 of `ReferenceBotLogic` switched to
Scout ("nothing of theirs seen for 300s") and then re-affirmed Scout on every assessment. That
held the doctrine there for the rest of the match. The Scout doctrine's army guards home, so the
whole army stood there ("on post, no threats", around 40,000 evaluations in the last 400 seconds)
while one or two scouts walked the map's rim.

In the hard-16-9 evaluation at 51e0e4c, three benchmark games timed out at 2,400 s this way, two
for a candidate and one for the champion itself. Each ended with 380 to 700 of our units, 76,000 to
131,000 of army value, against an enemy with no army and one or two buildings nobody found. A
timed-out benchmark game voids its pair, and three voided pairs stopped training outright.

[`Modes/ArmyHunt.cs`](Modes/ArmyHunt.cs) and [`Logic/HuntLogic.cs`](Logic/HuntLogic.cs) now make
the Scout doctrine's army hunt once it is worth 10,000 or more. The map is split into sectors of
12 cells, and each unit walks them on an attack-move from a sector picked by its actor id, so the
army spreads out instead of marching as one column (`hunt.sweep`). A hunter that sees a structure
records it for the whole side, asks for Attack (`hunt.found`) and hits it (`hunt.attack`). Rule 5
now sends a side that is ready to push straight to Attack when scouting has found their base,
rather than home through Opening (`doctrine.scout-found-push`).

The hunt is part of `DefensiveMode`, not a mode of its own. The platform keeps a unit's mode
instance only while its mode type stays the same, so a separate hunt mode reset every defender's
state at each doctrine switch. That changed the opening of games that never hunted, and two of
them flipped from wins to losses.

Measured on hard-16-9 against 51e0e4c: 6 wins to 5, with no timeouts. The five games that never
hunt are identical to the second. Two wins came sooner (1,103 s against 1,215 s, and 999 s
against 1,074 s), and seed 300008 went from a 2,400 s timeout to a win at 1,007 s. The paired gate
scores this a tie because it drops the pair the control timed out, and a shorter win scores
slightly less survival. It was adopted by hand for that reason.

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
  name `e3` and nothing else, so a ground-only unit can never satisfy one.
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
satisfied by tanks and buy no income at all.

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
  cannot reach the last one is a refinery that never gets placed.
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
reasonable on its own — so the rule is stated directly over the shipped plans: income reaches its
target before any vehicle factory, before any tech, for less than the opening bank, and a refinery
is cheaper than a factory plus a harvester. The plan this match was fought with breaks every one
of those.

## A ring is an ambition; a ladder is what you can reach

The refinery ring went in and did nothing. Worse than nothing.

On the fourth badland-ridges the income ladder worked exactly as designed — four refineries by
237s (51s, 117s, 178s, 237s) against the third one landing at 860s last time, and four harvesters
on the field by 237s. Every economic fix held. The bot still lost, army 0 to 68,160, because
**all four refineries went into the same 7-cell circle.**

Here is the entire base, every structure it built in 1,519 seconds, as distance in cells from the
construction yard at (82, 13):

| | `fact` | `nuke` | `proc` | `nuke` | `hand` | `proc` | `proc` | `nuke` | `proc` | `hq` | `afld` | `nuke` | `afld` | `hand` | `nuke` |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **at** | 0s | 13s | 51s | 65s | 79s | 117s | 178s | 193s | 237s | 272s | 323s | 343s | 600s | 768s | 892s |
| **cells** | 0.0 | 2.0 | **7.1** | 3.0 | 3.0 | **3.0** | **4.2** | 4.0 | **5.0** | 4.2 | 5.0 | 5.4 | 6.1 | 5.8 | 5.8 |

Nothing ever exceeded 7.07 cells. The four refineries are in bold, and the shape of the failure is
in those four numbers: **the only refinery that landed far out was the first one — the one that
used the plain default ring.** The three that asked for an expanded ring landed at 3.0, 4.2 and
5.0, every one of them *closer to home than the refinery that asked for nothing.*

That is not the expansion failing to help. That is the expansion actively hurting, and the reason
is the fallback. `BuildBaseMode` asked `FindBuildLocation(item, 8, 20)`, got nothing, and then ran
`FindBuildLocation(item)` from scratch — which searches from 2 cells outward and returns the first
legal cell it finds, which is near. One far request and then home, with no middle.

The income curve is the bill. Cash was 0 or 1 at every assessment from 300s on, so what the bot
spent is what it earned; pricing the build log and excluding the free actors (`c17` deliveries and
the harvester a `proc` hands out) gives income directly, against a fleet pinned at four harvesters
with none lost before 960s:

| Window | Harvesters | Income | Per harvester |
|---|---|---|---|
| 200–300s | 4 | 5,800 | 1,450 |
| 300–400s | 4 | 5,800 | 1,450 |
| 400–500s | 4 | 2,400 | 600 |
| 500–600s | 4 | 1,750 | **438** |
| 700–800s | 4 | 2,550 | 638 |

Credits per 100 game seconds. A 3.3-fold collapse with the fleet constant and nothing lost — the
harvesters were neither idle nor dead, they were walking further every trip because four docking
bays stood on one patch of tiberium. Cabal's army went 5,660 at 360s to 55,800 at 1,440s while
this bot's peaked at 9,750 at 420s and never beat it. The trade was almost even, 78 kills to 92
losses. It lost on income alone.

Two things were wrong, and they are one fix.

**A ring is an ambition, and a ladder is what is reachable.** Instead of one far request and then
the default, [`BasePlacementLogic.Ladder`](Logic/BasePlacementLogic.cs) walks the near edge inward
in `LadderStepCells` steps and leaves the far edge where the ambition put it: refinery two asks
8–20, then 6–20, then 4–20, then the default. Each rung *contains* the one before it, so the first
rung that matches anything is the furthest-out band this base can legally build in — which is the
question worth asking. The step is two cells rather than six, and that inequality is the mechanism:
a ladder descending a whole ring per rung would have exactly two rungs and no middle, which is the
behaviour being replaced. `LadderStepCells` is therefore deliberately smaller than `RingStepCells`,
which is what makes every ring a shipped plan can ask for yield at least three rungs. The last rung
is always the default ring, so this can only ever do better than what it replaces — the rungs in
between are extra chances and the fallback is unchanged.

**Only some structures can move the frontier at all.** A Tiberian Dawn cell is buildable when it
is close enough to a structure carrying `GivesBuildableArea`. Of everything this bot builds,
`silo`, `gtwr`, `gun`, `atwr` and `sam` carry `RequiresBuildableArea` and do **not** give it — so
no quantity of cheap towers, and no number of 100-credit silos, ever extends the base by one cell.
Only `fact`, `proc`, `nuke`, `pyle`/`hand`, `hq` and `weap`/`afld` do. At 500 credits the power
plant is the cheapest of those, against 1,500 for a refinery and 2,000 for a factory, and every
plan here already builds four or five. So power plants now lead the expansion and the refineries
follow into the ground they opened. The five this bot built stood at 2.0, 3.0, 4.0, 5.4 and 5.8
cells — every one of them behind refinery number one, none of them buying an inch of new ground.

Expansion is therefore a list of roles rather than a list of actors, because the two roles have to
be counted separately: a power plant going up must not advance the refinery ring, or they leapfrog
each other into ground neither can reach. `ReferencePlans.ExpandingRoles` names both, with how
many of each stay home first — one refinery, because the first is the whole economy and wants the
starting field, and two power plants, because the first two go up before there is anywhere to
expand to and a base whose every plant is on the frontier browns out the moment the frontier is
raided.

## You cannot catch an aeroplane

The ladder worked. On the fifth badland-ridges every structure that was meant to move outward
moved: refineries landed at 7.1, 13.4, 13.4 and 11.1 cells from the yard at (13, 82), and the
power plants that opened that ground at 2.0, 7.1, 13.5 and 7.1 — against a base where nothing had
ever exceeded 7.07 cells. Per-harvester income held up better for it, 733 credits per 100 seconds
at 550s against 438 the round before. Anti-air held too: 22 `e3` and a `sam`, and the rocket
soldiers killed two `orca` at 577s and 613s. Nothing that had been fixed came undone.

The bot lost anyway, and the reason is four decisions taken in the same second.

At **544s, twenty-one of its twenty-two rocket soldiers — the entire army, 6,300 credits — were
each ordered to attack the same aircraft, actor 482, at distances of 8,602 to 12,440 world
units.** An `e3` has one armament: `Rockets`, reaching **6,144 units**. Every one of those orders
was an order to walk. By 556s the same twenty-one were reporting the same target at 4,468–8,335
units: they had crossed four cells of open ground north-east of the barracks, and the ground they
stopped on was tiberium.

`e1`, `e2`, `e3` and `e6` all carry `DamagedByTerrain`. The battle log records it happening in
plain arithmetic — `damage=200 health=95`, then `damage=1600 hits=8 health=60`, then `health=24`,
then nothing:

| | 564s | 566s | 572s | 581s | 586s | 594–604s |
|---|---|---|---|---|---|---|
| `e3` lost to `world` | 1 | 3 | 1 | 2 | 3 | 5 |

**Fifteen rocket soldiers, 4,500 credits, killed by the map in forty seconds, at (19–21, 76–79),
with no enemy involved.** That is 38% of every unit this bot lost all match and **56% of the
8,100 credits it ever spent on an army** — and the bot's entire lifetime income was about 14,000.
Cabal lost nothing to it.

Everything after that is the funeral. The army stood at 33 units and 7,500 at 540s; it was 24 and
4,800 at 600s and 3 and 300 at 660s. Cabal walked in at 585s (`6_enemy_at_the_base`), killed three
harvesters between 600s and 660s, and the fleet went four to one. Income stopped, and from 604s
the bot completed exactly one more building in six minutes. From 780s `orca` and `a10` took the
base apart unopposed — `hand`, `nuke`, `gtwr`, `afld`, `hq`, `fact` and three `proc` — which is
what "no air defence" looks like from the outside. The air defence had been built. It was lying in
a tiberium field.

**An aircraft cannot be caught, so walking at one is movement that can never end in a shot.**
`orca` moves 4.541 cells per game second and `a10` 9.106, against `e3` at 0.952, `e1` at 1.318 and
the fastest thing this bot can field, `bggy`, at 4.15. There is no ground unit in the ruleset that
can close on an aircraft. By the time the soldier arrives the aircraft is elsewhere and the
soldier is standing wherever it used to be.

`DefensiveLogic.SelectTarget` had no idea. Its only distance rule was the leash, and
`DefensiveMode` scales that off the unit's own reach — `LeashRadiusUnits = range * 3`, so a 6-cell
rocket carries an 18-cell leash. An aircraft twelve cells away is comfortably inside it. Across
the match **62 aircraft engagements were ordered and 43 of them, 69%, were beyond the firing
unit's own weapon range**; 59 of the 62 fell in the single minute after 540s and 42 named actor
482.

Two lines of the same rule now:

- **An aircraft outside weapon range is not a target at all.** Not scored low — filtered, like a
  threat beyond the leash, because the cost is the same and it is movement rather than a shot. It
  degrades gracefully by construction: the unit stays on its anchor and still fires at anything
  overhead, and it loses nothing by waiting, because an `orca` has to close to 4.75 cells to
  attack and the rocket reaches 6. The defender wins that race by standing still.
- **An aircraft inside weapon range is now the top class, at 2,000 against the vehicle's 1,200.**
  The old table had it at 800, below infantry — exactly backwards. The shot is scarce, because
  almost nothing on this side can shoot upwards at all, and it is perishable, because the target
  crosses the envelope in a second or two while a tank will still be there next evaluation.

The rule has a deliberate boundary. A *ground* target at that same 8,602 units must still be
engaged, so this stays a statement about what can be caught rather than a quiet shrinking of the
leash.

## A rule that can stop a harvester and cannot start one

Everything above held. On the sixth badland-ridges no unit walked at an aircraft — all ten aircraft
engagements were inside the firing unit's own range — and losses to `world` fell from fifteen to
three. The refinery ladder put `proc` at 7.07, 13.42, 13.42 and 11.05 cells from the yard at
(13, 82). `sam` went up at 253s and fifteen `e3` were trained. The bot then killed **337** units
and lost **29**, against a winner that killed 34 and lost 369.

It lost anyway, four buildings to forty-four, because it stopped earning money at **358 seconds**.

| | first 358s | remaining 1,705s |
|---|---|---|
| credits spent, free actors excluded | 12,850 | 2,100 |
| income | **15.0/s** | **1.23/s** |
| share of everything it ever spent | 86% | 14% |
| share of the match | 17% | 83% |

Its whole lifetime was 14,950 credits against a 7,500 opening bank — 7,450 earned in 2,063
seconds, 3.6 a second. The winner put (76,520 army + 18,166 cash − 7,500) / 2,063 = **42 a second**
on the board without counting its forty-four buildings or the 369 units it lost. The bot won the
fight ten to one and lost the match on income.

**Six orders caused it, and they are every order any harvester received all match** — six of 4,290
unit decisions, 0.14%:

| second | harvester | order |
|---|---|---|
| 358 | `harv` 347 | `MoveTo` — *enemy nearby, running home* |
| 361 | `harv` 400 | `MoveTo` — *enemy nearby, running home* |
| 987 | `harv` 367 | `MoveTo` — *enemy nearby, running home* |
| 989 | `harv` 400 | `MoveTo` — *enemy nearby, running home* |
| 1643, 1672 | `harv` 568 | `MoveTo` — *enemy nearby, running home* |

The bot's last completed actor before the gap is at **358s** and its next is at **919s**: the gap
opens on the same second as the first order. `harv` 367 — the only one of the three not sent home
at 358s or 361s — carried the entire economy alone for those 561 seconds and banked exactly 1,500,
the `proc` that landed at 919s. That is 2.67 credits a second from one harvester while two stood
still. The pattern then repeats and finishes the match: 367 and 400 were both ordered home at 987s
and 989s, and **no actor ever completed again in the remaining 1,074 seconds**. The `hq` ordered at
920s (1,000) and the `gtwr` ordered at 355s (600) were both still unpaid when the base fell.

`RunHomeMode` had exactly two outcomes, `UnitDecision.Continue` and `UnitDecision.MoveTo(refinery)`.
The move cancels the harvest activity. On the next evaluation the raid has passed, so the mode
answers `Continue` — which is defined as *"leave the unit's current activity alone"*, and the
current activity is now nothing. **There is no path in that mode, or anywhere else in the bot, that
ever tells a harvester to go back to work.** It could stop one and could never start one.

The trigger bar made it cheap to hit. At 354s `harv` 400 took `damage=30 health=99` — one rifle
burst, one percent of its hit points — and any threat inside seven cells that `CanHitUs` was enough.
It abandoned a field for a scratch, permanently. A `harv` carries about 700 credits and moves 1.758
cells per game second, so the 13.42-cell trip from the far refinery is a 27-second round trip; at
1.23 credits a second the fleet was delivering one load roughly every nine minutes. They were not
driving. They were parked.

[`HarvesterLogic`](Logic/HarvesterLogic.cs) is built the other way round, on the principle that
**stopping a harvester has to be earned and always has to be recoverable**:

- **A scratch is not a reason to stop.** Fleeing now needs the harvester to be under
  `FleeBelowHealthPercent` (70) as well as threatened. 354s no longer qualifies; 366s, where
  `harv` 347 took 12,420 damage over 40 hits down to 80% and kept being shot, still does.
- **A stopped harvester is always restarted.** A watchdog counts consecutive evaluations of *idle
  **and** standing on the same cell*. A harvester driving, cutting or unloading has a live activity
  and is not idle, so this cannot fire on one that is still earning — the same guard
  `DefensiveLogic` already uses before it asserts `Hold`.
- **The restart is a ladder, not a demand.** First the cheap explanation: go to the refinery and
  unload. Still stopped once it is there, and the ground the refinery was placed on is finished —
  so walk outward on a widening octagon, rotating direction each time and clamped to the map, until
  the harvester finds something to cut. Sustained normal behaviour folds the ladder back to the
  near ring.

This section used to end by asserting that *"there is no resource-sensing API and reading the
resource layer under shroud would breach the guide's fairness rules"*. **Both halves were wrong**,
and the sentence outlived its own truth for several rounds while the harvesters it described starved.
`ModeContext` exposes `FindResourceFields`, `FindNearestResourceField`, `ResourceAt`, `HasResource`
and `CanHarvest`; every one of them is shroud-filtered, so a cell this side has never explored reads
as empty exactly as it does for a human player, and using them is ordinary play. Naming a field is
now the mechanism — see *A harvester that never stops* — and the blind octagon survives only as the
shroud case, for a harvester that knows of no field anywhere because nothing has scouted one.

Three properties keep the ladder honest and must not become vacuous: a moving harvester survives
fifty evaluations untouched, a stationary one with a live activity is never interrupted, and a
harvester wedged in the corner of the map is still given somewhere to go on every single stall.

## A tower is only defence where its weapon reaches

Everything above held. Only two doctrine switches in 1,453 seconds. `world` fell to 8 of 46 losses.
The `afld` finally stood — at **558s** — so three `bggy` scouted at 605s, 616s and 660s and `harv`
became buyable for the first time in this bot's history; it was ordered at 655s. Harvesters got 45
decisions and **not one** was a stall restart: income ran at **23.19 credits a second** to 662s
against last round's 15.0, and lifetime spend rose from 14,950 to **23,450**. The harvesters did
not stop. They were shot.

The refinery ladder worked, and that is the whole problem. From the yard at (13, 82):

| | placed | cells from yard |
|---|---|---|
| `proc` | 51s, 117s, 246s, 378s | 7.07, **13.42**, **13.42**, **11.05** |
| `gtwr` | 105s, 130s | **1.41**, **2.24** |
| `sam` | 250s | **2.83** |

`ExpandingRoles` pushed the economy out. Defence was excluded from it on the written grounds that
*"a tower that walks off to the frontier is a tower not defending anything"*, so every defence took
the default 2–14 ring — and `FindBuildLocation` answers that with the **nearest** legal cell. All
three went up on top of the construction yard. That is 1,850 credits, **7.9% of everything the bot
ever spent**, guarding the one part of the base nothing touched until 1,320s.

A `gtwr` reaches **6 cells**. From the nearer of the two, at (12, 81):

| refinery | distance | covered by a 6-cell gun |
|---|---|---|
| `proc` (6, 83) | 6.32 | no |
| `proc` (2, 83) | 10.20 | no |
| `proc` (1, 76) | 12.08 | no |
| `proc` (7, 70) | 12.08 | no |

**It covered nothing.** Not one refinery, not even the innermost, which it missed by a third of a
cell. The `sam` reaches 10 and covered two of the four.

So enemy `e3` walked up to the refineries and shot the economy to pieces, from ground where nothing
could answer — a rocket soldier reaches 6 cells too, so it simply stood outside the tower and
outranged the harvesters:

| second | harvester | killed by | cells from the nearest tower |
|---|---|---|---|
| 670 | `harv` 396 at (11, 71) | `e3` | 10.05 |
| 686 | `harv` 347 at (11, 72) | `e3` | 9.06 |
| 696 | `harv` 435 at (11, 73) | `e3` | 8.06 |
| 699 | `harv` at (9, 73) | `e3` | 8.54 |

Four harvesters, 29 seconds, 2.06 to 4.05 cells beyond the only gun that might have saved them.
The assessments read `harvesters=4` at 660s and `harvesters=0` at 720s, and **`refineries=4` for
the next 540 seconds**.

| | first 662s | remaining 791s |
|---|---|---|
| credits spent, free actors excluded | 22,850 | 600 |
| income | **23.19/s** | **0.76/s** |
| share of everything it ever spent | 97.4% | 2.6% |

A **96.7% collapse**, and the build log shows it from the other side as a single **623-second gap**
with nothing completed at all between 662s and 1,285s. The bot held refineries and no harvester for
**754 of the match's 1,453 seconds — 51.9%**. Its army peaked at 7,800 at 600s and read 300 from
720s to 1,320s while the winner's went 20,560 → 119,240.

The 1,850 credits were not wasted because they were spent on defence. They were wasted because they
were spent where the defence could not see the thing it was bought to defend.

[`CoveringRole`](Logic/BasePlacementLogic.cs) is the correction, and it is the mirror of
`ExpandingRole`: **a covering structure is looked for on the ring the economy has already reached,
and walks inward only as far as its own weapon can still reach that ring.**

- **The frontier is where the economy is**, not where the next refinery is going — `FrontierRing`
  takes the ring the last expanding structure landed in, across every expanding role, furthest out
  wins.
- **The ladder is floored at `frontier − reach`.** Below that the tower has stopped being cover and
  is just a building, so no rung nearer than that is worth a `FindBuildLocation` call. With four
  refineries the `gtwr` ladder is 16, 14, 12, 10 cells; the `atwr`/`sam` pair is floored at 9.
- **Reach is the pessimistic member of each pair**, so the rule is honest either way round: 6 for
  `gtwr`/`gun`, and 7 for `atwr`/`sam` because the AA tower's ground gun reaches 7 where the SAM
  site reaches 10.
- **One of each kind still stays home.** The yard, the barracks and the factory need something over
  them and nothing else provides it.
- **It is a preference, not a demand.** The ladder still ends at the default ring, so a cramped base
  puts its tower up at home rather than leaving 600 credits wedged in the Support queue.

This works because of a trait, not a distance. `gtwr`, `gun`, `atwr` and `sam` all carry
`RequiresBuildableArea` and none of them gives it, so out at 10–16 cells the only legal cells in the
whole map are the ones beside the outer refineries and the power plants that opened the ground for
them. The radius band is not an approximation of "next to the economy" — out there it is the same
set.

The first rung plus the tower's reach has to clear the 13-cell refinery. Four properties bound the
rule and must not become vacuous: the economy's own ladder stays byte-identical to what it was, an
unfloored ladder is still the original sequence, every covering ladder still ends at the default
ring, and the first tower of each kind still stays home.

## Forty-six units is one army; five speed classes is five attacks

Everything above held, and for the first time everything worked at once. `weap` stood at **370s**,
so `jeep` scouted at 402s/414s/649s, five `mtnk` were built, and at **806s** the bot bought a `harv`
for 1,100 — the first harvester it has ever paid for. The harvester watchdog fired 49 times
(*"stopped for N evaluations, searching 6 cells out"*) and income held at **35.7/s before 475s and
16.8/s after**, against the 15.0 → 1.23 collapse that this rule was written for. `world` killed
nothing at all. `ScoutMode` and `AttackBaseMode`, which had issued zero decisions between them last
round, issued 327. Lifetime spend went 14,950 → **38,380**.

The bot then destroyed its own army in 135 seconds.

| second | event |
|---|---|
| 475 | `Attack`: *army worth 10340 and their base is known.* 46 units ordered to their base, **~70,000u out — 68 cells** |
| 487 | `jeep` 462 alone: *objective in range* |
| 520 | `mtnk` 475 alone: *objective in range* |
| 528 | the 27 `e3` are still **25–32 cells** out |
| 543 | `mtnk` 475 alone in their base: *nowhere to push, engaging Vehicle at 2896u* |
| 545 | `Attack` → `Scout`: *nothing left to attack, going looking* |
| 600 | 6 units, army **1,600** |

At **517s** the column spanned **32.2 cells** — 28 units reporting distances to objective from
7,913u to 40,922u. The army did not arrive; its speed classes did, one at a time. Over the 68-cell
approach, at `jeep` 3.54 cells a second, `mtnk` 2.49, `e2` 1.66, `e1` 1.318 and `e3` 0.952 — a
**3.7× spread** — the leaders were in contact forty seconds before the mass.

**46 units and 12,140 credits died between 475s and 610s for 18 kills:**

| | count | cost |
|---|---|---|
| `e3` | 27 | 8,100 |
| `e1` | 12 | 1,200 |
| `mtnk` | 2 | 1,800 |
| `e2` | 4 | 640 |
| `jeep` | 1 | 400 |
| **total** | **46** | **12,140** — **31.6%** of the 38,380 the bot spent all match |

Thirty-six of them fell in the 540s minute alone, twenty-six of those `e3` killed by `e1` and `e3`.
The killers are the tell: 300-credit rocket infantry being shot down by 100-credit riflemen is not
a losing matchup, it is a losing *arrival*. Nothing after this mattered — the base fell from 960s
because there was no army left, and the harvesters lost to `msam` from 960s and the 1.21/s income
after 975s are that collapse, not a second cause.

`AttackBaseMode` had no notion of a force at all. Every unit sensed, decided and marched on its own,
the instant the doctrine flipped, from wherever it happened to be standing. There is no wrong line
to point at; the mode simply never asked whether anyone was coming with it.

[`AssaultStagingLogic`](Logic/AssaultStagingLogic.cs) makes the push form up before it goes in:

- **A staging point every unit agrees on.** `MusterCell` puts it on the line from their base back
  towards ours, **14 cells** short of the target — outside every reach in the ruleset (`msam` 11,
  `sam` 10, `atwr` 8, `gtwr` 6), so the army gathers where nothing static can shoot it. It is
  derived from `ctx.BaseCenter`, not from the unit asking, which is the only reason they converge
  instead of each forming a private crowd.
- **Release is "the crowd stopped growing", not an army-size constant.** A monotonic peak of allies
  within 6 cells; `PatienceEvaluations` (16, about 22 game seconds at the measured 1.4s cadence) of
  nobody new means the muster is complete. That clears the widest gap between two speed classes
  landing — `e1` to `e3`, 16 seconds over the 54-cell walk — and it cannot be wrong about the size
  of an army it never counted.
- **A preference with a fallback.** `MaxWaitEvaluations` (75, about 105 seconds) releases regardless,
  so a push that is never going to be joined still happens. The peak is monotonic so units dying or
  drifting cannot keep resetting the clock.
- **Holding is not the same as not shooting.** A waiting unit fires on anything already inside its
  weapon range — the lesson already paid for once, when an army stood still under fire and lost 71
  units for zero kills.
- **Four guards keep it from being the wrong answer:** an approach under 24 cells, a unit already
  inside the standoff, a unit whose objective is in weapon range, and a released unit — which is
  latched, so nobody walks back out to re-form.

There is a second effect worth naming. The staging point at 14 cells is outside `AttackBaseMode`'s
five-cell `ArrivedRadius`, and it is a unit standing inside that radius which calls
`EnemyBaseSightings.Forget` for the **whole side**. At 543s one `mtnk` that had arrived alone did
exactly that and cancelled the attack for 27 `e3` who were still seventeen cells short. A lone fast
unit can no longer reach the spot to do it.

A unit is still waiting after twelve evaluations — that is the `e1`-to-`e3` gap, and it is the
point of the rule. Four properties bound it and must not become vacuous: an unarmed or immobile
unit is never marched anywhere, a side that has never seen their base gets no staging opinion at
all, an out-of-range threat is not shot at, and the staging point stays outside every static
defence in the ruleset.


## A move order is an order not to fight

Everything above held, and the whole chain completed for the first time: `afld` at **542s**, three
`bggy` scouts at 578/596/622s, *their base has been found* at **630s**, `Attack` at **660s**. The
bot found the enemy and ran its attack doctrine — something it had never once done two rounds ago.
The economy held with it: four `proc` by 361s, `hq` at 419s, income **23.9/s to 660s** (15,750
credits earned on top of the 7,500 bank), which is **8.5 credits per harvester-second** against the
5/s floor that means a harvester is not driving. `HarvesterMode` issued nothing at all before 747s,
which is what a working harvester looks like — the watchdog only speaks when one is stuck. `world`
killed nothing, the furthest structure stood 13.45 cells out (`nuke` at (3,73)), both queues were
driven, and five doctrine switches in 1,192s is not thrash.

Then the army walked into their field army and was destroyed in twenty seconds.

| second | units | army | lost | killed | event |
|---|---|---|---|---|---|
| 660 | 38 | 9,000 | 2 | 0 | `Attack`: *army worth 9000 and their base is known* |
| 671 | 40 | 10,050 | 2 | 0 | all forty ordered `MoveTo(46,50)` — *forming up 45,716u short of their base* |
| 688 | 40 | 10,050 | 3 | 0 | eight `e1` shelled by an unseen `msam`, `seen=0` |
| 690 | 40 | 10,050 | 3 | 0 | contact at (44,59) and (31,54), 33–38 cells out |
| 695 | 31 | 9,150 | 12 | 1 | |
| 700 | 28 | 8,850 | 15 | 2 | |
| 705 | 17 | 5,550 | 25 | 3 | |
| 710 | **3** | **900** | 39 | 3 | `Attack` → `Opening`: *army down to 900* |

**43 units and 10,950 credits died between 643s and 718s for 3 kills:**

| | count | cost |
|---|---|---|
| `e3` | 27 | 8,100 |
| `e1` | 12 | 1,200 |
| `bggy` | 3 | 900 |
| `ltnk` | 1 | 750 |
| **total** | **43** | **10,950** — **41.9%** of the 26,150 the bot spent all match |

The bot never recovered. It held 0 units for 470 of the remaining 482 seconds and was demolished
building by building; the four harvesters `msam` killed from 720s and the 6.5/s income after are
that collapse, not a second cause.

The tell is in the order census. `AttackBaseMode` issued **96 decisions all match, and 80 of them
were `MoveTo` — *forming up Nu short of their base***. Exactly **nine** units ever received an
order that permits engaging: three `e1` at 688s, five `e3` at 698–700s and one `ltnk` at 689s. Those
nine made **all three kills** — the `ltnk` under `AttackMoveTo` took a `jeep` at 690s and an `apc`
at 699s, and a released `e3` took a `jeep` at 701s. The other thirty-one were still executing the
`MoveTo` issued at 671s and dealt **no damage whatsoever** while dying at point-blank range.

The killers make it airtight. `e3` rockets reach **6 cells**; the `e1` M16 and `e2` Grenade that
killed twenty-six of them reach **4**. Every one of those rocket troopers could have shot first and
not one of them did, because a unit executing a move activity is a unit that has been told to walk,
not to fight. `AttackBaseLogic.Approach` had already written this down — *"attack-move rather than
move, because the whole point is to arrive able to fight"* — and applied it only to the short leg
after the muster. The 44-cell leg before it, the one that crosses the map, was a plain walk.

[`AssaultStagingLogic`](Logic/AssaultStagingLogic.cs) now makes the march a fight:

- **The walk to the staging point is an `AttackMoveTo`.** The destination is still fixed before the
  unit sets off, so nothing it meets can redirect it — this is not a breach of the never-chase rule,
  it is the same sentence `AttackBaseLogic` already applied to the leg that follows.
- **A unit in contact stops and fights through.** `SelectLastStandTarget` is the same weapon-range
  filter the `WaitForTheRest` branch has always used, so a unit pauses only while something is
  already inside its own reach and resumes the instant it is not. It can never become a chase, and
  it can never become a stop: the restarting path is the branch it fell out of.
- **`FightWhileFormingUp` is a knob with the old behaviour as its off position**, which keeps this
  a statement about the bot's own rule rather than about the engine's auto-targeting.
- **Fighting through does not spend the army's patience**, for the same reason walking does not: a
  unit still crossing the map must not burn the clock that measures how long the arrivals have
  been waiting for it.

Two properties bound the rule and must not become vacuous: an unarmed unit in contact still gets no
staging opinion at all, and a unit that fought through must not have spent the clock that measures
how long the rest have been waiting.

**Verify next round** with the recipe-2 census: `AttackBaseMode, MoveTo` should be **zero**, replaced
by `AttackBaseMode, AttackMoveTo` and `AttackBaseMode, Attack` with *fighting through* reasons — and
the kill count during the push should no longer be in single figures.

## A harvester that never stops is never asked whether it should move

Everything above held on badland-ridges seven. `world` killed nothing at all, so no unit walked at
an aircraft. The four `proc` went up at 7.07, 13.15, 13.42 and 13.04 cells from the yard, all four
before `hq` (269s) and `weap` (362s), so income still led tech and the ladder still reached past ten
cells. `BuildBaseMode` placed from both queues — `gtwr` at 105s and 130s, `atwr` at 360s, all from
`Support`. Thirty-one `e3` were trained and aircraft accounted for 20 of 75 losses rather than 67 of
159. `AttackBaseMode` asked for `Scout` at 530s and **got** it. The bot was ahead on army at 480s:
10,640 against 8,700, 43 units against 39.

It then earned 27,760 credits in 1,178 seconds — 23.6 a second — against a winner who finished with
40,800 of army still standing, 36 buildings, 86 units and 880 in the bank, having lost 78 units of
its own. Cash read 0 or 1 at every assessment from 480s onward. **Two separate mechanisms held the
fleet at four harvesters and then let it decay in place**, and between them they are the whole
match.

### The fleet never grew, because a floor of four tanks is never met

Every harvester the bot has ever owned is still a refinery's free actor. `harv` appears in the build
log exactly four times — 51s, 117s, 155s and 229s — and each is the same second as a `proc`. The
vehicle factory stood at **362s** and a second at 635s, and in the 816 seconds that followed the
`Vehicle` queue was given nine orders: five `mtnk` and three `jeep`, 5,700 credits of armour and
**zero** harvesters.

The step that blocked it is one line of [`Plans.cs`](Plans.cs). `OpeningTrain` read

```
new("Vehicle", ["mtnk", "ltnk"], 4),     ← never satisfied
...
new("Vehicle", ["harv"], HarvesterSaturation),   ← therefore never reached
```

and `Until(n)` counts what is **standing**. Five `mtnk` were built and five died — at 480s, twice at
720s and twice at 780s — so four were never alive at once, so the rung never cleared and everything
under it was unreachable for the entire match. `AttackTrain` had the same shape with a floor of
eight. The harvester saturation step, which the previous round added specifically to be the
replacement rule, was vacuous exactly when it was needed: three harvesters died at 828s, 832s and
844s, the fourth at 1125s, and nothing the bot owned could build another one. Income went 37.1
credits a second in the window to 780s, to 1.6, to **zero for the last 338 seconds**.

Four more harvesters bought at 362s, earning even the depressed 6.3 credits a second each, are
20,563 credits over the remaining 816 seconds — **74% of everything this bot earned all match** —
against the 3,600 of medium tanks they displace, which between them killed so little that the bot
finished 28 kills to 75 losses.

Both plans now put saturation directly under the harvester floor and above the first tank rung.

### The fleet decayed in place, because the field rule only ran on a stall

`HarvesterMode` received **13 decisions in 1,178 seconds**. Twelve were flee orders, correctly
triggered — the lowest was 3% health. The thirteenth, and the only one that has anything to do with
where the tiberium is, came at 839s, after the base was already being overrun, and read *"no
tiberium in sight, searching 6 cells out"*.

The income curve is what the fleet was doing while nothing was being decided:

| | 180s | 240s | 300s | 360s | 480s | 540s | 600s | 720s |
|---|---|---|---|---|---|---|---|---|
| harvesters | 3 | 4 | 4 | 4 | 4 | 4 | 4 | 4 |
| credits per second, per harvester | **15.9** | 14.2 | 10.0 | 8.6 | 7.5 | 6.3 | 6.7 | **4.8** |

Nothing was lost until 828s, and the fleet never changed size. That 70% fall is one thing: the
ground near the refineries running out while the harvesters stay inside the engine's own bubble,
driving further each trip for less. Holding the 180s rate would have been worth another 15,792
credits over 360–780s — 57% of lifetime income — with no extra harvester at all.

The rule could not see it. [`HarvesterLogic`](Logic/HarvesterLogic.cs) asked the resource layer
**only when the stall watchdog had fired**, and the watchdog counts consecutive evaluations of
*idle and standing on the same cell*. A harvester grinding the last scraps of a dying patch is
neither: it is driving, cutting and unloading, exactly as it does on a rich one, just for a tenth of
the money. The one mechanism this bot had for leaving a dead field could only fire on a symptom the
dying field does not produce.

So the field is now **chosen on a clock, not recovered after a crash**:

- **Every `ReviewEvaluations` (32) the harvester asks which field it should be on**, through
  `ctx.FindResourceFields`, and holds the answer in its watchdog. A stall still forces the question
  immediately; it is a backstop now rather than the mechanism.
- **The scan is centred on the refinery, not the harvester.** What a field costs to work is the
  round trip to the refinery — a `harv` moves 1.758 cells a game second — and not how near the
  harvester happens to be when the question is asked.
- **A field is worth what is left in it, discounted by the drive.** `Score` is
  `TotalDensity × Scale / (Scale + DistanceUnits)` with `Scale` at 12 cells, so a field three times
  as far must hold four times as much to win. It is scale-free in density on purpose: nothing here
  knows what a full cell is worth in this mod, so no threshold pretends to.
- **A field is worked out when the scan stops returning it.** `FindResourceFields` only reports
  patches of at least `MinFieldCells`, so a patch ground down to scattered cells simply disappears,
  which is a mod-independent way of noticing that the ground under a harvester has gone. That, and
  only that, forces a reassignment.
- **Moving costs twice.** A different field has to score `SwitchScoreMultiplier` (2×) the one we are
  on before the harvester crosses to it. That margin is the whole anti-dither mechanism: after a
  switch the new field is the best one, so the field just left cannot immediately beat it twice
  over.
- **Identity is the patch centre; the order names the nearest cell.** The centre is what stays put
  between scans while the near edge is cut away, and aiming at a field's centre drives the harvester
  through it to the far side.

Reading the resource layer is fair play and always was — those reads are shroud-filtered, so a cell
this side has never explored reads as empty exactly as it does for a human player. An earlier
version of the paragraph above claimed the opposite and it was wrong; see the note under *A rule
that can stop a harvester*.

Two properties bound the rule and must not become vacuous: a harvester on the only field on the map
is never given a pointless order, and a working harvester whose field is still the best one is left
completely alone.

**Verify next round** with recipes 1 and 2. The recipe-2 census should show `harv` receiving *tens*
of decisions rather than thirteen, dominated by `HarvesterMode, Harvest` with *picking a field*,
*field worked out* and *field thinning to N* reasons rather than *hurt at N%*; a run still dominated
by *no tiberium in sight* means the bot is not scouting, not that the map is mined out. The recipe-1
build log should contain `harv` rows at seconds that are **not** `proc` seconds, starting shortly
after the first `weap`/`afld`. And the per-harvester income table above is the real test: recompute
it, and the 15.9 → 4.8 collapse should be gone.

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
│   │                                (drives both the Building and Support queues, and walks
│   │                                 a ladder of rings so income, the power plants that open
│   │                                 ground for it, and the towers that cover them all land
│   │                                 as far out as is legal)
│   ├── TrainUnitsMode.cs        ←   trains units from ctx.ProductionPlan
│   ├── DefensiveMode.cs         ←   holds ground, won't be baited, retreats to repair
│   │                                (and never walks at an aircraft — see DefensiveLogic)
│   ├── AttackBaseMode.cs        ←   pushes a base, never chases
│   │                                (forms the army up short of their base first, and fights
│   │                                 its way there rather than walking — see
│   │                                 AssaultStagingLogic)
│   ├── EnemyBaseSightings.cs    ←   where this side last saw their base
│   │                                (no single unit may delete it — see SightingMemoryLogic)
│   ├── HarvesterMode.cs         ←   keeps a harvester earning, on ground that still has
│   │                                tiberium in it (reviews its field on a clock — see
│   │                                 HarvesterLogic)
│   ├── RunHomeMode.cs           ←   template: flees to a refinery when threatened
│   ├── HarvesterEscortMode.cs   ←   guards a harvester
│   └── ScoutMode.cs             ←   searches a ladder of objectives derived from the map
├── Logic/                       ← pure decision functions, no engine
│                                  (WeaponMatchLogic says what each warhead is good at killing,
│                                   so a rifleman and a rocket soldier no longer share a
│                                   target list)
```

## Fifty-four percent of the shots were aimed at the wrong armour

Everything that had been fixed stayed fixed, and the economy finally worked. The bot completed
`weap` at 382s, bought four `harv` outright at 539s, 678s, 741s and 778s — the first harvesters it
has ever *paid* for — and spent **41,780 credits, 37.5 a second**, against 14,950 and 3.6 a second
the round before. Its longest production gap all match was 125 seconds. It scouted with a `jeep`,
found their base at 445s, and ran the `Attack` doctrine twice. At 480s it was level with the
winner: 41 units against 43, 9,440 army against 11,300, 15 buildings against 16.

It then killed 56 and lost 93, and by 960s it had no units at all.

The trade is the whole story, and the reason is what each unit chose to shoot. Cross-tabulate
every engagement order in the trace by the firing actor and the class it named:

| firing unit | → Infantry | → Vehicle | DPS vs infantry | DPS vs Heavy |
|---|---|---|---|---|
| `e1` | 162 | **422** | 1,875 | 125 |
| `e3` | **284** | 133 | 319 | 1,593 |
| `e2` | 75 | 111 | 2,500 | 850 |
| `jeep` | 1 | 36 | 5,391 | 359 |
| `gtwr` | 54 | **500** | 3,000 | 900 |
| `atwr` | 43 | 177 | 2,053 | 3,948 |

**706 of the 1,313 orders the mobile army ever issued — 54% — pointed a weapon at the armour
class it is worst against.** The damage figures come straight out of `game-rules.json`: an `e1`
rifle reads `None 150%, Light 40%, Heavy 10%`, and an `e3` rocket reads `None 28%, Light 140%,
Heavy 140%`. They are near-perfect mirrors of each other, and the bot had them back to front.
`e3` was also the single largest line item in the match — 33 of them at 300 credits, **9,900
credits, 23.7% of everything the bot ever spent** — and 284 of its 459 engagements were rockets
fired at riflemen for a fifth of their rated damage.

The static defences did it too, and they are the biggest population of all: the tower with the
machine gun preferred tanks 500 times to 54, and the tower with the missiles got its second
choice.

Meanwhile enemy infantry — `e1`, `e3` and `e2` — killed **60 of the bot's 93 losses, 65%**. The
army that could have answered them was busy shooting armour it was three times slower at killing,
and the army that should have been shooting armour was firing at the infantry.

`DefensiveLogic.ScoreThreat` had one class table for the entire side: `Vehicle` 1,200 above
`Infantry` 1,000, for a rifleman and a rocket soldier alike. That table is a statement about how
dangerous a class is, which is true whoever is looking — but it was the *only* term, and the term
it was missing is a statement about the shooter.

`Logic/WeaponMatchLogic.cs` adds it. Every actor id in the ruleset that this bot or either faction
can field is measured against `None` / `Light` / `Heavy` armour and sorted into `AntiInfantry` or
`AntiArmour`; `DefensiveMode` and `AttackBaseMode` read `self.Info.Name` once on entry and hand
the role to the pure scorers, which add `Effectiveness x 3000 / 100` alongside the class weight.
In a brawl where both candidates are in range and both can shoot back, an `e3` now scores a
vehicle 4,200 against a rifleman's 1,600, and an `e1` scores that rifleman 4,000 against the
vehicle's 2,100.

Three boundaries are arithmetic rather than hope, and each one protects a fix that already works:

- **An in-range target always beats an out-of-range one.** Between two candidates that both can
  or both cannot shoot back, the worst in-range total is 5,950 and the best out-of-range total is
  4,481. A better matchup is never a reason to give up a shot and walk — which is the same
  principle "You cannot catch an aeroplane" is built on.
- **`CanHitUs` still outranks everything.** Worst total with it is 10,950; best without is 10,132.
- **An in-range aircraft is still the top target for anything that can hurt it**: `AntiArmour`
  scores it 5,000, above the vehicle's 4,200. The filter that discards aircraft outside weapon
  range is untouched.

`WeaponRole.Unknown` is the fallback and it is deliberately flat — every class scores the same 60
— so an actor the table has never measured adds a constant to every candidate and the ordering
collapses to exactly what it was before. A preference that degrades into the old behaviour cannot
be worse than not having one.

## The watchdog that could never bark

Everything fixed before stayed fixed, and the weapon/armour table from the previous round worked
exactly as designed. `gtwr` — the tower with the machine gun — went from 500 vehicle engagements
against 54 infantry ones to **876 infantry against 194 vehicles**; `e1` went from 422-against-133
the wrong way round to **548 infantry against 301 vehicles**; `ltnk` preferred vehicles 137 to 71;
and `e3` still took its one aircraft shot, so the anti-air path did not collapse. Lifetime spend
rose again, to **68,700 credits over 1,618 seconds, 42.5 a second**, from 41,780 and 37.5 the round
before, with no production gap longer than two minutes anywhere in the match.

It lost anyway, 169 units to 107, to a side that finished with 147 units, 70,960 of army and 51
buildings standing.

The income curve says where. Credits earned per 120-second bucket, from spend plus the change in
cash:

| from | 120s | 240s | 360s | 480s | 600s | 720s | 840s | **960s** | **1080s** | **1200s** | 1320s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| credits/s | 39.5 | 44.2 | 43.9 | 34.3 | 76.2 | 82.7 | 77.6 | **31.6** | **42.9** | **39.9** | **0** |
| harvesters | 2 | 3 | 4 | 4 | 6 | 8 | 10 | 9 | 8 | 9 | 0 |

The economy did not run out of harvesters. It ran out **at its peak harvester count**. Over
600–960s the fleet earned 78.9 credits a second with an average of eight and a half harvesters —
9.3 each. Over 960–1320s, with the same eight and a half harvesters, it earned **38.2 a second, 4.5
each**, which is below the line where a harvester is not driving at all. The shortfall is
(78.9 − 38.2) × 360 = **14,650 credits, 21% of everything the bot spent all match**, and the bot's
cash read exactly 0 at 1,020s, 1,080s, 1,140s, 1,200s and 1,260s while the winner's army went from
18,340 to 52,980.

The decision trace names the mechanism without ambiguity. Of 504 `HarvesterMode` decisions:

| reason | count | action |
|---|---|---|
| `no tiberium in sight, searching N cells out` | **245** | `MoveTo` |
| `hurt at N%, running to the refinery` | 176 | `MoveTo` |
| `stopped for 4 evaluations, returning to the refinery` | 43 | `MoveTo` |
| `field thinning to N, crossing to …` | 26 | `Harvest` |
| `picking a field, harvesting …` | 11 | `Harvest` |
| `stopped for 4 evaluations, re-cutting …` | **3** | `Harvest` |

Every one of the 245 shroud probes happened after 840s — 12, then 73, then 73, then 87 — and 135 of
them were the *first* rung of the ladder, six cells out, meaning the harvester was being sent on a
short blind hop over and over rather than searching anywhere new. Against 288 stall events the
branch that exists to answer a stall, `re-cutting`, fired **three times**. And there was no shortage
of tiberium to name: the same trace shows `Harvest` orders at 1,119s, 1,147s, 1,189s, 1,191s, 1,199s
and 1,232s naming fields with 106 to 168 density still in them, 8 to 45 cells out. A `MoveTo` is an
order not to harvest, so each of those 245 orders cancelled the activity it was supposed to restart.

The cause is one line, and it is an off-by-one between two different watchdogs:

```csharp
var scanned = ShouldScan(watchdog, tuning);          // the watchdog from LAST evaluation
var seen = Observe(watchdog, state, tuning);         // ...advanced here
var stalled = seen.StillEvaluations >= tuning.StallEvaluations;
```

`ShouldScan` tested `watchdog.StillEvaluations >= 4`. A harvester crosses the threshold when
`Observe` raises the count *to* four, which means the incoming count was three and the stall term
read false. Worse, it could never read true on any later evaluation either, because every branch
that fires on a stall resets `StillEvaluations` to zero — so the incoming count never exceeded
three, ever. **The stall term was unreachable code.** The mode asks the same question before
deciding whether to pay for a map scan, so `fields` came back empty at precisely the moment it was
needed; `count > 0` failed, the whole field-selection block was skipped, and the rule fell through
to the shroud probe underneath it. The three `re-cutting` orders that did fire are the three
occasions when the unrelated 32-evaluation review clock happened to come due in the same
evaluation.

`HarvesterLogic.ShouldScan` now takes the `HarvesterState` and tests the observation the evaluation
is *about* to make, sharing one private `DueForScan` with `Decide` so the two cannot drift apart
again; `HarvesterMode` builds its state before deciding whether to scan. A stalled harvester is now
always scanned for, which means it is always handed a field.

A second, smaller trap sat behind it. The `re-cutting` branch is guarded by `ProbeIndex == 0` so a
repeated order is not issued twice — the host suppresses duplicates, so repeating one cannot restart
anything — but with that guard closed the rule had nowhere left to go except the probe. There is now
a `3b`: a harvester that is still stopped after a re-cut is given the best field **other** than the
one it is on. That is a genuinely different order rather than a suppressed duplicate, `Assign` moves
the remembered assignment with it, and the next stall excludes that field in turn, so the fleet
works outward through what it can see instead of grinding on one dead patch. The shroud probe is now
what it was always meant to be: the answer when a scan comes back with no field anywhere, which is a
statement that the side has not scouted rather than that the map is mined out.

### And nobody ever shot a harvester

The other half of the same match. In **3,957 engagement orders** the bot aimed at an enemy harvester
exactly **once**, by a `bggy`. `AttackBaseLogic.SelectBlocker` explains why:

```csharp
if (!isDefence && !t.CanHitUs)
    continue;
```

A harvester is unarmed, so `CanHitUs` is false, so it was filtered out before it could be scored —
a push walked past the thing paying for the army it was fighting. That filter is right for a tank
that would have to be chased and wrong for a harvester standing in range, because `SelectBlocker`
only ever considers targets already inside weapon range: taking the shot costs no forward progress
at all. `ThreatKind.Economy` now passes the filter and scores **1,500**.

That number is a bound, not a preference. `MatchBonus` tops out at 3,000 and the health and distance
terms add at most 132, so an economy target reaches **4,632**; the *worst* total a target that is
shooting back can score is 5,000 + 600 (AntiArmour against Infantry, the lowest entry in the
effectiveness table) = **5,600**, and a static defence starts at 10,000 whatever else is true. An
enemy harvester therefore only ever wins when the alternative is not shooting at all. `SelectObjective`
is deliberately untouched: an objective is pursued with `AdvanceToObjective`, and a harvester moves
at 1.758 cells a game second against an `e3`'s 0.952, so making one an objective would be a chase
that can never end.

### What the ruleset says cannot be done

The match review also suggested selling surplus refineries for a cash boost. `UnitAction` has no
sell, so there is no way to express it; the buildable answer to "too many refineries" is to build
fewer, and at six `proc` for 9,000 credits — 13% of spend, each carrying a free 1,100-credit
harvester — this bot is not yet there. Worth recording so it is not rediscovered.

## A build plan is finite; a map is not

Everything fixed before stayed fixed. The stall watchdog barks: `stopped for 4 evaluations`
appears 16 times and `field worked out, harvesting` 57, so the field rule ran on its own clock all
match. No production gap exceeded two minutes until 1,233s, by which point the base was being
overrun. Lifetime spend rose again — **81,450 credits over 1,586 seconds, 51.4 a second**, from
68,700 and 42.5 the round before, and 41,780 and 37.5 the round before that. Harvesters peaked at
ten instead of nine. `world` killed nothing. The weapon/armour table held.

It lost anyway, 202 units to 136, to a side that finished with **178 units, 84,680 of army, 52
buildings and 13,905 in hand**.

The construction yard is where the match was decided, and the evidence is a silence rather than a
mistake. Every rung of the Attack doctrine's build plan was satisfied when the fifth refinery
landed at **744s**:

| rung | wanted | standing at 744s |
|---|---|---|
| `proc` | 5 | 5 (51s, 117s, 169s, 245s, 744s) |
| `nuke` | 5 | 5 (13s, 65s, 189s, 358s, 696s) |
| `pyle`/`hand` | 2 | 2 (79s, 675s) |
| `weap`/`afld` | 2 | 2 (338s, 521s) |
| `hq` | 1 | 1 (271s) |
| `gtwr` | 2 | 2 (105s, 130s) |
| `atwr`/`sam` | 1 | 1 (174s) |

The `Building` queue then issued **no planned order for the remaining 842 seconds** — 53% of the
match. The only two it issued at all were replacements: a `proc` at 1,040s for the one an `orca`
killed at 980s, and a `hand` at 1,255s. Buildings peaked at 22 against the winner's 52.

The credits did not stop. They went somewhere that ended the match worth nothing:

| after 744s | credits | share |
|---|---|---|
| `ltnk` ×19 | 14,250 | 37% |
| `e3` ×41 | 12,300 | 32% |
| `e1` ×37 | 3,700 | 10% |
| `bggy` ×9 | 2,700 | 7% |
| **units total** | **32,950** | **86%** |
| `proc` ×2 (both replacements) | 3,000 | 8% |
| `gtwr` ×3, `sam` ×1 | 2,450 | 6% |

Thirty-three thousand credits of army, and the final army value was **0**.

The harvesters had already filed the complaint. The distance in their own `harvesting … N cells
out` reasons walked outward all match — median 13 cells before 300s, **23 at 600–900s**, 21
after 1,200s, with a maximum of **52 cells, half the map** — and **331 of their 492 decisions were
`hurt at N%, running`**, because ground that far out is ground no tower covers. Eight harvesters
died in 42 seconds between 1,272s and 1,314s at (66–71, 8–18), and the economy never recovered. A
refinery is not only income; it is the thing that makes a field close, and close is what makes a
field safe.

The player's own reading of the replay was the same one: *"they aggressively expanded to every
tiberium field and their economy was nearly double ours."*

So the refinery target stops being a constant. `Logic/ExpansionLogic.cs` sizes it from the
tiberium this side has actually explored — shroud-filtered, so it rewards scouting, and
self-limiting, so a poor map cannot turn the budget into refineries. `BuildBaseMode` consults it
**only after `BaseConstructionLogic.ChooseNext` has answered "nothing to do" over the doctrine's
own plan**, which is the whole safety argument: the opening is bit-for-bit what it was, no rung is
displaced, delayed or outbid, and the branch can only fire in exactly the situation above. A power
plant leads each refinery whenever the balance is under 50, because a plant is the cheapest thing
this bot builds that carries `GivesBuildableArea` and so is both the power for the next refinery
and the ground to put it on; the balance after 744s read 63, 63, 93, 63, 23, 38 and 28.

Three bounds, worked out rather than guessed:

- **The ceiling cannot outrun the placement ladder.** `BasePlacementLogic.RingAt` walks the near
  edge out by `RingStepCells` (6) per refinery and clamps it at `MaxMinRangeCells` (16), so from
  the fourth refinery onward every extra one asks for the same 16-cell near edge and the ladder
  walks inward from there. The tenth asks for exactly the band the fourth did.
- **Ten refineries is 15,000 credits** — less than half the 32,950 spent on units after 744s.
- **Sixteen harvesters is the fleet ceiling**, and ten refineries hand out ten free ones, so the
  plan pays for six: 6,600 credits against the 14,250 that went on `ltnk` alone.

`TrainUnitsMode` resizes the harvester saturation rung the same way, from the refineries actually
standing. It has to: the constant is `RefineryCore * 2` = 8, every plan reached it at 634s, and a
base that grows past four refineries would otherwise run one harvester each — the free actor a
refinery hands out — and never two. Only the **last** harvester rung is resized, so the floor near
the top of each plan keeps its position and its number, and the queue cannot be monopolised any
earlier than it already could be.

Every answer is floored at what the shipped plan already wanted, and both entry points return the
plan **by reference** when nothing is due, so a map that does not justify expansion produces
exactly the bot that fought this match. The next trace can be grepped for `for N fields found` and
`training harv for N refineries`; if neither literal appears, the branch never fired.

## One blind unit cancelled three attacks out of four

Everything above held. Lifetime spend was **65,020 credits, 46.6 a second** against 41,780 and
37.5 the round before, with no production gap over 120 seconds and the furthest structure 13.2
cells out. `world` killed 2 of 173 losses, so nothing was chasing aeroplanes into tiberium. The
harvesters reported `field thinning to N` 52 times, `field worked out` 24 and `picking a field`
12, and `no tiberium in sight` never once — the watchdog was working and the bot was scouting.
The armour table held on all three of its predictions: `gtwr` chose Infantry 274 times to
Vehicle 42, `atwr` chose Vehicle 87 to Infantry 41, `e3` still fired on Aircraft 8 times, and the
two mismatched buckets fell from **54% of the mobile army's engagement orders to 32%** (179
`e1`-at-Vehicle and 397 `e3`-at-Infantry out of 1,802).

And the bot lost anyway, 154 units to 76, because it recalled its own army from inside the enemy
base three times.

`AttackBaseMode` marches on `EnemyBaseSightings`, one remembered cell shared by the whole side.
Any unit that stood within the five-cell `ArrivedRadius` of that cell and could not see a
structure called `Forget` — and `Forget` deleted the cell for everybody. Sensing is per-unit and
shroud-filtered, so "I can see nothing here" is a statement about one unit behind one ridge, not
about their base.

The whole match is in the 71 `objective in range` decisions, which arrive in four bursts:

| assault | entered | contact | how it ended |
|---|---|---|---|
| 1 | `Attack` 425s | 20 `objective in range`, 74 `clearing … en route`, 480–520s; 5 enemy buildings destroyed | two `e3` at **5,526u and 5,895u** from the remembered cell with nothing in sight at 517s → `Scout` at 520s, *nothing left to attack* |
| 2 | `Attack` 695s | **0** and **0** | cancelled itself at 725s — the side's memory was still empty from 520s |
| 3 | `Attack` 805s | 16 `objective in range`, 71 `clearing … en route` | the identical two decisions, at **5,160u and 5,400u** → `Scout` at 885s |
| 4 | `Attack` 985s | 17 `objective in range` | ran to *army down to 1400* at 1,070s — the only one that ended on its own terms |

In the thirty seconds after the 520s recall, all 46 remaining decisions were `DefensiveMode`:
fifteen of them *engaging Economy at 5,526–10,765u* — the army was shooting their harvesters when
it was called off — and eight were `ReturnToAnchor`, *drifted 8,408u > tether 8,192u*, walking
home from sixty cells out. The 885s recall produced **666** `DefensiveMode` decisions in thirty
seconds, and twenty `e3` died at (40–41, 58–61) in the eight seconds after that.

Assault 2 is the same bug wearing different clothes. `BattleState.EnemyBaseFound` never goes back
to false, so `ReferenceBotLogic` rule 6 ordered an attack on a host flag that was still true while
this side's own memory was empty. The doctrine had nowhere to aim and cancelled itself thirty
seconds later having issued no attack order at all.

The price is everything the bot ever spent that moved:

| | built | lost | cost |
|---|---|---|---|
| `e3` | 65 | 65 | 19,500 |
| `e1` | 46 | 46 | 4,600 |
| `mtnk` | 11 | 11 | 9,900 |
| `jeep` | 8 | 8 | 3,200 |
| `e2` | 12 | 12 | 1,920 |
| **total** | **142** | **142** | **39,120 — 60.2%** of the 65,020 spent all match |

Not one mobile unit the bot built survived the match. They died in one patch of no-man's land at
(33–49, 51–61), 55–62 cells from their own yard at (82,13) and about 26 cells short of the cell
they were marching on — `msam` took 40 of them, `a10` 13 (ten `e3` in the two seconds 605–606s),
`htnk` 15. Ten enemy buildings fell out of the forty-eight Cabal built.

[`Logic/SightingMemoryLogic.cs`](Logic/SightingMemoryLogic.cs) makes forgetting corroborated
rather than unilateral. `EnemyBaseSightings` now stores the tick of the last sighting alongside
the cell, `Record` refreshes it on every evaluation in which any unit can see an enemy structure,
and `Forget` agrees only when **nobody on this side has seen one for fifteen seconds**. It returns
whether it actually discarded anything, and `AttackBaseMode` only ends the doctrine when it did.

Three bounds pin the fifteen seconds, and it has to sit inside all of them:

- **Above the gap between two corroborating sightings.** A unit in contact records on every
  evaluation and the measured cadence is about 1.4 game seconds, so this is roughly ten
  consecutive evaluations of seeing nothing — far more than the one or two it takes a vanguard to
  pick a new objective when the building it was shooting falls over.
- **Far below `LostContactSeconds` (300s)**, the backstop for a base that genuinely no longer
  exists. At 5% of it, a side that really has levelled their base still forgets the spot fifteen
  seconds after the last thing it could see there stopped existing, and rule 4 still catches an
  army nobody ever walks onto the crater.
- **Above zero**, because zero is the old behaviour.

A unit standing on an uncorroborated sighting now falls through to `AttackBaseLogic`'s last-stand
branch and shoots whatever is in reach, rather than being ordered to attack-move onto its own
cell. It has nowhere left to march either way; the difference is that the other thirty units still
have somewhere to march.

The next trace answers this in one query. `their base is not there any more` should appear only
where the push has actually razed what it could see, `Attack` → `Scout` *nothing left to attack*
should collapse from three, and the `objective in range` bursts should get longer instead of being
cut off at forty seconds.

## You cannot aim your way out of the wrong army

Everything above held, and one of them held loudly. The corroborated sighting worked: `Attack` →
`Scout` *nothing left to attack* **collapsed from three recalls to one**, `their base is not there
any more` never fired, and the `objective in range` burst ran 460s–539s — **80 seconds**, twice the
forty it used to be cut off at. The harvester watchdog held: 15 `field worked out`, 14 `field
thinning to N`, 8 `picking a field`, `no tiberium in sight` **zero**. `gtwr` still preferred
infantry, 222 to 173.

And the armour table — the fix that halved mismatched shots last round — went **backwards**, from
32% of the mobile army's engagement orders to **64%**: 460 `e3`-at-Infantry and 62 `e1`-at-Vehicle
out of 812. Nothing regressed in the scorer. The bot simply had almost nothing else to aim. **A
target scorer can only sort the army you built**, and 58% of this one was rocket infantry standing
in front of riflemen.

The match turned in two minutes. At 480s this bot led on units (38 to 34) and army (9,300 to
8,200) and was three buildings behind. By 600s it had **seven units left**, having lost 39 and
killed 23. Thirty-nine of its ninety losses — 43% — happened inside one five-by-four-cell patch at
(52–56, 38–41), two to six cells short of the `nuke` at (58,39). The army arrived. It just could
not win the fight it had walked 62 cells to start.

### Two airfields, eleven orders, and nothing that fights

The `Vehicle` queue was given **eleven orders in 1,403 seconds**:

| | orders | delivered | cost |
|---|---|---|---|
| `bggy` | 5 | 5 | 1,500 |
| `harv` | 6 | 4 | 4,400 |
| **anything that fights** | **0** | **0** | **0** |

The two `afld` that served them cost **4,000 credits, 11.6% of everything the bot ever spent**, and
stood at 385s and 630s. `hq` stood at 318s. From `game-rules.json`, that is every prerequisite
`arty` has — `anyhq`, `~techlevel.medium`, `Vehicle.Nod` — so **artillery was buildable for 1,018
seconds, 73% of the match, and was ordered zero times**. The last vehicle order was at 777s. The
next was at 1327s.

The cause is rung order, and it is the exact mirror of the bug the tank rung used to have.
`HarvesterSaturation` sat directly above the armour rung, and `ExpansionLogic.Saturate` sizes it at
two per standing refinery — eight, on four refineries. Harvesters die: all eight were hunted down
between 660s and 800s, at (27–30, 64–87), 14 to 17 cells from the yard at (13,82). So the rung was
never permanently met, `UnitProductionLogic.ChooseNext` does not gate on cash, and it handed the
vehicle queue `harv` for the rest of the game while everything below it stayed unreachable. Fixing
"this bot never buys income" had quietly created "this bot never buys reach".

What the army became instead, every line of it built and lost:

| | built | lost | cost |
|---|---|---|---|
| `e3` | 35 | 35 | 10,500 |
| `e1` | 26 | 26 | 2,600 |
| `bggy` | 5 | 5 | 1,500 |
| **total** | **66** | **66** | **14,600 — 42.5%** of the 34,350 spent all match |

Not one mobile unit survived. **72% of the army budget went on `e3`**, whose warhead does **318**
damage a second to `None` armour — the worst anti-infantry number in the ruleset — while **62% of
the army's engagements were against infantry** (601 against 366). Nothing the bot fielded reached
past 6 cells; enemy `arty`, which reaches 11, was its joint-largest killer at 13 of 90, level with
`heli`. Lifetime spend was **34,350 credits, 24.5 a second**, against 65,020 and 46.6 the round
before, and the bot bought nothing whatsoever after 782s — **44% of the match**.

### The fix: a rung that reach can actually reach

[`Plans.cs`](Plans.cs) gains `SiegeVehicles` — `["arty", "msam"]`, the 11-cell answer each faction
already has — and a `SiegeCore` of four, inserted in `OpeningTrain`, `DefenceTrain` and
`AttackTrain` (and so `ScoutTrain`) in one specific slot: **below the harvester floor and above
harvester saturation**. That slot is the whole argument.

- **Below the floor**, so income replacement still outranks everything. `HarvesterCore` is four and
  four refineries hand out four free harvesters, so the floor is normally met by actors the bot did
  not pay for and only fires when one dies — which is exactly when it should.
- **Above saturation**, because saturation is the rung that can never be permanently met under
  attack, and a rung like that starves everything beneath it. Reach now sits on the safe side of it.
- **Neither tank is listed.** `Until(n)` counts *every* candidate a step names, so a rung written
  `["arty", "ltnk"]` is satisfied by light tanks and buys no reach at all — the same trap that once
  made `["e3", "e1"]` buy 125 riflemen and zero rockets. The tanks keep their own rungs below.

The arithmetic the number must not cross: the rung displaces the same count of saturation
harvesters at 1,100 each, so it has to cost **less than the income it defers**. Lifetime spend was
34,350 over the 782 seconds the economy was alive — 43.9 a second across a fleet averaging five
live harvesters, so roughly 8.8 a second each and about **125 seconds for a harvester to repay
itself**. Four is the worst case at GDI prices: 4 × 900 = 3,600, which is **82 seconds** of that
income, less than the payback period of the single harvester it defers. At Nod prices it is 2,400,
or 55 seconds. What it buys for that: one `arty` does **5,390** a second to `None` armour where an
`e3` does **318**, so a 600-credit artillery piece is worth seventeen 300-credit rocket soldiers —
5,100 credits — against the infantry that was most of what this army met. It is also 1.758 cells a
second against `e3`'s 0.952, so it arrives in half the time.

Nothing else needed changing, which is the sign the slot is right. `WeaponMatchLogic.RoleOf`
already measures both — `arty` AntiInfantry at 5,390/4,312/2,888, `msam` AntiArmour at
577/2,405/1,154 — so the scorers divide their work the moment the units exist, and an id the table
has never seen still falls through to a flat `Unknown`. `DefensiveMode.OnEnter` already scales
tether and leash off the unit's own reach *specifically so artillery sits wider than a rifleman*;
at 11 cells that is a 22-cell tether and a 33-cell leash, and anything inside 11 cells is a free
shot needing no movement at all. A step whose candidates no driven queue can build is skipped in
silence, so a bot with no `hq` yet simply falls through to the rungs below — a preference with a
fallback, not a demand that can fail.

The next trace answers this in one query: **`training arty` or `training msam` should appear at
all** — they have never appeared once — and the `Vehicle` queue's order count should stop being
five scouts and six harvesters. If artillery is built and the army still dies in one patch short of
their base, the binding constraint has moved to how the push is staged, and `mustering` is still
sitting at **zero decisions for the third round running**.

## An economy of four free actors, three of them mining the enemy's base

`msam` appeared, and that verified: seven `msam` orders, six built, **13 kills from six units** —
2.2 each, the best ratio of anything the bot fielded, against `e3`'s 1.06 and `e1`'s 0.55. Every
other standing diagnosis held too. And the bot lost harder than the round before, because reach was
never the constraint: **income was, and the `Vehicle` queue bought none of it.**

Lifetime spend was **38,680 credits, 30.0 a second**, against 65,020 and 46.6 two rounds earlier.
Cash read 0 or 1 at 19 of the 22 sampled assessments from 180s to the end — **85% of the match at
zero**. The other side held 951–5,197 in hand the whole way while growing from 12 units at 600s to
**137**, and 20 buildings to **37**. At 480s this was an even game: 40 units and 9,140 of army
against 29 and 11,000. Both armies then annihilated each other by 600s — 47 kills for 44 losses, a
fair trade. Only one side could pay to rebuild.

### The floor was a rung that could never fire

Every plan writes the harvester floor as `HarvesterCore`, a constant equal to `RefineryCore`, which
is four. A refinery carries a `FreeActor` harvester. So four refineries satisfy a four-harvester
rung **four out of four**, and the floor was met by actors the bot never paid for.

The evidence is exact. Every `harv` this bot ever owned was built in the same second as a `proc` —
**51s, 117s, 174s, 263s** — and the `Vehicle` queue received **twelve orders in 1,289 seconds**:
four `jeep`, seven `msam`, and one `harv` at **1,144s**, 248 seconds after the last harvester had
already died.

The saturation rung underneath could not rescue it, because it sits below `SiegeCore` — and siege
vehicles die. Six `msam` built, **six lost**, so that rung was permanently unmet;
`UnitProductionLogic.ChooseNext` returns the first unmet step, and the queue answered `msam` at
456s, 515s, 558s, 622s, 724s, 791s and 814s without ever reaching the income beneath. **This is the
third time the same shape has cost a match**: tanks once blocked harvesters, harvesters once
blocked tanks, and now siege blocks harvesters. Position alone cannot protect income.

It compounded at the end. The `msam` ordered at 814s, at roughly zero income, held the queue busy
for **330 seconds** — which is the 305-second hole in the build log from 837s to 1,142s, 24% of the
match with nothing bought at all.

### And the field rule drove the fleet into their base

The field score is a hyperbola: `TotalDensity × 12 cells / (12 cells + distance)`. It discounts
distance and never refuses it. The harvesters' own reasons record where that went — 12, 17, 11 and
18 cells out for the first four minutes, 27 to 35 cells from 447s, and at **839s** a crossing to a
field **62 to 71 cells out at (59,35)**, nine cells from the enemy refinery at (62,37).

Enemy `e3` killed three of the four harvesters at **888s, 890s and 896s**, at (45,43), (49,39) and
(53,39) — **45 to 56 cells from this bot's own construction yard at (13,82)**. The fourth died at
962s. Income went from **43.3 credits a second across 300–840s to 1.56 across 840–1,289s**, no
structure was ever built again, and by 1,020s the bot had zero units.

### The fix: size the floor from docking places, and cap the haul

Two changes, one subsystem.

[`ExpansionLogic.Reinforce`](Logic/ExpansionLogic.cs) is the mirror of the existing `Saturate`: it
resizes the **first** harvester rung from the refineries actually standing, where `Saturate`
resizes the last. With four refineries the floor becomes eight — two per docking bay — so the four
free actors leave a deficit of four and the queue has to buy them. It only ever raises, so a plan
already asking for more is returned unchanged by reference, and it does nothing with **no refinery
standing**, because a harvester with nowhere to dock earns zero and replacing the refinery is the
`Building` queue's job.

The arithmetic it must not cross: between 300s and 840s, the window with exactly four harvesters
up, the bot spent 23,380 credits in 540 seconds — 43.3 a second, **10.8 per harvester per second**.
A harvester costs 1,100, so it repays in **102 seconds**. This rung can owe at most four bought
harvesters at once — 4,400 credits, **11.4% of everything the bot spent all match**, and 102
seconds of the income the four it already had were earning — so it can never cost more than the
first one it buys returns. At the ceilings above (`MaxRefineries` 10, `MaxHarvesters` 16) the worst
case is six bought and 6,600 outstanding.

[`HarvesterTuning.MaxHaulCells`](Logic/HarvesterLogic.cs) is 36, and `SelectFieldExcept` is now two
passes: best field inside the ceiling, and only if there is none, best field anywhere. **A
preference with a fallback** — the ceiling can refuse ground, never the last ground there is. A
harvester already assigned outside the ceiling comes home for any field inside it whatever the
scores say, and because that test needs the assigned field outside and the candidate inside, it can
only fire toward home and cannot dither.

Why 36: a harvester moves 1.758 cells a game second, so a round trip is 1.14 × distance seconds —
15 at the 13-cell fields this bot worked safely, 41 at 36 cells, 71 at 62. A cycle was about 65
seconds at 10.8 credits a second, so 36 cells makes it 91 seconds and 7.7 a second, **71% of the
near-field rate**, which is where a further field stops being an economy and becomes a commute; 62
cells is 121 seconds and 5.8, **54%**, before any risk is counted. The bound it must not cross is
that it has to admit every field this bot worked without losing a harvester — out to 35 cells — and
exclude the one that killed three, which began at 61. Thirty-six is the smallest number above the
first and the largest below the second.

**The next trace answers both in one query.** `training harv for N refineries` should appear — it
has appeared **zero** times, and `harv` was ordered exactly once all match — and `built` rows for
`harv` should outnumber `built` rows for `proc`, which has never once been true. On the harvester
side, `cells out is past the 36 cell haul limit` should appear whenever the fleet strays, and **no
`lost harv` row should sit more than 40 cells from the construction yard**; all three of this
round's did.

If income recovers and the bot still loses, the next constraint is the one the player named:
**enemy economy was never touched.** This bot killed exactly **one enemy `harv` (687s) and one
enemy `proc` (626s)** out of 89 kills — 2.2% — and only 43 of 1,157 engagement orders were aimed at
the `Economy` class at all. It spotted an enemy `harv` at 437s and did not kill one until 250
seconds later.

## The gather that never gathered

Every standing diagnosis held, and the economy fix worked outright. `training harv for N
refineries` appeared **15 times**, **18 `harv` were built against 5 `proc`** — the first match in
which bought harvesters outnumbered free ones — the 36-cell haul ceiling fired 16 times, and **no
harvester died more than 32 cells from the yard** against three at 45–56 cells the round before.
Lifetime spend was **74,160 credits, 49.1 a second**, the highest this bot has ever managed,
against 65,020 and 46.6. The build ladder kept climbing to **1,264s** where it used to stop at
640s. `msam` verified again: **7 built, 62 kills, 8.9 each**, against `e3`'s 0.72.

And the bot lost by more than ever, because the constraint was never economic. It held 2,472
credits at 1,200s with six units left. **At 480s this was a won game**: 43 units to 21, 9,940 of
army to 8,400, 15 buildings each. At 600s it was 16 units to 37 and 2,700 to 13,300, and it never
led again. In between it lost **42 units and killed 14**.

### Four hundred and fifteen walk orders, and not one gather

`AssaultStagingLogic` exists to stop the army arriving a speed class at a time. In 4,385 decisions
the trace holds **415 `MoveToMuster` orders and zero `mustering`** — the third match running, and
this time the reason is structural rather than slow. `Observe` is reached only from the
`WaitForTheRest` branch, so `StaleEvaluations` and `WaitedEvaluations` never advanced past zero;
`Release` needs one of them, so **it was unreachable by either route**, and `Released` was never
latched once. Every branch except the walk was dead code. The whole machine reduced to a one-way
attack-move order, and the army reached its objective only by falling through
`ObjectiveInRange`, the standoff test, or death.

The cell it walked to is the fault. `MusterCell` measured the standoff back from **their** base, so
on badland-ridges it returned **(46,48)** — 14 cells from theirs and **50.2 cells from this bot's
own yard at (82,13)**, with the whole contested map in between. `WaitForTheRest` only fires within
six cells of it. Nothing ever got there: the closest any unit reported was **25.8 cells short of
their base**, 11.8 cells short of the staging cell it was walking to.

So the gathering leg *was* the dangerous leg. Across the first assault (435–515s) the force was
spread from **25.8 to 71.9 cells short of their base — 46.1 cells of column**, which is most of the
map. Enemy `jeep` ate it: **400 credits, Light armour, 3.54 cells a second, 5,391 damage a second
against the `None` armour every one of this bot's infantry wears.** Between 470s and 545s it killed
**26 `e3`, 12 `e1` and 4 `e2` — 42 units and 9,640 credits, 13% of everything the bot spent all
match — for 14 kills**, and every single one of the 42 fell to a `jeep`. The losses centre on
**(44.8, 53.2)**: the staging cell itself.

That is not a fight this bot should lose. `e3` reaches 6 cells to `jeep`'s 4 and does **1,592
damage a second to Light armour**; twenty-six of them standing together annihilate any number of
jeeps. Arriving one at a time, they are 300-credit targets.

### The fix: legs measured from home, and a release that re-arms

One staging point cannot compress a 3.7x speed spread however well it is placed — after a single
release the army simply re-spreads over whatever leg remains. So
[`AssaultStagingLogic`](Logic/AssaultStagingLogic.cs) now gathers **repeatedly, on legs measured
from home**. `StagingAdvanceCells` returns the next boundary in front of a unit —
`(progress / leg + 1) * leg`, capped at `march = homeToTarget - standoff` — and `StagingCell` walks
that distance out from our own base rather than back from theirs. `MusterWatchdog.Released` becomes
`ReleasedThroughUnits`, so a release latches **one boundary instead of the push**: a released unit
can never turn round, but it does gather again further on. The crowd clocks reset with it, because
a peak carried over from the last leg can never be beaten by a crowd that has only just started
arriving, and that would release on arrival — the same bug in a new place.

On badland-ridges the legs land at **(71,25), (61,37), (50,49) and (46,54)**, then commit. The last
one is the cell the old code sent everybody to directly; the difference is that the force now
arrives there having re-concentrated three times, and the first gather happens 15 cells from home
under its own towers.

Why 16 cells: a leg of L is crossed in L/0.952 seconds by `e3` and L/3.54 by `jeep`, so it spreads
the force by **L × 0.768 seconds**. At 16 that is 12.3s, about 9 evaluations, comfortably inside the
16 of patience — the ceiling is L ≤ 22.4 / 0.768 = **29 cells**. The floor is the six-cell muster
radius, below which a unit released at one boundary is already inside the next one; 16 is also
longer than the longest reach in the ruleset (`msam`, 11), so a unit gathering at one boundary is
out of reach of anything sitting on the last. `MaxWaitEvaluations` drops from 75 to **32** and now
bounds **one gather** rather than the whole push: it must exceed the 9 + 16 = 25 evaluations a
normal gather costs or it would pre-empt the thing it backstops, and four legs × 45s caps the whole
approach at 180s against the ~100s a working gather actually spends.

Staging reasons are now low-cardinality on purpose. `forming up {N}u short of their base` produced
**242 distinct strings out of 415 orders** and buried the fact that the branch below it never ran;
they now read `forming up on leg N`, `mustering on leg N with K allies` and `mustering on leg N,
engaging {kind}`.

**The next trace answers this in one query.** `mustering on leg 1` must appear at all — it has been
zero for three matches — and **`mustering on leg 2` appearing proves both halves at once**: that a
gather completed and that the release re-armed for the next leg. The forming-up spread inside one
assault window should fall from 46.1 cells to something near one leg, and no single minute should
lose 42 units to a 400-credit unit again.

If the army arrives together and still loses, the next constraint is composition, and the numbers
already name it. **`e3` is 27% of the budget — 67 built, 67 lost, 48 kills** — and every mobile type
the bot fields has built equal to lost (`e1` 70/70, `e2` 16/16, `jeep` 10/10, `msam` 7/7). `msam`
returns 8.9 kills a unit for 8.5% of the spend. After that, two leads that are still untouched:
anti-air is **one `atwr`, built at 341s**, after whose death `orca` razed the base unopposed, and
the enemy economy is still untouched — **0 enemy `harv` and 1 `proc` killed out of 192 kills**.

## The gather worked, and then it ate the match

Last round's fix held, and that is the point of this one. `mustering` went from **zero in three
matches to 3,014**, spread across every leg — **811 on leg 1, 1,050 on leg 2, 780 on leg 3, 329 on
leg 4, 44 on leg 5** — so the gather completed and the release re-armed exactly as designed. Legs
measured from home were right. Every other standing diagnosis held too: lifetime spend reached
**257,000 credits, 53.3 a second** against 74,160 and 49.1; the 11-cell answer was finally fielded
(**31 `arty`**); `atwr`/`sam` and `e3` covered Air.

And the bot lost by more than ever, because **the machine that gathers had no timeout on the state
it actually spent its time in.**

The telemetry is not the telemetry of a bot that was outplayed. At 960s it led **54 units to 8** and
**17,550 of army to 3,980**. At 2,880s it led **145 to 7** and **49,950 to 2,400**. It finished
having killed **811 to Cabal's 497** while losing 544 to their 904. It out-produced, out-killed and
out-expanded the winner for the entire match — and **never razed their base**. Cabal's building
count never fell below 19 and ended at **43**. Then, between 4,320s and 4,800s, this bot went from
113 units and 19 buildings to **nothing**.

### Seventy-eight percent of the assault was walking to a field

Of `AttackBaseMode`'s **50,168** decisions, **39,332 — 78% — are `forming up`**. `objective in
range` is **797, 1.6%**. That is a ratio of **49:1** between getting ready to attack and attacking.

Two separate failures produce it, and both reduce to the same sentence.

**The walk had no clock of any kind.** `Observe` is reached only from the `WaitForTheRest` branch,
so a unit walking to a staging cell advanced no counter at all, and `MaxWaitEvaluations` could not
help because it counts waiting. A unit that cannot reach its staging cell therefore walks at it
forever. One `e1` — actor 355 — issued the identical **`AttackMoveTo(34,39)` 670 times between 460s
and 877s**. That is **417 game seconds** against the **36 seconds** an `e1` needs to cover the 47.9
cells from the yard at (13,82), and it reached the leg-3 gather **zero** times. Across the side,
**19,613 of the 39,332 forming-up orders were that one cell**, issued by **77 distinct units over
500 game seconds**; leg 3 drew **19,775 walk orders in the first 1,200 seconds against 780 gathers
in the whole match**.

**The standing still had a clock, but it was per leg and every re-entry reset it.** The doctrine
left `Attack` **27 times**, so `AttackBaseMode.OnEnter` ran 27 times, and each run cleared
`ReleasedThroughUnits` — re-imposing gathers the survivors had already been released through.

The arithmetic was never going to work. A gather costs 9 evaluations of arrival spread plus 16 of
patience, and the per-leg backstop allowed 32 more; four legs of that is **180 seconds**. The
`Attack` doctrine's **27 episodes averaged 70.2 game seconds** (min 30, max 340; 14 of 27 were 50s
or shorter). **The gather could not finish inside the episode that authorised it.**

### And the side kept deleting its own target

`Attack → Scout "nothing left to attack, going looking"` fired **24 times**. Every one was followed
**30 to 50 seconds later** by `Scout → Opening "scout found their base"` — the same base, intact,
which ended the match with 43 buildings.

Corroborated forgetting was the right idea measured against the wrong clock. `CorroborationTicks`
was **375 ticks, 15 game seconds**, derived from how often a unit *in contact* re-records. But the
staging rule deliberately parks the army 14 cells short of their base and up to a leg behind that,
in fog, for a whole gather. Nobody can see an enemy structure from there, so nobody corroborates,
so the window expires **during the bot's own approach** — and one fast unit reaching the remembered
cell then deletes the side's only target while the base is standing.

### The base stopped being rebuilt with 37% of the match left

`BasePlacementLogic.RingAt` grew its far edge as `14 + 6n` with **no ceiling**, while the near edge
had one from the day it was written. `FindBuildLocation` is backed by a tile search whose
`MaximumTileSearchRange` is **50 cells**, and it does not clamp — it **throws**, out of a mode's
tick. At n = 7 the ask is **56**. The trace holds **337 `BuildBaseMode` errors between 3,039s and
3,375s**, the last structure of the whole match went up at **3,007s**, and **nothing was built in
the remaining 1,814 seconds — 37.6% of it** — while the base fell from 26 buildings to zero. This is
the player's "need to restore power when our base is attacked", exactly.

### The fix: a budget the machine cannot outspend

[`AssaultStagingLogic`](Logic/AssaultStagingLogic.cs) gains **one counter and one bound**.
`MusterWatchdog.StagedEvaluations` counts every evaluation the staging machine holds a unit —
**walking as well as waiting** — and `MaxStagingEvaluations` caps it at **40**. Nothing resets it:
not a release, and not a new push. `MusterWatchdog.ForNewPush()` clears the per-leg clocks and the
release latch, which a unit setting out again genuinely needs, and carries the budget through, which
is the whole correction. Past the cap the verdict is `Release` for the rest of the unit's life, and
it is checked **above** the walk order so an out-of-budget unit is never sent back to a cell it has
already passed. `Charge` is deliberately split out of `Observe`: patience is about standing still,
a timeout is about being held at all, and conflating them is what left the walk with no clock.

Why 40. One leg done properly costs **37 evaluations** — `e3` at 0.952 cells a game second crosses
a 16-cell leg in 16.8s, which is 12 evaluations at 1.4s each, plus the 25 a gather costs. Two legs
is **74**. So `37 ≤ 40 < 74` buys exactly one gather per unit, ever, and 40 evaluations is ~56s,
inside the **70.2s mean `Attack` episode** that four legs × 45s never was. `MaxWaitEvaluations`
stays at 32, below the budget, so the per-leg backstop still fires inside a budget that has not run
out.

[`SightingMemoryLogic`](Logic/SightingMemoryLogic.cs) widens `CorroborationTicks` from 375 to
**2,250 — 90 game seconds**. The floor is the longest blind spell a committed push legitimately
has: one gather is ~35s and the final 14-cell run in from the standoff is 15s for `e3`, so 50s. The
ceiling is `ReferenceBotTuning.LostContactSeconds` (300s), the real backstop for a base that no
longer exists; at 30% of it a genuinely levelled base is still forgotten inside a minute and a half.

[`BasePlacementLogic`](Logic/BasePlacementLogic.cs) gains `MaxSearchRangeCells = 50` and clamps
`RingAt`'s far edge to it. [`RefinerySitingLogic`](Logic/RefinerySitingLogic.cs) gets the same
clamp, where the bug is worse but had not yet fired: `FindResourceFields` is uncapped by design, so
a field 60 cells out is ordinary and would have thrown identically. A field beyond the engine's
limit now collapses to one legal band that simply fails to match, and the caller's own ladder
answers — the neutral fallback it had before the siting rule existed.

**The next trace answers this in one query.** `forming up` must fall from **78% of
`AttackBaseMode`'s output to well under a quarter**, and `objective in range` must rise from
**1.6%**; no single actor id may hold more than **40** staging decisions in its whole life, because
that is now arithmetically impossible. `Attack → Scout "nothing left to attack, going looking"`
must fall from **24** toward zero, and any occurrence should no longer be followed 30–50s later by
`scout found their base`. There must be **zero `BuildBaseMode` errors**, and structures must still
be going up in the final quarter of the match.

## The queue never reached the rung that had the answer

Lost on `badland-ridges` as Nod after 1,528 seconds: **0 units, 1 building**, 72 killed against 78
lost, while Cabal finished with 225 units and an army worth 99,860. The economic reading of that
final row is wrong. At **480s the two sides were level** — 41 units, 9,900 army and 15 buildings
against 42, 10,980 and 16 — and the whole match was decided in the 120 seconds that followed.

Everything the last four rounds fixed held. `forming up` fell from **78% of `AttackBaseMode`'s
output to 12.4%** and `objective in range` rose from 1.6% to **3.5%**; no actor held more than
**3** staging decisions against a budget of 40; `mustering` fired for the **first time in four
recorded matches** (twice); there were **zero** `Attack → Scout "nothing left to attack"` switches
and **zero** `BuildBaseMode` errors. The one check that failed is the last structure at **771s,
50.5% of the match** — the base still stops growing halfway through.

### Zero combat vehicles, in 1,134 seconds of being able to build them

`hq` stood at 321s and `afld` at 394s, so Nod `arty` — 600 credits, `rangeCells` **11**,
5,390/4,312/2,888 damage a second — was buildable for **1,134 of the 1,528 seconds, 74% of the
match**. The `Vehicle` queue issued **twelve orders** in that time: six `bggy` and six `harv`. Zero
`arty`. Zero `msam`, `mtnk`, `ltnk`. The largest single killer of this bot was enemy `msam` at 17 of
97, reaching 11 cells against an army whose longest weapon reached **6**.

### Because the harvester floor is a rung that can never be met

[`ExpansionLogic.Reinforce`](Logic/ExpansionLogic.cs) sizes the harvester floor at
`HarvestersPerRefinery` (2) × refineries. Five refineries stood — 51s, 117s, 195s, 277s, 585s — so
the floor read **10**, and a refinery hands out exactly **one** `FreeActor` harvester. Ten
harvesters were built and ten were lost, nine of them between **660s and 780s**. The floor was
therefore unmet for essentially the whole match; `UnitProductionLogic.ChooseNext` returns the first
unmet step; and the floor sits **above** the siege rung that was added last round to buy exactly the
11 cells of reach this army lacked.

**This is the fourth match the same shape has decided.** Tanks once blocked harvesters, harvesters
once blocked tanks, siege once blocked harvesters, and now the floor blocks siege. Every previous
fix moved a rung, and the bug came back one rung higher.

### And the endless rung at the bottom was the dearest unit per kill

Sixty-eight mobile units, 15,400 credits, in three types:

| unit   | built | lost | spend  | share of lifetime spend | kills | credits per kill |
|--------|-------|------|--------|-------------------------|-------|------------------|
| `e3`   | 38    | 38   | 11,400 | **29.3%**               | 11    | **1,036**        |
| `e1`   | 25    | 25   | 2,500  | 6.4%                    | 26    | **96**           |
| `bggy` | 5     | 5    | 1,500  | 3.9%                    | 14    | **107**          |

Every plan ended `new("Infantry", ["e3"], int.MaxValue)`, so every leftover credit for the whole
match bought the 1,036 row — and lifetime spend was only 38,850 credits, 25.4 a second.

The ruleset says why. **63% of the mobile army's engagement orders were against Infantry** (563 of
894, excluding the towers), and `e3` deals **318** damage a second to `None` armour where `e1` deals
**1,875**. The enemy infantry that did the killing — `e1` 33, `e3` 14, `e2` 9, **56 of 97** — deals
that 1,875 straight back. A 300-credit unit out-traded 5.9 to 1 by a 100-credit one.

`e3` is also the slowest actor in the game at 0.952 cells a game second. Their yard stood at (50,18)
and ours at (13,82) — **73.9 cells, 78 game seconds of walking**. The one assault of the match died
strictly in speed order: `bggy` (4.15 c/s) at 491s, 526s and 527s at (58,35), (49,20) and (49,19),
inside their base; `e1` (1.318) at 566–574s at (41–44, 25–26), on the wall beside their `gun` at
(43,21); `e3` at 579–589s at (40–44, 30–33), still eight cells short of where the riflemen had
already died. **Thirty-nine units in fifty seconds, for nine kills.** `AttackBaseMode` issued its
last decision at 600s and none in the remaining **928 seconds — 61% of the match**.

### The fix: a floor that stands aside, and riflemen at the bottom of the ladder

[`ArmyBalanceLogic`](Logic/ArmyBalanceLogic.cs) is new, pure, and moves nothing. `Release` marks a
rung met — by rewriting its target down to what is standing — while the role is inside a slack band
of its target, and restores the original target the moment it falls below. It is a **preference with
a fallback**: income keeps absolute priority while the fleet is genuinely in trouble, and a fleet
that is merely one or two short stops holding the whole queue hostage for the rest of the game. It
is applied to a **named role** (`HarvesterUnits`) rather than to every rung, so
`new("Vehicle", ["jeep", "bggy"], 1)` and every other rung keep the previous behaviour exactly.

**Why three quarters.** The band is bounded on both sides. It must be strictly below 1 or nothing
changes. It must be strictly above **1/2**, because `Reinforce` sizes the floor at two per refinery
and a refinery hands out one free harvester — so free actors alone are always half the floor, and
any share at or below a half releases the rung before the bot has bought a single harvester, which
is the failure that pinned income at ~12 credits a second two rounds ago. `ceil(3n/4) > n/2` for
every `n > 0`, so 3/4 is strictly inside the band at every refinery count: a floor of 10 releases at
**8**, so three harvesters must be bought on top of the five free ones; a floor of 8 releases at 6;
a floor of 4 releases at 3. Targets of 1 and 2 release at their own target, so small rungs never
release at all.

[`Plans.cs`](Plans.cs) changes one rung in each of the three training plans: the endless
`new("Infantry", ["e3"], int.MaxValue)` becomes `new("Infantry", ["e1"], int.MaxValue)`. `e3` keeps
its bounded rung above it in every plan — 12 in `OpeningTrain`, 8 in `DefenceTrain` and
`AttackTrain` — because it is still the only unit this bot can build that shoots upwards and still
the best thing it can build per credit against `Heavy` armour (1,592 against `None`'s 318), so it
must be replaced when it dies. It simply must not be what every leftover credit buys. Capped at
twelve it costs 3,600 rather than 11,400, and that **7,800-credit difference is 20% of everything
the bot spent**, moved to the rung that killed 10.8 times more per credit.

**The next trace answers this in one query.** `harvester floor released at N/M` must appear in
`TrainUnitsMode` reasons — it is a literal that cannot be emitted any other way — and the `Vehicle`
queue must produce **at least one** `arty`, `msam`, `mtnk` or `ltnk`, against zero in each of the
last two matches. `e3` must fall from **29.3% of lifetime spend** to under 12%, and `e1` must become
the most-produced unit. If the floor releases and the queue still buys no combat vehicle, the
blocker is cash rather than rung order and the next fix belongs in income, not in the plan.

## A random walk is not a search

Lost on `badland-ridges` as GDI after 1,136 seconds, fitness **0.417** against a prior median of
0.68. Five of the six fitness components scored between 0.42 and 0.68. One scored **zero**:
`buildingsDestroyed`, worth 0.15 of the total. The bot destroyed **no enemy building at all**, and
the reason is not that the push failed. **There was no push.** Across 1,136 seconds and twelve
doctrine changes the bot ran `Opening`, `Scout` and `Defence` and never once entered `Attack`.

`Attack` is gated on `readyToPush = ArmyValue >= 6000 && EnemyBaseFound`. The army cleared 6,000 at
420s and peaked at 7,640. The other half never arrived: across **228 assessments**, `enemyBaseFound`
first read true at **1,045s** — 92% of the way through the match — and it read true then only
because *their* army had reached *our* base, with `nearestEnemyCells` at 6 and 77 enemies in sight.
Our own army was worth 0 by that point. **No scout ever saw an enemy structure.**

### The search was a uniform random walk that ran home on contact

`ScoutMode` was still the shipped template. It picked its destination with
`random.Next(bounds.Width)` — a uniformly random cell from the whole 98×98 map — and on sensing
anything inside six cells that could shoot it, ordered itself all the way back to `ctx.Anchor`.

Both halves are fatal, and the trace prices them. A uniform random destination has **no expected
progress**: the mean random cell is the middle of the map, so each new target undoes the last and
the scout oscillates around the centre. All four jeeps died there — (45,51), (54,61) and (48,72) on
a map whose centre is (48,48) — at a mean age of 111 seconds, having travelled halfway and turned
round. And running home discards every cell of progress at the first picket: the decision trace
holds **17 `spotted, falling back` against 15 `scouting`**, so more than half of every scout's
output was a retreat. 204 cells were explored, against a prior median of 394 and a fitness
reference of 300.

Exploration is not only a fitness component. It is the gate on the Attack doctrine, and — because
resource reads are shroud-filtered — it is the ceiling on income too.

### A ladder derived from the map beats a coin

[`ScoutSearchLogic`](Logic/ScoutSearchLogic.cs) replaces the coin with an ordered ladder of
objectives, every rung of it computed at runtime from `Map.Bounds` and `ctx.BaseCenter`:

1. our base **reflected through the centre of the map**, because skirmish maps place spawns
   symmetrically and that is the best single guess there is;
2. the two single-axis mirrors, for maps mirrored about one axis rather than rotated;
3. then a walk around the map's rim, striding three-eighths of the perimeter a step and skipping
   points that land near home, because bases sit near edges and the middle gets crossed anyway.

On this match rung 0 evaluates to **(84,15)**. The enemy spawn was **(83,14)**. Nothing in the code
knows that — it is arithmetic on the bounds and our own spawn — which is the difference between
deriving a position and memorising one.

The retreat is gone with it. A threatened scout now takes a six-cell step that is *away from the
threat and toward the objective at once*; where those disagree the components cancel on that axis
and the step becomes a genuine sideways go-around. A scout is worth exactly the ground behind it,
and a 400-credit jeep with no other job is not worth saving by throwing that away. A stall watchdog
covers the case the old code had no answer for at all: four evaluations at the same cell means the
rung is unreachable, so take the next one — which also breaks the deadlock where the host suppresses
a re-issued duplicate order and a scout with a cancelled move stands still for the rest of the match.

A scout that can actually see one of their structures stops searching and keeps watching it.
`EnemyBaseSightings.Forget` believes a sighting only while somebody has seen a structure in the last
90 game seconds, and the army's approach march is longer than that; a scout that ticked on to the
next rung the instant it arrived would let the side's only target go stale during the very march it
unlocked.

`ScoutMode` is also now **assigned in `OpeningDoctrine`**, not merely registered there. Scouting
used to happen only while the `Scout` doctrine happened to be running — 320 of 1,136 seconds — and
for the rest of the match the jeeps stood in the base as defenders. They are not defenders: four of
them finished with **0 kills**, 11,064 damage dealt and 30,539 taken. Finding the other side is the
only thing a jeep does that this bot cannot do without.

### What the next trace answers

`searching for their base` and `going around` are literals nothing else emits, and `falling back`
must not appear at all. The one that matters is `closing on their base`, from `AttackBaseLogic`:
it is emitted only while marching on a recorded sighting, and it was emitted **zero** times here.
`buildingsKilled` must leave zero.

**The economy is the standing risk, and it is not what this round changed.** All six harvesters
died between **635s and 659s** to Nod rocket infantry, 13 to 24 cells out around the forward
refinery at (7,70), and `earned` never moved again: 25,340 credits at 660s and 25,340 at 1,080s,
**zero income for the last 477 seconds**, with cash pinned at 1 while a refinery and a war factory
still stood. `secondsWithNoHarvester` was **530**. If the search works and that still happens, the
next round's fix belongs in the economy, not in the search — which is why it has a check of its own.

## The whole match ran on one harvester

`badland-ridges`, Hard, lost at 1,187s. Income finished at **17.2 credits a second** against a
fitness reference of 50, and every other failure in the ledger is downstream of that number: cash
read 0 or 1 at every one of the twenty economy samples from 180s on, the `Vehicle` queue issued
**two orders in the entire match**, the army was 100% infantry, and `buildingsDestroyed` was zero
for the second fight running.

The cause is one line in `units.csv`. Six harvesters ever existed, for **1,260 harvester-seconds
across a 1,187-second match** — a mean of **1.06 alive** — with four refineries standing from 260s
and five bought for 7,500 credits, 20.7% of everything ever spent. 550 seconds of the match had no
harvester at all and `earned` was frozen at 20,475 from 660s to the end.

### It was not the harvesters, and that is the point

Three previous rounds looked at `HarvesterLogic`. Divide the ledger instead: 20,475 credits over
1,260 harvester-seconds is **16.3 credits per harvester per second**, a full load every forty-odd
seconds, which is about as well as a harvester can do. The search was fine. There were simply
almost none of them, and the two reasons are both purchasing, not driving.

### A refinery is a one-shot harvester

`weap`/`afld` was the *last* rung of `Economy`, behind four refineries and the `hq`, on the
argument that four refineries pay for the factory sooner than two do. That argument only holds for
the **first** harvester. A refinery hands out one free actor and can never hand out another; the
factory sells a replacement every time one dies, and this bot's harvesters are hunted — enemy `e3`
alone killed four of the six, for 153,224 damage.

The ladder reached `afld` at **474s**. Two of the four free harvesters were already dead, at 271s
and 275s, and until 474s there was no way in the game to replace either. So the factory moves to
sixth, directly after the second refinery. It costs 2,000 and needs only `proc`, so power,
refinery, power, barracks, refinery, factory is **6,500 of the 7,500 opening bank** — affordable
before a single credit of income, with every refinery after it bought out of earnings. `hq` goes
last, because it is the only rung on the ladder that earns nothing at all.

### Rung order cannot arbitrate a shared bank

The second reason is subtler and had never been addressed, because every previous fix worked
*inside* one queue. `ExpansionLogic` sizes the harvester rungs, `ArmyBalanceLogic` stops an
unmeetable one starving what sits below it — and both arbitrate a single plan. Cash is shared by
every queue on the field, and nothing arbitrated that.

The airfield lived about 540 seconds, issued two orders, and took **143 seconds to deliver one
1,100-credit harvester**. Over the same match the barracks issued **59 orders worth 8,900 credits**
— a 100-credit rifleman roughly every twenty seconds. Lifetime spend tracked lifetime earnings to
the credit. A queue buying 100-credit items always wins that race against one buying an
1,100-credit item, and what it beat was the entire economy.

[`IncomeFirstLogic`](Logic/IncomeFirstLogic.cs) caps every `Infantry` rung at four bodies, and only
while all three of these hold at once:

1. a vehicle factory is standing, so a harvester is actually buyable;
2. the fleet is below the band `ArmyBalanceLogic.ReleaseAt` already defines, rather than below a
   second threshold invented for the occasion;
3. cash is below a harvester's price — which is what makes the rule **self-releasing**. The moment
   the side can afford a harvester *and* a rifleman, the rifleman costs the harvester nothing and
   the cap lifts on its own. On a healthy economy it never fires.

It caps rather than silences, so the base is never naked: the opening four rifles and four rockets
are below the cap and untouched, and what stops is the twelve-rifle rung re-firing every time one
dies. The towers were the better buy anyway — four `gtwr` cost 2,400 credits and killed 47 units
worth 10,740, at **51 credits a kill against the infantry queue's 171** — and they were starved by
the same bank.

### The rule that was switched off for the first two minutes

Both conditions above require a harvester to be *buildable*, and a harvester needs a vehicle
factory. So between the yard landing and the factory landing, nothing arbitrated the shared bank at
all — and that window is where the next `16:9` was lost. The whole opening, in order: `nuke` 13s,
`proc` 51s, `nuke` 65s, `hand` 79s, four `e1` to 91s, `gtwr` 105s, four `e3` to 123s, `afld` 129s,
`sam` 134s, `bggy` 148s. **8,150 credits by 148s, with one refinery standing.** The second
refinery — at 1,500 the cheapest harvester on offer and the only one buyable without a factory —
could not be started until income had rebuilt its price unaided, and stood at **249s**. One
harvester worked from 51s to 249s; the match finished on 3,955 credits at **5.9 a second** against
a prior median of 28.6, and the `Vehicle` queue the airfield had opened spent **470 seconds waiting**
for cash that was never going to exist.

Two changes, one idea — *the opening bank buys income*:

- [`ReferencePlans.Economy`](Plans.cs) puts the second refinery above the barracks and the
  2,000-credit factory. Power, refinery, power, refinery is 4,000 of the 7,500 bank and needs
  nothing that is not already standing; the second free harvester roughly doubles income from about
  90s, which buys the factory back inside a minute rather than deferring it. The barracks moving
  with the factory is deliberate: `gtwr` and `sam` both require one, so the `Support` queue cannot
  open a second front on the bank until the economy has had its first two rungs.
- [`IncomeFirstLogic.ReserveOpeningBank`](Logic/IncomeFirstLogic.cs) is this bot's first use of
  `IProductionBudgetBot.ReserveProductionBudget`. A rung reorder cannot fix a problem *between*
  queues — the yard asks for one thing at a time while three other queues keep spending — so the
  host arbitrates it centrally instead: one refinery's price is reserved for `Building`, and every
  other queue's new `Produce` is suppressed when it would take live cash below what is left of it.
  Marked `economy.bank-buys-income`.

It is the narrowest reservation that covers the observed loss. **One refinery's price, never the
whole ladder**, so against a 7,500 bank it is the *last* 1,500 that is defended and an opening
garrison still gets bought. **Only below three refineries**, past which the side has a barracks and
a factory too and the rules above take over. And **never while enemies are at the base**, because
every hold in this file has at some point become a latch on a condition the opponent controls.

### The exemption unblocked the plan and never unblocked the bank

`IsOpeningEmplacement` exists so the yard's economy hold cannot swallow the base's first gun, and
it works: the trace holds **55 `defence.opening-emplacement`** on the next `16:9`. The side still
spent **1,112 of 1,350 seconds with nothing standing that shoots**. The first `gtwr` stood at 143s
and died at 199s; the second was not ordered until roughly 440s and died at 518s; nothing replaced
it in the remaining 832 seconds. `Support` was given **four orders in the whole match** and spent
181 seconds waiting for cash. `Infantry` was given 23.

Releasing a rung does not pay for it. A queue buying 100-credit bodies wins every race against a
600-credit tower, and what it beat was the best trade on the field:

|            | credits | kills       | credits a kill | dealt   | taken   |
| ---------- | ------- | ----------- | -------------- | ------- | ------- |
| `gtwr` ×2  | 1,200   | **11** of 30 | **109**       | 119,682 | 4,775   |
| `e1` ×13   | 1,300   | 4           | 325            | 30,723  | 27,127  |
| `e3` ×8    | 2,400   | 4           | 600            | 253,888 | 170,450 |

Against enemy `e1` the towers dealt 35,406 and took **nothing** back. Seven of the bodies bought
instead died **0 to 6 seconds** after leaving the barracks, to a jeep parked outside it.

And it is not only a trade. Every retreat rule this bot owns sends its earners home — **314
`runs-home`, 214 `escape-en-route` and 197 `sheltering`** of the 841 decisions the harvesters made
— on the written premise that *home is where this side's guns are*. There were none, and **33 of
the side's 44 losses fell inside one six-cell circle** on the base.

[`IncomeFirstLogic.EmplacementBeforeBodies`](Logic/IncomeFirstLogic.cs) stands the unit queues down
until that first gun is paid for. Marked `defence.emplacement-before-bodies`, and narrow on every
axis so it cannot latch:

- **Only at zero standing**, the same bound `IsOpeningEmplacement` already uses. One tower silences
  it completely and depth stays behind the doctrine's own rungs, so it can never become a turtle.
- **Only while `Support` can actually sell one.** No barracks, no power or no yard means the saved
  credits would buy nothing — which is also what retires the rule when the yard dies.
- **Only below 600 credits**, a ruleset price for both `gtwr` and `gun`. At a tower's price in hand
  a rifleman costs the tower nothing and the hold lifts itself.
- **Never over harvester recovery.** Income outranks a gun, because a gun bought with the last
  credits of a dying economy is the last thing the side ever buys.

### A deferral with no deadline is a deadlock

The cross-queue hold above has a twin inside the construction yard, and that one had no release at
all. [`BuildBaseMode`](Modes/BuildBaseMode.cs) holds construction cash whenever the fleet is below
its release band and a war factory stands, so the vehicle queue can finish a harvester. On `16:9`
the yard returned that hold on **293 of roughly 600 evaluations**, and **149 of them fell between
600s and 731s** — a window in which lifetime earnings were frozen at 19,950 and cash read 0 at
every sample. It was reserving credits that did not exist, for a harvester that could not be paid
for. While it ran the yard issued nothing, reached neither the frontier nor the anti-air branch,
and declined to repair: the `Support` queue was asked **nine times in the whole match** and
delivered four emplacements against a plan that wants six towers and two anti-air. Those four were
the best buy on the field — **2,800 credits for 13,100 killed**, where every rifleman built came to
3,200 for 6,900.

Two bounds, both in [`IncomeFirstLogic`](Logic/IncomeFirstLogic.cs):

- `TrackRecovery` measures the hold against the thing it claims to fund. The vehicle queue's
  `CurrentRemainingCost` falls as an item is paid off, so a harvester whose remaining cost has not
  moved for a minute of game time is a harvester nothing is being spent on, and the hold ends. The
  clock resets the moment it moves — indefinite on a healthy economy, bounded on a dead one, which
  is the distinction the old rule could not draw. Marked `economy.recovery-hold-stalled`.
- `IsRecoveryRefinery` exempts refineries outright. `proc` carries `FreeActor`: 1,500 credits buys
  an 1,100-credit harvester *and* the dock it has to reach, and it is the only harvester on offer
  once the war factory is dead. Three of this side's seven harvesters arrived that way against four
  bought. Holding cash to help the vehicle queue buy one while refusing to build the structure that
  hands one over is the rule working against its own purpose. Marked
  `economy.refinery-is-recovery`.

### One reading lesson, written into `checks.json`

Three of the previous round's ten checks were meaningless and looked decisive. `units.count(type=e1)`
read **100** and `units.count(type=e3)` read **78** against a side that built 44 and 14:
`units.csv` holds **both** players, so `units.count`/`units.mean` are never a statement about this
bot. `summary.unitTypes[<type>].built` is. Every own-side claim this round is phrased that way.

## The Attack doctrine ran for zero seconds

Same `badland-ridges` loss. The economy above explains why the bot was poor; this explains why it
never once attacked. Across **238 assessments the Attack doctrine was entered zero times** — ten
doctrine episodes, all of them Opening, Scout or Defence — and `buildingsDestroyed` has now scored
a structural **0.0 out of 0.15** two fights running.

### A conjunction whose halves were never true at the same instant

The gate was `ArmyValue >= 6000 && EnemyBaseFound`. Grep the assessments for each half:

| condition | true when |
| --- | --- |
| `armyValue >= 6000` | 360s – 570s (42 samples) |
| `enemyBaseFound` | 900s – 1185s (58 samples) |
| **both** | **never (0 samples)** |

`enemyBaseFound` is only ever set by a unit that can see an enemy structure, and **no scout vehicle
was built all match** — the `Vehicle` queue issued two orders in 1,187 seconds and both were
harvesters. The flag first read true at 900s only because *their* army had arrived at *our* base,
by which point our own army was worth 1,500 and falling.

So the bot was not choosing to attack late. It was structurally incapable of attacking, and had
been for two fights.

### Waiting for certainty was the mistake

[`AttackBaseMode.Probe`](Modes/AttackBaseMode.cs) is the fix, and it is a deduction rather than a
sighting. Skirmish maps place their spawns symmetrically, so **our own base reflected through the
centre of the map** is overwhelmingly the best single guess at where they live — which is exactly
[`ScoutSearchLogic.Objective`](Logic/ScoutSearchLogic.cs) rung 0, already written, already used by
the scouts, and already arithmetic on `Map.Bounds` and `ctx.BaseCenter`. Nothing in it knows which
map is being played.

A push with no sighting now marches at that cell under `AttackMoveTo`, so it fights through what
it meets rather than being shot for free on the way. Three things follow for one change:

- the attack happens at all;
- the army *is* the scout — 146 cells were explored last fight against a reference of 300, and
  because resource reads are shroud-filtered, unexplored tiberium is unusable income as well as
  lost fitness;
- staging is skipped while there is no sighting (`MusterState.HasTarget` is false), so the first
  probe goes out immediately instead of gathering — and once anyone sees a structure, the sighting
  is recorded and the normal staged assault applies to everything after it.

Standing on the guess and still seeing nothing is the one honest way to learn the map was
asymmetric, and that hands over to the search ladder, which has more rungs.

### 6,000 credits is a stockpile, not a threshold

The other half. The opponent's first attack was killing harvesters in our base at **271s**; on a
97-cell diagonal that means they set out at roughly 170s with whatever they had. Our own army
reached 2,000 at **140s**, 3,000 at 190s and 6,000 only at **360s**. A bot that waits for six
thousand credits of army concedes the first four minutes to an opponent that does not.

`AttackArmyValue` drops to **2,000** — twenty rifles or six rockets, a raiding party rather than an
army. That is the point: it buys contact early, and the units the barracks turns out behind it join
the same push, so the group **expands while it is in the field** instead of being assembled before
it leaves. `RetreatArmyValue` drops to **600** with it, because two thresholds a handful of
riflemen apart make the bot commute — push, lose three units, go home, rebuild, push again.

### A veto that fires every minute is not a veto

Rule 1 recalled the army for **any** building lost in the 60-second window. Twenty buildings were
lost, so against an opponent that raids continuously that rule describes most of the match, and it
does not postpone a push — it cancels it. That is the same trap rule 2 already documents, and it
gets the same shape of answer: a side with an army to spend rides out the first building and comes
home for the **second**, which is a base being dismantled rather than a raid getting lucky. A side
with no army still turtles on the first, having nothing better to do with the next minute.

### The risk, stated rather than hidden

A raiding party can simply die, and `valueExchangeRatio` was 0.722 with the army safe at home.
`aggression-does-not-just-feed-the-enemy` in `checks.json` is that risk written as a falsifiable
floor. If it fails, the answer is to raise `AttackArmyValue` — not to abandon the probe, which is
the thing that made the Attack doctrine reachable at all.

## Fleeing was a ratchet, and the ratchet was the match

On 16:9 at Hard the bot lost with **19.589 credits a second earned** against a prior median of
28.319, a mean army of 480.9, and no enemy building destroyed. The economy series is the whole
story: `earned` sat at exactly 15,400 from 540s to 720s — three minutes of nothing — with five
harvesters and three refineries alive and none of them dead yet. Then every production mode
returned `no change` **502 consecutive times from 520s to the end**, because there was no cash to
gate on. The army never came back from its 2,600 peak and nothing the bot owned ever finished a
structure.

The harvesters were not stuck in the engine's search bubble. They were running away.

`HarvesterLogic`'s escape rule has one entry that is not spent on use: health below
`FleeBelowHealthPercent`. Nothing in this bot repairs a harvester, and `ThreatClearTicks` releases
the shelter twelve seconds after the last contact — so a harvester that has once been shot below
70% flees again on the *next* contact, and the one after that, forever. Against a side camped in
the base that is every thirteen seconds. The decision trace says so directly: **72 withdrawal
episodes across six harvesters, mean 17 seconds, 1,244 of 2,918 harvester-alive seconds spent
withdrawing** — 43% — and 63 of the roughly 69 entries carried the unrepaired-escape reason, some
of them at 1% health. Each withdrawal is a `MoveTo`, which cancels the harvest activity, so the
partial load goes with it.

It bought nothing. All six harvesters died anyway, three of them together in a map corner they had
fled to.

### A withdrawal has to pay for itself

`HarvesterTuning.WorkCycleTicks` is now both bounds of the same rule, and the number is the
delivery cycle rather than a taste: a harvester moves 1.758 cells a game second and measured 10.8
credits a second over the good window, so a cycle is about 65 seconds. Sixty seconds is just
inside one.

* A withdrawal **ends** once it has run longer than the delivery it displaced
  (`economy.harvester-withdrawal-timed-out`), resuming on ground away from whatever drove the
  harvester off — the contested-field memory rule 2 already had.
* Finishing one **guarantees** the harvester a full cycle of earning before another may begin
  (`economy.harvester-work-window`), so contact lands on "keep cutting" instead of "cancel the
  load and shelter for seventeen seconds".

Worst case is now a 50% duty cycle by construction; at the episode lengths actually observed it is
about 22%.

### And the bot had never repaired anything

Zero `RepairBuilding` orders in 913 seconds, while fourteen buildings died — about 14,900 of the
31,900 credits lost — and `valueExchange` scored 0.524 against a reference of 2. The SDK has
exposed the action the whole time; no mode had ever asked for one.

[`BaseRepairLogic`](Logic/BaseRepairLogic.cs) is deliberately small, because repair has a losing
mode too: a base under permanent attack can sink every credit into structures that die anyway.
So `BuildBaseMode` asks it **last** — only on an evaluation where construction had nothing to
order and neither economy hold was running — and it answers only for a structure already at or
below half health, where the alternative to repairing is paying the full cost again rather than
repairing later.

## Nothing was ever concentrated on the thing already damaged

16:9 again, at Hard, lost after 1,042 seconds: **27 buildings destroyed to 1**. Three separate
symptoms, one shape.

### The push spread 378,000 damage across a base and finished one building

From the engagement matrix: 227,720 damage into construction yards, 55,920 into refineries that
need about 72,000 each, 49,850 into guard towers, 28,875 into an advanced tower. One kill.
`buildingsDestroyed` scored **0.125 of 1.0**, the worst component in the fight.

[`AttackBaseLogic.SelectObjective`](Logic/AttackBaseLogic.cs) scored class and proximity and
ignored `HealthPercent` outright, so every unit independently walked at whatever was nearest to
*it* and a force arriving together still split its fire across a whole base — which the other side
then repaired.

Damage is the one signal that makes an uncoordinated force converge, because the force leaves it
behind itself: missing health is now worth **40 points a percent**, which dominates the
2,000-point gap between a production structure and a static defence, so a building at half health
outranks a pristine one of any class. The objective a unit already holds keeps a **900-point**
commitment bonus — enough to stop it swapping between two untouched buildings, never enough to
keep it on an untouched one while the rest of the push is halfway through something else.

Stickiness is suspended in exactly one case, and the two bounds on it are what keep the search off
the hot path: an objective that is **untouched** *and* **out of weapon reach**. A unit in range is
about to damage its own objective, and a unit whose objective is damaged has something invested;
both commit, and neither rescans. `assault.join-damaged-objective` marks the swap and
`assault.finish-damaged-objective` marks the shot that follows it.

### The screen held the middle of a base that was dying at its edges

`DefensiveMode` produced **16,377 `Hold` decisions against 3,033 `Attack` for `e1` alone** — 84% of
its evaluations were *on post, no threats* — while losses clustered at seven places **8 to 17
cells** from the construction yard. The mode anchors every unit on `ctx.BaseCenter` and tethers it
within twice its own reach, which for a 4-cell rifle is 8 cells. The outer clusters were never
inside anybody's leash, and the bot's best unit per credit spent the match standing still.

The fix needs no extra units, no wider leash and no map knowledge. Own buildings are known
exactly, so a health bar that **fell since the last look** is a precise report of where the enemy
is — and it works against an 11-cell gun that no defender can see, which is what was doing the
killing. [`BaseDamageWatch`](Modes/BaseDamageWatch.cs) takes that look once per tick for the whole
side, [`BaseGuardLogic`](Logic/BaseGuardLogic.cs) decides which report to believe, and
`DefensiveMode` points `ctx.Anchor` at it.

Stickiness again, for the same reason: a screen that chased the most recent hit would flip between
two raids every tick and arrive at neither. The post moves only to somewhere **strictly worse
hurt**, stays hot while its building is still being shot, expires thirty seconds after that stops,
and is dropped immediately if the building ceases to exist. Ties break on health and then actor
id, so every unit reaches the same answer without coordinating. Immobile defences and unarmed
units are excluded. `defence.guard-damaged-building` marks the walk.

### Then it stood on the target and held position anyway

The screen arrived. That was not the same as answering anything. On the next 16:9 — lost after
1,400 seconds, fitness 0.247 — `DefensiveMode` returned `Hold("on post, no threats")` **7,368
times** while GDI rocket launchers took the base apart from **eleven cells**, and the whole of
this side's reply to them was **zero damage, all match**: every `msam` row in the engagement
matrix reads `damageDealt 0`. Those guns killed **28 of 82 losses, 17,400 credits, 47% of
everything the side lost** — the airstrip, the construction yard, both barracks, three refineries,
two towers and six of nine harvesters. Production ended with the buildings, and the last 732
seconds bought 141 credits of anything.

Nothing on this side reaches eleven cells. `e1`, `bggy` and `ltnk` reach 4, `e3` and `gtwr` 6. A
piece parked at its own maximum range is, to everything this bot fields, a weapon that cannot be
answered — and it sits in fog, so `SenseThreats` returns nothing to answer it with.

[`CounterBatteryLogic`](Logic/CounterBatteryLogic.cs) has been the answer to exactly this since
badland-ridges. It was wired into `AttackBaseMode` only, and the `Attack` doctrine ran for **zero
seconds** of that match: the army peaked at 2,200 credits against a 4,000 commitment bar, so not
one `assault.*` id appears anywhere in the trace. A rule that is right and unreachable is worth
what a rule that is wrong is worth.

Two things changed, and neither is a new idea:

* `DefensiveMode` now consults it — last, after the ordinary rules, and only when the unit has
  **nothing at all inside its own leash** and is not withdrawing for repair. A unit that already
  has something it can hurt is never pulled off it. `defence.counter-battery` marks the walk.
* The report is **side-scoped**. Of the 863,494 damage those guns dealt, roughly 13,000 landed on
  something with a weapon; the rest went into refineries, power plants and harvesters, none of
  which can shoot back and three of which cannot move. A memory written only by the unit that was
  hit hears almost none of a siege, so [`ShellingReports`](Modes/ShellingReports.cs) keeps one
  slot for the whole side — the fifth sibling of `BaseDamageWatch`, `EarnerUnderFire`,
  `ContestedGround` and `EnemySightings`, and written from the same damage notification
  `HarvesterThreats` already relies on.

Stickiness again, and the ranking is the interesting part: of two live reports the one that came
from **furthest out** wins. Standoff is the whole complaint. Something shooting a refinery from
four cells is already inside the reach of the riflemen standing on it and the ordinary scorers
will take it; something shooting it from eleven is what nothing on this side has ever touched.

The bounds are unchanged and they are what keep this from becoming the chase every defensive rule
exists to refuse. Twelve cells from the reading unit — the longest mobile reach in the ruleset
plus a cell — so nothing further away can be walked at. Twelve seconds of memory, so a gun that
has displaced or stopped firing is forgotten rather than followed. Twenty-four evaluations for a
unit's **entire life**, never refilled, and pointedly not reset by `OnEnter`, because this mode is
re-entered on every doctrine change and a budget a doctrine switch refills is not a budget.

A gun still in fog cannot be given as a target at all, which is the ordinary case rather than the
exception, so the rule attack-moves at the cell it fired from instead — the same answer
`HarvesterEscortMode` already gives a harvester's unseen attacker, and it ends in a shot for the
same reason: the unit arrives with the gun inside its own reach and the scorers take over. And
`ModeContext.HasPosition` gates every attacker before its location is read, because a superweapon
credits its damage to the firing player's actor, this side lost a refinery to one on 16:9, and an
exception raised inside a damage notification ends the match rather than the reaction.

### And the repair band was narrower than the fight

Repair was added last round and it ran — 12 orders. It was not enough, and the ordering was not
why. The yard reached the branch that asks on roughly **900 evaluations** (591 `Continue`, 330
`Hold`) and found a qualifying building on **24** of them. The opportunity was never the
constraint; the 50% band was. A building under fire crosses half health and dies in the same few
seconds, so a rule that only opens there opens after the race is lost.

`RepairBelowHealthPercent` is now **75**. Widening it is nearly free because OpenRA charges repair
by the hit point rather than by the order — topping a 1,500-credit refinery up from three-quarters
costs roughly a twentieth of replacing it. What it buys is the case that actually decides a siege:
chip damage between salvos and between air passes, where a fixed repair rate wins. The ordering is
untouched, so income keeps the absolute priority every rule above it assumes.

## The fleet learned it five times and never remembered it once

16:9 at Hard, lost after 958 seconds: **23.017 credits a second earned** against a reference of 50
and a prior median of 32.014, mean army 446 against a reference of 6,000, and no enemy building
destroyed. Every failing check and every flagged regression is downstream of the first number —
`armyValueIntegral` scored 0.0743 because there was never any money to buy an army with.

The fleet was not stuck in the engine's search bubble, and it was not commuting: `MaxHaulCells`
held, and the withdrawal bound from the previous round held too, taking withdrawal duty from 43%
down to about a third. It was being shot, in the same place, over and over.

`units.csv` names the place. Five of the seven harvesters built died inside a **four-cell circle** —
(25,28), (28,29), (24,28), (25,28) and (28,30) — one after another between 538s and 676s, and
`summary.lossClusters` carries them in a single 67-unit, 20,850-credit cluster running from 188s to
949s. Across 2,041 harvester-alive seconds the side earned 10.8 credits per harvester-second
against the 16.3 a working harvester manages, spent 28 withdrawal episodes and **663** follow-up
`escape-en-route` and `sheltering` evaluations against only 42 `assign-field`, and left **335 of
958 seconds with no live harvester at all**.

### The memory was already there. It just died with the unit that had it

`HarvesterWatchdog.ContestedX` records the field a harvester was shot off, and
`SelectFieldAvoiding` already steers the next assignment away from it. Both live in the mode
instance, and there is **one mode instance per unit**, reset in `OnEnter`.

So the knowledge was destroyed at exactly the moment it was proven. The replacement harvester
started with a blank sheet, asked `Score` which patch was best, and `Score` is a pure statement of
what a patch holds — it cannot see the rocket infantry standing on it. The only thing that could
see them was dead. Each harvester paid full price for the same lesson and took it to the grave.

[`ContestedGround`](Modes/ContestedGround.cs) promotes the report to the side, keyed on the owning
player like `BaseDamageWatch` and `EnemySightings`. One slot, most recent wins, mirroring the
single slot it shadows: the question is "where did this side last get shot off", and a list would
only let a stale entry outvote a live one. Nothing in it is map knowledge — every coordinate is a
place one of this side's own harvesters was standing when it was shot.

* A harvester with no report of its own, or an older one, **adopts** the side's
  (`economy.harvester-inherits-contested`). A replacement now avoids the ambush on its first
  assignment instead of discovering it.
* A harvester already working a field the fleet was driven off **leaves before it is shot**,
  through the eviction branch rule 2 already had.
* First-hand reports win over inherited ones, and only first-hand reports are published, so a
  seeded value can never be echoed back and refresh itself into a permanent exclusion.

### Ground has to come back

`ContestedMemoryTicks` is three work cycles. The floor is one — a field given up for less time than
the delivery it displaces is not given up at all. The ceiling exists because tiberium is finite and
raiders are not stationary, and ground abandoned forever is ground handed over one patch at a time.

The number only governs how long after the shooting *stops*, because any harvester driven off the
same field refreshes the entry: a patch still being camped stays excluded for as long as the
camping lasts, and one whose attacker has left is retried three cycles later. And the avoidance it
feeds is a soft preference with a fallback — it may refuse ground, but it can never refuse the last
field on the map.

## The push arrived, and then it stopped

`16:9` at Hard was won — fitness 0.9738, with every component except match length at its cap — and
it was still the most idle match this bot has played. **46,305 idle unit-seconds against a prior
median of 9,710**, the one regression the harness flagged. The cause is a single branch.

`AttackBaseMode.Approach` marched the army at the remembered enemy base. The moment a unit stood
within five cells of it with no structure in sensor range, the method returned `ApproachOrders.None`
— no orders at all — and `AttackBaseLogic` fell through to `Hold("no objective assigned")`. Nothing
moved that unit again until somebody else refreshed the side's sighting to a different cell.

| the trace | count |
| --- | --- |
| `Hold("no objective assigned")`, evaluated | **62,649** across 180 actors |
| ...of which `e1` / `arty` / `e3` | 29,651 / 8,280 / 5,258 |
| every other assault evaluation combined | about a third as many |

It was priced, too. Standing in the other side's half of the map is where this army was shelled for
free: 17 `e1` and 9 `e3` killed by enemy `arty`, `e1` returning **400 damage for 97,500 taken**, and
three of the four largest loss clusters sitting deep in their territory rather than at home. The
army banked 32,200 credits of which a mean of 9,745 was ever committed, and the other side was
still alive with a building standing at 1,329s.

### A push with nothing in front of it hunts

[`AssaultSweepLogic`](Logic/AssaultSweepLogic.cs) is the fix, and it reuses a ladder that was
already written. A unit that has arrived somewhere and found nothing walks the next rung of
[`ScoutSearchLogic.Objective`](Logic/ScoutSearchLogic.cs) — the single-axis mirrors, then the map's
rim — as an `AttackMoveTo`, so it fights through what it meets and explores permanently on the way.

Three bounds keep it from becoming the opposite bug:

- **The rung belongs to the side**, in [`AssaultSweeps`](Modes/AssaultSweeps.cs), keyed on the
  player exactly as the sighting memory is. 180 units each running a private search would deliver
  the army to 180 corners one unit at a time. It only ever advances, so a side that has swept half
  the map carries on from there rather than re-walking what it has answered.
- **It is pre-empted by anything already in reach.** A unit with a target inside its own weapon
  range falls through to the last-stand scorer and shoots it; the hunt waits for the next
  evaluation. Walking away from something you can already kill is never the better trade.
- **It never deletes a sighting and never outranks an objective.** As soon as a structure enters
  the 40-cell sensor radius the objective rules take over on their own. A sweep is only ever what a
  unit does instead of nothing.

A rung retires when somebody stands on it and still sees nothing, debounced by fifteen seconds so
the fast half of a column cannot run the destination away from the slow half, or on a sixty-second
clock, which is the only evidence available that a cell cannot be reached at all. Immobile actors
are excluded from both: a guard tower reporting that it is standing on the hunt's current cell
would retire a rung the army has never been to.

## The fleet died with a factory still standing

The next `16:9` at Hard was lost, and the reason is one number: **585 seconds — 32% of the match —
with no live harvester**. Lifetime earnings froze at 55,930 credits at 1,260s and never moved
again, which is what dragged income to 30.8 credits a second against a reference of 50 and a prior
median of 38.5.

The tempting reading is that the harvesters were badly handled. They were not. What happened is
that nobody ever bought another one.

| the fleet | |
| --- | --- |
| peak, at 780s | 9 |
| 1,140s → 1,320s | 9, 6, 4, 1, **0** |
| last `harv` ordered | **747s** |
| war factories alive until | **1,437s** and **1,505s** |
| cash sampled at 1,140s / 1,200s / 1,260s | 1, 0, 157 |
| `Infantry` deliveries in that window | `e1` at 1,218s, `e3` at 1,440s |

A harvester was buyable for 690 seconds after the last one was ordered. The `Vehicle` queue simply
never held 1,100 credits at one time: a queue buying 100-credit riflemen out of a trickle always
beats a queue saving for an 1,100-credit earner, and the barracks always has an unmet rung because
infantry die.

Across the last ten runs this is not a detail. Ranked by fitness, the four best runs are exactly
the four highest incomes (57.0, 66.8, 68.0, 64.3 credits a second) and the six worst are exactly
the six lowest (22.6 to 38.5). No other metric separates them.

### A cap cannot stop a queue spending what it is already holding

[`IncomeFirstLogic.Hold`](Logic/IncomeFirstLogic.cs) was already aimed at this race and cannot win
it. It caps each `Infantry` rung at four bodies — and the plan has five rungs, so the barracks
answers a cap with the next rung down and spends the same credits anyway.

The host offers the thing that does work, and this bot was using it for the first 155 seconds of a
match and then switching it off. `ReserveOpeningBank` holds a refinery's price for the construction
yard while the side is below three refineries; past that, nothing arbitrated the shared bank at all.
[`ReserveHarvesterRecovery`](Logic/IncomeFirstLogic.cs) is the other half: once four refineries
stand, one harvester's price is held for the `Vehicle` queue whenever the fleet is short of the
docking places those refineries provide.

It is narrow on every axis, and each bound is a number the bot already had:

- **Four refineries, not three.** `BattleState` cannot see a war factory, so plan order is the only
  evidence that a harvester is buyable: `Economy` buys `weap`/`afld` at rung seven and its fourth
  `proc` at rung ten. Handing over at three would arm the reservation in the exact window the yard
  needs 2,000 credits clear to buy the factory that spends it.
- **The fleet the refineries already standing were bought to feed**, two per refinery, capped at the
  `Vehicle` plan's own saturation figure of eight. No new target is invented.
- **Self-limiting by construction.** The host suppresses another queue's order only when its full
  cost would take live cash *below* the reservation, so a side with 2,000 banked buys its rifleman
  as usual. Only the last 1,100 is defended — which is the sum the vehicle queue was short of.
- **Released while the base is overrun**, for the same reason the opening reservation is: bodies now
  beat income later once they are already inside the base — *unless the fleet has already
  collapsed*. See "Bodies bought out of a dead bank are not defence" below.
- **Bounded in both directions.** It stands down after sixty seconds in which the fleet has not
  grown, and re-arms sixty seconds later rather than permanently. The obvious stranding case — the
  factory dying while the fleet is short — is handled by the host instead: a reservation naming a
  queue this side does not own resolves as *unmatched* and suppresses nothing.

## Bodies bought out of a dead bank are not defence

The next `16:9` at Hard was lost with the whole base still standing at 1,200s, and the evidence
puts the whole of it in one window. **From 780s to the end of a 1,379-second match, every
assessment reported `units 0`, `armyValue 0` and `cash 0`** — 599 seconds, 43% of the match — while
a war factory stood until 1,359s, a barracks until 1,363s and a refinery until 1,379s. Lifetime
earnings froze at 42,880 credits at 780s and never moved again. The side did not lose an army; it
lost the ability to buy one.

Two rules produced that, and they are the same mistake written twice: **the bot suspends its
economy exactly when the base is under attack, and the suspension is what ends the match.**

### The withdrawal was driving harvesters into the raid

`HarvesterLogic`'s escape rule aims every withdrawal at the refinery or the base centre, on the
argument that home is the only ground this side keeps guns on. That argument stops being true the
moment the raid *is* the base:

| the withdrawal, on 16:9 | |
| --- | --- |
| withdrawal `MoveTo` orders issued | **1,235** |
| ...naming a cell within 5 cells of this side's own yard | **751 (61%)** |
| `enemiesNearBase` over the same window | **16 → 75** |
| `economy.harvester-sheltering` — a `Hold`, at the dock | **205** |
| harvester evaluations that were withdrawal or shelter | **973 of 1,199** |
| harvesters built / lost | **9 / 9**, eight of them to enemy `e3` |
| credits earned from 780s | **0**, with eight harvesters and five refineries alive |

A `MoveTo` cancels the harvest activity and throws away the partial load, and `Hold` on a harvester
suppresses the engine's own delivery behaviour outright. So the rule bought no survival at all —
the whole fleet died anyway — and cost the entire economy.

**Arriving is the end of a withdrawal, because there is nowhere further back to go.** Once the
harvester is inside `SafeDistanceUnits` of its refinery the rule has delivered everything it can: a
second order to the same cell moves it nowhere. It now ends there, starts its earning window, and
goes back to work (`economy.harvester-works-the-raid`); and it may not start a withdrawal from
there either. A harvester that keeps working is no easier to kill than one parked on the same cell,
and the load it delivers is the only thing that buys its replacement.

### ...and the one rule that could rebuild the fleet was switched off

`ReserveHarvesterRecovery` is the bot's only defence against a `Vehicle` queue that never holds
1,100 credits at once. The `production-budget` trace reads `inactive`, `reservedCash 0` at
**every** assessment from 690s to the end of the match, for two reasons that were both permanently
true:

| release | assessments after 600s where it held |
| --- | --- |
| `EnemiesNearBase > 0 \|\| BaseUnderAttack` | **151 of 156** |
| `Refineries < 4` (the third refinery fell at 813s) | **113 of 156** |

Both releases describe the situation the rule exists for. "Bodies now beat income later" is true of
a raid on a working economy and false of a side that has none: a hundred-credit rifleman bought out
of a dead bank is not defence, it is the reason the eleven hundred never accumulates. And the
refinery floor is a hand-over to `ReserveOpeningBank` that is only correct *on the way up* — a side
that has lost refineries back through the floor has a factory, and the opening rule stops at three
and will never cover it.

So a fleet at or below `FamineFleet` with a refinery still standing now overrides all three
releases and the duty cycle with them, under `economy.bank-restarts-income`. It is self-terminating
— buying one harvester leaves the famine band and the ordinary releases resume — and self-limiting
when it is not, because a side earning nothing has nothing for the reservation to suppress.

## The economy can only work the ground somebody has looked at

The next `16:9` at Hard (Nod against Nod) was lost at 1,405s. **The fleet worked the three fields
beside the yard until they were gone, and no unit ever looked for a fourth.**

| | |
| --- | --- |
| income 420-480s | 122 credits a second, 7 harvesters |
| income 540-600s | **37**, 8-9 harvesters, before the first enemy shell landed at 583s |
| harvester reasons from 554s | "field worked out, harvesting 4-9 cells (16-39 left)" |
| enemy income 300-1000s | 107-142 credits a second |
| `ScoutMode` time with a live screen vehicle | **34 seconds** (375-409s), straight at their base |

Resource reads are shroud-filtered, so tiberium within haul reach that no unit has looked at does not
exist for `FindResourceFields`. The only scouting of the match was two `bggy` driving rung 0 of
`ScoutSearchLogic` to the enemy's yard, where both died at 421-423s. The rest of their lives was
spent in `HarvesterEscortMode`, because Defence ran for 970 of the match's 1,405 seconds.

[`TiberiumSurvey`](Modes/TiberiumSurvey.cs) and [`SurveyLogic`](Logic/SurveyLogic.cs) fly one pass
over our own half of the map: a 12-cell lattice (a screen vehicle sees 8) inside the haul reach and
nearer our yard than its point mirror, visited nearest-first with the ground toward them last. The
route and the progress along it belong to the side, so a replacement picks it up where a dead
surveyor stopped. One `jeep`/`bggy` at a time flies it, from both `ScoutMode` and
`HarvesterEscortMode`, which are the two modes a screen vehicle runs outside an assault. Once it is
flown it is over and both modes behave exactly as before. Its reason ids are `economy.survey-tiberium`,
`economy.survey-evade`, `economy.survey-skip-point` and `economy.survey-complete`.

`HarvesterMode` now also ranks fields on `TotalValue` rather than `TotalDensity`, falling back to
density where a mod declares no value, so blue tiberium outranks green at the same size. Every use
of that number in `HarvesterLogic` is a ratio or a `> 0` test.

### Two things in this match that are not what they look like

- **Earnings froze at 51,270 from 1,020s with eight harvesters alive**, and that was not a
  harvester fault. One enemy nuclear strike at 939s (killer `player`) destroyed the construction
  yard, both `hand`, both `afld`, the `hq` and a refinery together. The side then had nothing to
  spend on, cash sat at 2,966 (three refineries' storage), and whatever was harvested past that cap
  was lost.
- **`c17` in the unit ledger is the Nod airstrip's delivery plane**: 25 "built", 50,000 "spent".
  None of it was bought. The headline's `creditsSpent` (58,770) is the real figure.

## The survey found the middle of the map, and the fleet moved into it

The next `16:9` at Hard (GDI against Nod) was lost at 877s with fitness 0.226 against a prior median
of 0.634. **The survey worked, and the ground it revealed killed the economy.**

| | |
| --- | --- |
| 245s | an in-base raid drives a harvester off the 8,225-credit field 10 cells from the yard |
| 253s | all fifteen raiders are dead; the field stays excluded fleet-wide until ~425s |
| 279-282s | all four harvesters leave a home field with ~2,000 left for a 13,160-credit field 34 cells out, scored ~3× higher |
| 293s, 342s | both replacement harvesters are sent to the same field |
| 373s | an enemy construction yard is spotted 9 cells from it |
| 379-405s | `bike` and `e3` kill all six harvesters on the road home, 21-26 cells from the yard |
| 405-877s | no harvester (525 s in all), cash 0, income frozen at 14,075; `heli` then takes the base apart |

The score is what a field pays per cell of commute, and it cannot see which side of the map the
commute crosses. That field lay 1.22 times as far from the point mirror of our yard as from the yard
(0.9-1.4 against the enemy structures the jeep found at 309-316s); every field worked without loss lay
4.9-6.5 times as far.

**Home ground now comes before the frontier** ([`HarvesterLogic.IsFrontier`](Logic/HarvesterLogic.cs)).
A field is home ground when its centre lies at least `HomeGroundRatioPercent` (150%) as far from the
enemy as from our base centre. "The enemy" is the nearer of the map's point mirror of our base and the
last enemy structure a scout recorded. Frontier fields are a fallback tier inside the contested
avoidance and above the haul ceiling, so the rule may still refuse ground but never the last ground.
A harvester on the frontier returns when home ground worth half its field comes back. **A home field
the side was driven off is offered back after one work cycle** (`HomeContestedMemoryTicks`) instead of
three, and a harvester's first-hand memory of being driven off now expires by the same clock rather
than never. Reason ids: `economy.harvester-declines-frontier` (home ground changed the answer),
`economy.harvester-frontier-fallback` (no home ground left), `economy.harvester-leaves-frontier`.

Not changed, and worth reading next time: the infantry screen answered **3,825** "on post, no threats"
evaluations over 340-410s while the fleet died 21-26 cells out, beyond `EarnerRadiusCells` (16). A
fleet that is kept on home ground keeps inside that radius; the fallback to frontier ground does not.

### Two things in this match that are not what they look like

- **The fight did not run this source.** Its DLL was built at 21:55 from an uncommitted candidate,
  and the restored source files carry older timestamps, so an incremental build would never have
  replaced it. The trace's `economy.harvester-withdrawal-off-field` ("shot away from its field so the
  field stays open") exists in no commit. That variant is what sent three harvesters that had escaped
  home back into the raid via `economy.harvester-works-the-raid`; committed code always marks the
  field contested. Check a trace's reason ids against the source before reasoning from them.
- **`units.mean(lifetimeSeconds,type=harv)` read 83.5 because it counts enemy harvesters too**, whose
  blank lifetimes count as zero. Own harvesters lived 194.8 s on average. Add `owner=Commander`.

## The opening was ordered by the shroud, and the engine could see through it

The next `16:9` at Hard (GDI against Nod) was lost at 1,842s — but slowly, and on economics. Home
ground held: own harvesters lived 634 s on average (194.8 before), income was 42.2 credits a second
(16.0 before) and every doctrine episode traded above one for one on value. The opponent simply
earned **205,725 credits to 77,785** with the same income per harvester (7.0 against 7.9 credits a
second at 900-1,200s) and twice the fleet, on ground it expanded onto.

The gap does not start in the mid-game. It starts in the first two minutes, in the same way in
every recorded 16:9 match, and it is this bot's doing:

| | this side | opponent |
| --- | --- | --- |
| earned at 120s, all 13 scored runs | **700** every time (one load) | 700-2,400 |
| earned at 180s, median of 13 | 2,765 | 4,200 |
| earned at 300s, median of 13 | 9,225 | 11,990 |
| income per harvester-second, 0-300s, this match | 13.9 | 20.5 |

The harvester orders repeat run after run: in all fourteen recorded openings the first harvester is
sent at 51s to a **4-cell patch 16-17 cells out** — the only ground the yard's sight reaches — the
second at 103s to a field 9-10 cells out, and in half of them the third at 155s to **the same field
21-24 cells out**. In this match a full green field lay 8-9 cells from the first and third
refineries, and a blue one about as near the second, none of it explored until the jeep flew the
survey at 229-284s, when all three crossed to it.

`FindResourceFields` honours the shroud, correctly. The engine's own harvester search does not —
it reads the resource layer from the refinery a harvester is created at, as any player's harvester
does — and a new harvester is created already running it. The first explicit order replaced a
search that could see the home fields with a destination that could not.

**A fresh harvester is now left to the engine's search until the home survey is flown**
([`HarvesterLogic.DefersToEngine`](Logic/HarvesterLogic.cs)), provided it has never been assigned
a field, holds no contested report, is not under fire, has moved within `EngineSearchLiveTicks`
(ten seconds), and the best field this side can see is more than `EngineFirstCells` (eight) from
its refinery. Any of those failing hands it straight back to the explicit rules, so a search that
finds nothing costs one review rather than a match, and once the survey is flown nothing differs
from the last round. `TiberiumSurvey.IsFlown` supplies the survey flag. Reason id:
`economy.harvester-engine-first`.

### Two things in this match that are not what they look like

- **`idleUnitSeconds` rose to 18,977 without anything idling.** 8,089 of it is harvesters working
  without needing a new order for longer than 15 seconds — which is what a harvester that lives 634
  seconds on home ground does — and most of the rest is structures. Combat units account for
  about 1,800.
- **`secondsWithNoHarvester` failed its check at 336 because of the last 300 seconds**, when enemy
  `arty` outranged the towers from 1,236s and `heli` (21 of the 35 buildings lost) then took the
  base apart. The fleet did not die on the road this time; it died with its refineries.

## Thirty-seven rocket soldiers against an army with no tanks

The next `16:9` at Hard (Nod against Nod) was won at 1,190s, fitness 0.89. The engine-first opening
held: 1,400 earned at 120s (700 in every earlier run) and 4,060 at 180s. Every fitness component
scored 1.0 except `armyValueIntegral` (mean army 3,706 against a reference of 6,000) and
`survival`, which is only the length of a won match.

The opponent fielded **98 `e1`, 85 `e4`, 80 `e3`, three `heli` and not one combat vehicle**. This
side logged 283 infantry sightings against three aircraft, one vehicle and 16 economy actors, so
about 7% of what it saw wore armour. The endless rung read that correctly and bought rifles. The
**bounded** rocket rungs had no way to. They ask for 4 then 12 in Opening, 2 then 8 in Defence and
8 in Attack, and a bounded rung that names something that dies re-buys it every time it does:

| | built | spent | kills | credits killed | credits a kill | died |
| --- | --- | --- | --- | --- | --- | --- |
| `e3` | 37 | 11,100 | 8 | 5,800 | 1,387 | 29 |
| `e1` | 111 | 11,100 | 126 | 24,300 | 88 | 71 |

Against enemy `e4` the rockets scored 1 kill for 11 deaths, and against `e1` 1 for 9. The duel lab
says this is not one match: at equal cost `e3` is swept by every infantry type (-0.9 against `e1`,
`e2`, `e4` and `e5`), and `e1` beats `mtnk` (+0.7) and `ltnk` (+0.8) about as well as `e3` does.
The one thing only a rocket does is shoot upwards (+0.9 against `orca`, +0.6 against `heli`).

**Against an infantry army, every bounded rocket rung is now capped at an anti-air screen**
([`ArmyMixLogic.RocketCeiling`](Logic/ArmyMixLogic.cs), `CapBounded`). The cap is
`DefenceAntiAirCore` (two) plus one per distinct enemy aircraft this side has seen. It applies only
once six or more enemies have been seen and under 60% of them wore armour, the same test the endless
rung already uses. It only ever lowers a bounded rung, so a side that has met nobody, or has met
armour, trains exactly what it trained before. `EnemySightings` now counts aircraft separately.
Airstrike planes are never counted, because the SDK drops actors with no target types from
sensing. A barracks decision is tagged `production.rockets-held-to-air-threat` only when the
uncapped plan, carrying the same funding caps, would have bought a rocket there.

### Two things in this match that are not what they look like

- **`c17` is 53.8% of spend in the unit ledger, and none of it was spent.** It is the Nod airstrip's
  delivery plane, about one per vehicle delivered (45 planes, 44 vehicles), priced at 2,000 each. The per-type spend column
  sums to 167,250 against 73,583 actually spent, so read shares of spend with `c17` removed.
- **`idleUnitSeconds` rose to 19,542, and it is not idle combat units.** 6,234 of it is nine
  harvesters, alive 759s on average, working without needing an order. About 7,000 more is
  structures (`gtwr`, `sam`, `mcv`, `fact`, `afld`, `hand`).

Not addressed this round, and worth measuring next: one `a10` napalm run at 527-529s killed three
`arty` and six `e3` (3,600 credits, 12% of everything lost) packed inside two cells at (68-72,
36-39). Both failed pushes, at 460s and 680s, launched at 4,400 of army and bled to the 1,500
retreat floor. The second went in against an opponent whose army was 6,600.

## A scratched building switched self-defence off

The next `16:9` at Hard (GDI against Nod) was won at 729s, fitness 0.94, with every component at
1.0 except `survival`, which for a won match is only its length. The loss ledger still had one
shape in it, and the shape was a rule.

`AttackBaseLogic.Decide` let an objective screen pre-empt the objective only while the objective
was **untouched**. A building in a base assault is untouched for about one volley:

| in-range evaluations, whole match | count |
| --- | --- |
| `assault.finish-damaged-objective` | **5,115** |
| `assault.objective-in-range` | 49 |
| `assault.screen-before-objective` | **17** |

So once anybody had scratched a building, nothing in weapon range of it defended itself. Joining
each own loss in `battle.csv` to its last evaluated decision: **13 of the 36 units lost in the
assault died on `finish-damaged-objective`**, 11 of them riflemen finishing a Nod `sam` — a weapon
that only targets aircraft, on Concrete that `M16` does 10% to — while **one `bggy` killed eight of
them in twelve seconds** (389-401s) at the staging ground. The sampled hit log agrees: the largest
share of enemy hits taken during the assault (33) landed on units holding that decision.

**A damaged objective now yields to a mobile shooter when the objective cannot shoot back**
([`AttackBaseLogic.SelectReturnFire`](Logic/AttackBaseLogic.cs)). The bounds are what keep fire
concentrated: only when the objective cannot hit this unit (a damaged tower that is shooting us is
still finished); only infantry, vehicles and aircraft, never another building; only something able
to hit this unit and inside its own reach of it plus a cell; never outside our own weapon range;
never for a siege piece (reach of 10 cells or more — `msam`, `arty`); and only when the shooter is at
least as good a match for this unit's warhead as the objective, so rifles answer infantry and
buggies while rockets and tanks keep shelling the building. The objective id is untouched, so the
unit resumes the same building when the shooter is dead or gone. Reason id:
`assault.return-fire-over-harmless-objective`.

### Two things in this match that are not what they look like

- **`e1` credits per kill failed its check at 212.8 (≤ 150 asked) mostly on the denominator.**
  100 rifles were built and **54 were alive at the end**: the enemy's army was 0 from 660s while
  this side's grew to 18,900, and the opponent fielded 63 combat units in all against the 266 of
  the fight before. The rifles that did die for nothing are the 11 above, and the five below.
- **`e1,msam` in the engagement matrix is this side's own `msam`.** Its splash dealt 41,697 damage
  to own riflemen (27% of all damage `e1` took) and killed five, four of them while it was aimed at
  an enemy harvester the riflemen were already standing beside. The
  `durationSeconds`, `creditsKilled` and `cellsExplored` regressions are what a quick win against
  a small opponent looks like, not faults: exploration still scored 1.0.

Not addressed this round, and worth measuring next: the push spent **20,261** evaluations on
`assault.sweep-for-targets`. After the last building of their main base fell at 605s it killed
nothing for 96 seconds while the enemy had six buildings and no army, and the last expansion was
on the sweep's sixth rung.

## Start your own


```powershell
./scripts/new-bot.ps1 -Name MyBot
cd bots/MyBot
dotnet build .\MyBot.sln
```

Then in game: `/bots` to see it, `/bot MyBot` to load it, `/why` to ask what it is thinking.

A bot builds against AutoC&C **binaries**, so it can live in its own repository:

```powershell
dotnet build /p:AutoCnCPath=C:\games\autocnc
```

`Logic/` has no engine dependency, so the decision layer is plain C# you can read and reason about
without a game in front of you.

There is no test project here, and adding one is not an improvement. A bot is judged by whether it
wins, which no assertion can tell you, so the verification is a recorded fight:

```powershell
./scripts/run-bot.ps1 -Map tiberium-rift.oramap
```

## Licence

GPL-3.0-or-later, like everything that links against OpenRA. See the repository `LICENSE` and
`NOTICE.md`. Bots you write and distribute inherit the same terms.
