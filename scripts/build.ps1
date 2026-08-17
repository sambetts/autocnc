<#
.SYNOPSIS
    Builds the OpenRA engine (if needed) and the AutoC&C mod assembly.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipEngine
    Skip the engine build. Use for fast iteration once the engine is already built.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipEngine
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'

if (-not (Test-Path (Join-Path $engineDir 'OpenRA.sln'))) {
    throw 'Engine submodule not found. Run ./scripts/setup.ps1 first.'
}

if (-not $SkipEngine) {
    Write-Host "==> Building OpenRA engine ($Configuration)" -ForegroundColor Cyan
    dotnet build (Join-Path $engineDir 'OpenRA.sln') -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Engine build failed with exit code $LASTEXITCODE" }
}

Write-Host "==> Building AutoC&C ($Configuration)" -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Mod build failed with exit code $LASTEXITCODE" }

Write-Host ''
Write-Host 'Build complete. Next: ./scripts/launch.ps1' -ForegroundColor Green
