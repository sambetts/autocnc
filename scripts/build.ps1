<#
.SYNOPSIS
    Builds the OpenRA engine, the AutoC&C platform, and any battle modules in modules/.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipEngine
    Skip the engine build. Use for fast iteration once the engine is already built.

.PARAMETER SkipModules
    Build only the platform, not the battle modules.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipEngine,
    [switch]$SkipModules
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

# The platform must build first: modules compile against its binaries, not its projects.
Write-Host "==> Building AutoC&C platform ($Configuration)" -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Platform build failed with exit code $LASTEXITCODE" }

if (-not $SkipModules) {
    $moduleSolutions = Get-ChildItem (Join-Path $repoRoot 'modules') -Recurse -Filter *.sln -ErrorAction SilentlyContinue
    foreach ($solution in $moduleSolutions) {
        Write-Host "==> Building battle module: $($solution.Directory.Name)" -ForegroundColor Cyan
        dotnet build $solution.FullName -c $Configuration -v minimal --nologo
        if ($LASTEXITCODE -ne 0) { throw "Module build failed: $($solution.FullName)" }
    }

    $moduleDir = Join-Path $engineDir 'bin\modules'
    if (Test-Path $moduleDir) {
        $installed = Get-ChildItem $moduleDir -Filter *.dll | Select-Object -ExpandProperty BaseName
        Write-Host "Installed modules: $($installed -join ', ')" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Build complete. Next: ./scripts/launch.ps1' -ForegroundColor Green