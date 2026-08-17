# Notices and attributions

AutoC&C is an independent, unofficial project. This file records what it builds on, what it
contains, and what it deliberately does not contain.

---

## AutoC&C itself

Copyright (c) The AutoC&C Developers and Contributors.

Licensed under the **GNU General Public License, version 3 or later**. The full text is in
[`LICENSE`](LICENSE).

This is not a free choice. AutoC&C links against and extends OpenRA, which is GPLv3, making it a
derivative work. Any publicly distributed build must make its complete corresponding source
available under the same terms.

---

## OpenRA

- **Project:** <https://github.com/OpenRA/OpenRA> · <https://www.openra.net>
- **Copyright:** Copyright (c) OpenRA Developers and Contributors
- **Licence:** GPL-3.0-or-later — [COPYING](https://github.com/OpenRA/OpenRA/blob/bleed/COPYING)
- **Contributors:** [AUTHORS](https://github.com/OpenRA/OpenRA/blob/bleed/AUTHORS)

The engine is included as a **git submodule pinned to tag `playtest-20260222`**. It is not
modified and its source is not copied into this repository — running `git submodule update`
fetches it from the upstream project.

### Files in this repository derived from OpenRA

| File | Derived from | Notes |
|---|---|---|
| `mods/autocnc/mod.yaml` | `mods/cnc/mod.yaml` | Substantially copied, then modified: metadata, mod search paths, assemblies and rules list. |
| `mods/autocnc/rules/units.yaml` | — | Original, but overrides trait templates defined in `mods/cnc/rules/defaults.yaml`. |
| `src/AutoCnC.Mod/**` | — | Original code. Written against OpenRA's public trait, order and activity APIs, and follows its file-header and coding conventions. |

All of the above are GPL-3.0-or-later, consistent with the upstream licence.

AutoC&C is **not** affiliated with, endorsed by, or supported by the OpenRA project. Please do
not report AutoC&C issues to OpenRA.

---

## Command & Conquer, and Electronic Arts

**Command & Conquer**, **C&C**, **Tiberian Dawn**, **GDI** and **Nod** are trademarks of
**Electronic Arts Inc.** Their use here is descriptive, to identify the game this project is a
mod for. AutoC&C is an unofficial fan project with no affiliation with, sponsorship by, or
endorsement from Electronic Arts.

- Electronic Arts: <https://www.ea.com>
- EA legal and trademark information: <https://legal.ea.com>

### Game assets are not distributed here

This repository contains **no** artwork, audio, video or data files from any Command & Conquer
game.

AutoC&C inherits Tiberian Dawn's rules and presentation, so it needs those assets to run. On
first launch, OpenRA's own content installer offers to download them from a mirror of the
**2007 Command & Conquer Gold freeware release**, published by Electronic Arts. That download,
its mirrors and its terms are handled entirely by OpenRA — see
[`mods/cnc-content`](https://github.com/OpenRA/OpenRA/tree/bleed/mods/cnc-content) upstream.

Assets are installed into your local OpenRA support directory and are never committed here. If
you own an original disc or digital copy, OpenRA's Advanced Install can use that instead.

---

## Third-party packages

Test-time only, not shipped in the mod assemblies:

| Package | Licence |
|---|---|
| [NUnit](https://nunit.org/) | [MIT](https://github.com/nunit/nunit/blob/main/LICENSE.txt) |
| [NUnit3TestAdapter](https://github.com/nunit/nunit3-vs-adapter) | [MIT](https://github.com/nunit/nunit3-vs-adapter/blob/master/LICENSE) |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | [MIT](https://github.com/microsoft/vstest/blob/main/LICENSE) |

The GPLv3 text in [`LICENSE`](LICENSE) is published by the
[Free Software Foundation](https://www.fsf.org/) and is reproduced verbatim, as its own terms
require.

---

## Reporting a problem with this file

If you believe something here is inaccurate, or that this project uses your work without proper
attribution, please open an issue at
<https://github.com/sambetts/autocnc/issues> and it will be corrected.
