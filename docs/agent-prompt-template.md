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
- Spend the whole budget on battle logic. Do not write unit tests or add a test project: this is a
  game bot, and the next fight is what measures it.
- Build the bot before finishing. Do not stop while the build is failing; fix failures and rebuild
  until it exits successfully. The host will clean generated output and build it again
  independently.
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

1. Read the bot source.
2. Locate the decisive weakness by correlating telemetry turning points with nearby battle events
   and decisions.
3. State the concrete weakness and evidence.
4. Implement the smallest coherent improvement to the battle logic.
5. Build the bot, fixing failures before finishing.

{nextPromptContract}
