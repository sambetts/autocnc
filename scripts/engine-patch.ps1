<#
.SYNOPSIS
    Applies AutoC&C's required patches to the pinned upstream OpenRA source.
#>

function Initialize-EnginePatch([string]$EngineDirectory) {
    $patches = @('openra-local-random.patch', 'openra-gl-performance.patch') | ForEach-Object {
        Join-Path (Split-Path -Parent $PSScriptRoot) "patches\$_"
    }
    foreach ($patch in $patches) {
        if (-not (Test-Path -LiteralPath $patch -PathType Leaf)) {
            throw "Required engine patch not found: $patch"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $EngineDirectory 'OpenRA.sln') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $EngineDirectory '.git'))) {
        throw 'Engine submodule not found. Run ./scripts/setup.ps1 first.'
    }

    $git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    # Windows PowerShell treats redirected native stderr as errors, including an expected
    # reverse-check failure on clean source. Native exit codes are written to global scope.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $changed = $false
    foreach ($patch in $patches) {
        $global:LASTEXITCODE = $null
        $output = @(& $git -C $EngineDirectory apply --reverse --check -- $patch 2>&1)
        if ($global:LASTEXITCODE -eq 0) { continue }

        Write-Host "==> Applying OpenRA patch: $(Split-Path -Leaf $patch)" -ForegroundColor Cyan
        $global:LASTEXITCODE = $null
        $output = @(& $git -C $EngineDirectory apply -- $patch 2>&1)
        if ($global:LASTEXITCODE -ne 0) {
            throw "Could not apply the required engine patch '$patch'. Inspect your engine edits with 'git -C engine diff'; resolve the conflict and rerun setup. No engine edits were discarded.`n$($output -join "`n")"
        }
        $changed = $true
    }
    return $changed
}
