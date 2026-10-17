# Play your bot against other people

Your battle bot can play your side of an AutoC&C multiplayer match, against people
controlling their armies manually or running their own bots. **You join as a player;
your bot controls your units.** It is not an AI opponent selected in the lobby.

Everyone needs the same AutoC&C mod and compatible OpenRA engine, but **only you need
your bot's DLL**. Its decisions become ordinary player orders sent over the network;
opponents do not receive or execute your code. See [how this works](determinism.md).
Agree with the other players that bots are allowed before playing.

## 1. Prepare each player's game

Follow [Getting started](getting-started.md) to install prerequisites, build AutoC&C,
and install the free C&C assets. For the simplest compatible setup, everyone should
use the same AutoC&C commit and run its setup and build scripts, rather than mixing
different engine or mod builds.

This is an **AutoC&C** game, not a way to join a stock OpenRA Tiberian Dawn server.
Players who want to control units manually still launch AutoC&C, but do not need
to install your bot.

## 2. Build and select your bot before the match

On Windows, use the launcher's **Bot bay → Build & deploy** for your project.
Do **not** use **Deploy & fight** for this: that starts a local test against AI,
not a multiplayer lobby.

Alternatively, from the repository root in PowerShell, create and build a starter bot:

```powershell
./scripts/new-bot.ps1 -Name MyRush
dotnet build ./bots/MyRush/MyRush.sln
```

If you already have a bot, build its solution instead. Generated projects deploy to
`engine/bin/bots` by default. For an external project, check its configured output
directory; the game also searches `autocnc/bots` inside OpenRA's support directory.
Only install bot assemblies you trust: they execute as normal C# code on your machine.

Close any running game before rebuilding. Then open the game at its main menu with
your bot explicitly selected:

```powershell
./scripts/launch.ps1 -ExtraArgs @('Launch.BattleBot=MyRush')
```

Replace `MyRush` with the bot's declared `Name`, not necessarily its DLL or class name.
For a trusted DLL outside the search directories, you can select it by absolute path
instead:

```powershell
./scripts/launch.ps1 -ExtraArgs @('Launch.BattleBotPath=C:\Bots\MyRush.dll')
```

Use your DLL's actual absolute path on your OS. If an assembly contains multiple bots,
also pass `Launch.BattleBot=<name>` to choose one. The selection is applied when the
match world loads, so your bot can start from the first tick without waiting for chat.

## 3. Host or join a multiplayer lobby

**Host:**

1. From the AutoC&C main menu, open **Multiplayer** and create a server.
2. Choose a map with enough playable slots and leave slots open for your friends.
   Do not fill their seats with built-in AI bots.
3. Share the server's address and port, or have friends find it in the multiplayer
   browser if it is advertised and reachable.

**Other players:**

1. Launch AutoC&C using `launch.ps1` as above, selecting their own bot if desired.
   For manual play, use `./scripts/launch.ps1 -NoBots`.
2. Open **Multiplayer** and join the host's game through the browser or direct connect
   using the host's address and port.

For a LAN game, use the host's LAN address. Across the internet, the host must allow
the game's listening TCP port through their firewall and, if behind a router, forward
that port to the hosting machine. Use the port shown in the server setup. If inbound
connections are unavailable (for example, behind carrier-grade NAT), use a mutually
trusted VPN/LAN connection or a reachable host. Advertising a server does not by itself
make it reachable.

In the lobby, each participant must occupy a **player slot**, not an observer slot.
Choose factions, spawn points and teams; use opposing teams for opponents. Start with
**Normal** speed rather than the accelerated training speeds. Once everyone is ready,
the host starts the match. Keep each bot player's game client running throughout:
the server does not run their custom bot for them.

## 4. Check that your bot is playing

Once in the match, press **Enter** to open chat:

```text
/bots
/bot
/why
```

`/bots` lists installed bots and marks the loaded one; `/bot` reports the current
selection; `/why` explains its current doctrine and assessment.

You can also load or change a bot during play:

```text
/bot MyRush
```

This controls only your side. Each opponent chooses their own bot independently.
There is no need to exchange DLLs or add a bot to the mod manifest or lobby AI list.
If exactly one bot is installed it loads automatically; with multiple installed bots,
select one explicitly instead of assuming the right one will run.

For manual players, launch with:

```powershell
./scripts/launch.ps1 -NoBots
```

This disables local battle bot discovery and loading for the entire process, including
automatic selection, explicit bot names/paths, and in-game `/bot` loading. Installed
DLLs can stay where they are. It does not affect other players' bots or lobby AI
opponents. The equivalent engine argument is `Launch.NoBots=true`, which can also be
passed through `-ExtraArgs`. Restart without this option to use a battle bot again.

## Troubleshooting

| Symptom | Check |
|---|---|
| Cannot see or join the server | Confirm everyone launched AutoC&C, uses matching builds, and has the same map. Try direct connect, then check the host address, listening port, firewall and router forwarding. |
| My bot is not in the lobby's AI list | Expected: take a player slot yourself and load your bot on your client. |
| `/bots` does not list my bot | Check the deployed DLL location and any “Battle bot problem” messages. Rebuild against the current platform, then restart the game; discovery is cached for the process. |
| The wrong bot runs, or none runs | Pass its declared name with `Launch.BattleBot`, or its DLL with `Launch.BattleBotPath`. A failed explicit selection does not silently fall back to another bot. |
| Commands do nothing while watching | Join a playable slot: observers have no local army for the bot to control. |
| Units sit idle | Check `/bot` and `/why`, then your bot's build plans and mode assignments. The platform supplies no strategy when no bot is loaded. |
| A manual player's army moves on its own | Restart their client with `./scripts/launch.ps1 -NoBots` to disable local battle bots, even when DLLs are installed. |

To review the match afterwards, use OpenRA's replay browser. Replays contain the
orders already issued, so watching does not require opponents' bot code. Exit the game
cleanly so the replay recorder can finish writing its index.

Next: [write or improve your bot](writing-bots.md), or return to the
[homepage](../README.md).
