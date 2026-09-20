<#
.SYNOPSIS
    Runs a local coding agent against a battle bot, then verifies and deploys the result.

.DESCRIPTION
    This is the command-line half of the launcher's optional Analyze & improve action. A training
    run contains the fight manifest, telemetry, battle log and decision trace. The launcher also
    captures a reversible source snapshot before invoking this script.

    Agent configuration is provider-neutral JSON:
      {
        "command": "copilot",
        "arguments": ["--allow-all-tools", "--add-dir", "{evidence}", "--add-dir", "{repoRoot}"],
        "stdin": "{prompt}"
      }

    Supported placeholders are {prompt}, {promptFile}, {project}, {workspace}, {repoRoot}, {evidence},
    {sessionId}, and {run}, in both arguments and stdin. The rendered prompt is larger than Windows
    allows on a command line, so the built-in Copilot configuration sends it in on standard input.
    The built-in configuration grants access to {evidence} and the AutoC&C checkout {repoRoot},
    without adding the whole run directory containing the launcher's reversible source snapshot.
    The prompt restricts edits to the bot; directory grants are not read-only permissions.

    {sessionId} pins one agent conversation per fight, so this round, any repair that follows it,
    and anything chat-bot.ps1 sends before or after share the same memory.

.PARAMETER BattleBot
    The bot project to edit and verify.

.PARAMETER RunDirectory
    The completed training-run directory containing manifest.json and the fight evidence.

.PARAMETER AgentConfiguration
    Optional path to provider-neutral agent JSON. Defaults to agent-command.json in RunDirectory,
    then to GitHub Copilot CLI when that file is absent.

.PARAMETER Configuration
    Build configuration used for the post-agent test and deploy. Defaults to Release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BattleBot,
    [Parameter(Mandatory)]
    [string]$RunDirectory,
    [string]$AgentConfiguration,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = (Resolve-Path -LiteralPath $BattleBot).Path
$run = (Resolve-Path -LiteralPath $RunDirectory).Path

if ([IO.Path]::GetExtension($project) -ne '.csproj') {
    throw "AI improvement requires a battle bot project (.csproj): $project"
}

