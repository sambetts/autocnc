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
when a hard cap says the rest are not coming. See [`Logic/AssaultStagingLogic.cs`](Logic/AssaultStagingLogic.cs).

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
  so walk outward on a widening octagon, 6 cells then 11, 16, 21 and 24, rotating direction each
  time and clamped to the map, until the harvester finds something to cut. Sustained normal
  behaviour folds the ladder back to the near ring.

There is no resource-sensing API and reading the resource layer under shroud would breach the
guide's fairness rules, so the search is expressed **entirely in move orders** — the same way
`ScoutMode` looks for a base it cannot see. Nothing here knows where tiberium is; it knows only
that a harvester which is idle and motionless is worth nothing where it stands.

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
│   │                                (forms the army up short of their base first — see
│   │                                 AssaultStagingLogic)
│   ├── EnemyBaseSightings.cs    ←   where this side last saw their base
│   ├── HarvesterMode.cs         ←   keeps a harvester earning, and restarts a stopped one
│   ├── RunHomeMode.cs           ←   template: flees to a refinery when threatened
│   ├── HarvesterEscortMode.cs   ←   guards a harvester
│   └── ScoutMode.cs             ←   wanders, runs from anything armed
├── Logic/                       ← pure decision functions, no engine
```

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
