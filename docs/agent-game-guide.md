# AutoC&C game and bot guide for improvement agents

This document is the shared ruleset for any coding agent improving an AutoC&C battle bot. Treat
it as authoritative context alongside the source and the evidence from one completed fight.

## Objective and match basics

- AutoC&C runs on OpenRA's Tiberian Dawn rules. The bot wins by defeating every opposing player
  while keeping its own side alive.
- A normal side starts with an MCV. It must deploy a construction yard, build power and resource
  processing, create production structures, train an army, find the enemy, and fight.
- Cash comes from harvesters returning resources to refineries. A plan that loses every harvester
  or stops replacing them will eventually stop.
- **Harvesters do not find distant tiberium on their own.** OpenRA's built-in harvester search is
  radius-capped and never widens: 12 cells from the last cell it cut, or 24 cells from the
  refinery. Once the tiberium inside that bubble is exhausted the harvester waits, re-searches the
  same dead bubble, and waits again for the rest of the match. This is the single most common
  cause of an economy that dies partway through a fight while tiberium is still on the map.
- The fix is to read the resource layer and give an explicit order. `ctx.FindResourceFields(...)`
  returns every tiberium field on the map with no radius cap, nearest first; sending a stalled
  harvester to one with `UnitDecision.Harvest(field.NearestX, field.NearestY, reason)` re-centres
  the engine's search on that field. `UnitDecision.MoveTo` will not do — it parks the harvester on
  the tiberium and stops it.
- Refinery placement matters for the same reason. A refinery built next to a large field keeps its
  harvesters inside their own search bubble for far longer than one built in the middle of a base.
- **Resource reads respect shroud, so scouting is an economic act, not just a military one.** A
  cell the side has never explored reads as empty, exactly as it does for a human player. Measured
  on `tiberium-rift`, a side that never scouts has explored about 36 of the map's 366 tiberium
  cells — roughly a tenth of the map's income — so `FindResourceFields` legitimately reports a
  single field no matter how good the harvester logic is. Exploration is permanent: once a cell
  has been seen, its tiberium stays readable for the rest of the match. Sending one cheap fast unit
  around the map early therefore raises the income ceiling for the whole game, and a bot whose
  harvesters keep stalling with "no tiberium in sight" is telling you it has not scouted, not that
  the map is mined out.
- Buildings and production require sufficient power. A negative power balance slows the economy.
- GDI and Nod use different actor IDs for equivalent roles. Candidate lists express alternatives:
  `Build("powr", "nuke")` means build whichever faction's power plant is available.
- Distances in engine-facing mode code are world units; 1024 world units equal one map cell.

## Bot architecture

- `IBattleBot` owns one or more complete doctrines and chooses which doctrine should run.
- An `IDoctrine` declares the base build plan, unit production plan, available modes, and mode
  assignments for one coherent strategy.
- An `IUnitMode` controls one unit instance. It senses through `ModeContext`, delegates judgement
  to plain C# logic where practical, and returns a `UnitDecision`.
- Keep the decision layer pure and engine-free. Pure state-to-decision functions are easy to reason
  about and make strategy changes explainable.
- A doctrine switch changes plans and assignments for the whole side. Switches are rate-limited;
  do not create rules that oscillate between doctrines. An explicit
  `DoctrineDecision.SwitchUrgentlyTo("Defence", ...)` bypasses the dwell only while
  `EnemiesNearBase > 0`; neither a doctrine name nor a rolling building loss implies urgency.

## Information and fairness rules

- Strategy code may use only `BattleState` and information exposed by its unit's `ModeContext`.
- Enemy information is visibility-filtered. Do not derive strategy from omniscient telemetry or
  encode facts that the side could not know during the match.
- Own economy, forces, queues, and buildings are known exactly. Enemy actors are known only when
  visible, except for facts the bot legitimately remembers such as having found an enemy base.
- `BattleState` exposes rolling income, exact own value lost, observed enemy value killed, visible
  enemy value and mix, and own versus enemy value near the base. Enemy value is conservative and
  comes only from the latest pre-damage visibility sample. Kills between samples may be omitted;
  post-damage visibility is never trusted because damage handlers can reveal cloaked units.
