<#
.SYNOPSIS
    Compiles a doctrine and launches AutoC&C with it loaded.

.DESCRIPTION
    The full authoring loop in one command: build the doctrine, install it where the platform
    scans, and start a game running it.

    Because doctrines consume AutoC&C as NuGet packages, the doctrine does not have to live in
    this repository — pass a path to one anywhere on disk.

.PARAMETER Doctrine
    Name of a doctrine folder under doctrines/, or a path to a doctrine project or folder.
    Defaults to the reference doctrine.

.PARAMETER Map
    Map UID to launch straight into. Omit to start at the menu.

.PARAMETER Test
    Run the doctrine's unit tests first and stop if they fail.

.PARAMETER NoLaunch
    Build and install only; don't start the game.

.EXAMPLE
    ./scripts/run-doctrine.ps1
    Build the reference doctrine and play it.

.EXAMPLE
    ./scripts/run-doctrine.ps1 -Doctrine MyRush -Test
    Test, build and play your own doctrine.

.EXAMPLE
    ./scripts/run-doctrine.ps1 -Doctrine C:\code\my-doctrine -Map 8ca9974c6ba14bfe294efe85306e72035eacabff
    Build a doctrine from another repository and drop straight into a map.
#>
[CmdletBinding()]
param(
    [string]$Doctrine = 'Reference',
    [string]$Map,
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
function Resolve-DoctrineProject([string]$nameOrPath) {
    $candidates = @()

    if (Test-Path $nameOrPath) {
        $item = Get-Item $nameOrPath
        if ($item.PSIsContainer) { $candidates += Get-ChildItem $item.FullName -Filter *.csproj }
        else { $candidates += $item }
    }
    else {
        $folder = Join-Path $repoRoot "doctrines\$nameOrPath"
        if (Test-Path $folder) { $candidates += Get-ChildItem $folder -Filter *.csproj }
    }

    # A doctrine folder also contains a Tests project; we want the doctrine itself.
    $project = $candidates | Where-Object { $_.Name -notmatch '\.Tests\.csproj$' } | Select-Object -First 1

    if (-not $project) {
        $available = (Get-ChildItem (Join-Path $repoRoot 'doctrines') -Directory -ErrorAction SilentlyContinue).Name
        throw "Doctrine '$nameOrPath' not found. Available: $($available -join ', '). You can also pass a path."
    }

    return $project
}

$project = Resolve-DoctrineProject $Doctrine
Write-Host "==> Doctrine: $($project.BaseName)" -ForegroundColor Cyan
Write-Host "    $($project.FullName)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 2. Make sure the platform and its packages exist
# ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $binDir 'AutoCnC.Platform.dll'))) {
    Write-Host '==> Platform not built; building it first' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -SkipModules
    if ($LASTEXITCODE -ne 0) { throw 'Platform build failed.' }
}

$packages = Join-Path $repoRoot 'packages'
if (-not (Test-Path (Join-Path $packages 'AutoCnC.Sdk.*.nupkg'))) {
    Write-Host '==> Packing the AutoC&C SDK so doctrines can reference it' -ForegroundColor Cyan
    dotnet pack (Join-Path $repoRoot 'AutoCnC.sln') -c $Configuration -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Pack failed.' }
}

# ---------------------------------------------------------------------------
# 3. Optionally test the doctrine's strategy — no game needed
# ---------------------------------------------------------------------------
if ($Test) {
    $tests = Get-ChildItem $project.Directory.FullName -Recurse -Filter *.Tests.csproj
    foreach ($testProject in $tests) {
        Write-Host "==> Testing $($testProject.BaseName)" -ForegroundColor Cyan
        dotnet test $testProject.FullName -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Doctrine tests failed. Fix them before playing.' }
    }
}

# ---------------------------------------------------------------------------
# 4. Build and install
# ---------------------------------------------------------------------------
Write-Host "==> Building $($project.BaseName)" -ForegroundColor Cyan
dotnet build $project.FullName -c $Configuration -v quiet --nologo `
    /p:AutoCnCPath="$repoRoot" /p:DoctrineInstallDirectory="$doctrineDir"
if ($LASTEXITCODE -ne 0) { throw 'Doctrine build failed.' }

$installed = Get-ChildItem $doctrineDir -Filter *.dll -ErrorAction SilentlyContinue
if (-not $installed) { throw "Nothing was installed into $doctrineDir." }

Write-Host "    Installed: $(($installed | Select-Object -ExpandProperty BaseName) -join ', ')" -ForegroundColor Green

if ($NoLaunch) {
    Write-Host ''
    Write-Host 'Built and installed. Launch with ./scripts/launch.ps1' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------------------
# 5. Play it
# ---------------------------------------------------------------------------
$doctrineName = $project.BaseName -replace '^AutoCnC\.', '' -replace 'Doctrine$', ''

$gameArgs = @(
    (Join-Path $binDir 'OpenRA.dll'),
    "Engine.EngineDir=$engineDir",
    "Engine.ModSearchPaths=$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')",
    'Game.Mod=autocnc'
)

if ($Map) { $gameArgs += "Launch.Map=$Map" }

Write-Host '==> Launching' -ForegroundColor Cyan
Write-Host "    In game: /doctrines to list, /doctrine $doctrineName to load, /modelog to trace decisions." -ForegroundColor DarkGray
& dotnet @gameArgs
