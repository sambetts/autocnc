<#
.SYNOPSIS
    Plays every combat unit against every other one in the engine and records how each fight went.

.DESCRIPTION
    The rules export says what a weapon does per shot. It cannot say what happens when a group of
    rifles meets a tank that crushes them, when a missile's inaccuracy misses a fast target, when
    splash damage lands in a crowd, or when an 11-cell gun on a unit that sees 6 cells has nothing
    to aim at. This script lets the simulation answer instead.

    It installs the duel lab map (tools/DuelLab/map) into the user map folder, runs it headless at
    maximum speed, and parses the one DUEL| line per fight that the map's Lua script writes to
    lua.log. Blue always starts on the left and Red on the right, 18 cells apart, and both
    attack-move toward each other unless the target is static. Scenarios:

      1v1           one of each
      cost          equal budgets (3,600 credits a side), so splash, crushing and numbers count
      cost-spotted  the same, with both sides able to see the whole lane (range without sight)
      tower         2,400 credits of a unit ordered to destroy one powered defence
      raid          1,200 credits of a unit ordered to destroy a harvester or a refinery

    The output directory gets duels.csv (one row per fight), matchups.csv (one row per ordered
    pair of units, combining the three head-to-head scenarios), lua.log and performance.json.

.PARAMETER Units
    Only fight these unit types, e.g. -Units e1,e3,mtnk. Defaults to every combat unit.

.PARAMETER Scenarios
    Only run these scenarios. Defaults to all of them.

.PARAMETER OutputDirectory
    Where results are written. Defaults to %LOCALAPPDATA%\AutoCnC\DuelLab\<UTC timestamp>.

.PARAMETER MaxGameSeconds
    Safety limit on the whole lab, in game seconds.

.PARAMETER KeepMap
    Leave the lab map installed in the user map folder afterwards, e.g. to watch it rendered.

.PARAMETER Seeds
    World random seeds to play the whole lab with, one full run each. Inaccuracy and splash are
    random, so several seeds average them out; one seed alone is exactly reproducible.

.PARAMETER DebugScript
    A Lua file appended to the run's configuration. If it defines LabDebug(), that function
    runs instead of the duels, for investigating how the engine treats one setup.

.EXAMPLE
    ./scripts/duel-lab.ps1
    Every duel. Takes a few minutes.

.EXAMPLE
    ./scripts/duel-lab.ps1 -Units e1,e3,jeep,orca -Scenarios 1v1,cost
    A quick check of four units.
