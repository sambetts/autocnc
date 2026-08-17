<#
.SYNOPSIS
    Runs OpenRA's own YAML linter against the AutoC&C mod.

.DESCRIPTION
    This is the highest-value check in the project. It constructs every actor in the mod, so it
    catches missing trait dependencies, unsatisfied Requires<T> constraints, conditions that are
    consumed but never granted, and non-canonical YAML formatting — none of which the C#
    compiler can see.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $repoRoot 'engine'
$utility = Join-Path $engineDir 'bin\OpenRA.Utility.dll'

if (-not (Test-Path $utility)) {
    throw 'Engine not built. Run ./scripts/build.ps1 first.'
}

$env:ENGINE_DIR = $engineDir
$env:MOD_SEARCH_PATHS = "$(Join-Path $repoRoot 'mods'),$(Join-Path $engineDir 'mods')"

Write-Host '==> Linting autocnc mod YAML' -ForegroundColor Cyan
$output = & dotnet $utility autocnc --check-yaml 2>&1
$output | ForEach-Object { Write-Host $_ }

$errors = @($output | Select-String -Pattern '^Error')
if ($errors.Count -gt 0) {
    throw "YAML lint failed with $($errors.Count) error(s)."
}

Write-Host ''
Write-Host 'YAML lint passed.' -ForegroundColor Green
