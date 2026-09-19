<#
.SYNOPSIS
    Sends one message to a training run's coding agent and prints its reply.

.DESCRIPTION
    This is the command-line half of the launcher's Chat tab. A training run pins one agent
    session id, and every turn resumes it, so the improvement round, any repair that follows it,
    and anything asked before or after share one conversation and one memory.

    The agent is invoked with the run's own agent-command.json, so a customised agent — a
    different model, extra allowed directories — answers questions as the same agent that does
    the improving. Only {prompt} differs: it carries the message instead of the round's evidence
    packet, and travels whichever channel that configuration uses for the prompt — standard input
    by default, since that is where the improvement round sends it.

    Talking does not build, verify or deploy. The agent can still edit the bot if asked to; run
    verify-bot.ps1 afterwards when it has.

.PARAMETER BattleBot
    The bot project whose workspace the conversation is about.

.PARAMETER RunDirectory
    The training-run directory containing manifest.json and the pinned agent session id.

.PARAMETER Message
    The message to send. Use -MessageFile instead for anything multi-line.

.PARAMETER MessageFile
    A UTF-8 file containing the message to send. Takes precedence over -Message.

.PARAMETER AgentConfiguration
    Optional path to provider-neutral agent JSON. Defaults to agent-command.json in RunDirectory,
    then to GitHub Copilot CLI when that file is absent.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BattleBot,
    [Parameter(Mandatory)]
    [string]$RunDirectory,
    [string]$Message,
    [string]$MessageFile,
    [string]$AgentConfiguration
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $BattleBot).Path
$run = (Resolve-Path -LiteralPath $RunDirectory).Path

if ([IO.Path]::GetExtension($project) -ne '.csproj') {
    throw "Talking to the improvement agent requires a battle bot project (.csproj): $project"
}

$manifestPath = Join-Path $run 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Training run is missing manifest.json.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($MessageFile) {
    $resolvedMessageFile = (Resolve-Path -LiteralPath $MessageFile).Path
    $Message = Get-Content -LiteralPath $resolvedMessageFile -Raw
}

if ([string]::IsNullOrWhiteSpace($Message)) {
    throw 'There is no message to send.'
}

$evidence = Join-Path $run 'evidence'
if (-not (Test-Path -LiteralPath $evidence -PathType Container)) {
    $evidence = $run
}

# Created here when the player speaks first. An improvement round that has never been started has
# not written one yet, and being able to steer the agent before it reads anything is the point.
$sessionId = [string]$manifest.AgentSessionId
if (-not $sessionId) {
    $sessionId = [guid]::NewGuid().ToString()
    $manifest | Add-Member -NotePropertyName AgentSessionId -NotePropertyValue $sessionId -Force
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}

$configurationPath = if ($AgentConfiguration) {
    $AgentConfiguration
} else {
    Join-Path $run 'agent-command.json'
}

# Talking before the first improvement round is the point of talking early, and that round is what
# writes agent-command.json. A configuration that is not there yet is normal, not an error.
if (Test-Path -LiteralPath $configurationPath) {
    $configurationPath = (Resolve-Path -LiteralPath $configurationPath).Path
    $agent = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
} else {
    $agent = [pscustomobject]@{
        command = 'copilot'
        arguments = @(
            '--session-id', '{sessionId}',
            '--allow-all-tools',
            '--no-ask-user',
            '--no-custom-instructions',
            '--no-remote-export',
            '--add-dir', '{evidence}'
        )
        stdin = '{prompt}'
    }
}

if (-not $agent.command) {
    throw 'Agent configuration has no command.'
}

$workspace = Split-Path -Parent $project
$arguments = @($agent.arguments)
$agentStdin = if ($agent.PSObject.Properties['stdin'] -and $agent.stdin) { [string]$agent.stdin } else { $null }

# The message travels the channel the prompt travels, whichever that is. A configuration that
# names {prompt} in its arguments keeps carrying it there; everything else gets it on standard
# input, which is where the improvement round puts it and the only channel a long message cannot
# overflow.
$carriesPrompt = @($arguments) + @($agentStdin) | Where-Object { $_ } | Where-Object {
    $_.Contains('{prompt}') -or $_.Contains('{promptFile}')
}
if (-not $carriesPrompt) {
    $agentStdin = '{prompt}'
}

# Copilot-specific repairs, applied only to Copilot so a provider-neutral configuration is not
# handed flags its agent has never heard of.
if ($agent.command -eq 'copilot') {
    # An agent configured before conversations existed has no {sessionId} to bind, and without it
    # every turn would open a new session and forget the last one.
    if (-not ($arguments -contains '--session-id' -or $arguments -contains '--resume')) {
        $arguments = @($arguments) + @('--session-id', '{sessionId}')
    }

    # Only the answer. A round's transcript wants the run statistics that follow it; a
    # conversation wants something quotable, and the launcher records whatever is below the
    # "=== Agent ===" heading as what the agent said.
    if (-not ($arguments -contains '--silent' -or $arguments -contains '-s')) {
        $arguments = @($arguments) + @('--silent')
    }
}

$replacements = [ordered]@{
    '{prompt}' = $Message
    '{promptFile}' = if ($MessageFile) { $resolvedMessageFile } else { '' }
    '{project}' = $project
    '{workspace}' = $workspace
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

$agentArguments = foreach ($argument in $arguments) {
    Expand-AgentPlaceholders ([string]$argument)
}

$agentInput = if ($agentStdin) { Expand-AgentPlaceholders $agentStdin } else { $null }

$transcript = Join-Path $run 'agent-chat-turn.txt'
"=== You ===" | Tee-Object -FilePath $transcript | Out-Null
Add-Content -LiteralPath $transcript -Value $Message

Write-Host "==> Asking $($agent.command) about $([IO.Path]::GetFileNameWithoutExtension($project))" -ForegroundColor Cyan
Write-Host ''

# The launcher reads the reply back as everything below the last heading, so this marks where the
# message stops and the answer starts. Without it a turn's own question is read back as its answer.
Add-Content -LiteralPath $transcript -Value "=== Agent ==="

Push-Location $workspace
try {
    # Windows PowerShell pipes to native commands as ASCII by default, which would quietly mangle
    # every non-ASCII character in the message.
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $agentResult = @{ ExitCode = $null }
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

    $exitCode = $agentResult.ExitCode
} finally {
    Pop-Location
}

if ($null -ne $exitCode -and $exitCode -ne 0) {
    throw "The improvement agent exited with code $exitCode."
}

Write-Output "AUTOCNC_CHAT_TRANSCRIPT=$transcript"
