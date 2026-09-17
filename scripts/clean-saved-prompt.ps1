<#
.SYNOPSIS
    One-off: removes hand-written gospel from the saved launcher prompt template.

.DESCRIPTION
    The evolving prompt used to carry its own copy of the SDK surface and engine constants under
    headings like "Known SDK surface - do not rediscover this". That copy is now injected from
    docs/agent-mechanics.md, generated from the compiled assemblies, so the saved copy is both
    redundant and actively wrong: it still claims "There is no resource/tiberium sensing API".

    This deletes those sections from the saved template and leaves the learned half alone. The
    launcher adds the {gameMechanics} placeholder itself on next use, so this only has to remove.

.PARAMETER Reset
    Discards the saved template entirely, so the next round renders the repository default in
    docs/agent-prompt-template.md.

    This is the right switch after the evidence pipeline changes. The saved template is the
    learned half of the prompt, and a template written before summary.json and units.csv existed
    spends most of its length on triage recipes that re-derive, by hand and in PowerShell, the
    aggregates the harness now computes once in tested code. Cleaning cannot fix that; the recipes
    are not wrong, they are simply obsolete, and the default has been rewritten around the derived
    artifacts. The agent rewrites this half at the end of every round anyway, so resetting costs
    one round's accumulated wording and nothing else.

.PARAMETER WhatIf
    Report what would change without writing.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path (Join-Path $env:LOCALAPPDATA 'AutoCnC') 'launcher.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    Write-Host "No launcher settings found at $settingsPath - nothing to clean." -ForegroundColor Yellow
    return
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$template = $settings.AgentPromptTemplate
if ([string]::IsNullOrWhiteSpace($template)) {
    Write-Host 'No saved prompt template - the repository default will be used.' -ForegroundColor Yellow
    return
}

if ($Reset) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $backup = Join-Path (Join-Path $env:LOCALAPPDATA 'AutoCnC') "launcher.backup-$stamp.json"

    Write-Host ("Discarding the saved {0:N0}-character template; the repository default will be used." -f $template.Length) -ForegroundColor Cyan
    if ($PSCmdlet.ShouldProcess($settingsPath, 'Discard saved prompt template')) {
        Copy-Item -LiteralPath $settingsPath -Destination $backup
        $settings.AgentPromptTemplate = $null
        $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
        Write-Host "Backed up to $backup" -ForegroundColor DarkGray
        Write-Host 'Done. The next round renders docs/agent-prompt-template.md.' -ForegroundColor Green
    }

    return
}

# Sections whose entire content is now injected gospel, and which actively contradict it: the
# saved copy still says "There is no resource/tiberium sensing API". Deliberately narrow. Sections
# like "Ruleset facts worth knowing" are left alone: they are partly redundant with game-rules.json
# but they do not contradict anything, and they carry real learned analysis such as harvester
# round-trip economics. Losing that would cost more than the duplication does.
$gospelHeadings = @(
    'Known SDK surface',
    'SDK surface'
)

$lines = $template -split "`r?`n"
$keep = New-Object System.Collections.Generic.List[string]
$removedSections = @()
$dropping = $false
$droppingLevel = 0

foreach ($line in $lines) {
    if ($line -match '^(#{2,6})\s+(.*)$') {
        $level = $Matches[1].Length
        $title = $Matches[2].Trim()

        if ($dropping -and $level -le $droppingLevel) {
            $dropping = $false
        }

        if (-not $dropping) {
            foreach ($heading in $gospelHeadings) {
                if ($title -like "$heading*") {
                    $dropping = $true
                    $droppingLevel = $level
                    $removedSections += $title
                    break
                }
            }
        }
    }

    if (-not $dropping) { $keep.Add($line) }
}

if ($removedSections.Count -eq 0) {
    Write-Host 'No hand-written gospel sections found. Nothing to do.' -ForegroundColor Green
    return
}

$cleaned = ($keep -join [Environment]::NewLine).Trim()

# Refuse to save something the launcher would reject.
foreach ($placeholder in '{workspace}', '{gameGuide}', '{gameRules}', '{fightManifest}',
                         '{battleLog}', '{telemetry}', '{decisionTrace}', '{battle}',
                         '{result}', '{sourceRevision}', '{nextPromptContract}') {
    if ($cleaned -notlike "*$placeholder*") {
        throw "Refusing to save: cleaning removed the $placeholder placeholder."
    }
}

Write-Host 'Removing now-generated sections:' -ForegroundColor Cyan
$removedSections | ForEach-Object { Write-Host "  - $_" }
Write-Host ("Template {0:N0} -> {1:N0} characters." -f $template.Length, $cleaned.Length)

if ($PSCmdlet.ShouldProcess($settingsPath, 'Save cleaned prompt template')) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path (Join-Path $env:LOCALAPPDATA 'AutoCnC') "launcher.backup-$stamp.json"
    Copy-Item -LiteralPath $settingsPath -Destination $backup
    Write-Host "Backup written to $backup" -ForegroundColor DarkGray

    $settings.AgentPromptTemplate = $cleaned
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Write-Host 'Saved. The launcher will add {gameMechanics} on the next improvement run.' -ForegroundColor Green
}
