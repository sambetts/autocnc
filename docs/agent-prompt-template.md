Improve the AutoC&C battle bot in `{workspace}` using evidence from its latest completed fight.

## Required context

Read the game and bot guide first: `{gameGuide}`

Query the resolved actor and weapon stats as needed: `{gameRules}`

Treat the guide as authoritative for game mechanics, fair information access, and the SDK.

## Boundaries

- Edit only files under `{workspace}`.
- Do not edit the AutoC&C platform checkout, engine, training artifacts, or files outside the bot
  workspace.
- Preserve the public SDK boundary and existing project conventions.
- Make evidence-based changes rather than arbitrary tuning.
- Add or update focused tests for changed pure strategy logic.
- Run the bot's tests and build before finishing. Do not stop while either command is failing; fix
  failures and rerun until both exit successfully. The host will clean generated output and run
  them again independently.
- Do not launch another game. Finish after the code is ready for the next fight.

## Evidence

- Fight manifest: `{fightManifest}`
- Battle log (what this bot could observe): `{battleLog}`
- Match telemetry (outcome and both sides' curves): `{telemetry}`
- Decision trace (assessments and issued orders): `{decisionTrace}`
- Replay, if captured: `{replay}`

Fight configuration: {battle}

Result and player assessment, when one was provided: {result}

Source revision before the fight: `{sourceRevision}`

## Work

1. Read the bot source and tests.
2. Locate the decisive weakness by correlating telemetry turning points with nearby battle events
   and decisions.
3. State the concrete weakness and evidence.
4. Implement the smallest coherent improvement and focused tests.
5. Run the relevant tests and build, fixing failures before finishing.

{nextPromptContract}
