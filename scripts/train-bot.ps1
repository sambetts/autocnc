<#
.SYNOPSIS
    Runs a local coding agent against a battle bot, then verifies and deploys the result.

.DESCRIPTION
    This is the command-line half of the launcher's optional Analyze & improve action. A training
    run contains the fight manifest, telemetry, battle log and decision trace. The launcher also
    captures a reversible source snapshot before invoking this script.

    Agent configuration is provider-neutral JSON:
      { "command": "copilot", "arguments": ["-p", "{prompt}", "--allow-all-tools"] }

    Supported placeholders are {prompt}, {promptFile}, {project}, {workspace}, {evidence}, and
    {run}. The built-in Copilot configuration grants access to {evidence}, but not to the
    launcher's reversible source snapshot stored elsewhere in the run.

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
$promptFile = Join-Path $evidence 'agent-prompt.txt'
$transcript = Join-Path $run 'agent-transcript.txt'
$statusFile = Join-Path $run 'agent-status.json'

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
            "$($_.Name): $($_.Outcome), army=$($_.ArmyValue), buildings=$($_.Buildings), killed=$($_.Killed), lost=$($_.Lost), cash=$($_.Cash)"
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

The template must retain these placeholders exactly:
{workspace}, {gameGuide}, {gameRules}, {fightManifest}, {battleLog}, {telemetry}, {decisionTrace},
{battle}, {result}, {sourceRevision}, {nextPromptContract}

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
        '{battle}' = "map=$($battle.Map), difficulty=$($battle.Difficulty), opponents=$($battle.Opponents), faction=$($battle.Faction), opponent faction=$($battle.BotFaction), speed=$($battle.GameSpeed), execution=$($battle.ExecutionMode)"
        '{result}' = "$($result.Outcome) after $($result.DurationSeconds) game seconds; $score`n$playerFeedback"
        '{sourceRevision}' = [string]$manifest.SourceRevision
        '{nextPromptContract}' = $nextPromptContract
    }

    $prompt = $template
    foreach ($replacement in $promptReplacements.GetEnumerator()) {
        $prompt = $prompt.Replace([string]$replacement.Key, [string]$replacement.Value)
    }

    Set-Content -LiteralPath $promptFile -Value $prompt -Encoding utf8
}

$prompt = Get-Content -LiteralPath $promptFile -Raw
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
        arguments = @(
            '-p', '{prompt}',
            '--allow-all-tools',
            '--no-ask-user',
            '--no-custom-instructions',
            '--no-remote-export',
            '--add-dir', '{evidence}'
        )
    }
}

if (-not $agent.command) {
    throw 'Agent configuration has no command.'
}

$replacements = [ordered]@{
    '{prompt}' = $prompt
    '{promptFile}' = $promptFile
    '{project}' = $project
    '{workspace}' = $workspace
    '{run}' = $run
    '{evidence}' = $evidence
}

$agentArguments = foreach ($argument in @($agent.arguments)) {
    $resolved = [string]$argument
    foreach ($pair in $replacements.GetEnumerator()) {
        $resolved = $resolved.Replace($pair.Key, $pair.Value)
    }

    $resolved
}

Set-Content -LiteralPath $transcript -Value "=== Agent: $($agent.command) ==="
Write-Host "==> Improving $([IO.Path]::GetFileNameWithoutExtension($project)) with $($agent.command)" -ForegroundColor Cyan
Write-Host "    Evidence: $run" -ForegroundColor DarkGray
Write-AgentStatus -State 'running' -Phase 'agent'

Push-Location $workspace
try {
    $agentFailure = $null
    try {
        & $agent.command @agentArguments 2>&1 | Tee-Object -FilePath $transcript -Append
        $agentExitCode = $LASTEXITCODE
    } catch {
        $agentExitCode = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
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
