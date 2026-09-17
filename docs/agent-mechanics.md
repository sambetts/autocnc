# AutoC&C mechanics and SDK — gospel

This document is **injected verbatim** into every improvement prompt. It is the half of the prompt
that does not change between rounds: how the game and the SDK actually work.

It is version-controlled and lives outside the bot workspace, so an improvement agent cannot edit
it. That is deliberate. The evolving half of the prompt — what the last round learned — is the
agent's to rewrite; this half is not, because a fact the agent treats as gospel must not be
something a bad round can overwrite.

> **A worked example of why.** A round once inlined "there is no resource/tiberium sensing API"
> into the evolving prompt under the heading "do not rediscover this". It was true when written.
> The API was added later, and every subsequent round still read that line, believed it, and
> steered away from the one fix its harvesters needed — while also being told that using the
> resource layer "would breach the guide's fairness rules", which it does not. The SDK section
> below is therefore **generated from the compiled assemblies**, not typed by hand.

## Objective and match basics

- AutoC&C runs on OpenRA's Tiberian Dawn rules. The bot wins by defeating every opposing player
  while keeping its own side alive.
- A normal side starts with an MCV. It must deploy a construction yard, build power and resource
  processing, create production structures, train an army, find the enemy, and fight.
- Buildings and production require sufficient power. A negative power balance slows the economy.
- GDI and Nod use different actor IDs for equivalent roles. Candidate lists express alternatives:
  `Build("powr", "nuke")` means build whichever faction's power plant is available.
- Distances in engine-facing mode code are world units; 1024 world units equal one map cell.

## How a bot is measured

- A bot is judged by whether it wins, which no assertion can tell you. The verification for a
  strategy change is the next recorded fight and the evidence it leaves behind.
- **Do not write unit tests, and do not add a test project to a bot workspace.** Not for changed
  strategy logic, not for a helper you extracted, not to show a change is safe. Budget spent on a
  suite is budget not spent on battle logic, and the suite measures nothing the next fight does not
  measure better.
- This rule is here, in the half no round can rewrite, because it has already been lost once. It
  was stated only in the evolving half; a round replaced that half without it, and the round after
  re-created the reference bot's test project. Independent verification now **fails** when a bot
  workspace contains one, so a round that adds tests does not finish.
- Keeping the decision layer pure is still worth doing, because it makes strategy readable and
  explainable — but the reason is legibility, not testability.
- An invariant worth remembering belongs in prose beside the code it constrains, which is what the
  next round actually reads.
- A claim about what a change will do belongs in `checks.json`, not in prose. The harness evaluates
  the previous round's checks against the next fight and reports pass or fail with actual values,
  so a prediction is settled by the harness rather than by a later round taking your word for it.

## The evidence a fight leaves behind

Two kinds of artifact. The **raw records** are written by the engine during the match. The
**derived artifacts** are computed from them afterwards by `tools/AutoCnC.Evidence`, once, before
an improvement round is called — so a round reads the answers rather than recomputing them. Every
artifact is versioned and append-only: columns are added at the end, JSON fields are added and
never renamed or removed, and a reader that finds a column missing is reading an older run rather
than a broken file.

### Derived — read these first

| Artifact | What it answers |
| --- | --- |
| `summary.json` | Almost everything. Held under 20 KB so it can be read whole in one go. |
| `units.csv` | One row per unit, whole lifecycle. |
| `check-results.json` | The previous round's checks, evaluated against this fight. |
| `trend.json` | This bot's headline metrics across recent runs, with regressions flagged. |
| `map.json` | Map dimensions, spawn cells, home-to-enemy distance, resource cells, random seed. |

`summary.json` (`schemaVersion` 1) holds: `provenance` (which inputs existed and at what schema —
so a missing number reads as unknown rather than as zero), `fight`, `headline`, `fitness`,
`crossover`, `economy`, `map`, `unitTypes`, `production`, `doctrineEpisodes`, `engagements`,
`lossClusters`, `notes` and `truncated`.

The tabular sections — `unitTypes`, `production`, `doctrineEpisodes`, `engagements`,
`lossClusters` and `economy.series` — are `{ "columns": [...], "rows": ["a,b,c", ...] }`: a CSV
table inside the JSON, one row per line. That is a third of the size of the equivalent indented
objects, which is what lets the whole ledger fit rather than being trimmed. The first column of
every table is its natural key. If a very long match does overflow the ceiling, `truncated` names
each list that was shortened and by how much — a short list is never silently short.

