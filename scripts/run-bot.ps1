<#
.SYNOPSIS
    Compiles a battle bot and launches AutoC&C straight into a battle with it loaded.

.DESCRIPTION
    The full authoring loop in one command: build the bot, install it where the platform scans,
    and start a game running it against a test AI.

    Because bots consume AutoC&C as NuGet packages, a bot does not have to live in this
    repository — pass a path to one anywhere on disk. You can also pass a prebuilt .dll, which is
    played straight out of its own folder with nothing copied anywhere.

    Pass -Map to skip the menus entirely: the game boots into that map, seats the requested AI
    opponent and loads your bot before the first tick.

.PARAMETER BattleBot
    One of: a bot folder name under bots/, a path to a bot project or folder, or a path to an
    already-built bot .dll. Defaults to the reference bot. -Doctrine still works: an assembly
    with doctrines but no bot is played as one bot per doctrine.

.PARAMETER Map
    Map to launch straight into, as a UID or a file name such as tiberium-rift.oramap.
    Omit to start at the menu, in which case no battle is set up.

.PARAMETER Difficulty
    Test opponent strength: Rookie, Easy, Normal, Hard or Brutal. These are defined in
    scripts/difficulties.json. Defaults to the level marked default in that file.

.PARAMETER Opponents
    How many AI opponents to seat. 0 launches the map with no opponent at all.

.PARAMETER Bot
    Overrides the bot personality chosen by -Difficulty, e.g. watson, cabal or hal9001.

.PARAMETER BotHandicap
    Overrides the opponent handicap chosen by -Difficulty. 0-95, in steps of 5. Higher is weaker.

.PARAMETER Handicap
    Overrides your own handicap. 0-95, in steps of 5. Higher is weaker.

.PARAMETER Faction
    Your faction: gdi, nod or Random.

.PARAMETER BotFaction
    The opponents' faction: gdi, nod or Random.

.PARAMETER GameSpeed
    How fast the match runs: slowest, slower, default, fast, faster, fastest, turbo (5x),
    ludicrous (10x), plaid (20x) or maximum (40x). These come from the GameSpeeds block in
    mods/autocnc/mod.yaml. Handy for watching a bot's early game in slow motion, or for
    finding out how a whole match went while you make a cup of tea. The simulation is identical
    at every speed; only the wall-clock rate changes.

    The turbo speeds run VSync-off, because the engine draws one frame per logic tick and the
    refresh rate would otherwise be the real limit. What you actually get past that is down to
    the machine — /speed in-game reports the difference, as does debug.log. maximum gains more
    than its timestep suggests: at 1ms the loop has no spare time to sleep away.

.PARAMETER Telemetry
    Where the match writes its per-player army and economy CSV. Defaults to
    autocnc-telemetry.csv in the OpenRA logs folder; the launcher passes a path of its own so it
    can graph the run it just started. Pass 'none' to record nothing.

.PARAMETER BattleLog
    Where the match writes the battle log: the players in the game, every event your side could
    actually react to — enemies coming into view, hits taken, units lost and killed — and every
    doctrine the bot switched to and why.
    Defaults to autocnc-battle.csv in the OpenRA logs folder. Pass 'none' to record nothing.

.PARAMETER DecisionTrace
    Where the match writes newline-delimited JSON connecting battle assessments and issued mode
    decisions to their outcomes. Disabled unless a path is supplied.

.PARAMETER Test
    Run the bot's unit tests first and stop if they fail.

.PARAMETER NoLaunch
    Build and install only; don't start the game.

.EXAMPLE
    ./scripts/run-bot.ps1
    Build the reference bot and play it from the menu.

.EXAMPLE
    ./scripts/run-bot.ps1 -Map tiberium-rift.oramap -Difficulty Hard
    Drop straight into a fight against HAL 9001.

.EXAMPLE
    ./scripts/run-bot.ps1 -Map tiberium-rift.oramap -GameSpeed maximum
    Same fight as fast as the machine will go, for when you want the outcome rather than the
    spectacle. It is recorded either way, so ./scripts/launch.ps1 -Replay can show you the part
    that mattered afterwards.

.EXAMPLE
    ./scripts/run-bot.ps1 -BattleBot C:\code\my-bot\bin\Release\MyBot.dll -Map tiberium-rift.oramap
    Play a bot somebody sent you, without building anything.
