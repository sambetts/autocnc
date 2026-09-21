<#
.SYNOPSIS
    Checks engine patch application, repeatability, and preservation of local edits.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'engine-patch.ps1')
$gitApplication = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1

$sourceLines = @(git -C (Join-Path $repoRoot 'engine') show HEAD:OpenRA.Game/World.cs)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the pinned upstream World.cs. Run ./scripts/setup.ps1 first.' }
$source = ($sourceLines -join "`n") + "`n"
$unseeded = 'LocalRandom = new MersenneTwister();'
$seeded = "LocalRandom = new MersenneTwister(`n`t`t`t`tunchecked(orderManager.LobbyInfo.GlobalSettings.RandomSeed ^ 0x5f356495));"
if (-not $source.Contains($unseeded)) { throw 'Expected the unpatched upstream engine revision.' }

$scratch = Join-Path ([IO.Path]::GetTempPath()) "autocnc-engine-patch-$([Guid]::NewGuid().ToString('N'))"
$engine = Join-Path $scratch 'engine'
$world = Join-Path $engine 'OpenRA.Game\World.cs'
$encoding = New-Object Text.UTF8Encoding $false

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $world) -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $engine 'OpenRA.sln') | Out-Null
    try {
        $null = Initialize-EnginePatch $engine
        throw 'A directory without an engine checkout was accepted.'
    }
    catch {
        if ($_.Exception.Message -ne 'Engine submodule not found. Run ./scripts/setup.ps1 first.') { throw }
    }

    git -C $engine init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the patch test fixture.' }

    foreach ($lineEnding in @("`n", "`r`n")) {
        $autoCrlf = if ($lineEnding -eq "`r`n") { 'true' } else { 'false' }
        git -C $engine config core.autocrlf $autoCrlf
        if ($LASTEXITCODE -ne 0) { throw 'Could not configure the patch test fixture.' }

        $localEdit = "// Unrelated local edit retained by the patch.`n"
        $original = ($source + $localEdit).Replace("`n", $lineEnding)
        [IO.File]::WriteAllText($world, $original, $encoding)
        git -C $engine add -- OpenRA.sln OpenRA.Game/World.cs
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage the patch test fixture.' }

        $changed = & {
            # Linux PATH can expose both /usr/bin/git and /bin/git.
            function Get-Command { $gitApplication; $gitApplication }
            $LASTEXITCODE = 123
            $PSNativeCommandUseErrorActionPreference = $true
            Initialize-EnginePatch $engine
        }
        if (-not $changed) { throw 'The clean source was not patched.' }
        $expected = $source.Replace($unseeded, $seeded) + $localEdit
        $actual = [IO.File]::ReadAllText($world).Replace("`r`n", "`n")
        if ($actual -cne $expected) { throw 'The patch changed more than the local-random seed.' }

        $patchedHash = (Get-FileHash -LiteralPath $world).Hash
        if (Initialize-EnginePatch $engine) { throw 'An already-applied patch was applied again.' }
        if ((Get-FileHash -LiteralPath $world).Hash -ne $patchedHash) {
            throw 'Repeat patch application changed the engine source.'
        }

        $conflicting = $original.Replace($unseeded, 'LocalRandom = new MersenneTwister(123);')
        [IO.File]::WriteAllText($world, $conflicting, $encoding)
        $rejected = $false
        try {
            $null = Initialize-EnginePatch $engine
        }
        catch {
            if ($_.Exception.Message -notlike 'Could not apply the required engine patch.*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'A conflicting engine edit was not rejected.' }
        if ([IO.File]::ReadAllText($world) -cne $conflicting) {
            throw 'A conflicting engine edit was overwritten.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}

Write-Host 'Engine patch checks passed: missing checkout, LF/CRLF, repeat application, local edits, and conflicts.' -ForegroundColor Green