`units.csv` columns: `actorId, type, cost, faction, owner, bornSeconds, diedSeconds,
lifetimeSeconds, killerActor, killerPlayer, deathX, deathY, kills, creditsKilled, damageDealt,
damageTaken, cellsTravelled, secondsIdle, modesUsed, decisionCount, free`. `cellsTravelled` is the
distance between places the unit was *observed*, so it is a lower bound. `secondsIdle` is time
spent in gaps longer than 15 seconds between that unit's own decisions.

### Raw — for questions the derived artifacts cannot answer

- `battle.csv` — what this side could observe: `spotted`, `attacked`, `dealt`, `built`, `lost`,
  `killed`, `doctrine`, `player`, `over`.
- `telemetry.csv` — both sides' curves at one-second resolution.
- `decisions.jsonl` — assessments and every issued unit decision.
- `game-rules.json` — the resolved ruleset, including each actor's `freeActors`.
- `replay.orarep` — the match itself.

### Two traps that have produced wrong analyses

- A `killed` row is written from the **victim's** point of view: `player` owns the victim and
  `otheractor` is the killer. This side's kills are the rows where `otherplayer` is this side.
- `cash` is a stock. `cash = 0` cannot distinguish "earning nothing" from "spending it the instant
  it arrives". The cumulative `earned` and `spent` columns are the flows.

### checks.json

Written by a round into its own bot workspace; evaluated by the harness against the *next* fight.

```json
{ "schemaVersion": 1, "authoredForRevision": "abc1234",
  "checks": [ { "id": "escort-runs", "description": "the new branch executes",
                "query": "reason:escorting harvester", "operator": ">=", "value": "1" } ] }
```

Queries: `summary.<dotted.path>`, `summary.unitTypes[<type>].<column>`,
`summary.production[<queue>].<column>`, `units.count(type=x)`, `units.sum(<field>,type=x)`,
`units.mean(<field>,type=x)`, `units.max(...)`, `units.min(...)`, and `reason:<literal>` for the
number of decisions whose reason contains that literal. Operators: `>=`, `>`, `<=`, `<`, `==`,
`!=`, `contains`, `present`, `absent`.

The `reason:` form is the important one. Give any new code path a reason literal nothing else uses
and assert it: that is what separates "the new branch is wrong" from "the new branch never ran",
which are the two explanations that get confused when the same bug reappears under a new name.

### Fitness

The score is graded rather than a single bit, with named components reported separately:
`economicRate`, `valueExchange`, `armyValueIntegral`, `buildingsDestroyed`, `exploration` and
`survival`. A change that loses the match while doubling income shows up as a component that
improved, which is the difference between partial progress and noise. Each component's `reference`
is the value that scores 1.0, and those references are fixed constants — a score normalised
against its own match would rate every match average and could never show a trend.

### History is for deciding, never for memorising

The cross-run index and `trend.json` exist for the improvement agent between matches. They are
never readable by a running bot, never compiled into one, and must never justify a constant in
strategy code. No literal map cells, no branching on which map or opponent was drawn, no constant
tuned to one seed. Deriving positions at runtime from `BattleState` and `ModeContext` is the only
permitted way to know where anything is.

## Economy, and the harvester trap

- Cash comes from harvesters returning resources to refineries. A plan that loses every harvester
  or stops replacing them will eventually stop.
- **OpenRA's built-in harvester search is radius-capped and never widens**: 12 cells from the last
  cell it cut, or 24 cells from the refinery. Once the tiberium inside that bubble is exhausted the
  harvester waits, re-searches the same dead bubble, and waits again for the rest of the match.
  Nothing the harvester does by itself escapes it. This is the most common cause of an economy that
  dies partway through a fight while tiberium is still on the map.
- The fix is to read the resource layer and give an explicit order. `ctx.FindResourceFields(...)`
  returns every tiberium field on the map with no radius cap, nearest first;
  `UnitDecision.Harvest(field.NearestX, field.NearestY, reason)` re-centres the engine's search on
  that field. **`UnitDecision.MoveTo` will not do** — it parks the harvester on the tiberium and
  stops it, because a move order cancels the harvest activity.
- Aim at a field's `NearestX`/`NearestY`, not its centre; the centre drives the harvester through
  the field to the far side. `TotalDensity` is what is actually left, so a field mined down to a
  rind has a large `CellCount` and a small `TotalDensity`.
- `FindResourceFields` walks every cell and flood-fills each patch, so it is a scan rather than a
  lookup. Call it when a harvester has run out of work, not every tick.
