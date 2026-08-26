<#
.SYNOPSIS
    Builds and opens the AutoC&C battle launcher.

.DESCRIPTION
    A window over the same authoring loop the other scripts give you: pick the doctrine you are
    working on, pick a map and an opponent, and play. Everything it does, it does by running
    these scripts, so anything you can do there you can also do here.

    Windows only — it is a Windows Forms app. On Linux and macOS use run-doctrine.ps1 directly,
    which takes exactly the same options.

.PARAMETER NoBuild
    Open the last build instead of compiling first.

.EXAMPLE
    ./scripts/launcher.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'tools\AutoCnC.Launcher'
$project = Join-Path $projectDir 'AutoCnC.Launcher.csproj'
$exe = Join-Path $projectDir "bin\$Configuration\net8.0-windows\AutoCnC.Launcher.exe"

if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
    throw 'The launcher is a Windows Forms app. On this platform use ./scripts/run-doctrine.ps1, which takes the same options.'
}

if (-not $NoBuild) {
    Write-Host '==> Building the launcher' -ForegroundColor Cyan
    dotnet build $project -c $Configuration -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
}

if (-not (Test-Path $exe)) { throw "Launcher not built: $exe is missing. Run this without -NoBuild." }

Write-Host '==> Opening the launcher' -ForegroundColor Cyan

# Started detached: the launcher runs the build and the game itself, so tying it to this
# terminal would mean closing the terminal takes the game down with it.
Start-Process -FilePath $exe -WorkingDirectory $repoRoot
