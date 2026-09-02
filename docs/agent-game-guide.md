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
- Keep the decision layer pure and engine-free. Pure state-to-decision functions are fast to test
  and make strategy changes explainable.
- A doctrine switch changes plans and assignments for the whole side. Switches are rate-limited;
  do not create rules that oscillate between doctrines.

## Information and fairness rules

- Strategy code may use only `BattleState` and information exposed by its unit's `ModeContext`.
- Enemy information is visibility-filtered. Do not derive strategy from omniscient telemetry or
  encode facts that the side could not know during the match.
- Own economy, forces, queues, and buildings are known exactly. Enemy actors are known only when
  visible, except for facts the bot legitimately remembers such as having found an enemy base.
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

## Plans and assignments

- `Until(n)` is cumulative: existing and queued actors count toward the target.
- `Forever()` belongs at the end of a production plan so factories keep replacing losses.
- Modes should read `ctx.BuildPlan` and `ctx.ProductionPlan` instead of hardcoding a doctrine's
  plan in engine-facing code.
- Assignment precedence is per-unit override, control group, unit type or role, then global.
- Automated doctrines normally assign by role, unit type, or globally; they cannot depend on a
  human creating control groups.

## Reading a training run

- `fight.json` identifies the bot source revision, match configuration, result, and final scores.
- `game-rules.json` is generated from OpenRA's resolved runtime rules. Query it for actor health,
  armor, movement, sight, build data and armaments, plus weapon reload, burst, fire cycle,
  shots-per-game-second, range, projectile, damage, target, and armor-modifier fields. Tiberian
  Dawn has no universal shield stat; defensive mechanics appear as armor and actor traits.
- `telemetry.csv` is an omniscient after-match record of army, economy, losses, and result. Use it
  to locate turning points, never as information the runtime bot could have read.
- `battle.csv` records what the local side could observe: sightings, damage, losses, kills,
  production, doctrine changes, and the final result.
- `decisions.jsonl` records each bot assessment and every mode decision that actually became an
  order. Correlate it with nearby battle events and telemetry changes.
- `replay.orarep` is optional visual evidence. Do not require it when the structured records are
  sufficient.

## Improvement rules

1. Edit only the selected battle-bot workspace. Never modify AutoC&C platform, engine, or training
   artifacts.
2. Diagnose from evidence before editing. State the concrete observed weakness and cite the
   relevant time range or events.
3. Prefer one coherent, measurable improvement over unrelated strategy rewrites.
4. Preserve faction portability unless the bot explicitly declares itself faction-specific.
5. Add or update focused tests for changed pure decision logic.
6. Keep existing tests meaningful; do not weaken assertions merely to make a change pass.
7. Build and test the bot after editing. Fix failures before finishing.
8. Do not launch another game. The player decides when to run the next evaluation fight.
9. Do not optimize solely for one random event. Favor rules that generalize to another match on
   the same game mechanics.
10. End with a concise explanation of the diagnosis, files changed, expected behavioral effect,
    and verification performed.
