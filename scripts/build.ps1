<#
.SYNOPSIS
    Builds the OpenRA engine, the AutoC&C platform, and any doctrines in modules/.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipEngine
    Skip the engine build. Use for fast iteration once the engine is already built.

.PARAMETER SkipModules
    Build only the platform, not the doctrines.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipEngine,
    [switch]$SkipDoctrines
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

# Doctrines consume AutoC&C as NuGet packages, so pack before building them.
Write-Host "==> Packing AutoC&C packages" -ForegroundColor Cyan
dotnet pack (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Pack failed with exit code $LASTEXITCODE" }

if (-not $SkipDoctrines) {
    $doctrineSolutions = Get-ChildItem (Join-Path $repoRoot 'doctrines') -Recurse -Filter *.sln -ErrorAction SilentlyContinue
    foreach ($solution in $doctrineSolutions) {
        Write-Host "==> Building doctrine: $($solution.Directory.Name)" -ForegroundColor Cyan
        dotnet build $solution.FullName -c $Configuration -v minimal --nologo
        if ($LASTEXITCODE -ne 0) { throw "Doctrine build failed: $($solution.FullName)" }
    }

    $doctrineDir = Join-Path $engineDir 'bin\doctrines'
    if (Test-Path $doctrineDir) {
        $installed = Get-ChildItem $doctrineDir -Filter *.dll | Select-Object -ExpandProperty BaseName
        Write-Host "Installed doctrines: $($installed -join ', ')" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Build complete. Next: ./scripts/launch.ps1' -ForegroundColor Green