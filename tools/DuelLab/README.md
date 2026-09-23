# Duel lab

Measures how every combat unit fares against every other one by letting the engine fight it out,
instead of inferring it from `game-rules.json`. The rules say what a weapon does per shot; they
cannot say what splash does to a crowd, whether a tank crushes the riflemen around it, how often a
missile misses a fast target, or what an 11-cell gun on a unit that sees 6 cells can actually hit.

```powershell
./scripts/duel-lab.ps1                                   # every duel, about three minutes
./scripts/duel-lab.ps1 -Units e1,e3,orca -Scenarios cost # a quick subset
./scripts/duel-lab-report.ps1 -DuelsPath <run>\duels.csv # rebuild the tables from a run
```

`map/` is an open-ground map with three players. `Lab` is the observer the local client occupies.
`Blue` and `Red` are script players that own every unit. `duel-lab.lua` runs six lanes at once and
prints one `DUEL|` line per fight to `lua.log`. The runner installs the map into the user map
folder, runs it headless at maximum speed, then removes it. It writes `duels.csv`, and
`duel-lab-report.ps1` turns that into `matchups.csv`, `matchups.json` and `unit-matchups.md`.

Scenarios, each with Blue on the left and Red 18 cells to the right:

| scenario | setup |
|---|---|
| `1v1` | one of each, both attack-moving toward the other |
| `cost` | 3,600 credits a side, so numbers, splash and crushing count |
| `cost-spotted` | the same, with cameras giving both sides vision of the whole lane |
| `tower` | 2,400 credits ordered to destroy one powered defence whose position is known |
| `raid` | 1,200 credits ordered to destroy an undefended harvester or refinery |

Two deliberate differences from a normal AutoC&C match, both in `map/rules.yaml` or the script:

- **Engine targeting is restored.** `mods/autocnc/rules/units.yaml` forces every actor to
  `HoldFire` with no idle scanning, so that a battle bot is the only thing choosing targets. The lab
  has no bot, so its script players get Tiberian Dawn's own `AttackAnything` stance back.
- **Static targets are revealed** with a camera before the attack order. The engine refuses an
  attack order on something the attacker's side has never seen. A scouted structure also stays
  targetable under fog, which is what the attacker would be relying on in a real game.

`results/` holds the run behind `docs/unit-matchups.md`. The duels are deterministic for a given
engine build and rules, so regenerate them whenever either changes. Results are one fight per
pairing from a fixed formation on open ground, with engine targeting rather than a bot's
micro-management. Treat a margin near zero as an even trade, not a ranking.
