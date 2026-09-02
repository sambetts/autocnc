<#
.SYNOPSIS
    Independently cleans, tests, builds, and deploys a bot improvement.

.DESCRIPTION
    Shared by train-bot.ps1 and the launcher's Retry verification action. Generated output is
    cleaned first so a stale or corrupt obj/ref assembly cannot be mistaken for a source failure.
    Progress is appended to the run transcript and machine-readable status is written beside it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BattleBot,
    [Parameter(Mandatory)]
    [string]$RunDirectory,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $BattleBot).Path
$run = (Resolve-Path -LiteralPath $RunDirectory).Path
$workspace = Split-Path -Parent $project
$transcript = Join-Path $run 'agent-transcript.txt'
$statusFile = Join-Path $run 'agent-status.json'
$runBot = Join-Path $PSScriptRoot 'run-bot.ps1'

if ([IO.Path]::GetExtension($project) -ne '.csproj') {
    throw "Verification requires a battle bot project (.csproj): $project"
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

'=== Independent verification ===' | Tee-Object -FilePath $transcript -Append | Write-Host
Write-AgentStatus -State 'running' -Phase 'verification' -AgentExitCode 0

try {
    $verifyProjects = @($project) + @(Get-ChildItem $workspace -Recurse -Filter *.Tests.csproj |
        Select-Object -ExpandProperty FullName)
    foreach ($verifyProject in $verifyProjects) {
        Write-Host "==> Cleaning $([IO.Path]::GetFileNameWithoutExtension($verifyProject))" -ForegroundColor Cyan
        & dotnet clean $verifyProject -c $Configuration --nologo -v quiet 2>&1 |
            Tee-Object -FilePath $transcript -Append
        if ($LASTEXITCODE -ne 0) {
            throw "Could not clean generated output for $verifyProject."
        }
    }

    & $runBot -BattleBot $project -Test -NoLaunch -Configuration $Configuration 2>&1 |
        Tee-Object -FilePath $transcript -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Post-agent verification exited with code $LASTEXITCODE."
    }

    Write-AgentStatus -State 'completed' -Phase 'verification' -AgentExitCode 0 -VerificationExitCode 0
    Write-Host 'Independent verification passed.' -ForegroundColor Green
}
catch {
    $exitCode = if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
    $_ | Out-String | Tee-Object -FilePath $transcript -Append | Write-Host
    Write-AgentStatus -State 'failed' -Phase 'verification' -AgentExitCode 0 `
        -VerificationExitCode $exitCode -Message $_.Exception.Message
    throw
}
