<#
.SYNOPSIS
    Fails if a battle bot workspace contains a test project.

.DESCRIPTION
    Battle bots have no unit tests. A bot is judged by whether it wins, which no assertion can
    tell you, so the verification is a recorded fight and the budget belongs in battle logic.

    Saying so was not enough. The rule was stated only in the half of the improvement prompt that
    each round rewrites, so a round dropped it and the round after re-created the reference bot's
    suite - the same rot that once convinced every round the resource API did not exist. The rule
    is gospel now (docs/agent-mechanics.md), and this is the gate that makes it stick: it runs in
    the independent verification after every improvement round, and in CI over bots/.

    A project counts as a test project if it is named *.Tests.csproj or references a test SDK, so
    renaming the folder does not get around it.

.PARAMETER Path
    Bot workspace, or a directory of them such as bots/. Defaults to the repository's bots/.

.EXAMPLE
    ./scripts/check-bot-tests.ps1
    Check every bot in the repository.
#>
[CmdletBinding()]
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Path) { $Path = Join-Path $repoRoot 'bots' }
if (-not (Test-Path -LiteralPath $Path)) { throw "No such path: $Path" }

$root = (Resolve-Path -LiteralPath $Path).Path

# A .csproj under bin/ or obj/ is generated output, not something an author wrote.
$offenders = Get-ChildItem -LiteralPath $root -Recurse -Filter *.csproj -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Where-Object {
        $_.Name -like '*.Tests.csproj' -or
        (Get-Content -LiteralPath $_.FullName -Raw) -match 'Microsoft\.NET\.Test\.Sdk|\bNUnit\b|\bxunit\b|MSTest'
    }

if (-not $offenders) {
    Write-Output "No test projects under $root - battle bots are verified by fighting."
    exit 0
}

# Write-Output, not Write-Host: this report has to survive being piped into the run transcript,
# which is what the next improvement round is handed when verification fails.
Write-Output 'Battle bots must not contain test projects. Found:'
foreach ($offender in $offenders) {
    Write-Output "  $($offender.FullName)"
}

Write-Output ''
Write-Output 'A bot is judged by whether it wins, not by an assertion. Delete the project and its'
Write-Output 'solution entries, keep any invariant worth remembering as prose beside the code it'
Write-Output 'constrains, and spend the budget on battle logic. See docs/agent-mechanics.md.'
exit 1
