<#
.SYNOPSIS
    Plays a battle bot over a named benchmark set, optionally against a control arm, and reports
    win rate and medians instead of one match.

.DESCRIPTION
    One match on a random map against a random faction is not a measurement. It is one sample of a
    distribution whose spread is wider than most of the changes being tested, which is why a round
    could lose and call it a regression, or win and call it progress, with equal justification and
    no evidence for either.

    This runs a fixed set of matches - map, factions and seed all pinned, defined in
    scripts/benchmarks.json - so two revisions are measured on the same thing. With -Control it
    also plays the previous revision over the identical set, which is the only way a win rate
    means anything: without it, the map and the opponent are free to explain the whole result.

    Every match writes a full evidence directory and is folded into the bot's cross-run history,
    so the trend and the regression alarm work exactly as they do for a single fight.

    Matches run headless at maximum speed. Use -Parallel to run several at once.

.PARAMETER BattleBot
    The bot to measure: a folder name under bots/, or a path to a project or built .dll.

.PARAMETER Benchmark
    The named set from scripts/benchmarks.json. Defaults to the set marked default in that file.

.PARAMETER Repeats
    How many times to play the whole set. Repeats use the set's seeds, so they measure the bot's
    own non-determinism rather than the draw.

.PARAMETER Control
    A git revision to play over the identical set as a control arm. The working tree is left
    alone: the revision is checked out into a temporary worktree and built there.

.PARAMETER Parallel
    Reserved concurrency ceiling. Multiple OpenRA processes currently share local-random/profile
    state on supported desktop platforms, so matches run sequentially for reliable retries and
    evidence even when a larger value is supplied.

.PARAMETER MatchRetries
    How many times to retry a match whose game process fails before recording it as Undefined.

.PARAMETER Difficulty
    Overrides the difficulty named by the benchmark set.

.PARAMETER MaxGameSeconds
    Overrides the benchmark set's match time limit. Use 0 for no limit.

.PARAMETER OutputDirectory
    Where run directories are written. Defaults to the usual TrainingRuns location for the bot.

.PARAMETER ResultPath
    Optional path for the machine-readable benchmark summary. Defaults to benchmark-result.json
    under OutputDirectory.

.EXAMPLE
    ./scripts/benchmark-bot.ps1 -BattleBot Reference
    Play the default set once and report the win rate.

.EXAMPLE
    ./scripts/benchmark-bot.ps1 -BattleBot Reference -Benchmark standard -Control HEAD~1 -Parallel 4
    Measure the working tree against the previous commit over twelve pinned matches, four at a
    time, and report both arms.
#>
[CmdletBinding()]
param(
    [string]$BattleBot = 'Reference',
    [string]$Benchmark,
    [ValidateRange(1, 50)]
    [int]$Repeats = 1,
    [string]$Control,
    [ValidateRange(1, 16)]
    [int]$Parallel = 1,
    [ValidateRange(0, 5)]
    [int]$MatchRetries = 1,
    [string]$Difficulty,
    [ValidateRange(-1, 86400)]
    [int]$MaxGameSeconds = -1,
    [string]$OutputDirectory,
    [string]$ResultPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$catalogPath = Join-Path $PSScriptRoot 'benchmarks.json'
if (-not (Test-Path -LiteralPath $catalogPath)) { throw "Benchmark catalogue not found: $catalogPath" }
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json

$setName = if ($Benchmark) { $Benchmark } else { $catalog.default }
$set = $catalog.sets | Where-Object { $_.name -eq $setName } | Select-Object -First 1
if (-not $set) { throw "Unknown benchmark '$setName'. Available: $(($catalog.sets.name) -join ', ')." }

$botName = [IO.Path]::GetFileNameWithoutExtension((Split-Path -Leaf $BattleBot))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA "AutoCnC\TrainingRuns\$botName"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not $ResultPath) {
    $ResultPath = Join-Path $OutputDirectory 'benchmark-result.json'
}
$historyPath = Join-Path $OutputDirectory 'history.json'

$evidenceTool = Join-Path $repoRoot 'tools\AutoCnC.Evidence\AutoCnC.Evidence.csproj'
$evidenceDll = Join-Path $repoRoot "tools\AutoCnC.Evidence\bin\$Configuration\net8.0\AutoCnC.Evidence.dll"
if (-not (Test-Path -LiteralPath $evidenceDll)) {
    Write-Host '==> Building the evidence tool' -ForegroundColor Cyan
    dotnet build $evidenceTool -c $Configuration -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Evidence tool build failed.' }
}

