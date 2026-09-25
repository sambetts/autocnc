<#
.SYNOPSIS
    Identifies the game rules a fight is played under.

.DESCRIPTION
    A fight's result depends on the rules as much as on the bot. When the opponent AI's
    InitialStanceAI was HoldFire, its guard towers, obelisks and SAM sites never fired; the fix made
    the same champion win 5 of 8 benchmark games instead of 8. Every run before the fix then
    looked like a bot that had regressed. The cross-run trend compares only fights with the same
    fingerprint, as it already does for difficulty.

    The fingerprint covers the AutoC&C mod's own YAML and the engine commit whose cnc mod supplies
    the rest. Comments and blank lines are left out, so documenting a rule does not split the
    history, but changing one does.
#>

function Get-RulesFingerprint([Parameter(Mandatory)][string]$RepoRoot) {
    $mod = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'mods\autocnc'))
    $files = @(Get-ChildItem -LiteralPath $mod -Recurse -File -Filter '*.yaml' |
        ForEach-Object { $_.FullName.Substring($mod.Length + 1).Replace('\', '/') } |
        Where-Object { $_ -notmatch '^maps/' } |
        Sort-Object { $_.ToLowerInvariant() })

    $text = [Text.StringBuilder]::new()
    foreach ($file in $files) {
        [void]$text.Append('file ').Append($file).Append("`n")
        foreach ($line in [IO.File]::ReadAllLines((Join-Path $mod $file))) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
            [void]$text.Append($line.TrimEnd()).Append("`n")
        }
    }

    $engine = $null
    $engineDir = Join-Path $RepoRoot 'engine'
    if (Test-Path -LiteralPath $engineDir) {
        $engine = (git -C $engineDir rev-parse HEAD 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or -not $engine) { $engine = $null } else { $engine = $engine.Trim() }
    }
    [void]$text.Append('engine ').Append($(if ($engine) { $engine } else { 'unknown' })).Append("`n")

    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))
    [pscustomobject]@{
        Fingerprint = ([Convert]::ToHexString($hash).ToLowerInvariant()).Substring(0, 16)
        EngineRevision = $engine
        Files = $files
    }
}

function Write-RulesFingerprint([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Path) {
    $rules = Get-RulesFingerprint $RepoRoot
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    [ordered]@{
        schemaVersion = 1
        fingerprint = $rules.Fingerprint
        engineRevision = $rules.EngineRevision
        files = $rules.Files
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Path -Encoding utf8
    return $rules
}
