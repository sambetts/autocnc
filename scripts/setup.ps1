<#
.SYNOPSIS
    One-time setup: fetch the pinned OpenRA engine submodule.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host '==> Fetching OpenRA engine submodule' -ForegroundColor Cyan
Push-Location $repoRoot
try {
    git submodule update --init --depth 1
    if ($LASTEXITCODE -ne 0) { throw "git submodule update failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$engineDir = Join-Path $repoRoot 'engine'
if (-not (Test-Path (Join-Path $engineDir 'OpenRA.sln'))) {
    throw "Engine submodule looks empty: $engineDir. Try: git submodule update --init --force"
}

Write-Host ''
Write-Host 'Setup complete. Next: ./scripts/build.ps1' -ForegroundColor Green
