# Battle Command

The launcher is the place where you turn a codebase into a better opponent. It should
feel like returning to your command post between sorties, not configuring a Windows
utility. The implemented direction is **an original field-command console**: dark olive
enclosures, recessed displays, phosphor-green information, amber deployment controls,
and a persistent right-hand command deck.

## What C&C95 contributes

Research references, inspected September 2026:

| Reference | Observed design idea | Translation, not reproduction |
|---|---|---|
| [C&C95 main menu](https://cnc-comm.com/command-and-conquer/gallery?category=Screenshots&item=mainmenu.png) | Black inset menu, industrial frames, green labels, dominant metallic identity. The archived capture includes a community patch; it is not the remaster. | A framed command surface with decisive controls, rather than a stock toolbar. |
| [C&C95 EVA installation imagery](https://cncnz.com/games/command-conquer/10th-anniversary-gallery/cc95-installation-images/) | Even installation is presented as entering a fictional computer system: green technical lettering, schematics and image displays. | The bot dossier and original doctrine-core drawing establish the world before asking for configuration. |
| [Westwood sidebar history](https://cnc.fandom.com/wiki/Sidebar) | A persistent right-hand command area keeps orders separate from the battlefield. Production categories changed in later games; those later tabs are not attributed to the 1995 original. | Persistent workstation navigation and deployment/stop controls, beside the active work area. |

The reference is the 1995 game's visual family and its Windows/C&C95 presentation,
not the 2020 remaster or Red Alert 2. No Westwood logos, sprites, textures, voices,
music or screenshots are bundled. The dossier illustration is original vector drawing.

## The player loop

- **Bot bay:** create or select a bot, open its code, choose pre-battle tests, build and deploy.
- **Proving ground:** configure map, AI difficulty, opponent count, factions, execution mode
  and speed. A leading **Your battle feedback** section shows the last battle's feedback state
  and offers a dedicated review action. Manual rendered battles ask for optional observations;
  headless battles require watching their own recorded replay first. Zero opponents is still a
  solo test. Headless still selects maximum speed.
- **AI training:** use a completed battle's evidence to improve source, inspect the agent
  workspace, retry verification or restore the previous iteration. Continuous training
  explicitly repeats the fight/improve cycle until stopped. **Train from battle** names the
  selected recording with its date, result, duration, map, feedback status and session ID.
  A neighboring replay action uses that recording, not the latest global replay.
- **Systems:** repository selection, platform rebuild, build output, logs and replays.

The selected bot's file/project name, editable-versus-prebuilt status, actual readiness
and last battle result stay above every workstation. These are not invented ranks,
ratings or progress bars. A large amber **Deploy & fight** command becomes **Start AI
training** only when continuous improvement applies to an editable project. **Stop
operation** remains outside the configuration groups locked during a fight/training cycle.

History remains one click away and opens on **Recorded sessions**, not on the battle charts.
The newest-first table shows every session's feedback status, result, duration, and final local
stats; a persistent feedback preview offers add/edit and replay actions for the selected battle.
Missing feedback is explicit, including battles from earlier continuous-training runs. Manual
improvement provides a protected save-and-improve, evidence-only, or cancel decision before
the agent starts. Continuous training stays unattended, with review available after it stops.
Right-clicking **Train from this battle** selects that row in AI training without invoking an
agent. A separate **Delete session** command requires confirmation and removes the run's saved
evidence and restore snapshots, leaving current bot source untouched. It is disabled during
operations; selection and trends update after deletion. The latest-fight readout stays independent
of the manually selected training battle.
The result charts, battle log and agent workspace still
open separately, remain available after a fight and share the command-console palette.
No new multiplayer service is implied: this launcher configures local AI skirmishes;
OpenRA's lobby is separate.

## Visual system and implementation

| Role | Treatment |
|---|---|
| Ground / panels / inputs | `#101713` / `#19231D` / `#0B120E` |
| Primary text / secondary text | `#E7EBDB` / `#A8B8A4` |
| Ready / selection | Phosphor `#B5DA8F`, accompanied by text or selected control state |
| Deployment / attention | Amber `#EDBA66`; dark text on the primary action |
| Stop / destructive intent | Muted coral `#EE9B88`, with explicit action text |
| Typography | Bahnschrift display headings; Segoe UI controls; existing monospace for code/logs |
| Materials | Square enclosures, restrained bevels, inset fields and a schematic display; no glowing glass |

WinForms is retained to avoid rewriting the mature script and training orchestration or
adding a web runtime. Shared `ActionButton`, `CommandSection` and `CommandTabs` controls
keep native button/tab semantics while painting the visual system. Forms remain movable
and resizable; file pickers and confirmation dialogs remain native. Keyboard access uses
Tab, Enter and Alt+1 through Alt+4 for workstation selection, with visible focus cues.

The initial size is bounded by the current monitor. At smaller window sizes, workstations
scroll while the command deck compresses and the deployment controls remain on screen.
The complete control tree is built before applying the 96-DPI layout baseline, so fonts,
field widths and the dossier scale together. Workstation headings size to their text;
command buttons and the navigation stack can shrink again after the window grows.
The schematic and painted controls invalidate their whole surface on resize, rather than
leaving the previous drawing behind when only the newly exposed strip is repainted.
Disabled labels, links, and checkbox captions are deliberately painted with readable colors
rather than WinForms' default dark-on-dark disabled text. This also applies when a whole
configuration panel is locked during training: the battle details and hints remain readable
without enabling the controls. Rendered-pixel tests cover both direct and inherited disabled
states at native DPI and the 96-DPI baseline. There is no idle animation or unsolicited sound.

Launcher UI tests use the same per-monitor DPI awareness as the application and include
live resize invalidation and repeated grow/shrink checks. Run them in a fresh test process
with `AUTOCNC_UI_DPI_UNAWARE=1` to also check the 96-DPI layout; switching awareness inside
an already-running WinForms process would reuse cached font and scaling metrics.

## Scope deliberately left for later

A real map preview, bot-emblem customization and opponent matchmaking could deepen the
game identity. They need their own data/asset and interaction work; the current interface
does not substitute fictional terrain, fake telemetry, cosmetic ranks or dead buttons.