#>
[CmdletBinding()]
param(
    [Alias('Doctrine')]
    [string]$BattleBot = 'Reference',
    [string]$Map,
    [string]$Difficulty,
    [int]$Opponents = 1,
    [string]$Bot,
    [int]$BotHandicap = -1,
    [int]$Handicap = -1,
    [string]$Faction = 'Random',
    [string]$BotFaction = 'Random',
    [ValidateSet('slowest', 'slower', 'default', 'fast', 'faster', 'fastest', 'turbo', 'ludicrous', 'plaid', 'maximum')]
    [string]$GameSpeed,
    [string]$Telemetry,
    [string]$BattleLog,
    [string]$DecisionTrace,
    [switch]$Test,
    [switch]$NoLaunch,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$binDir = Join-Path $engineDir 'bin'
$botDir = Join-Path $binDir 'bots'

# ---------------------------------------------------------------------------
# 1. Locate the battle bot
# ---------------------------------------------------------------------------

<#
    Works out what the caller handed us:
      Project  — build it, then play whatever the build produced
      Assembly — a prebuilt .dll, or a folder of them, played where it already sits
#>
function Resolve-BotSource([string]$nameOrPath) {
    function New-ProjectSource($project) {
        [pscustomobject]@{ Kind = 'Project'; Project = $project; Path = $project.FullName }
    }

    function New-AssemblySource($item) {
        [pscustomobject]@{ Kind = 'Assembly'; Project = $null; Path = $item.FullName }
    }

    if (Test-Path -LiteralPath $nameOrPath) {
        $item = Get-Item -LiteralPath $nameOrPath

        if (-not $item.PSIsContainer) {
            switch ($item.Extension) {
                '.dll' { return New-AssemblySource $item }
                '.csproj' { return New-ProjectSource $item }
                default { throw "'$nameOrPath' is neither a bot project (.csproj) nor a built bot (.dll)." }
            }
        }

        # A bot folder also contains a Tests project; we want the bot itself.
        $project = Get-ChildItem $item.FullName -Filter *.csproj |
            Where-Object { $_.Name -notmatch '\.Tests\.csproj$' } | Select-Object -First 1

        if ($project) { return New-ProjectSource $project }
        if (Get-ChildItem $item.FullName -Filter *.dll) { return New-AssemblySource $item }

        throw "'$nameOrPath' contains no bot project or assembly."
    }

    $folder = Join-Path $repoRoot "bots\$nameOrPath"
    if (Test-Path $folder) { return Resolve-BotSource $folder }

    $available = (Get-ChildItem (Join-Path $repoRoot 'bots') -Directory -ErrorAction SilentlyContinue).Name
    throw "Battle bot '$nameOrPath' not found. Available: $($available -join ', '). You can also pass a path to a project or a .dll."
}

$source = Resolve-BotSource $BattleBot
Write-Host "==> Battle bot: $(Split-Path -Leaf $source.Path)" -ForegroundColor Cyan
Write-Host "    $($source.Path)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 2. Make sure the platform and its packages exist
# ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $binDir 'AutoCnC.Platform.dll'))) {
    Write-Host '==> Platform not built; building it first' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -SkipBots
    if ($LASTEXITCODE -ne 0) { throw 'Platform build failed.' }
}