<#
    One arm is one revision played over the whole set. The candidate arm is the working tree; a
    control arm is a git revision checked out into its own worktree, so measuring a comparison
    never disturbs the code being measured. Other agents may be working in this clone.
#>
function New-ControlWorktree([string]$revision) {
    $path = Join-Path ([IO.Path]::GetTempPath()) "autocnc-control-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    Write-Host "==> Preparing control arm from $revision" -ForegroundColor Cyan
    git -C $repoRoot worktree add --detach $path $revision 2>&1 | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "Could not create a worktree for '$revision'." }
    return $path
}

function Resolve-Revision([string]$root) {
    $revision = git -C $root rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $revision) { return 'unknown' }
    return $revision.Trim()
}

function Remove-ControlWorktree([string]$path) {
    if (-not $path) { return }
    git -C $repoRoot worktree remove --force $path 2>&1 | Write-Verbose
}

<#
    The control arm plays the PREVIOUS REVISION OF THE BOT through the CURRENT harness.

    It deliberately does not invoke the old revision's own scripts/run-bot.ps1. A worktree made by
    `git worktree add` has no engine submodule checked out, so there is nothing there to play at
    all; and an older run-bot.ps1 has no -Seed or -MapFacts parameter, so the identical
    configuration this whole comparison rests on could not even be requested of it. Either failure
    would show up as matches that silently never happened - which is worse than having no control,
    because it reads as a control that lost every game.

    So the harness is held constant and the bot source is what varies, which is what a control arm
    is for.
#>
function Resolve-ControlBot([string]$worktree) {
    $name = [IO.Path]::GetFileNameWithoutExtension((Split-Path -Leaf $BattleBot))

    $folder = Join-Path $worktree "bots\$name"
    if (Test-Path -LiteralPath $folder) { return $folder }

    $project = Get-ChildItem -LiteralPath (Join-Path $worktree 'bots') -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName -like "*$name*" } | Select-Object -First 1
    if ($project) { return $project.FullName }

    throw "The control revision has no bot matching '$name' under bots/, so there is nothing to compare against."
}

<#
    Builds the list of matches for one arm. Each gets a unique run directory up front so the
    matches can be started in parallel without racing for a name, and each carries the complete
    command line it will be played with - which is what lets the parallel path stay a one-liner.
#>
function New-MatchPlan($arm, $root, $revision, $bot) {
    $difficulty = if ($Difficulty) { $Difficulty } else { $set.difficulty }
    $maxSeconds = if ($MaxGameSeconds -ge 0) {
        $MaxGameSeconds
    } elseif ($null -ne $set.maxGameSeconds) {
        $set.maxGameSeconds
    } else {
        5400
    }

    $plan = @()
    for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
        $index = 0
        foreach ($match in $set.matches) {
            $index++
            $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
            $id = "$stamp-$([guid]::NewGuid().ToString('N').Substring(0,12))"
            $runDirectory = Join-Path $OutputDirectory $id
            $evidence = Join-Path $runDirectory 'evidence'

            $plan += [pscustomobject]@{
                Arm = $arm
                Root = $root
                Revision = $revision
                Repeat = $repeat
                Index = $index
                Id = $id
                RunDirectory = $runDirectory
                Evidence = $evidence
                Map = $match.map
                Faction = $match.faction
                BotFaction = $match.botFaction
                Seed = $match.seed
                Script = (Join-Path $repoRoot 'scripts\run-bot.ps1')
                Succeeded = $false
                Attempts = 0
                Error = $null

                # A hashtable, not an array. Array splatting binds positionally, so
                # @('-Map', 'x', '-Seed', 1) reaches run-bot.ps1 as -BattleBot '-Map' and fails on
                # the first typed parameter it hits.
                Arguments = @{
                    BattleBot = $bot
                    Map = $match.map
                    Faction = $match.faction
                    BotFaction = $match.botFaction
                    Seed = $match.seed
                    Difficulty = $difficulty
                    ExecutionMode = 'Headless'
                    MaxGameSeconds = $maxSeconds
                    Configuration = $Configuration
                    Telemetry = (Join-Path $evidence 'telemetry.csv')
                    BattleLog = (Join-Path $evidence 'battle.csv')
                    DecisionTrace = (Join-Path $evidence 'decisions.jsonl')
                    MapFacts = (Join-Path $evidence 'map.json')
                    PerformanceReport = (Join-Path $evidence 'performance.json')
                }
            }
        }
    }
    return $plan
}

