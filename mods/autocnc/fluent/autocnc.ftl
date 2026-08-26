## AutoC&C mod metadata strings.
## Copyright (c) The AutoC&C Developers and Contributors. GPL-3.0-or-later; see LICENSE.
mod-title = AutoC&C
mod-windowtitle = AutoC&C - Programmable RTS

## GameSpeeds, mod.yaml
## Speeds past the engine's own list. A separate message rather than extra attributes on
## options-game-speed, because a bundle overrides a message whole and redeclaring the engine's
## six here would leave two copies of them to keep in step.
options-autocnc-game-speed =
    .turbo = Turbo (5x)
    .ludicrous = Ludicrous (10x)
    .plaid = Plaid (20x)
    .maximum = Maximum (40x)
