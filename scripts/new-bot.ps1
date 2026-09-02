<#
.SYNOPSIS
    Creates a new AutoC&C battle bot from the starter template.

.DESCRIPTION
    Copies templates/BattleBot into a named child of OutputDirectory, replaces all template
    tokens, and verifies the generated solution unless NoBuild is supplied.

.PARAMETER Name
    Display name for the bot. A legal PascalCase C# identifier and destination folder name are
    derived from it.

.PARAMETER OutputDirectory
    Parent directory for the generated bot. Defaults to the repository's bots directory.

.PARAMETER Force
    Replaces the exact destination directory if it already exists. The destination is validated
    as a direct child of OutputDirectory before anything is removed.

.PARAMETER NoBuild
    Creates the bot without building or running its starter tests.

.EXAMPLE
    ./scripts/new-bot.ps1 -Name "First Contact"

.EXAMPLE
    ./scripts/new-bot.ps1 -Name "First Contact" -OutputDirectory "C:\Bot Workspaces"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Name,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'bots'),

    [switch]$Force,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-BotIdentifier {
    param([Parameter(Mandatory = $true)][string]$Value)

    $displayName = $Value.Trim()
    if ($displayName.Length -eq 0) {
        throw 'Bot name must not be empty or whitespace.'
    }

    if ($displayName.Length -gt 80) {
        throw 'Bot name must be 80 characters or fewer.'
    }

    foreach ($invalidCharacter in [System.IO.Path]::GetInvalidFileNameChars()) {
        if ($displayName.IndexOf($invalidCharacter) -ge 0) {
            throw "Bot name contains an invalid character: '$invalidCharacter'."
        }
    }

    $words = [System.Text.RegularExpressions.Regex]::Matches($displayName, '[A-Za-z0-9]+')
    if ($words.Count -eq 0) {
        throw 'Bot name must contain at least one ASCII letter or digit.'
    }

    $parts = @()
    foreach ($match in $words) {
        $word = $match.Value
        $part = $word.Substring(0, 1).ToUpperInvariant()
        if ($word.Length -gt 1) {
            $part += $word.Substring(1)
        }

        $parts += $part
    }

    $identifier = $parts -join ''
    if ([char]::IsDigit($identifier[0])) {
        $identifier = "Bot$identifier"
    }

    if ($identifier -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        $identifier = "Bot$identifier"
    }

    if ($identifier.Length -gt 64) {
        throw "The derived C# identifier '$identifier' is longer than 64 characters. Use a shorter name."
    }

    return $identifier
}

function Assert-DirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $trimCharacters = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $parentPrefix = $Parent.TrimEnd($trimCharacters) + $separator

    if (-not $Child.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing destination outside OutputDirectory: $Child"
    }

    $relativePart = $Child.Substring($parentPrefix.Length)
    if ($relativePart.Length -eq 0 -or
        $relativePart.IndexOf([System.IO.Path]::DirectorySeparatorChar) -ge 0 -or
        $relativePart.IndexOf([System.IO.Path]::AltDirectorySeparatorChar) -ge 0) {
        throw "Destination must be one direct child of OutputDirectory: $Child"
    }
}

function ConvertTo-XmlText {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function ConvertTo-JsonStringContent {
    param([Parameter(Mandatory = $true)][string]$Value)

    $quoted = ConvertTo-Json -InputObject $Value -Compress
    return $quoted.Substring(1, $quoted.Length - 2)
}

function ConvertTo-CSharpStringContent {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace('\', '\\').Replace('"', '\"')
}

function Get-AutoCnCVersion {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $propsPath = Join-Path $RepositoryRoot 'src\Directory.Build.props'
    $propsText = [System.IO.File]::ReadAllText($propsPath)
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $propsText,
        '<Version>\s*([^<\s]+)\s*</Version>')

    if (-not $match.Success) {
        throw "Could not determine the AutoC&C package version from '$propsPath'."
    }

    return $match.Groups[1].Value
}

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$templateRoot = Join-Path $repoRoot 'templates\BattleBot'
if (-not (Test-Path -LiteralPath $templateRoot -PathType Container)) {
    throw "Battle bot template not found: $templateRoot"
}

$displayName = $Name.Trim()
$identifier = ConvertTo-BotIdentifier $displayName
$rootNamespace = "AutoCnC.$identifier"
$autoCnCVersion = Get-AutoCnCVersion $repoRoot

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot -PathType Leaf) {
    throw "OutputDirectory is a file, not a directory: $outputRoot"
}

$destination = [System.IO.Path]::GetFullPath((Join-Path $outputRoot $identifier))
Assert-DirectChildPath -Parent $outputRoot -Child $destination

