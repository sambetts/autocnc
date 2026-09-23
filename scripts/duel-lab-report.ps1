<#
.SYNOPSIS
    Turns duel-lab duels.csv into a matchup table: who beats whom, by how much, and from how far.

.DESCRIPTION
    Reads the duels written by scripts/duel-lab.ps1 and writes, into the output directory:

      matchups.csv       one row per ordered pair (unit, opponent): the 1v1, equal-cost and
                         equal-cost-with-vision results from that unit's point of view
      matchups.json      the same, keyed for code: margin[unit][opponent] per scenario, plus the
                         tower and raid results and each unit's observed engagement ranges
      unit-matchups.md   a readable report: the equal-cost matrix, each unit's counters and
                         counterers, towers, raids and what vision changes

    Margin is (share of the opponent's value destroyed) minus (share of own value destroyed), so
    +1 is a clean sweep, -1 is being swept, and 0 is an even trade. A duel that timed out or went
    quiet is scored on the damage actually traded.

.PARAMETER DuelsPath
    duels.csv from a lab run.

.PARAMETER OutputDirectory
    Where to write the tables. Defaults to the folder holding DuelsPath.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DuelsPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $DuelsPath) }
$duels = @(Import-Csv -LiteralPath $DuelsPath)
if ($duels.Count -eq 0) { throw "No duels in $DuelsPath." }

function Num([string]$value) { [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture) }

# One duel seen from one side. Blue is the unit listed first in the lab's queue.
function Get-View($duel, [bool]$asBlue) {
    $me = if ($asBlue) { 'blue' } else { 'red' }
    $them = if ($asBlue) { 'red' } else { 'blue' }
    $myValue = Num $duel."${me}Value"
    $theirValue = Num $duel."${them}Value"
    $myLost = $myValue - (Num $duel."${me}ValueLeft")
    $theirLost = $theirValue - (Num $duel."${them}ValueLeft")
    $result = switch ($duel.winner) {
        $me { 'won' }
        $them { 'lost' }
        'draw' { 'draw' }
        default { 'unresolved' }
    }
    [pscustomobject]@{
        Unit = $duel.$me
        Opponent = $duel.$them
        Scenario = $duel.scenario
        Result = $result
        Reason = $duel.reason
        Seconds = [math]::Round((Num $duel.ticks) / 25, 1)
        Count = [int]$duel."${me}Count"
        OpponentCount = [int]$duel."${them}Count"
        Value = $myValue
        OpponentValue = $theirValue
        ValueLost = [math]::Round($myLost)
        OpponentValueLost = [math]::Round($theirLost)
        Margin = [math]::Round(($theirLost / [math]::Max(1, $theirValue)) - ($myLost / [math]::Max(1, $myValue)), 3)
        FirstShotSeconds = if ((Num $duel."${me}FirstTick") -ge 0) { [math]::Round((Num $duel."${me}FirstTick") / 25, 1) } else { $null }
        FirstShotCells = if ((Num $duel."${me}FirstDistance") -ge 0) { Num $duel."${me}FirstDistance" } else { $null }
        LongestShotCells = if ((Num $duel."${me}MaxDistance") -ge 0) { Num $duel."${me}MaxDistance" } else { $null }
        OpponentFirstShotSeconds = if ((Num $duel."${them}FirstTick") -ge 0) { [math]::Round((Num $duel."${them}FirstTick") / 25, 1) } else { $null }
    }
}

$heads = @($duels | Where-Object { $_.scenario -in '1v1', 'cost', 'cost-spotted' })
$rawViews = @(foreach ($d in $heads) { Get-View $d $true; Get-View $d $false })

function Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    if ($v.Count % 2) { return $v[[int][math]::Floor($v.Count / 2)] }
    return ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2
}

