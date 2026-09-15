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
│   ├── HarvesterMode.cs         ←   keeps a harvester earning, on ground that still has
│   │                                tiberium in it (reviews its field on a clock — see
│   │                                 HarvesterLogic)
│   ├── RunHomeMode.cs           ←   template: flees to a refinery when threatened
│   ├── HarvesterEscortMode.cs   ←   guards a harvester
│   └── ScoutMode.cs             ←   wanders, runs from anything armed
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
