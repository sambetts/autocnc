<#
.SYNOPSIS
    Builds the OpenRA engine, the AutoC&C platform, and any battle bots in bots/.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipEngine
    Skip the engine build. Use for fast iteration once the engine is already built.

.PARAMETER SkipBots
    Build only the platform, not the battle bots. -SkipDoctrines still works.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipEngine,
    [Alias('SkipDoctrines')]
    [switch]$SkipBots
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

# The platform must build first: bots compile against its binaries, not its projects.
Write-Host "==> Building AutoC&C platform ($Configuration)" -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Platform build failed with exit code $LASTEXITCODE" }

# Battle bots consume AutoC&C as NuGet packages, so pack before building them.
Write-Host "==> Packing AutoC&C packages" -ForegroundColor Cyan
dotnet pack (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "Pack failed with exit code $LASTEXITCODE" }

<#
    Drops the locally packed AutoC&C packages out of NuGet's global cache.

    A local pack keeps its version number, and NuGet keys its cache on id and version alone — so
    the second time you build a bot it silently reuses the copy it extracted the first time, and
    compiles against an SDK from whenever that was. The symptom is a bot that will not see a type
    you just added, which reads as a mistake in the bot.
#>
function Remove-CachedPackages {
    $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }

    foreach ($id in 'autocnc.core', 'autocnc.sdk') {
        $extracted = Join-Path $cache $id
        if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Remove-CachedPackages

if (-not $SkipBots) {
    $botSolutions = Get-ChildItem (Join-Path $repoRoot 'bots') -Recurse -Filter *.sln -ErrorAction SilentlyContinue
    foreach ($solution in $botSolutions) {
        Write-Host "==> Building battle bot: $($solution.Directory.Name)" -ForegroundColor Cyan
        dotnet build $solution.FullName -c $Configuration -v minimal --nologo
        if ($LASTEXITCODE -ne 0) { throw "Battle bot build failed: $($solution.FullName)" }
    }

    $botDir = Join-Path $engineDir 'bin\bots'
    if (Test-Path $botDir) {
        $installed = Get-ChildItem $botDir -Filter *.dll | Select-Object -ExpandProperty BaseName
        Write-Host "Installed battle bots: $($installed -join ', ')" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Build complete. Next: ./scripts/launch.ps1' -ForegroundColor Green