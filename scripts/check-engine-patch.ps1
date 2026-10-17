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
$graphicsSources = @{}
foreach ($path in @('OpenRA.Platforms.Default/OpenGL.cs', 'OpenRA.Test/OpenRA.Test.csproj')) {
    $lines = @(git -C (Join-Path $repoRoot 'engine') show "HEAD:$path")
    if ($LASTEXITCODE -ne 0) { throw "Could not read the pinned upstream $path." }
    $graphicsSources[$path] = ($lines -join "`n") + "`n"
}
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
        foreach ($path in $graphicsSources.Keys) {
            $file = Join-Path $engine $path
            New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
            [IO.File]::WriteAllText($file, $graphicsSources[$path].Replace("`n", $lineEnding), $encoding)
        }
        $graphicsTest = Join-Path $engine 'OpenRA.Test/OpenRA.Platforms.Default/OpenGLTest.cs'
        if (Test-Path -LiteralPath $graphicsTest) { Remove-Item -LiteralPath $graphicsTest }
        git -C $engine add -- OpenRA.sln OpenRA.Game/World.cs OpenRA.Platforms.Default/OpenGL.cs OpenRA.Test/OpenRA.Test.csproj
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
        $gl = [IO.File]::ReadAllText((Join-Path $engine 'OpenRA.Platforms.Default/OpenGL.cs'))
        if (-not $gl.Contains('case GL_DEBUG_SEVERITY_HIGH when type != GL_DEBUG_TYPE_PERFORMANCE:')) {
            throw 'The graphics performance patch was not applied.'
        }
        if (-not (Test-Path -LiteralPath $graphicsTest)) { throw 'The graphics regression tests were not installed.' }
        $testProject = [IO.File]::ReadAllText((Join-Path $engine 'OpenRA.Test/OpenRA.Test.csproj')).Replace("`r`n", "`n")
        $expectedProject = $graphicsSources['OpenRA.Test/OpenRA.Test.csproj'].Replace(
            '    <ProjectReference Include="..\OpenRA.Game\OpenRA.Game.csproj" />',
            "    <ProjectReference Include=`"..\OpenRA.Game\OpenRA.Game.csproj`" />`n" +
            '    <ProjectReference Include="..\OpenRA.Platforms.Default\OpenRA.Platforms.Default.csproj" />')
        if ($testProject -cne $expectedProject) { throw 'The patch did not add only the graphics test project reference.' }

        $patchedHash = (Get-FileHash -LiteralPath $world).Hash
        if (Initialize-EnginePatch $engine) { throw 'An already-applied patch was applied again.' }
        if ((Get-FileHash -LiteralPath $world).Hash -ne $patchedHash) {
            throw 'Repeat patch application changed the engine source.'
        }

        $graphicsPatch = Join-Path $repoRoot 'patches/openra-gl-performance.patch'
        git -C $engine apply --reverse -- $graphicsPatch
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pre-graphics-patch fixture.' }
        if (-not (Initialize-EnginePatch $engine)) { throw 'An existing checkout did not receive the new graphics patch.' }
        if ((Get-FileHash -LiteralPath $world).Hash -ne $patchedHash) {
            throw 'Installing the graphics patch changed the already-patched local-random source.'
        }

        $conflicting = $original.Replace($unseeded, 'LocalRandom = new MersenneTwister(123);')
        [IO.File]::WriteAllText($world, $conflicting, $encoding)
        $rejected = $false
        try {
            $null = Initialize-EnginePatch $engine
        }
        catch {
            if ($_.Exception.Message -notlike 'Could not apply the required engine patch*') { throw }
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

Write-Host 'Engine patch checks passed: missing checkout, LF/CRLF, repeat application, partial upgrades, local edits, and conflicts.' -ForegroundColor Green
exit 0