$manifestPath = Join-Path $run 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Training run is missing manifest.json.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.BotProject) {
    $recordedProject = [IO.Path]::GetFullPath([string]$manifest.BotProject)
    if (-not [string]::Equals($recordedProject, $project, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Training run belongs to a different bot project: $recordedProject"
    }
}

$evidence = Join-Path $run 'evidence'
if (-not (Test-Path -LiteralPath $evidence -PathType Container)) {
    $evidence = $run
}

$fightManifest = Join-Path $evidence 'fight.json'
if (-not (Test-Path -LiteralPath $fightManifest)) {
    Copy-Item -LiteralPath $manifestPath -Destination $fightManifest
}

$gameGuide = Join-Path $evidence 'game-guide.md'
if (-not (Test-Path -LiteralPath $gameGuide)) {
    $sharedGuide = Join-Path $repoRoot 'docs\agent-game-guide.md'
    if (-not (Test-Path -LiteralPath $sharedGuide)) {
        throw "Agent game guide not found: $sharedGuide"
    }

    Copy-Item -LiteralPath $sharedGuide -Destination $gameGuide
}

# The gospel half of the prompt: mechanics and the generated SDK surface. Kept with the fight so a
# rendered prompt still shows the reference that fight was actually given.
$mechanics = Join-Path $evidence 'mechanics.md'
if (-not (Test-Path -LiteralPath $mechanics)) {
    $sharedMechanics = Join-Path $repoRoot 'docs\agent-mechanics.md'
    if (-not (Test-Path -LiteralPath $sharedMechanics)) {
        throw "Agent mechanics reference not found: $sharedMechanics"
    }

    Copy-Item -LiteralPath $sharedMechanics -Destination $mechanics
}

$gameRules = Join-Path $evidence 'game-rules.json'
if (-not (Test-Path -LiteralPath $gameRules)) {
    & (Join-Path $PSScriptRoot 'export-agent-rules.ps1') -Output $gameRules
}

foreach ($required in 'battle.csv', 'telemetry.csv', 'decisions.jsonl') {
    if (-not (Test-Path -LiteralPath (Join-Path $evidence $required))) {
        throw "Training run evidence is missing $required."
    }
}

$workspace = Split-Path -Parent $project

# ---------------------------------------------------------------------------
# Derived evidence
# ---------------------------------------------------------------------------

<#
    Everything below this line is derived from the raw records above and replaces none of them.

    It is built here, once, because the alternative is what the loop used to do: every round spent
    the larger part of its budget re-deriving the same aggregates from the raw event stream with
    bespoke scripts, in a fresh process that paid the decision-trace parse again each time. The
    same arithmetic, done once by code that is tested, is the single cheapest improvement available
    to the analysis budget.

    It also evaluates the previous round's checks against this fight and folds the result into the
    bot's cross-run history, which is what makes a regression something the harness reports rather
    than something a later round happens to notice.
#>
$evidenceDll = Join-Path $repoRoot "tools\AutoCnC.Evidence\bin\$Configuration\net8.0\AutoCnC.Evidence.dll"
if (-not (Test-Path -LiteralPath $evidenceDll)) {
    Write-Host '==> Building the evidence tool' -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'tools\AutoCnC.Evidence\AutoCnC.Evidence.csproj') `
        -c $Configuration -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Evidence tool build failed.' }
}

$botName = [IO.Path]::GetFileNameWithoutExtension($project)
$historyPath = Join-Path (Split-Path -Parent $run) 'history.json'

# The checks a previous round wrote live with the bot, because they are a claim about the bot
# rather than about any one fight. Carried into the evidence so the fight keeps its own copy.
$authoredChecks = Join-Path $workspace 'checks.json'
$fightChecks = Join-Path $evidence 'checks.json'
if ((Test-Path -LiteralPath $authoredChecks) -and -not (Test-Path -LiteralPath $fightChecks)) {
    Copy-Item -LiteralPath $authoredChecks -Destination $fightChecks
}

Write-Host '==> Deriving summary.json, units.csv, checks and the cross-run trend' -ForegroundColor Cyan
dotnet $evidenceDll summarise $evidence --history $historyPath --bot $botName
if ($LASTEXITCODE -ne 0) { throw 'Deriving the fight evidence failed.' }

$summaryPath = Join-Path $evidence 'summary.json'
$unitsPath = Join-Path $evidence 'units.csv'
$mapFactsPath = Join-Path $evidence 'map.json'
$checkResultsPath = Join-Path $evidence 'check-results.json'
$trendPath = Join-Path $evidence 'trend.json'

# The generated halves of the prompt. These replace two sections a round used to hand-maintain:
# the checks it promised to verify, and the cross-round comparison it carried as prose.
$checkReport = if (Test-Path -LiteralPath $checkResultsPath) {
    (Get-Content -LiteralPath $checkResultsPath -Raw | ConvertFrom-Json).rendered
} else {
    'No checks were carried into this round. Write some in checks.json before finishing.'
}

$trendReport = if (Test-Path -LiteralPath $trendPath) {
    (Get-Content -LiteralPath $trendPath -Raw | ConvertFrom-Json).rendered
} else {
    'No cross-run history yet.'
}

$botAudit = & { dotnet $evidenceDll audit-bot $workspace } | Out-String

$promptFile = Join-Path $evidence 'agent-prompt.txt'
$transcript = Join-Path $run 'agent-transcript.txt'
$statusFile = Join-Path $run 'agent-status.json'

# One conversation per fight. The improvement round, any repair that follows it, and anything the
# player types before or after all resume this id, so the agent is the same correspondent
# throughout rather than a stranger who has read the same files.
$sessionId = [string]$manifest.AgentSessionId
if (-not $sessionId) {
    $sessionId = [guid]::NewGuid().ToString()
    $manifest | Add-Member -NotePropertyName AgentSessionId -NotePropertyValue $sessionId -Force
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

function Write-AgentStatus {
    param(
        [Parameter(Mandatory)][string]$State,
        [Parameter(Mandatory)][string]$Phase,
        [Nullable[int]]$AgentExitCode,
        [Nullable[int]]$VerificationExitCode,
        [string]$Message
    )

    $status = [ordered]@{
        State = $State
        Phase = $Phase
        AgentExitCode = $AgentExitCode
        VerificationExitCode = $VerificationExitCode
        Message = $Message
    }
    $temporary = "$statusFile.tmp"
    $status | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $statusFile -Force
}

if (-not (Test-Path -LiteralPath $promptFile)) {
    $templatePath = Join-Path $repoRoot 'docs\agent-prompt-template.md'
    if (-not (Test-Path -LiteralPath $templatePath)) {
        throw "Default agent prompt template not found: $templatePath"
    }

    $template = Get-Content -LiteralPath $templatePath -Raw
    $battle = $manifest.Battle
    $result = $manifest.Result
    $score = if ($result.Players) {
        ($result.Players | ForEach-Object {
            "$($_.Name): $($_.Outcome), army=$($_.ArmyValue) (peak $($_.PeakArmyValue)), units=$($_.Units) (peak $($_.PeakUnits)), buildings=$($_.Buildings) (peak $($_.PeakBuildings)), killed=$($_.Killed), lost=$($_.Lost), cash=$($_.Cash)"
        }) -join '; '
    } else {
        'no final score was recorded'
    }
    $playerFeedback = if ($result.PlayerFeedback) {
        "Player assessment of why the battle was won or lost:`n$($result.PlayerFeedback)"
    } else {
        'No player assessment was provided.'
    }

    $nextPromptContract = @"