# Several seeds of the same pairing collapse into one view: margins and times are means, the
# result is the most common one, and Wins/Runs says how often it went that way.
function Merge-Views($views) {
    foreach ($g in @($views | Group-Object Scenario, Unit, Opponent)) {
        $items = @($g.Group)
        $first = $items[0]
        $margins = @($items | ForEach-Object Margin)
        [pscustomobject]@{
            Unit = $first.Unit
            Opponent = $first.Opponent
            Scenario = $first.Scenario
            Result = ($items | Group-Object Result | Sort-Object Count -Descending | Select-Object -First 1).Name
            Wins = @($items | Where-Object Result -eq 'won').Count
            Runs = $items.Count
            Reason = ($items | Group-Object Reason | Sort-Object Count -Descending | Select-Object -First 1).Name
            Seconds = [math]::Round(($items | Measure-Object Seconds -Average).Average, 1)
            Count = $first.Count
            OpponentCount = $first.OpponentCount
            Value = $first.Value
            OpponentValue = $first.OpponentValue
            ValueLost = [math]::Round(($items | Measure-Object ValueLost -Average).Average)
            OpponentValueLost = [math]::Round(($items | Measure-Object OpponentValueLost -Average).Average)
            Margin = [math]::Round(($margins | Measure-Object -Average).Average, 3)
            MarginSpread = [math]::Round(($margins | Measure-Object -Maximum).Maximum - ($margins | Measure-Object -Minimum).Minimum, 3)
            FirstShotSeconds = Median ($items | ForEach-Object FirstShotSeconds)
            FirstShotCells = Median ($items | ForEach-Object FirstShotCells)
            LongestShotCells = Median ($items | ForEach-Object LongestShotCells)
            OpponentFirstShotSeconds = Median ($items | ForEach-Object OpponentFirstShotSeconds)
        }
    }
}

$views = @(Merge-Views $rawViews)
$seeds = @($duels | ForEach-Object { if ($_.PSObject.Properties['seed']) { $_.seed } else { '1' } } | Select-Object -Unique)
$units = @($views.Unit | Select-Object -Unique)
$order = @('e1', 'e2', 'e3', 'e4', 'e5', 'rmbo', 'jeep', 'apc', 'mtnk', 'htnk', 'msam', 'bggy', 'bike',
    'ltnk', 'ftnk', 'stnk', 'arty', 'mlrs', 'orca', 'heli')
$units = @($order | Where-Object { $units -contains $_ }) + @($units | Where-Object { $order -notcontains $_ })

$index = @{}
foreach ($v in $views) { $index["$($v.Scenario)|$($v.Unit)|$($v.Opponent)"] = $v }
function Find([string]$scenario, [string]$unit, [string]$opponent) { $index["$scenario|$unit|$opponent"] }

$rows = foreach ($u in $units) {
    foreach ($o in $units) {
        if ($u -eq $o) { continue }
        $one = Find '1v1' $u $o
        $cost = Find 'cost' $u $o
        $seen = Find 'cost-spotted' $u $o
        [pscustomobject]@{
            unit = $u
            opponent = $o
            oneVsOne = $one.Result
            oneVsOneWins = "$($one.Wins)/$($one.Runs)"
            oneVsOneSeconds = $one.Seconds
            oneVsOneMargin = $one.Margin
            cost = $cost.Result
            costWins = "$($cost.Wins)/$($cost.Runs)"
            costCount = "$($cost.Count)v$($cost.OpponentCount)"
            costMargin = $cost.Margin
            costMarginSpread = $cost.MarginSpread
            costValueLost = $cost.ValueLost
            costOpponentValueLost = $cost.OpponentValueLost
            costSeconds = $cost.Seconds
            spotted = $seen.Result
            spottedWins = "$($seen.Wins)/$($seen.Runs)"
            spottedMargin = $seen.Margin
            spottedMarginSpread = $seen.MarginSpread
            spottedSeconds = $seen.Seconds
            firstShotCells = $cost.FirstShotCells
            longestShotCells = $cost.LongestShotCells
            spottedLongestShotCells = $seen.LongestShotCells
            shotFirst = if ($null -ne $cost.FirstShotSeconds -and ($null -eq $cost.OpponentFirstShotSeconds -or $cost.FirstShotSeconds -lt $cost.OpponentFirstShotSeconds)) { 'yes' } elseif ($null -ne $cost.FirstShotSeconds -and $cost.FirstShotSeconds -eq $cost.OpponentFirstShotSeconds) { 'same' } else { 'no' }
        }
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'matchups.csv') -NoTypeInformation -Encoding utf8

$towerViews = @(Merge-Views @($duels | Where-Object scenario -eq 'tower' | ForEach-Object { Get-View $_ $true }))
$raidViews = @(Merge-Views @($duels | Where-Object scenario -eq 'raid' | ForEach-Object { Get-View $_ $true }))

$reach = @{}
foreach ($u in $units) {
    $mine = @($views | Where-Object { $_.Unit -eq $u })
    $reach[$u] = [ordered]@{
        blindLongestCells = Median ($mine | Where-Object Scenario -ne 'cost-spotted' | ForEach-Object LongestShotCells)
        spottedLongestCells = Median ($mine | Where-Object Scenario -eq 'cost-spotted' | ForEach-Object LongestShotCells)
    }
}

$json = [ordered]@{
    schemaVersion = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    seeds = $seeds
    marginDefinition = 'share of opponent value destroyed minus share of own value destroyed, -1..+1, mean over seeds'
    units = $units
    oneVsOne = [ordered]@{}
    cost = [ordered]@{}
    costSpotted = [ordered]@{}
    towers = [ordered]@{}
    raids = [ordered]@{}
    reach = $reach
}
foreach ($u in $units) {
    $json.oneVsOne[$u] = [ordered]@{}
    $json.cost[$u] = [ordered]@{}
    $json.costSpotted[$u] = [ordered]@{}
    foreach ($o in $units) {
        if ($u -eq $o) { continue }
        $json.oneVsOne[$u][$o] = (Find '1v1' $u $o).Margin
        $json.cost[$u][$o] = (Find 'cost' $u $o).Margin
        $json.costSpotted[$u][$o] = (Find 'cost-spotted' $u $o).Margin
    }
    $json.towers[$u] = [ordered]@{}
    foreach ($t in @($towerViews | Where-Object Unit -eq $u)) {
        $json.towers[$u][$t.Opponent] = [ordered]@{ result = $t.Result; seconds = $t.Seconds; valueLost = $t.ValueLost; count = $t.Count }
    }
    $json.raids[$u] = [ordered]@{}
    foreach ($t in @($raidViews | Where-Object Unit -eq $u)) {
        $json.raids[$u][$t.Opponent] = [ordered]@{ result = $t.Result; seconds = $t.Seconds; count = $t.Count }
    }
}
$json | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'matchups.json') -Encoding utf8