<#
    Plays every match in the plan.

    The parallel body is written out in full rather than shared with the sequential path through a
    variable, because PowerShell 7 refuses to marshal a script block into ForEach-Object -Parallel
    at all: "A ForEach-Object -Parallel using variable cannot be a script block." It throws before
    a single match starts. Each job therefore carries its own argument list, which reduces both
    bodies to the same two lines and leaves nothing worth sharing.
#>
function Invoke-Arm($plan) {
    $runSequentially = {
        param($jobs)

        foreach ($job in $jobs) {
            Write-Host "    [$($job.Arm)] $($job.Map) $($job.Faction) vs $($job.BotFaction) seed $($job.Seed)" -ForegroundColor DarkGray
            New-Item -ItemType Directory -Path $job.Evidence -Force | Out-Null

            for ($attempt = 1; $attempt -le $MatchRetries + 1; $attempt++) {
                $job.Attempts = $attempt
                try {
                    # Splatting needs a variable: `@($job.Arguments)` is array syntax, and an
                    # array splat binds positionally rather than by name.
                    $arguments = $job.Arguments
                    & $job.Script @arguments 2>&1 | Out-String -Width 200 | Write-Verbose
                    $job.Succeeded = $true
                    $job.Error = $null
                    break
                }
                catch {
                    $job.Succeeded = $false
                    $job.Error = $_.Exception.Message
                    if ($attempt -le $MatchRetries) {
                        Write-Warning ("Match failed; retrying attempt {0} of {1}. {2}" -f `
                                ($attempt + 1), ($MatchRetries + 1), $job.Error)
                    }
                }
            }

            if (-not $job.Succeeded) {
                Write-Warning ("Match remained Undefined after {0} attempt(s): {1}" -f `
                        $job.Attempts, $job.Error)
            }
        }
    }

    if ($Parallel -gt 1) {
        Write-Warning ('Parallel game processes share OpenRA local-random/profile state on this ' +
            'platform. Running sequentially so retries and evidence status remain reliable.')
    }

    & $runSequentially $plan
}

<#
    Writes the battle configuration the evidence tool reads back out of fight.json, including the
    benchmark name, the arm and the seed. Without these three the cross-run index cannot tell a
    candidate from a control, or a pinned match from a lucky one.
#>
function Write-FightManifest($job, $outcome, $durationSeconds) {
    $manifest = [ordered]@{
        SchemaVersion = 8
        Id = $job.Id
        Status = 'completed'
        CreatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        SourceRevision = $job.Revision
        Battle = [ordered]@{
            Map = $job.Map
            Difficulty = if ($Difficulty) { $Difficulty } else { $set.difficulty }
            Opponents = 1
            Faction = $job.Faction
            BotFaction = $job.BotFaction
            GameSpeed = 'maximum'
            ExecutionMode = 'Headless'
            Seed = $job.Seed
            Benchmark = $set.name
            Batch = $batch
            Arm = $job.Arm
            Repeat = $job.Repeat
            Scenario = $job.Index
        }
        Result = [ordered]@{
            DurationSeconds = $durationSeconds
            Outcome = $outcome
        }
    }

    $manifest | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath (Join-Path $job.Evidence 'fight.json') -Encoding utf8
}

function Complete-Match($job) {
    $battleLog = Join-Path $job.Evidence 'battle.csv'
    if (-not (Test-Path -LiteralPath $battleLog)) {
        Write-Warning "[$($job.Arm)] $($job.Map) seed $($job.Seed): no battle log, the match did not record."
        return [pscustomobject]@{
            RunId = $job.Id
            Evidence = $job.Evidence
            Arm = $job.Arm
            Repeat = $job.Repeat
            Scenario = $job.Index
            Map = $job.Map
            Faction = $job.Faction
            BotFaction = $job.BotFaction
            Seed = $job.Seed
            Status = 'Undefined'
            Succeeded = $false
            Attempts = $job.Attempts
            Error = $job.Error ?? 'No battle log was recorded.'
            Outcome = 'Undefined'
            Fitness = $null
            EarnedPerSecond = $null
            SpentPerSecond = $null
            Exchange = $null
            BuildingsKilled = $null
            DurationSeconds = 0
        }
    }

    # The battle log's closing row is the authoritative result: it is written by the engine at
    # game over, not inferred from an exit code.
    $rows = Import-Csv -LiteralPath $battleLog
    $over = $rows | Where-Object { $_.event -eq 'over' } | Select-Object -Last 1
    $outcome = if ($over -and $over.detail -match 'result=(\w+)') { $Matches[1] } else { 'Unknown' }
    $duration = if ($rows) { [int]($rows | Select-Object -Last 1).seconds } else { 0 }

    Write-FightManifest $job $outcome $duration

    $rulesPath = Join-Path $job.Evidence 'game-rules.json'
    if (-not (Test-Path -LiteralPath $rulesPath)) {
        & (Join-Path $PSScriptRoot 'export-agent-rules.ps1') -Output $rulesPath | Out-Null
    }

    dotnet $evidenceDll summarise $job.Evidence --history $historyPath --bot $botName | Out-Null

    $summary = Get-Content -LiteralPath (Join-Path $job.Evidence 'summary.json') -Raw | ConvertFrom-Json
    $valid = $summary.fight.outcome -in @('Won', 'Lost')
    return [pscustomobject]@{
        RunId = $job.Id
        Evidence = $job.Evidence
        Arm = $job.Arm
        Repeat = $job.Repeat
        Scenario = $job.Index
        Map = $job.Map
        Faction = $job.Faction
        BotFaction = $job.BotFaction
        Seed = $job.Seed
        Status = if ($valid) { 'Completed' } else { 'Undefined' }
        Succeeded = $valid
        Attempts = [math]::Max(1, $job.Attempts)
        Error = if ($valid) { $null } else { $job.Error ?? "Outcome was $($summary.fight.outcome)." }
        Outcome = $summary.fight.outcome
        Fitness = $summary.fitness.total
        EarnedPerSecond = $summary.headline.creditsEarnedPerSecond
        SpentPerSecond = $summary.headline.creditsSpentPerSecond
        Exchange = $summary.headline.valueExchangeRatio
        BuildingsKilled = $summary.headline.buildingsKilled
        DurationSeconds = $summary.fight.durationSeconds
    }
}

function Get-Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

function Show-Arm($name, $results) {
    $results = @($results)
    if ($results.Count -eq 0) {
        Write-Host "  $name : no matches completed." -ForegroundColor Yellow
        return $null
    }

    $completed = @($results | Where-Object { $_.Succeeded })
    $wins = @($completed | Where-Object { $_.Outcome -eq 'Won' }).Count
    $summary = [pscustomobject]@{
        Arm = $name
        Wins = $wins
        Expected = $results.Count
        Played = $completed.Count
        Undefined = $results.Count - $completed.Count
        MedianFitness = [math]::Round((Get-Median ($completed | ForEach-Object { $_.Fitness })), 4)
        MedianEarnedPerSecond = [math]::Round((Get-Median ($completed | ForEach-Object { $_.EarnedPerSecond })), 2)
        MedianSpentPerSecond = [math]::Round((Get-Median ($completed | ForEach-Object { $_.SpentPerSecond })), 2)
        MedianExchange = [math]::Round((Get-Median ($completed | ForEach-Object { $_.Exchange })), 3)
        MedianBuildingsKilled = Get-Median ($completed | ForEach-Object { $_.BuildingsKilled })
    }

    Write-Host ("  {0,-10} {1} of {2} completed won ({3} undefined); median fitness {4}, spend {5} cr/s, exchange {6}, buildings {7}" -f `
            $name, $wins, $completed.Count, $summary.Undefined, $summary.MedianFitness, $summary.MedianSpentPerSecond,
        $summary.MedianExchange, $summary.MedianBuildingsKilled) -ForegroundColor Green
    return $summary
}

function Compare-PairedResults($candidateResults, $controlResults) {
    $candidateResults = @($candidateResults)
    $controlResults = @($controlResults)
    if ($candidateResults.Count -eq 0 -or $controlResults.Count -eq 0) {
        return @()
    }

    $controlByScenario = @{}
    foreach ($result in $controlResults) {
        $controlByScenario["$($result.Repeat):$($result.Scenario)"] = $result
    }

    $pairs = @()
    foreach ($candidateResult in $candidateResults) {
        $key = "$($candidateResult.Repeat):$($candidateResult.Scenario)"
        if (-not $controlByScenario.ContainsKey($key)) {
            continue
        }

        $controlResult = $controlByScenario[$key]
        if (-not $candidateResult.Succeeded -or -not $controlResult.Succeeded) {
            $pairs += [pscustomobject]@{
                Repeat = $candidateResult.Repeat
                Scenario = $candidateResult.Scenario
                Map = $candidateResult.Map
                Faction = $candidateResult.Faction
                BotFaction = $candidateResult.BotFaction
                Seed = $candidateResult.Seed
                Status = 'Undefined'
                CandidateOutcome = $candidateResult.Outcome
                ControlOutcome = $controlResult.Outcome
                FitnessDelta = $null
                EarnedPerSecondDelta = $null
                SpentPerSecondDelta = $null
                ExchangeDelta = $null
                BuildingsKilledDelta = $null
            }
            continue
        }

        $pairs += [pscustomobject]@{
            Repeat = $candidateResult.Repeat
            Scenario = $candidateResult.Scenario
            Map = $candidateResult.Map
            Faction = $candidateResult.Faction
            BotFaction = $candidateResult.BotFaction
            Seed = $candidateResult.Seed
            Status = 'Completed'
            CandidateOutcome = $candidateResult.Outcome
            ControlOutcome = $controlResult.Outcome
            FitnessDelta = [math]::Round($candidateResult.Fitness - $controlResult.Fitness, 4)
            EarnedPerSecondDelta = [math]::Round(
                $candidateResult.EarnedPerSecond - $controlResult.EarnedPerSecond, 3)
            SpentPerSecondDelta = [math]::Round(
                $candidateResult.SpentPerSecond - $controlResult.SpentPerSecond, 3)
            ExchangeDelta = [math]::Round($candidateResult.Exchange - $controlResult.Exchange, 4)
            BuildingsKilledDelta = $candidateResult.BuildingsKilled - $controlResult.BuildingsKilled
        }
    }

    return $pairs
}

<#
    Builds one arm's bot once and returns the assembly to play.

    Both arms are the same project name and install into the same folder, so letting every match
    build it again would have several processes writing one output at once - and with -Parallel
    that is a build failure, not a slow build. run-bot.ps1 plays a prebuilt .dll exactly where it
    sits, so building once up front removes the race and the redundant work together.
#>
function Build-ArmBot([string]$bot, [string]$arm, [string]$revision) {
    $resolvedBot = $bot
    if (-not (Test-Path -LiteralPath $resolvedBot)) {
        $repositoryBot = Join-Path $repoRoot "bots\$bot"
        if (Test-Path -LiteralPath $repositoryBot) {
            $resolvedBot = $repositoryBot
        }
    }

    Write-Host "==> Building $resolvedBot" -ForegroundColor Cyan
    $safeRevision = ($revision -replace '[^A-Za-z0-9_.-]', '-')
    $installDirectory = Join-Path $OutputDirectory "artifacts\$arm-$safeRevision"

    & (Join-Path $repoRoot 'scripts\run-bot.ps1') -BattleBot $resolvedBot -Configuration $Configuration `
        -InstallDirectory $installDirectory -NoLaunch |
        Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "Could not build the bot at '$resolvedBot'." }

    $project = if ([IO.Path]::GetExtension($resolvedBot) -eq '.csproj') {
        $resolvedBot
    } else {
        (Get-ChildItem -LiteralPath $resolvedBot -Filter *.csproj -ErrorAction SilentlyContinue |
            Select-Object -First 1).FullName
    }

    if (-not $project) { return $resolvedBot }

    $target = (dotnet msbuild $project -getProperty:TargetPath -nologo `
            -p:Configuration=$Configuration -p:AutoCnCPath="$repoRoot" `
            -p:BattleBotInstallDirectory="$installDirectory").Trim()

    if (-not (Test-Path -LiteralPath $target)) { throw "The build reported '$target', which does not exist." }
    return $target
}

# ---------------------------------------------------------------------------
# Run the arms
# ---------------------------------------------------------------------------
$matchCount = $set.matches.Count * $Repeats

# One id per invocation of this script. It is what keeps a re-run of the same benchmark from
# being compared against the previous revision's runs, which would silently invalidate the
# candidate-versus-control win count.
$batch = "$($set.name)-$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0,6))"

Write-Host "==> Benchmark '$($set.name)': $($set.matches.Count) match(es) x $Repeats repeat(s) = $matchCount per arm" -ForegroundColor Cyan
Write-Host "    $($set.summary)" -ForegroundColor DarkGray
Write-Host "    Batch: $batch" -ForegroundColor DarkGray

$controlWorktree = $null
try {
    $candidateRevision = Resolve-Revision $repoRoot
    $candidateBot = Build-ArmBot $BattleBot 'candidate' $candidateRevision
    $plan = New-MatchPlan 'candidate' $repoRoot $candidateRevision $candidateBot

    if ($Control) {
        $controlWorktree = New-ControlWorktree $Control
        $controlRevision = Resolve-Revision $controlWorktree
        $controlBot = Build-ArmBot (Resolve-ControlBot $controlWorktree) 'control' $controlRevision
        $plan += New-MatchPlan 'control' $repoRoot $controlRevision $controlBot

        if ([IO.Path]::GetFullPath($candidateBot) -eq [IO.Path]::GetFullPath($controlBot)) {
            throw 'Candidate and control resolved to the same bot assembly path.'
        }
    }

    Invoke-Arm $plan

    $results = @()
    foreach ($job in $plan) {
        $result = Complete-Match $job
        if ($result) { $results += $result }
    }

    Write-Host ''
    Write-Host "==> Benchmark '$($set.name)' result" -ForegroundColor Cyan
    $candidate = Show-Arm 'candidate' ($results | Where-Object { $_.Arm -eq 'candidate' })
    $control = if ($Control) { Show-Arm 'control' ($results | Where-Object { $_.Arm -eq 'control' }) } else { $null }

    if ($control -and $candidate) {
        $verdict = if ($candidate.Wins -gt $control.Wins) { 'candidate ahead on wins' }
        elseif ($candidate.Wins -lt $control.Wins) { 'control ahead on wins' }
        elseif ($candidate.MedianFitness -gt $control.MedianFitness) { 'level on wins, candidate ahead on fitness' }
        elseif ($candidate.MedianFitness -lt $control.MedianFitness) { 'level on wins, control ahead on fitness' }
        else { 'indistinguishable' }

        Write-Host ''
        Write-Host "  Verdict: $verdict." -ForegroundColor Cyan
        Write-Host ("  This change won {0} of {1} against the control's {2} of {3}." -f `
                $candidate.Wins, $candidate.Played, $control.Wins, $control.Played) -ForegroundColor Cyan
    }
    elseif (-not $Control) {
        Write-Host '  No control arm was run, so this win rate is not attributable to the change.' -ForegroundColor Yellow
        Write-Host '  Pass -Control HEAD~1 to compare against the previous revision.' -ForegroundColor DarkGray
    }

    dotnet $evidenceDll trend $historyPath --out (Join-Path $OutputDirectory 'trend.json')

    $candidateResults = @($results | Where-Object { $_.Arm -eq 'candidate' })
    $controlResults = @($results | Where-Object { $_.Arm -eq 'control' })
    $pairs = @(Compare-PairedResults $candidateResults $controlResults)

    $machineResult = [ordered]@{
        SchemaVersion = 1
        GeneratedUtc = (Get-Date).ToUniversalTime().ToString('o')
        Benchmark = $set.name
        Batch = $batch
        Difficulty = if ($Difficulty) { $Difficulty } else { $set.difficulty }
        MaxGameSeconds = if ($MaxGameSeconds -ge 0) { $MaxGameSeconds } elseif ($null -ne $set.maxGameSeconds) { $set.maxGameSeconds } else { 5400 }
        ExpectedMatchesPerArm = $matchCount
        Candidate = $candidate
        Control = $control
        Paired = @($pairs)
        Matches = @($results)
    }

    $resultDirectory = Split-Path -Parent $ResultPath
    if ($resultDirectory) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }

    $machineResult | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $ResultPath -Encoding utf8

    Write-Host "  Machine-readable result: $ResultPath" -ForegroundColor DarkGray
    $results | Format-Table Arm, Repeat, Scenario, Map, Faction, BotFaction, Seed, Outcome, Fitness, SpentPerSecond, Exchange, BuildingsKilled -AutoSize

    if (@($results | Where-Object { -not $_.Succeeded }).Count -gt 0) {
        exit 1
    }
}
finally {
    Remove-ControlWorktree $controlWorktree
}
