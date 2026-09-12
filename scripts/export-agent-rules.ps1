<#
.SYNOPSIS
    Exports the resolved AutoC&C actor and weapon rules for an improvement agent.

.DESCRIPTION
    Invokes an AutoC&C OpenRA utility command that reads ModData.DefaultRules after the engine has
    merged inherited Tiberian Dawn YAML and AutoC&C overrides. The JSON is a generated snapshot,
    never a second source of truth.

.PARAMETER Output
    Destination game-rules.json path.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$utility = Join-Path $engineDir 'bin\OpenRA.Utility.dll'
$platform = Join-Path $engineDir 'bin\AutoCnC.Platform.dll'

if (-not (Test-Path -LiteralPath $utility) -or -not (Test-Path -LiteralPath $platform)) {
    throw 'Engine and platform must be built before exporting resolved game rules.'
}

$outputPath = [IO.Path]::GetFullPath($Output)
. (Join-Path $PSScriptRoot 'engine-runtime.ps1')
$engineRuntime = Get-EngineRuntime
$previousEngine = $env:ENGINE_DIR
$previousSearchPaths = $env:MOD_SEARCH_PATHS
try {
    $env:ENGINE_DIR = $engineDir
    $env:MOD_SEARCH_PATHS = "$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')"

    Write-Host '==> Exporting resolved actor and weapon rules' -ForegroundColor Cyan
    & $engineRuntime.DotNetPath $utility autocnc --export-agent-rules $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Game-rules export failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:ENGINE_DIR = $previousEngine
    $env:MOD_SEARCH_PATHS = $previousSearchPaths
}

Write-Output "AUTOCNC_GAME_RULES=$outputPath"
