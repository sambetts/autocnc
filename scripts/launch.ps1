<#
.SYNOPSIS
    Launches OpenRA with the AutoC&C mod.

.DESCRIPTION
    Points the engine at this repository's mods/ directory via Engine.ModSearchPaths, so the mod
    is loaded straight from the working tree with no copying or symlinking.

    AutoC&C inherits Tiberian Dawn's rules and art, so OpenRA will prompt to download the free
    C&C content on first run.

.PARAMETER Map
    Map to boot straight into, as a UID or a file name.

.PARAMETER Replay
    A recorded match (.orarep) to watch instead of playing. Every battle is recorded, so this is
    how you go back over one you ran at 40x: replays are not tied to one frame per tick, so the
    replay bar can scrub and fast-forward independently of the speed it was played at.
#>
[CmdletBinding()]
param(
    [string]$Map,
    [string]$Replay,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$launcher = Join-Path $engineDir 'bin\OpenRA.dll'

if (-not (Test-Path $launcher)) {
    throw 'Engine not built. Run ./scripts/build.ps1 first.'
}

$modSearchPaths = "$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')"

# No embedded quotes. PowerShell quotes each array element as needed when it builds the native
# command line, so adding our own would be passed through as literal characters — OpenRA would
# then see a path containing quote marks, decide it isn't rooted, and resolve it against bin/.
$gameArgs = @(
    $launcher,
    "Engine.EngineDir=$engineDir",
    "Engine.ModSearchPaths=$modSearchPaths",
    'Game.Mod=autocnc',

    # Watching anything at all depends on this. Game.Platform is a saved setting, so a headless
    # battle leaves "Headless" behind in the profile and a replay that does not ask for a platform
    # inherits it: the game starts, loads the mod, plays the whole replay through a null renderer
    # and never opens a window. -ExtraArgs is applied after this, so a caller can still choose.
    'Game.Platform=Default'
)

if ($Map) { $gameArgs += "Launch.Map=$Map" }
if ($Replay) {
    if (-not (Test-Path -LiteralPath $Replay)) { throw "No such replay: $Replay" }
    $gameArgs += "Launch.Replay=$Replay"
    Write-Host "==> Replay: $(Split-Path -Leaf $Replay)" -ForegroundColor Cyan
}
if ($ExtraArgs) { $gameArgs += $ExtraArgs }

Write-Host '==> Launching AutoC&C' -ForegroundColor Cyan
& dotnet @gameArgs
exit $LASTEXITCODE