## Create the complete prompt for the next round

After finishing the code improvement, propose an entirely new, standalone prompt template for the
next improvement round. Replace this prompt rather than adding advice to it. Optimize the next
prompt to reduce analysis overhead and improve recommendation quality.

The prompt has two halves and you are only writing one of them. The mechanics and SDK reference
above is gospel: it is injected from version control, generated from the compiled assemblies, and
is not yours to edit. Your template is the learned half — how to read this bot's evidence, what has
already been diagnosed, and what to try next.

So do not restate game mechanics, the ``ModeContext`` surface, ``UnitAction`` values,
``UnitDecision`` factories or engine constants in your template. Write {gameMechanics} on a line by
itself where that reference belongs and the launcher will insert the current one. Copying those
facts into your template is how they go stale: a template that claimed "there is no
resource/tiberium sensing API" outlived the API by many rounds and steered every one of them away
from the fix its harvesters needed.

The template must retain these placeholders exactly:
{workspace}, {gameMechanics}, {gameGuide}, {gameRules}, {fightManifest}, {battleLog}, {telemetry},
{decisionTrace}, {summary}, {units}, {mapFacts}, {checks}, {checkResults}, {trend}, {checkReport},
{trendReport}, {botAudit}, {battle}, {result}, {sourceRevision}, {nextPromptContract}

Do not replace any placeholder with a path or value from this fight, even where the rendered prompt
above shows that value.

Do not write triage recipes. ``{summary}`` and ``{units}`` are computed by the harness from the raw
records, by tested code, before you are called: the per-unit-type ledger with credits per kill and
share of spend, the economy series, the production and doctrine tables, the engagement matrix, the
loss clusters and the telemetry crossover are all already there. A template that tells the next
round to re-derive those from ``{decisionTrace}`` is spending its budget on arithmetic that has
already been done, which is the single largest thing that was wrong with this loop. Read
``{decisionTrace}`` only for a question the summary genuinely cannot answer, and say which.