- Refinery placement matters for the same reason. A refinery built next to a large field keeps its
  harvesters inside their own search bubble far longer than one built in the middle of a base.
- Tiberium regrows, so a field is not gone forever — but it regrows far more slowly than a
  saturated harvester fleet consumes it.

## Information and fairness rules

- Strategy code may use only `BattleState` and information exposed by its unit's `ModeContext`.
- Enemy information is visibility-filtered. Do not derive strategy from omniscient telemetry or
  encode facts that the side could not know during the match.
- Own economy, forces, queues, and buildings are known exactly. Enemy actors are known only when
  visible, except for facts the bot legitimately remembers such as having found an enemy base.
- **Reading the resource layer through `ModeContext` is fair play, not cheating.** Those reads are
  shroud-filtered for you: a cell the side has never explored reads as empty, exactly as it does
  for a human player. There is no need to add a second fairness check of your own.
- Because resource reads respect shroud, **scouting is an economic act, not just a military one**.
  Measured on `tiberium-rift`: the map holds 366 tiberium cells, and a side that never scouts has
  explored 36 of them — about a tenth of the map's income. Exploration is permanent, so one cheap
  fast unit exploring early raises the income ceiling for the whole match. A harvester stalling
  with "no tiberium in sight" is telling you the bot has not scouted, not that the map is mined out.
- A damage callback can identify an unseen attacker because the attacked unit receives that same
  notification. Do not generalize that exception into map-wide enemy knowledge.
- Improvement happens between matches. Edited assemblies require a rebuild and a fresh game; do
  not attempt live code replacement.

## Decisions and common semantics

- `UnitDecision.Continue` means leave the unit's current activity alone.
- `UnitDecision.Hold` stops an idle combat unit; using it on a working harvester can prevent useful
  default behavior.
- Attack, movement, retreat, deploy, production, and placement decisions become normal player
  orders a few ticks later. Do not assume an order applies immediately.
- Returning the same intent repeatedly is cheap because the host suppresses duplicate orders.
- Target selection should be stable. Re-picking equivalent targets every evaluation makes units
  dither.
- Drive a production queue only when `ctx.OwnsQueue(category)` is true.
- For deployment, check both `ctx.CanDeploy` and `ctx.DeploysIntoBuilding`; otherwise a
  construction yard can repeatedly pack and unpack.
- Do not retain lists returned by sensing methods; their buffers are reused.
- Fill every decision's reason with a concise explanation. Reasons appear in the decision trace
  and are essential when correlating behavior with an outcome.

## Modes, doctrines and plans

- `IBattleBot` owns one or more complete doctrines and chooses which doctrine should run.
- An `IDoctrine` declares the base build plan, unit production plan, available modes, and mode
  assignments for one coherent strategy.
- An `IUnitMode` controls one unit instance. It senses through `ModeContext`, delegates judgement
  to plain C# logic where practical, and returns a `UnitDecision`.
- Keep the decision layer pure and engine-free. Pure state-to-decision functions are easy to reason
  about and make strategy changes explainable.
- A doctrine switch changes plans and assignments for the whole side. Switches are rate-limited;
  do not create rules that oscillate between doctrines.
- Mode assignment is by unit type, control group, or globally. Precedence is most-specific-wins:
  unit override, then control group, then unit type, then the actor's YAML default, then global.
- A plan step that no driven queue can build is skipped silently, without an error.

## The SDK surface

Everything below is generated from the compiled assemblies by `scripts/export-agent-api.ps1`, so
it cannot drift from the code. **Do not restate it in a proposed prompt, and do not spend a round
rediscovering it** — if a member is not listed here, it does not exist.

<!-- BEGIN GENERATED SDK SURFACE -->

<!-- Generated by scripts/export-agent-api.ps1. Do not edit by hand. -->

### ModeContext — everything a mode can see and do

One instance per unit. This is the complete public surface; nothing else is reachable from a mode.