if ([string]::Equals($destination, $templateRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to replace the battle bot template itself.'
}

$replaceExisting = $false
if (Test-Path -LiteralPath $destination) {
    $existing = Get-Item -LiteralPath $destination -Force
    if (-not $existing.PSIsContainer) {
        throw "Destination exists and is not a directory: $destination"
    }

    if (-not $Force) {
        throw "Destination already exists: $destination. Pass -Force to replace this exact directory."
    }

    if (($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to replace a symbolic link or junction: $destination"
    }

    if (Test-Path -LiteralPath (Join-Path $destination '.git')) {
        throw "Refusing to replace a directory containing git metadata: $destination"
    }

    $replaceExisting = $true
}

$packagesPath = Join-Path $repoRoot 'packages'
if (-not $NoBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK is required to verify the new bot. Install it or pass -NoBuild.'
    }

    $requiredPackages = @(
        (Join-Path $packagesPath "AutoCnC.Core.$autoCnCVersion.nupkg"),
        (Join-Path $packagesPath "AutoCnC.Sdk.$autoCnCVersion.nupkg")
    )
    $missingPackages = @($requiredPackages | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missingPackages.Count -gt 0) {
        throw "AutoC&C packages are not built. Run '.\scripts\build.ps1 -SkipBots' first, or pass -NoBuild."
    }
}

if ($replaceExisting) {
    Write-Host "==> Replacing $destination" -ForegroundColor Yellow
    Remove-Item -LiteralPath $destination -Recurse -Force
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($destination) | Out-Null

$generationComplete = $false
try {
    foreach ($entry in @(Get-ChildItem -LiteralPath $templateRoot -Force)) {
        Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse -Force
    }

    $identifierToken = '__AUTOCNC_BOT_IDENTIFIER__'
    $namedEntries = @(Get-ChildItem -LiteralPath $destination -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($entry in $namedEntries) {
        if ($entry.Name.Contains($identifierToken)) {
            $newName = $entry.Name.Replace($identifierToken, $identifier)
            Rename-Item -LiteralPath $entry.FullName -NewName $newName
        }
    }

    $replacements = [ordered]@{
        '__AUTOCNC_BOT_DISPLAY_NAME_CSHARP__' = (ConvertTo-CSharpStringContent $displayName)
        '__AUTOCNC_BOT_IDENTIFIER__' = $identifier
        '__AUTOCNC_BOT_ROOT_NAMESPACE__' = $rootNamespace
        '__AUTOCNC_BOT_PROJECT_GUID__' = ([guid]::NewGuid().ToString('D').ToUpperInvariant())
        '__AUTOCNC_TEST_PROJECT_GUID__' = ([guid]::NewGuid().ToString('D').ToUpperInvariant())
        '__AUTOCNC_REPOSITORY_PATH_XML__' = (ConvertTo-XmlText $repoRoot)
        '__AUTOCNC_PACKAGES_PATH_XML__' = (ConvertTo-XmlText $packagesPath)
        '__AUTOCNC_REPOSITORY_PATH_JSON__' = (ConvertTo-JsonStringContent $repoRoot)
        '__AUTOCNC_VERSION__' = $autoCnCVersion
    }

    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $generatedFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Force)
    foreach ($file in $generatedFiles) {
        $content = [System.IO.File]::ReadAllText($file.FullName)
        foreach ($replacement in $replacements.GetEnumerator()) {
            $content = $content.Replace([string]$replacement.Key, [string]$replacement.Value)
        }

        [System.IO.File]::WriteAllText($file.FullName, $content, $utf8WithoutBom)
    }

    $unresolved = @()
    foreach ($file in $generatedFiles) {
        $content = [System.IO.File]::ReadAllText($file.FullName)
        if ($content -match '__AUTOCNC_[A-Z0-9_]+__') {
            $unresolved += $file.FullName
        }
    }

    $unresolvedNames = @(Get-ChildItem -LiteralPath $destination -Recurse -Force |
        Where-Object { $_.Name -match '__AUTOCNC_[A-Z0-9_]+__' })
    if ($unresolved.Count -gt 0 -or $unresolvedNames.Count -gt 0) {
        throw 'One or more template tokens were not replaced.'
    }

    $generationComplete = $true
}
finally {
    if (-not $generationComplete -and (Test-Path -LiteralPath $destination -PathType Container)) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}

$projectPath = Join-Path $destination "$identifier.csproj"
$solutionPath = Join-Path $destination "$identifier.sln"
$testProjectPath = Join-Path $destination "Tests\$identifier.Tests.csproj"

Write-Host "==> Created battle bot '$displayName'" -ForegroundColor Green
Write-Host "    Project: $projectPath"

if (-not $NoBuild) {
    Write-Host '==> Building generated solution' -ForegroundColor Cyan
    & dotnet build $solutionPath -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Generated solution build failed with exit code $LASTEXITCODE. The scaffold remains at '$destination'."
    }

    Write-Host '==> Running starter tests' -ForegroundColor Cyan
    & dotnet test $testProjectPath -c Debug --no-build --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Generated bot tests failed with exit code $LASTEXITCODE. The scaffold remains at '$destination'."
    }
}
else {
    Write-Host '    Build skipped (-NoBuild).' -ForegroundColor DarkGray
}

Write-Output "AUTOCNC_BOT_PROJECT=$projectPath"