Put {checkReport} and {trendReport} on lines by themselves where those sections belong. They are
generated: {checkReport} is the harness evaluating the checks the previous round wrote, and
{trendReport} is the cross-run comparison. Do not write either by hand, and do not maintain a
prose list of "already diagnosed, verify this" - that list is exactly what checks.json is for.

Keep a section telling the next round to write ``checks.json`` into {workspace} before it finishes,
and to give any new code path a reason literal nothing else uses so a ``reason:`` check can prove
it ran. That is what distinguishes "the new branch is wrong" from "the new branch never ran".

Put {nextPromptContract} on a line by itself where this section belongs. Do not copy this contract
text or its marker example into the replacement; that placeholder inserts the current contract when
the launcher renders the next round.

Return the full template without a Markdown code fence, between these marker lines:

AUTOCNC_NEXT_PROMPT_BEGIN
<complete replacement prompt template>
AUTOCNC_NEXT_PROMPT_END

In manual mode the player will review and edit it before it is saved. Continuous improvement may
accept a valid template automatically.
"@

    $promptReplacements = [ordered]@{
        '{workspace}' = $workspace
        '{gameGuide}' = $gameGuide
        '{gameRules}' = $gameRules
        '{fightManifest}' = $fightManifest
        '{battleLog}' = (Join-Path $evidence 'battle.csv')
        '{telemetry}' = (Join-Path $evidence 'telemetry.csv')
        '{decisionTrace}' = (Join-Path $evidence 'decisions.jsonl')
        '{replay}' = (Join-Path $evidence 'replay.orarep')

        # Derived artifacts. These are what a round should read; the raw records above are for
        # the questions these cannot answer.
        '{summary}' = $summaryPath
        '{units}' = $unitsPath
        '{mapFacts}' = $mapFactsPath
        '{checks}' = $fightChecks
        '{checkResults}' = $checkResultsPath
        '{trend}' = $trendPath

        # Generated prose, injected verbatim. These two replace sections a round used to write by
        # hand and the next round was supposed to verify by eye.
        '{checkReport}' = $checkReport
        '{trendReport}' = $trendReport
        '{botAudit}' = $botAudit.Trim()

        '{battle}' = "map=$($battle.Map), difficulty=$($battle.Difficulty), opponents=$($battle.Opponents), faction=$($battle.Faction), opponent faction=$($battle.BotFaction), speed=$($battle.GameSpeed), execution=$($battle.ExecutionMode)"
        '{result}' = "$($result.Outcome) after $($result.DurationSeconds) game seconds; $score`n$playerFeedback"
        '{sourceRevision}' = [string]$manifest.SourceRevision

        # Inlined, and second to last so the contract's own placeholders stay literal.
        '{gameMechanics}' = (Get-Content -LiteralPath $mechanics -Raw).Trim()
        '{nextPromptContract}' = $nextPromptContract
    }

    $prompt = $template
    foreach ($replacement in $promptReplacements.GetEnumerator()) {
        $prompt = $prompt.Replace([string]$replacement.Key, [string]$replacement.Value)
    }

    Set-Content -LiteralPath $promptFile -Value $prompt -Encoding utf8
}

$prompt = Get-Content -LiteralPath $promptFile -Raw

<#
    Resolve the derived-evidence placeholders on the prompt as it actually stands.

    This runs unconditionally, and that matters: the block above only renders a prompt when one is
    not already there, but the launcher writes evidence/agent-prompt.txt itself before invoking
    this script (TrainingAgent.cs). The launcher fills in the paths; the three generated reports
    below it cannot, because the files they summarise are derived by this script moments ago.

    Substitution stops at the next-prompt contract. The contract names these placeholders as
    literal text - it is telling the agent which ones to keep - so replacing them inside it would
    turn an instruction to retain "{summary}" into an instruction to retain a path from this one
    fight, and the next template would lose the artifact entirely.