```
CPos Anchor { get; set }
CPos BaseCenter { get }
IReadOnlyList<BuildStep> BuildPlan { get }
bool CanDeploy { get }
bool CanMove { get }
int Cash { get }
bool DeploysIntoBuilding { get }
int DistanceFromAnchorUnits { get }
string Doctrine { get }
IReadOnlyList<string> Doctrines { get }
int GroupId { get }
bool HasResourceLayer { get }
bool HasWeapon { get }
int HealthPercent { get }
bool IsBuilding { get }
bool IsHarvester { get }
bool IsIdle { get }
string ModeName { get }
Player Owner { get }
int PowerBalance { get }
IReadOnlyList<ProductionStep> ProductionPlan { get }
MersenneTwister Random { get }
bool ResourcesExhausted { get }
Actor Self { get }
int WeaponRangeUnits { get }
World World { get }
int WorldTick { get }
IReadOnlyCollection<string> BuildableItems(string category)
Order BuildOrder(UnitDecision decision)
bool CanAttack(Actor target)
bool CanHarvest(CPos cell)
static ThreatKind Classify(Actor actor)
int DistanceTo(Actor other)
int DistanceTo(CPos cell)
CPos? FindBuildLocation(string actorType, int minRange = 2, int maxRange = 14)
Actor FindNearestAllied()
CPos? FindNearestResource(int radiusCells = 24, CPos? origin = null)
ResourceField? FindNearestResourceField(int minCells = 4, CPos? origin = null)
Actor FindRefinery()
Actor FindRepairBay()
IReadOnlyList<ResourceField> FindResourceFields(int minCells = 4, int maxFields = 16, CPos? origin = null)
bool HasResource(CPos cell)
static bool IsVisibleEnemy(Player viewer, Actor actor)
string ItemReadyToPlace(string category)
IReadOnlyDictionary<string, int> OwnedBuildingCounts()
IReadOnlyDictionary<string, int> OwnedUnitCounts()
bool OwnsQueue(string category)
string ProducingItem(string category)
ProductionQueue QueueFor(string category)
IReadOnlyCollection<ProductionQueueState> QueueStates()
void RequestReevaluation()
Actor ResolveActor(uint actorId)
ResourceCell ResourceAt(CPos cell)
IEnumerable<Actor> SenseAllies(WDist radius, string actorType = null)
IReadOnlyList<ThreatSnapshot> SenseStructures(WDist radius)
IReadOnlyList<ThreatSnapshot> SenseThreats(WDist radius)
ThreatSnapshot Snapshot(Actor actor)
void SwitchDoctrine(string doctrine, string reason)
```

### UnitAction

The complete set of actions a decision can carry.

```
enum UnitAction: Continue, Hold, Attack, ReturnToAnchor, Retreat, AdvanceToObjective, MoveTo, AttackMoveTo, Deploy, Produce, PlaceBuilding, Harvest
```

### UnitDecision

Returned from `OnTick`. The static factories are the intended way to build one.

```
UnitAction Action { get; init }
string ItemName { get; init }
string Queue { get; init }
string Reason { get; init }
uint TargetActorId { get; init }
int TargetX { get; init }
int TargetY { get; init }
static UnitDecision AdvanceToObjective(uint objectiveActorId, string reason)
static UnitDecision Attack(uint targetActorId, string reason)
static UnitDecision AttackMoveTo(int x, int y, string reason)
static UnitDecision Deploy(string reason)
static UnitDecision Harvest(int x, int y, string reason)
static UnitDecision Hold(string reason)
static UnitDecision MoveTo(int x, int y, string reason)
static UnitDecision PlaceBuilding(string queue, string itemName, int x, int y, string reason)
static UnitDecision Produce(string queue, string itemName, string reason)
static UnitDecision Retreat(string reason)
static UnitDecision ReturnToAnchor(string reason)
bool SameIntent(UnitDecision other)
```

### ResourceCell

```
int Density { get; init }
static ResourceCell Empty { get }
bool HasResource { get }
string ResourceType { get; init }
int X { get; init }
int Y { get; init }
```

### ResourceField

```
int CellCount { get; init }
int CenterX { get; init }
int CenterY { get; init }
int DistanceUnits { get; init }
int NearestX { get; init }
int NearestY { get; init }
string ResourceType { get; init }
int TotalDensity { get; init }
```

### ThreatSnapshot

```
uint ActorId { get; init }
bool CanHitUs { get; init }
int DistanceUnits { get; init }
int HealthPercent { get; init }
bool IsAttackable { get; init }
ThreatKind Kind { get; init }
```

### ThreatKind

```
enum ThreatKind: Unknown, Infantry, Vehicle, Aircraft, Structure, Defence, Economy
```

### Every public AutoCnC.Core type

```
ArmyPlanState, AssaultState, AssignmentScope, BaseBuildLogic, BasePlanState, BattleState, BuildStep, DefensiveState, DoctrineDecision, ModeAssignments, ProductionChoice, ProductionQueueState, ProductionStep, ResourceCell, ResourceField, ThreatKind, ThreatSnapshot, UnitAction, UnitDecision, UnitProductionLogic
```

<!-- END GENERATED SDK SURFACE -->