if ($source.Kind -eq 'Project') {
    $packages = Join-Path $repoRoot 'packages'
    if (-not (Test-Path (Join-Path $packages 'AutoCnC.Sdk.*.nupkg'))) {
        Write-Host '==> Packing the AutoC&C SDK so bots can reference it' -ForegroundColor Cyan
        dotnet pack (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
    }

    # A local pack keeps its version number, and NuGet keys its cache on id and version alone —
    # so without this a bot silently compiles against whichever SDK was extracted first, which is
    # the opposite of what a script called "run the code I just wrote" should do.
    $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
    foreach ($id in 'autocnc.core', 'autocnc.sdk') {
        $extracted = Join-Path $cache $id
        if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------
# 3. Optionally test the bot's strategy — no game needed
# ---------------------------------------------------------------------------
if ($Test) {
    if ($source.Kind -ne 'Project') { throw 'A prebuilt .dll has no tests to run; drop -Test, or pass the project instead.' }

    $tests = Get-ChildItem $source.Project.Directory.FullName -Recurse -Filter *.Tests.csproj
    foreach ($testProject in $tests) {
        Write-Host "==> Testing $($testProject.BaseName)" -ForegroundColor Cyan
        dotnet test $testProject.FullName -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Bot tests failed. Fix them before playing.' }
    }
}

# ---------------------------------------------------------------------------
# 4. Build and install
# ---------------------------------------------------------------------------

# Where the game should load the bot from. Naming the exact assembly rather than a bot name
# means nothing has to know the Name the author declared inside it, and a bot played from its
# own build output cannot be shadowed by a stale installed copy.
$botPath = $source.Path

if ($source.Kind -eq 'Project') {
    Write-Host "==> Building $($source.Project.BaseName)" -ForegroundColor Cyan
    dotnet build $source.Project.FullName -c $Configuration -v quiet --nologo `
        /p:AutoCnCPath="$repoRoot" /p:BattleBotInstallDirectory="$botDir"
    if ($LASTEXITCODE -ne 0) { throw 'Battle bot build failed.' }

    # AssemblyName need not match the project file name, so ask MSBuild where the build landed.
    $botPath = (dotnet msbuild $source.Project.FullName -getProperty:TargetPath -nologo `
            -p:Configuration=$Configuration -p:AutoCnCPath="$repoRoot" -p:BattleBotInstallDirectory="$botDir").Trim()

    if (-not (Test-Path -LiteralPath $botPath)) { throw "The build reported '$botPath', which does not exist." }

    Write-Host "    Installed: $(Split-Path -Leaf $botPath)" -ForegroundColor Green
}
else {
    Write-Host '    Prebuilt, so it is loaded where it is and nothing is copied.' -ForegroundColor DarkGray
}

if ($NoLaunch) {
    Write-Host ''
    Write-Host 'Built and installed. Launch with ./scripts/launch.ps1' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------
# 5. Work out who you are fighting
# ---------------------------------------------------------------------------
function Resolve-Difficulty([string]$requested) {
    $table = Get-Content (Join-Path $PSScriptRoot 'difficulties.json') -Raw | ConvertFrom-Json

    $name = if ($requested) { $requested } else { $table.default }
    $level = $table.levels | Where-Object { $_.name -eq $name } | Select-Object -First 1

    if (-not $level) { throw "Unknown difficulty '$name'. Available: $(($table.levels.name) -join ', ')." }

    return $level
}

$battleArgs = @()

if ($Map -and $GameSpeed) {
    $battleArgs += "Launch.GameSpeed=$GameSpeed"
    Write-Host "==> Speed: $GameSpeed" -ForegroundColor Cyan
}

if ($Telemetry) {
    # 'none' is how you say "record nothing" on a command line with no way to pass an empty value;
    # the trait understands it.
    $battleArgs += "Launch.Telemetry=$Telemetry"
    Write-Host "==> Telemetry: $Telemetry" -ForegroundColor Cyan
}

if ($BattleLog) {
    $battleArgs += "Launch.BattleLog=$BattleLog"
    Write-Host "==> Battle log: $BattleLog" -ForegroundColor Cyan
}

if ($DecisionTrace) {
    $battleArgs += "Launch.DecisionTrace=$DecisionTrace"
    Write-Host "==> Decision trace: $DecisionTrace" -ForegroundColor Cyan
}

if ($Map -and $Opponents -gt 0) {
    $level = Resolve-Difficulty $Difficulty

    $botType = if ($Bot) { $Bot } else { $level.bot }
    $botPenalty = if ($BotHandicap -ge 0) { $BotHandicap } else { $level.botHandicap }
    $playerPenalty = if ($Handicap -ge 0) { $Handicap } else { $level.playerHandicap }

    $battleArgs += @(
        "Launch.Bot=$botType",
        "Launch.Opponents=$Opponents",
        "Launch.BotHandicap=$botPenalty",
        "Launch.Handicap=$playerPenalty",
        "Launch.Faction=$Faction",
        "Launch.BotFaction=$BotFaction"
    )

    Write-Host "==> Opponent: $($level.name) — $($level.summary)" -ForegroundColor Cyan
    if ($Opponents -gt 1) { Write-Host "    $Opponents of them." -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------------------
# 6. Play it
# ---------------------------------------------------------------------------

# No embedded quotes: PowerShell quotes each array element as needed when it builds the native
# command line, and adding our own would reach OpenRA as literal characters.
$gameArgs = @(
    (Join-Path $binDir 'OpenRA.dll'),
    "Engine.EngineDir=$engineDir",
    "Engine.ModSearchPaths=$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')",
    'Game.Mod=autocnc',
    "Launch.BattleBotPath=$botPath"
)

if ($Map) { $gameArgs += "Launch.Map=$Map" }
$gameArgs += $battleArgs

Write-Host '==> Launching' -ForegroundColor Cyan
if (-not $Map) {
    Write-Host '    No -Map, so you land on the menu. /bots to check yours loaded, /modelog to trace decisions.' -ForegroundColor DarkGray
}

& dotnet @gameArgs