# --- The readable report --------------------------------------------------------------------
function Cell($margin) {
    if ($null -eq $margin -or "$margin" -eq '') { return ' ' }
    $m = [double]$margin
    if ($m -ge 0.5) { return "**+$('{0:0.0}' -f $m)**" }
    if ($m -le -0.5) { return "$('{0:0.0}' -f $m)" }
    return ('{0:+0.0;-0.0;0.0}' -f $m)
}

$md = [Collections.Generic.List[string]]::new()
$md.Add('# Unit matchups, measured')
$md.Add('')
$md.Add("Generated by ``scripts/duel-lab.ps1`` from $($duels.Count) duels over $($seeds.Count) world seed(s), played by the engine on open ground")
$md.Add('(Tiberian Dawn rules as loaded by the autocnc mod, engine targeting restored for the lab only).')
$md.Add('Every number below is the mean of fights that happened, not arithmetic on the rules. See')
$md.Add('`tools/DuelLab/README.md` for the setup and its limits.')
$flips = @($views | Where-Object { $_.Scenario -eq 'cost' -and $_.Runs -gt 1 -and $_.Wins -gt 0 -and $_.Wins -lt $_.Runs })
$pairs = @($views | Where-Object Scenario -eq 'cost').Count
$flipMedian = Median ($flips | ForEach-Object { [math]::Abs($_.Margin) })
$md.Add('')
$md.Add("The equal-cost result differed between seeds in $($flips.Count) of $pairs ordered pairs. That means a win in one seed and")
$md.Add("a loss or an unfinished fight in another, which also covers a sweep where one straggler survived. The median")
$md.Add("margin across those pairs is $('{0:0.00}' -f $flipMedian). ``matchups.csv`` lists each pair's wins and margin spread.")
$md.Add('')
$md.Add('**Margin** = share of the opponent''s value destroyed minus share of your own value destroyed.')
$md.Add('+1.0 is a clean sweep, -1.0 is being swept, 0 an even trade. Bold is a decisive win (>= +0.5).')
$md.Add('')
$md.Add('- **1v1**: one of each, 18 cells apart, both attack-moving.')
$md.Add('- **Equal cost**: 3,600 credits a side, so splash, crushing and numbers count.')
$md.Add('- **With vision**: the same, but both sides see the whole lane, so long-range units can use')
$md.Add('  their full reach. The gap between the two is what scouting and spotting are worth.')
$md.Add('')
$md.Add('## Equal cost: row unit against column unit')
$md.Add('')
$md.Add('| vs | ' + ($units -join ' | ') + ' |')
$md.Add('|---|' + (($units | ForEach-Object { '---' }) -join '|') + '|')
foreach ($u in $units) {
    $cells = foreach ($o in $units) { if ($u -eq $o) { '—' } else { Cell (Find 'cost' $u $o).Margin } }
    $md.Add("| **$u** | " + ($cells -join ' | ') + ' |')
}
$md.Add('')
$md.Add('## Equal cost with full vision')
$md.Add('')
$md.Add('| vs | ' + ($units -join ' | ') + ' |')
$md.Add('|---|' + (($units | ForEach-Object { '---' }) -join '|') + '|')
foreach ($u in $units) {
    $cells = foreach ($o in $units) { if ($u -eq $o) { '—' } else { Cell (Find 'cost-spotted' $u $o).Margin } }
    $md.Add("| **$u** | " + ($cells -join ' | ') + ' |')
}
$md.Add('')
$md.Add('## Each unit: what it beats and what beats it (equal cost)')
$md.Add('')
$md.Add('| unit | beats (margin) | beaten by (margin) | reach blind / with vision (cells) |')
$md.Add('|---|---|---|---|')
foreach ($u in $units) {
    $beats = @($units | Where-Object { $_ -ne $u } | ForEach-Object { [pscustomobject]@{ o = $_; m = (Find 'cost' $u $_).Margin } } |
        Where-Object { $null -ne $_.m -and $_.m -ge 0.5 } | Sort-Object m -Descending | ForEach-Object { "$($_.o) $('{0:+0.0}' -f $_.m)" })
    $beaten = @($units | Where-Object { $_ -ne $u } | ForEach-Object { [pscustomobject]@{ o = $_; m = (Find 'cost' $u $_).Margin } } |
        Where-Object { $null -ne $_.m -and $_.m -le -0.5 } | Sort-Object m | ForEach-Object { "$($_.o) $('{0:0.0}' -f $_.m)" })
    $r = $reach[$u]
    $md.Add("| **$u** | $(if ($beats) { $beats -join ', ' } else { '—' }) | $(if ($beaten) { $beaten -join ', ' } else { '—' }) | $($r.blindLongestCells) / $($r.spottedLongestCells) |")
}
$md.Add('')
$md.Add('## What vision changes')
$md.Add('')
$md.Add('Pairs whose equal-cost margin moves by 0.5 or more when both sides can see the whole lane.')
$md.Add('')
$md.Add('| unit | opponent | blind | with vision |')
$md.Add('|---|---|---|---|')
foreach ($row in @($rows | Where-Object { $null -ne $_.costMargin -and $null -ne $_.spottedMargin -and [math]::Abs($_.spottedMargin - $_.costMargin) -ge 0.5 -and $units.IndexOf($_.unit) -lt $units.IndexOf($_.opponent) } | Sort-Object { [math]::Abs($_.spottedMargin - $_.costMargin) } -Descending)) {
    $md.Add("| $($row.unit) | $($row.opponent) | $(Cell $row.costMargin) | $(Cell $row.spottedMargin) |")
}
$md.Add('')
$md.Add('## Against defences: 2,400 credits ordered to destroy one powered tower')
$md.Add('')
$towers = @($towerViews.Opponent | Select-Object -Unique)
$md.Add('| unit | ' + ($towers -join ' | ') + ' |')
$md.Add('|---|' + (($towers | ForEach-Object { '---' }) -join '|') + '|')
foreach ($u in $units) {
    $cells = foreach ($t in $towers) {
        $v = $towerViews | Where-Object { $_.Unit -eq $u -and $_.Opponent -eq $t } | Select-Object -First 1
        if (-not $v) { ' ' } elseif ($v.Result -eq 'won') { "killed in $($v.Seconds)s, lost $($v.ValueLost)" } elseif ($v.Result -eq 'lost') { "wiped out" } else { "cannot ($($v.Reason))" }
    }
    $md.Add("| **$u** | " + ($cells -join ' | ') + ' |')
}
$md.Add('')
$md.Add('## Raiding: seconds for 1,200 credits to destroy an undefended target')
$md.Add('')
$targets = @($raidViews.Opponent | Select-Object -Unique)
$md.Add('| unit | ' + ($targets -join ' | ') + ' |')
$md.Add('|---|' + (($targets | ForEach-Object { '---' }) -join '|') + '|')
foreach ($u in $units) {
    $cells = foreach ($t in $targets) {
        $v = $raidViews | Where-Object { $_.Unit -eq $u -and $_.Opponent -eq $t } | Select-Object -First 1
        if (-not $v) { ' ' } elseif ($v.Result -eq 'won') { "$($v.Seconds)" } else { 'cannot' }
    }
    $md.Add("| **$u** | " + ($cells -join ' | ') + ' |')
}

$md | Set-Content -LiteralPath (Join-Path $OutputDirectory 'unit-matchups.md') -Encoding utf8
Write-Host "==> Matchups: $(Join-Path $OutputDirectory 'unit-matchups.md')" -ForegroundColor Cyan