#>
$contractHeading = '## Create the complete prompt for the next round'
$contractAt = $prompt.IndexOf($contractHeading)
$head = if ($contractAt -ge 0) { $prompt.Substring(0, $contractAt) } else { $prompt }
$tail = if ($contractAt -ge 0) { $prompt.Substring($contractAt) } else { '' }

$derivedReplacements = [ordered]@{
    '{summary}' = $summaryPath
    '{units}' = $unitsPath
    '{mapFacts}' = $mapFactsPath
    '{checks}' = $fightChecks
    '{checkResults}' = $checkResultsPath
    '{trend}' = $trendPath
    '{checkReport}' = $checkReport
    '{trendReport}' = $trendReport
    '{botAudit}' = $botAudit.Trim()
}

$before = $head
foreach ($replacement in $derivedReplacements.GetEnumerator()) {
    $head = $head.Replace([string]$replacement.Key, [string]$replacement.Value)
}

if ($head -ne $before) {
    $prompt = $head + $tail
    Set-Content -LiteralPath $promptFile -Value $prompt -Encoding utf8
}

# A learned template written before these artifacts existed has no placeholder to fill, so the
# agent would never be told they are there. Append them rather than silently sending the old
# prompt: the artifacts are the point of the round.
if ($prompt -notlike '*summary.json*') {
    $appendix = @"

## Derived evidence (appended: this prompt template predates it)

The harness has precomputed these from the raw records. Read them before parsing anything by hand.

- Fight summary, read this first and read it whole: ``$summaryPath``
- Unit ledger, one row per unit lifecycle: ``$unitsPath``
- Map facts: ``$mapFactsPath``

Do not re-derive per-unit-type credits-per-kill, share of spend, the economy series, doctrine
episodes, loss clusters, the engagement matrix or the telemetry crossover from
``decisions.jsonl`` - they are already in the summary.

### Checks carried in from the previous round

$checkReport

### How this bot is trending

$trendReport
"@

    $prompt += $appendix
    Set-Content -LiteralPath $promptFile -Value $prompt -Encoding utf8
    Write-Host '    Appended the derived-evidence section: the saved prompt template predates it.' -ForegroundColor DarkGray
}

# Tool approval does not bypass Copilot's separate path checks. Trust the checkout for Git and
# SDK inspection, but keep the prompt's bot-only editing boundary and avoid --allow-all-paths.
$previousArguments = @(
    '--allow-all-tools',
    '--no-ask-user',
    '--no-custom-instructions',
    '--no-remote-export',
    '--add-dir', '{evidence}'
)
$sessionArguments = @('--session-id', '{sessionId}') + $previousArguments
$defaultArguments = $sessionArguments + @('--add-dir', '{repoRoot}')

$configurationPath = if ($AgentConfiguration) {
    (Resolve-Path -LiteralPath $AgentConfiguration).Path
} else {
    Join-Path $run 'agent-command.json'
}

if (Test-Path -LiteralPath $configurationPath) {
    $agent = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
} else {
    $agent = [pscustomobject]@{
        command = 'copilot'
        arguments = $defaultArguments

        # Windows caps a command line at 32,767 characters and the rendered prompt is larger than
        # that on its own, so it goes in on standard input instead of in -p.
        stdin = '{prompt}'
    }
}

if (-not $agent.command) {
    throw 'Agent configuration has no command.'
}

# Saved runs can still carry the former defaults. Do not broaden a customised configuration.
if ($agent.command -eq 'copilot' -and
    ((@($agent.arguments) -join "`n") -ceq ($previousArguments -join "`n") -or
     (@($agent.arguments) -join "`n") -ceq ($sessionArguments -join "`n"))) {
    $agent.arguments = $defaultArguments
}

