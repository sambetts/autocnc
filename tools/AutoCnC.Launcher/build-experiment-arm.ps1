<#
.SYNOPSIS
    Builds one immutable continuous-evaluation arm into an isolated output directory.

.DESCRIPTION
    Unlike run-bot.ps1, this never writes to engine/bin/bots. Candidate and control builds receive
    different output directories, so compiling the second arm cannot overwrite the first arm's
    assembly before its benchmark runs.

.PARAMETER ResultPath
    Writes the evaluated MSBuild TargetPath as JSON for the launcher. AssemblyName may come from
    imports, conditions or property expansion, so the caller must not guess it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [Parameter(Mandatory)]
    [string]$AutoCnCPath,
    [Parameter(Mandatory)]
    [string]$ResultPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$repositoryPath = (Resolve-Path -LiteralPath $AutoCnCPath).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$packages = Join-Path $repositoryPath 'packages'
$sharedBotDirectory = [IO.Path]::GetFullPath(
    (Join-Path $repositoryPath 'engine\bin\bots'))
$trimSeparators = [char[]]@(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$sharedBotDirectory = $sharedBotDirectory.TrimEnd($trimSeparators)
$outputPath = $outputPath.TrimEnd($trimSeparators)
$sharedPrefix = $sharedBotDirectory + [IO.Path]::DirectorySeparatorChar
if ([string]::Equals($outputPath, $sharedBotDirectory,
        [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($sharedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Immutable experiment arms cannot be built into engine/bin/bots.'
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

dotnet build $projectPath -c $Configuration -v quiet --nologo `
    /p:AutoCnCPath="$repositoryPath" `
    /p:BattleBotInstallDirectory="$outputPath" `
    /p:RestoreSources="$packages"
if ($LASTEXITCODE -ne 0) { throw 'Immutable battle bot build failed.' }

$target = (dotnet msbuild $projectPath -getProperty:TargetPath -nologo `
        -p:Configuration=$Configuration `
        -p:AutoCnCPath="$repositoryPath" `
        -p:BattleBotInstallDirectory="$outputPath").Trim()
if (-not (Test-Path -LiteralPath $target)) {
    throw "The immutable build reported '$target', which does not exist."
}

$targetDirectory = (Resolve-Path -LiteralPath (Split-Path -Parent $target)).Path
$resolvedOutput = (Resolve-Path -LiteralPath $outputPath).Path
if (-not [string]::Equals($targetDirectory, $resolvedOutput,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The immutable build escaped its output directory: $target"
}

$resultDirectory = Split-Path -Parent $ResultPath
if ($resultDirectory) {
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
}
[ordered]@{
    SchemaVersion = 1
    TargetPath = [IO.Path]::GetFullPath($target)
} | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8

Write-Host "Immutable arm: $target"
