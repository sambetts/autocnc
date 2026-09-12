<#
.SYNOPSIS
    Shared .NET host and native-library selection for OpenRA.
#>

function Get-EngineOperatingSystem {
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        return 'win'
    }
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) {
        return 'linux'
    }
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
        return 'osx'
    }

    throw 'OpenRA requires Windows, Linux or macOS.'
}

function Get-EngineDotNetInfo([string]$DotNetPath) {
    $previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
    try {
        $env:DOTNET_CLI_UI_LANGUAGE = 'en'
        $info = @(& $DotNetPath --info 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
    }

    $text = $info -join "`n"
    if ($exitCode -ne 0) {
        throw "Could not inspect .NET host '$DotNetPath' (exit $exitCode): $text"
    }

    # Inspect the .NET host, not PowerShell: the two processes can have different architectures.
    $architecture = [regex]::Match($text, '(?m)^\s*Architecture:\s*(\S+)\s*$')
    if (-not $architecture.Success) {
        throw "Could not determine the architecture of .NET host '$DotNetPath'."
    }

    [pscustomobject]@{
        DotNetPath = $DotNetPath
        Architecture = $architecture.Groups[1].Value.ToLowerInvariant()
        HasNet8Runtime = $text -match '(?m)^\s*Microsoft\.NETCore\.App\s+8\.0\.\d+\s'
    }
}

function Get-WindowsX64DotNetPaths {
    if ($env:DOTNET_ROOT_X64) {
        Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe'
    }

    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
        if ($null -ne $key) {
            $location = [string]$key.GetValue('InstallLocation')
            if ($location) { Join-Path $location 'dotnet.exe' }
        }
    }
    finally {
        if ($null -ne $key) { $key.Dispose() }
        $registry.Dispose()
    }

    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if ($programFiles) {
        Join-Path $programFiles 'dotnet\x64\dotnet.exe'
        Join-Path $programFiles 'dotnet\dotnet.exe'
    }
}

function Get-EngineRuntime {
    $operatingSystem = Get-EngineOperatingSystem
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $runtime = Get-EngineDotNetInfo $dotnet

    if ($operatingSystem -eq 'win' -and $runtime.Architecture -eq 'arm64') {
        # The pinned OpenRA packages have Windows x64 DLLs, but no Windows ARM64 DLLs.
        # Only engine execution uses this host; compilation stays on the SDK from PATH.
        $compatible = $null
        foreach ($candidate in @(Get-WindowsX64DotNetPaths | Select-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

            $candidateRuntime = Get-EngineDotNetInfo $candidate
            if ($candidateRuntime.Architecture -eq 'x64' -and $candidateRuntime.HasNet8Runtime) {
                $compatible = $candidateRuntime
                break
            }
        }

        if ($null -eq $compatible) {
            throw 'OpenRA on Windows ARM64 requires the .NET 8 x64 runtime. Install it alongside ARM64 .NET, or set DOTNET_ROOT_X64 to an existing x64 .NET 8 installation. The ARM64 SDK and launcher do not need replacing.'
        }

        $runtime = $compatible
        Write-Host "==> OpenRA: x64 compatibility on Windows ARM64 ($($runtime.DotNetPath))" -ForegroundColor DarkGray
    }

    $targetPlatform = "$operatingSystem-$($runtime.Architecture)"
    if ($targetPlatform -notin @('win-x64', 'win-x86', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
        throw "The pinned OpenRA native libraries do not support '$targetPlatform'."
    }

    [pscustomobject]@{
        DotNetPath = $runtime.DotNetPath
        TargetPlatform = $targetPlatform
    }
}