$replacements = [ordered]@{
    '{prompt}' = $prompt
    '{promptFile}' = $promptFile
    '{project}' = $project
    '{workspace}' = $workspace
    '{repoRoot}' = $repoRoot
    '{run}' = $run
    '{evidence}' = $evidence
    '{sessionId}' = $sessionId
}

function Expand-AgentPlaceholders {
    param([string]$Text)

    foreach ($pair in $replacements.GetEnumerator()) {
        $Text = $Text.Replace($pair.Key, $pair.Value)
    }

    $Text
}

$agentArguments = foreach ($argument in @($agent.arguments)) {
    Expand-AgentPlaceholders ([string]$argument)
}

$agentInput = if ($agent.PSObject.Properties['stdin'] -and $agent.stdin) {
    Expand-AgentPlaceholders ([string]$agent.stdin)
} else {
    $null
}

# CreateProcess rejects a command line over 32,767 characters, and Windows reports it as the
# thoroughly misleading "The filename or extension is too long". A prompt that has outgrown argv
# belongs on standard input, so say so rather than letting the agent die at the starting line.
$commandLineLength = $agent.command.Length +
    [int](@($agentArguments) | Measure-Object -Property Length -Sum).Sum +
    (3 * @($agentArguments).Count)
if ($commandLineLength -ge 32767) {
    throw ("The agent command line is $commandLineLength characters, over the 32,767 Windows " +
        'limit. Pass the prompt on standard input ("stdin": "{prompt}") or as a path ' +
        '({promptFile}) instead of putting {prompt} in the arguments.')
}

"=== Agent: $($agent.command) ===" | Tee-Object -FilePath $transcript | Out-Null
Write-Host "==> Improving $([IO.Path]::GetFileNameWithoutExtension($project)) with $($agent.command)" -ForegroundColor Cyan
Write-Host "    Evidence: $run" -ForegroundColor DarkGray
Write-AgentStatus -State 'running' -Phase 'agent'

Push-Location $workspace
try {
    # Windows PowerShell pipes to native commands as ASCII by default, which would quietly mangle
    # every non-ASCII character in the prompt.
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $agentFailure = $null
    $agentResult = @{ ExitCode = $null }
    try {
        & {
            # Windows PowerShell turns native stderr into ErrorRecords. Only the process exit
            # code signals agent failure; keep strict error handling outside this child scope.
            $ErrorActionPreference = 'Continue'
            $PSNativeCommandUseErrorActionPreference = $false
            $global:LASTEXITCODE = $null
            if ($null -ne $agentInput) {
                $agentInput | & $agent.command @agentArguments 2>&1
            } else {
                & $agent.command @agentArguments 2>&1
            }

            $agentResult.ExitCode = $global:LASTEXITCODE
        } | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord] -and
                $_.FullyQualifiedErrorId -notin @('NativeCommandError', 'NativeCommandErrorMessage')) {
                throw $_
            }

            $_.ToString()
        } | Tee-Object -FilePath $transcript -Append

        $agentExitCode = $agentResult.ExitCode
    } catch {
        $agentExitCode = if ($agentResult.ExitCode) { $agentResult.ExitCode } else { 1 }
        $agentFailure = $_
        $_ | Out-String | Tee-Object -FilePath $transcript -Append | Write-Host
    }

    if ($agentFailure -or ($null -ne $agentExitCode -and $agentExitCode -ne 0)) {
        $message = if ($agentFailure) { $agentFailure.Exception.Message } else { "The improvement agent exited with code $agentExitCode." }
        Write-AgentStatus -State 'failed' -Phase 'agent' -AgentExitCode $agentExitCode -Message $message
        throw "The improvement agent exited with code $agentExitCode."
    }

    & (Join-Path $PSScriptRoot 'verify-bot.ps1') -BattleBot $project -RunDirectory $run `
        -Configuration $Configuration
} finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Improvement verified and deployed. Fight again to measure it.' -ForegroundColor Green
Write-Output "AUTOCNC_AGENT_TRANSCRIPT=$transcript"
