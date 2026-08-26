<#
.SYNOPSIS
    Compiles a doctrine and launches AutoC&C straight into a battle with it loaded.

.DESCRIPTION
    The full authoring loop in one command: build the doctrine, install it where the platform
    scans, and start a game running it against a test AI.

    Because doctrines consume AutoC&C as NuGet packages, the doctrine does not have to live in
    this repository — pass a path to one anywhere on disk. You can also pass a prebuilt .dll,
    which is played straight out of its own folder with nothing copied anywhere.

    Pass -Map to skip the menus entirely: the game boots into that map, seats the requested AI
    opponent and loads your doctrine before the first tick.

.PARAMETER Doctrine
    One of: a doctrine folder name under doctrines/, a path to a doctrine project or folder, or
    a path to an already-built doctrine .dll. Defaults to the reference doctrine.

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
    mods/autocnc/mod.yaml. Handy for watching a doctrine's early game in slow motion, or for
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

.PARAMETER Test
    Run the doctrine's unit tests first and stop if they fail.

.PARAMETER NoLaunch
    Build and install only; don't start the game.

.EXAMPLE
    ./scripts/run-doctrine.ps1
    Build the reference doctrine and play it from the menu.

.EXAMPLE
    ./scripts/run-doctrine.ps1 -Map tiberium-rift.oramap -Difficulty Hard
    Drop straight into a fight against HAL 9001.

.EXAMPLE
    ./scripts/run-doctrine.ps1 -Map tiberium-rift.oramap -GameSpeed maximum
    Same fight as fast as the machine will go, for when you want the outcome rather than the
    spectacle. It is recorded either way, so ./scripts/launch.ps1 -Replay can show you the part
    that mattered afterwards.

.EXAMPLE
    ./scripts/run-doctrine.ps1 -Doctrine C:\code\my-doctrine\bin\Release\MyRush.dll -Map tiberium-rift.oramap
    Play a doctrine somebody sent you, without building anything.
#>
[CmdletBinding()]
param(
    [string]$Doctrine = 'Reference',
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
    [switch]$Test,
    [switch]$NoLaunch,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$binDir = Join-Path $engineDir 'bin'
$doctrineDir = Join-Path $binDir 'doctrines'

# ---------------------------------------------------------------------------
# 1. Locate the doctrine
# ---------------------------------------------------------------------------

<#
    Works out what the caller handed us:
      Project  — build it, then play whatever the build produced
      Assembly — a prebuilt .dll, or a folder of them, played where it already sits
#>
function Resolve-DoctrineSource([string]$nameOrPath) {
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
                default { throw "'$nameOrPath' is neither a doctrine project (.csproj) nor a built doctrine (.dll)." }
            }
        }

        # A doctrine folder also contains a Tests project; we want the doctrine itself.
        $project = Get-ChildItem $item.FullName -Filter *.csproj |
            Where-Object { $_.Name -notmatch '\.Tests\.csproj$' } | Select-Object -First 1

        if ($project) { return New-ProjectSource $project }
        if (Get-ChildItem $item.FullName -Filter *.dll) { return New-AssemblySource $item }

        throw "'$nameOrPath' contains no doctrine project or assembly."
    }

    $folder = Join-Path $repoRoot "doctrines\$nameOrPath"
    if (Test-Path $folder) { return Resolve-DoctrineSource $folder }

    $available = (Get-ChildItem (Join-Path $repoRoot 'doctrines') -Directory -ErrorAction SilentlyContinue).Name
    throw "Doctrine '$nameOrPath' not found. Available: $($available -join ', '). You can also pass a path to a project or a .dll."
}

$source = Resolve-DoctrineSource $Doctrine
Write-Host "==> Doctrine: $(Split-Path -Leaf $source.Path)" -ForegroundColor Cyan
Write-Host "    $($source.Path)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 2. Make sure the platform and its packages exist
# ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $binDir 'AutoCnC.Platform.dll'))) {
    Write-Host '==> Platform not built; building it first' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -SkipDoctrines
    if ($LASTEXITCODE -ne 0) { throw 'Platform build failed.' }
}

if ($source.Kind -eq 'Project') {
    $packages = Join-Path $repoRoot 'packages'
    if (-not (Test-Path (Join-Path $packages 'AutoCnC.Sdk.*.nupkg'))) {
        Write-Host '==> Packing the AutoC&C SDK so doctrines can reference it' -ForegroundColor Cyan
        dotnet pack (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
    }
}

# ---------------------------------------------------------------------------
# 3. Optionally test the doctrine's strategy — no game needed
# ---------------------------------------------------------------------------
if ($Test) {
    if ($source.Kind -ne 'Project') { throw 'A prebuilt .dll has no tests to run; drop -Test, or pass the project instead.' }

    $tests = Get-ChildItem $source.Project.Directory.FullName -Recurse -Filter *.Tests.csproj
    foreach ($testProject in $tests) {
        Write-Host "==> Testing $($testProject.BaseName)" -ForegroundColor Cyan
        dotnet test $testProject.FullName -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Doctrine tests failed. Fix them before playing.' }
    }
}

# ---------------------------------------------------------------------------
# 4. Build and install
# ---------------------------------------------------------------------------

# Where the game should load the doctrine from. Naming the exact assembly rather than a doctrine
# name means nothing has to know the Name the author declared inside it, and a doctrine played
# from its own build output cannot be shadowed by a stale installed copy.
$doctrinePath = $source.Path

if ($source.Kind -eq 'Project') {
    Write-Host "==> Building $($source.Project.BaseName)" -ForegroundColor Cyan
    dotnet build $source.Project.FullName -c $Configuration -v quiet --nologo `
        /p:AutoCnCPath="$repoRoot" /p:DoctrineInstallDirectory="$doctrineDir"
    if ($LASTEXITCODE -ne 0) { throw 'Doctrine build failed.' }

    # AssemblyName need not match the project file name, so ask MSBuild where the build landed.
    $doctrinePath = (dotnet msbuild $source.Project.FullName -getProperty:TargetPath -nologo `
            -p:Configuration=$Configuration -p:AutoCnCPath="$repoRoot" -p:DoctrineInstallDirectory="$doctrineDir").Trim()

    if (-not (Test-Path -LiteralPath $doctrinePath)) { throw "The build reported '$doctrinePath', which does not exist." }

    Write-Host "    Installed: $(Split-Path -Leaf $doctrinePath)" -ForegroundColor Green
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
    "Launch.DoctrinePath=$doctrinePath"
)

if ($Map) { $gameArgs += "Launch.Map=$Map" }
$gameArgs += $battleArgs

Write-Host '==> Launching' -ForegroundColor Cyan
if (-not $Map) {
    Write-Host '    No -Map, so you land on the menu. /doctrines to check yours loaded, /modelog to trace decisions.' -ForegroundColor DarkGray
}

& dotnet @gameArgs
