<#
.SYNOPSIS
    Launches OpenRA with the AutoC&C mod.

.DESCRIPTION
    Points the engine at this repository's mods/ directory via Engine.ModSearchPaths, so the mod
    is loaded straight from the working tree with no copying or symlinking.

    AutoC&C inherits Tiberian Dawn's rules and art, so OpenRA will prompt to download the free
    C&C content on first run.
#>
[CmdletBinding()]
param(
    [string]$Map,
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

$gameArgs = @(
    $launcher,
    "Engine.EngineDir=`"$engineDir`"",
    "Engine.ModSearchPaths=`"$modSearchPaths`"",
    'Game.Mod=autocnc'
)

if ($Map) { $gameArgs += "Launch.Map=`"$Map`"" }
if ($ExtraArgs) { $gameArgs += $ExtraArgs }

Write-Host '==> Launching AutoC&C' -ForegroundColor Cyan
& dotnet @gameArgs