#>
[CmdletBinding()]
param(
    [string[]]$Units,
    [ValidateSet('1v1', 'cost', 'cost-spotted', 'tower', 'raid')]
    [string[]]$Scenarios,
    [string]$OutputDirectory,
    [ValidateRange(60, 86400)]
    [int]$MaxGameSeconds = 20000,
    [switch]$KeepMap,
    [ValidateRange(1, 1000000)]
    [int[]]$Seeds = @(1, 2, 3),
    [string]$DebugScript
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$binDir = Join-Path $engineDir 'bin'
$labSource = Join-Path $repoRoot 'tools\DuelLab\map'
if (-not (Test-Path -LiteralPath (Join-Path $binDir 'OpenRA.dll'))) {
    throw 'Engine not built. Run ./scripts/build.ps1 first.'
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA ('AutoCnC\DuelLab\' + (Get-Date -AsUTC -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# The engine only launches maps it has indexed, so the lab is installed as an unpacked map folder
# in the user map directory for this mod version and removed again afterwards.
$supportDir = Join-Path $env:APPDATA 'OpenRA'
$mapName = 'autocnc-duel-lab'
$installed = Join-Path $supportDir "maps\autocnc\{DEV_VERSION}\$mapName"
if (Test-Path -LiteralPath $installed) { Remove-Item -LiteralPath $installed -Recurse -Force }
New-Item -ItemType Directory -Path $installed -Force | Out-Null
Copy-Item -Path (Join-Path $labSource '*') -Destination $installed

$config = @('-- Written by scripts/duel-lab.ps1 for this run.')
if ($Units) {
    $config += 'LabMobile = { ' + (($Units | ForEach-Object { '"' + $_.ToLowerInvariant() + '"' }) -join ', ') + ' }'
}
if ($Scenarios) {
    $config += 'LabScenarios = { ' + (($Scenarios | ForEach-Object { '["' + $_ + '"] = true' }) -join ', ') + ' }'
}
if ($DebugScript) {
    $config += Get-Content -LiteralPath $DebugScript
}
Set-Content -LiteralPath (Join-Path $installed 'duel-lab-config.lua') -Value $config -Encoding utf8

# Open ground: every cell the temperate clear tile, no resources. TileFormat 2, column-major,
# as OpenRA.Game/Map/Map.cs SaveBinaryData writes it; this grid has no height layer.
$mapYaml = Get-Content -LiteralPath (Join-Path $labSource 'map.yaml') -Raw
$size = [regex]::Match($mapYaml, '(?m)^MapSize:\s*(\d+),(\d+)')
$width = [int]$size.Groups[1].Value
$height = [int]$size.Groups[2].Value
$cells = $width * $height
$binary = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($binary)
$writer.Write([byte]2)
$writer.Write([uint16]$width)
$writer.Write([uint16]$height)
$writer.Write([uint32]17)
$writer.Write([uint32]0)
$writer.Write([uint32](3 * $cells + 17))
$clearTile = [byte[]](255, 0, 0)
for ($i = 0; $i -lt $cells; $i++) { $writer.Write($clearTile) }
$writer.Write([byte[]]::new(2 * $cells))
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $installed 'map.bin'), $binary.ToArray())
Copy-Item -LiteralPath (Join-Path $engineDir 'mods\cnc\maps\blank-shellmap\map.png') -Destination $installed

$luaLog = Join-Path $supportDir 'Logs\lua.log'
$columns = 'id', 'scenario', 'blue', 'blueCount', 'blueValue', 'red', 'redCount', 'redValue',
    'winner', 'reason', 'ticks', 'blueAlive', 'redAlive', 'blueValueLeft', 'redValueLeft',
    'blueDealt', 'redDealt', 'blueKills', 'redKills',
    'blueFirstTick', 'blueFirstDistance', 'blueMaxDistance',
    'redFirstTick', 'redFirstDistance', 'redMaxDistance', 'friendlyDamage'

Write-Host "==> Duel lab: $OutputDirectory" -ForegroundColor Cyan
. (Join-Path $PSScriptRoot 'engine-runtime.ps1')
$engineRuntime = Get-EngineRuntime
$duels = [Collections.Generic.List[object]]::new()
try {
    foreach ($seed in $Seeds) {
        if (Test-Path -LiteralPath $luaLog) { Remove-Item -LiteralPath $luaLog -Force }
        $performancePath = Join-Path $OutputDirectory "performance-seed$seed.json"

        # No embedded quotes: PowerShell quotes each element as needed. Game.Platform is a saved
        # setting, so a headless launch has to say so explicitly, as run-bot.ps1 does.
        $gameArgs = @(
            (Join-Path $binDir 'OpenRA.dll'),
            "Engine.EngineDir=$engineDir",
            "Engine.ModSearchPaths=$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')",
            'Game.Mod=autocnc',
            'Game.Platform=Headless',
            "Launch.Map=$mapName",
            'Launch.GameSpeed=maximum',
            "Launch.Seed=$seed",
            'Launch.Headless=true',
            "Launch.HeadlessReport=$performancePath",
            "Launch.HeadlessMaxGameSeconds=$MaxGameSeconds"
        )

        Write-Host "==> Seed $seed" -ForegroundColor Cyan
        & $engineRuntime.DotNetPath @gameArgs
        if ($LASTEXITCODE -ne 0) { throw "Duel lab process failed with exit code $LASTEXITCODE (seed $seed)." }
        if (-not (Test-Path -LiteralPath $luaLog)) { throw "The lab wrote no lua.log at $luaLog (seed $seed)." }

        $seedLog = Join-Path $OutputDirectory "lua-seed$seed.log"
        Copy-Item -LiteralPath $luaLog -Destination $seedLog -Force
        $lines = Get-Content -LiteralPath $seedLog

        $errors = @($lines | Where-Object { $_ -match 'LAB\|error|Fatal Lua Error|stack traceback' })
        if ($errors) {
            Write-Warning "Seed $seed reported $($errors.Count) script error line(s):"
            $errors | Select-Object -First 10 | ForEach-Object { Write-Warning $_ }
        }

        $recorded = 0
        foreach ($line in $lines) {
            $at = $line.IndexOf('DUEL|')
            if ($at -lt 0) { continue }
            $parts = $line.Substring($at).Split('|')
            if ($parts.Count -ne $columns.Count + 1) {
                Write-Warning "Skipping malformed duel line: $line"
                continue
            }
            $row = [ordered]@{ seed = $seed }
            for ($i = 0; $i -lt $columns.Count; $i++) { $row[$columns[$i]] = $parts[$i + 1] }
            $duels.Add([pscustomobject]$row)
            $recorded++
        }

        $queued = $lines | Where-Object { $_ -match 'LAB\|queued\|(\d+)' } | Select-Object -First 1
        $expected = if ($queued -match 'LAB\|queued\|(\d+)') { [int]$Matches[1] } else { $null }
        Write-Host "    $recorded of $expected duels recorded" -ForegroundColor DarkGray
        if ($expected -and $recorded -lt $expected) {
            Write-Warning "Seed $seed : some duels did not report. See $seedLog."
        }
    }
}
finally {
    if (-not $KeepMap -and (Test-Path -LiteralPath $installed)) {
        Remove-Item -LiteralPath $installed -Recurse -Force
    }
}

$duelsPath = Join-Path $OutputDirectory 'duels.csv'
$duels | Export-Csv -LiteralPath $duelsPath -NoTypeInformation -Encoding utf8
Write-Host "==> $($duels.Count) duels over $($Seeds.Count) seed(s): $duelsPath" -ForegroundColor Cyan

$report = Join-Path $PSScriptRoot 'duel-lab-report.ps1'
if (Test-Path -LiteralPath $report) {
    & $report -DuelsPath $duelsPath -OutputDirectory $OutputDirectory
}