- `VisibleEnemyMix` is normalized into a fixed profile, so `BattleState` equality and hashing use
  threat values rather than list identity.
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
- Building repair is player-scoped: choose a damaged entry from `ctx.OwnedBuildingStates()` and
  return `UnitDecision.RepairBuilding(id, reason)`. The SDK rejects enemy, dead, non-repairable,
  full-health, already-active, and already-requested targets, including a synchronized recheck
  before applying the engine repair order.
- Production cancellation is exact: queue, item, and positive count must still match the live
  queue when the synchronized order resolves, or nothing is cancelled. `ctx.QueueStates()` exposes
  current item/progress/cost and counts.
- Support powers come from `ctx.SupportPowerStates()`, not faction-specific constants. Activate a
  ready active key or configured order name at a cell with `UnitDecision.ActivateSupportPower`;
  configured names resolve to concrete ready keys before duplicate suppression.
- Player-scoped repair, cancellation, and support-power requests are coalesced across all
  controllers before the tick's orders are issued.
- Returning the same intent repeatedly is cheap because the host suppresses duplicate orders.
- Target selection should be stable. Re-picking equivalent targets every evaluation makes units
  dither.
- Drive a production queue only when `ctx.OwnsQueue(category)` is true.
- For deployment, check both `ctx.CanDeploy` and `ctx.DeploysIntoBuilding`; otherwise a
  construction yard can repeatedly pack and unpack.
- Do not retain lists returned by sensing methods; their buffers are reused.
- Fill every decision's `Reason` with a concise explanation and use the overload's final
  `ReasonId` argument for a stable machine identifier. Both appear in the decision trace;
  `SameIntent` ignores both.
- `ThreatSnapshot` includes actor type, cell coordinates, value, and maximum enabled weapon range
  in addition to its historical combat fields.

## Plans and assignments

- `Until(n)` is cumulative: existing and queued actors count toward the target.
- `Forever()` belongs at the end of a production plan so factories keep replacing losses.
- Modes should read `ctx.BuildPlan` and `ctx.ProductionPlan` instead of hardcoding a doctrine's
  plan in engine-facing code.
- Assignment precedence is per-unit override, control group, unit type or role, then global.
- Automated doctrines normally assign by role, unit type, or globally; they cannot depend on a
  human creating control groups.

## Reading a training run

- `fight.json` identifies the bot source revision, match configuration, result, and scores. The
  scores are the state of each side when the match was decided, plus the peak units, army value,
  buildings and base value each one reached. They are deliberately not the final instant: a
  defeated player has every actor they own destroyed at once, so that instant reads zero for every
  loss regardless of how the battle went. Compare peaks to judge whether a bot failed to build an
  army or built one and squandered it.
- `game-rules.json` is generated from OpenRA's resolved runtime rules. Query it for actor health,
  armor, movement, sight, build data and armaments, plus weapon reload, burst, fire cycle,
  shots-per-game-second, range, projectile, damage, target, and armor-modifier fields. Tiberian
  Dawn has no universal shield stat; defensive mechanics appear as armor and actor traits.
- `telemetry.csv` is an omniscient after-match record of army, economy, losses, and result. Use it
  to locate turning points, never as information the runtime bot could have read.
- `battle.csv` records what the local side could observe: sightings, damage, losses, kills,
  production, doctrine changes, and the final result.
- `decisions.jsonl` records each bot assessment, every mode evaluation and its outcome, and the
  legacy issued-decision event for evaluations that actually became orders. Correlate it with
  nearby battle events and telemetry changes.
- `replay.orarep` is optional visual evidence. Do not require it when the structured records are
  sufficient.

## Improvement rules

1. Edit only the selected battle-bot workspace. Never modify AutoC&C platform, engine, or training
   artifacts.
2. Diagnose from evidence before editing. State the concrete observed weakness and cite the
   relevant time range or events.
3. Prefer one coherent, measurable improvement over unrelated strategy rewrites.
4. Preserve faction portability unless the bot explicitly declares itself faction-specific.
5. Spend the budget on battle logic. Do not write unit tests or add a test project: a bot is
   measured by the next fight, not by a suite.
6. Build the bot after editing. Fix failures before finishing.
7. Do not launch another game. The player decides when to run the next evaluation fight.
8. Do not optimize solely for one random event. Favor rules that generalize to another match on
   the same game mechanics.
9. End with a concise explanation of the diagnosis, files changed, expected behavioral effect,
   and verification performed.
