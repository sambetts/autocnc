<#
.SYNOPSIS
    Regenerates the SDK surface section of docs/agent-mechanics.md by reflection.

.DESCRIPTION
    The improvement agent is told what the SDK offers. When that list was maintained by hand it
    rotted: a round inlined "there is no resource/tiberium sensing API" into the evolving prompt,
    labelled it "do not rediscover this", and every later round believed it - long after the API
    existed. A fact the agent treats as gospel must not be typed by hand.

    So this reads the built assemblies and rewrites the block between the markers in
    docs/agent-mechanics.md. Run it after changing the public SDK surface; CI fails if the
    committed file does not match what this produces.

.PARAMETER Check
    Exit non-zero if the file is out of date instead of rewriting it. Used by CI.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path (Join-Path $repoRoot 'engine') 'bin'
$docPath = Join-Path (Join-Path $repoRoot 'docs') 'agent-mechanics.md'

$beginMarker = '<!-- BEGIN GENERATED SDK SURFACE -->'
$endMarker = '<!-- END GENERATED SDK SURFACE -->'

foreach ($required in 'AutoCnC.Sdk.dll', 'AutoCnC.Core.dll') {
    if (-not (Test-Path (Join-Path $binDir $required))) {
        throw "$required not found in $binDir. Build first: ./scripts/build.ps1"
    }
}

# Reflecting over ModeContext pulls in OpenRA types, so probe the engine's output directory.
$resolver = [System.ResolveEventHandler] {
    param($sender, $e)
    $name = (New-Object System.Reflection.AssemblyName($e.Name)).Name
    $candidate = Join-Path $binDir "$name.dll"
    if (Test-Path $candidate) { [System.Reflection.Assembly]::LoadFrom($candidate) } else { $null }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

try {
    $sdk = [System.Reflection.Assembly]::LoadFrom((Join-Path $binDir 'AutoCnC.Sdk.dll'))
    $core = [System.Reflection.Assembly]::LoadFrom((Join-Path $binDir 'AutoCnC.Core.dll'))

    $shortNames = @{
        'System.Int32' = 'int'; 'System.Boolean' = 'bool'; 'System.String' = 'string'
        'System.Void' = 'void'; 'System.Byte' = 'byte'; 'System.UInt32' = 'uint'
        'System.Int64' = 'long'; 'System.Object' = 'object'; 'System.Double' = 'double'
    }

    function Format-TypeName {
        param($Type)

        if ($Type.IsByRef) { return (Format-TypeName $Type.GetElementType()) }
        if ($Type.IsArray) { return (Format-TypeName $Type.GetElementType()) + '[]' }

        if ($Type.IsGenericType) {
            $definition = $Type.GetGenericTypeDefinition().FullName
            $args = @($Type.GetGenericArguments() | ForEach-Object { Format-TypeName $_ })
            if ($definition -eq 'System.Nullable`1') { return $args[0] + '?' }
            $bare = ($definition -split '`')[0]
            $bare = ($bare -split '\.')[-1]
            return "$bare<$($args -join ', ')>"
        }

        if ($shortNames.ContainsKey($Type.FullName)) { return $shortNames[$Type.FullName] }
        if ($null -eq $Type.FullName) { return $Type.Name }
        return ($Type.FullName -split '\.')[-1]
    }

    function Format-Parameter {
        param($Parameter)

        $text = (Format-TypeName $Parameter.ParameterType) + ' ' + $Parameter.Name
        if ($Parameter.IsOptional) {
            $default = $Parameter.RawDefaultValue
            $rendered = if ($null -eq $default) { 'null' }
                elseif ($default -is [bool]) { $default.ToString().ToLowerInvariant() }
                elseif ($default -is [string]) { '"' + $default + '"' }
                else { [string]$default }
            $text += " = $rendered"
        }
        return $text
    }

    # Skip compiler-generated record plumbing and object overrides: they are noise the agent
    # would have to read past on every single round.
    $ignoredMembers = @(
        'ToString', 'GetHashCode', 'Equals', 'GetType', 'Deconstruct',
        'PrintMembers', 'op_Equality', 'op_Inequality', '<Clone>$', 'EqualityContract'
    )

    function Get-TypeSurface {
        param($Type)

        $lines = @()
        $flags = [System.Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'

        if ($Type.IsEnum) {
            $names = [Enum]::GetNames($Type)
            $lines += "enum $($Type.Name): " + ($names -join ', ')
            return $lines
        }

        foreach ($property in $Type.GetProperties($flags) | Sort-Object Name) {
            if ($ignoredMembers -contains $property.Name) { continue }
            $accessors = @()
            if ($property.GetMethod -and $property.GetMethod.IsPublic) { $accessors += 'get' }
            if ($property.SetMethod -and $property.SetMethod.IsPublic) {
                # An init-only setter carries the IsExternalInit modreq. Rendering it as "set"
                # would invite the agent to mutate a readonly record struct it cannot mutate.
                $isInit = $property.SetMethod.ReturnParameter.GetRequiredCustomModifiers() |
                    Where-Object { $_.Name -eq 'IsExternalInit' }
                $accessors += if ($isInit) { 'init' } else { 'set' }
            }
            $prefix = if ($property.GetMethod -and $property.GetMethod.IsStatic) { 'static ' } else { '' }
            $lines += "$prefix$(Format-TypeName $property.PropertyType) $($property.Name) { $($accessors -join '; ') }"
        }

        foreach ($method in $Type.GetMethods($flags) | Sort-Object Name) {
            if ($method.IsSpecialName -or $ignoredMembers -contains $method.Name) { continue }
            $parameters = @($method.GetParameters() | ForEach-Object { Format-Parameter $_ })
            $prefix = if ($method.IsStatic) { 'static ' } else { '' }
            $lines += "$prefix$(Format-TypeName $method.ReturnType) $($method.Name)($($parameters -join ', '))"
        }

        return $lines
    }

    $sections = @()

    function Add-Section {
        param($Title, $Type, $Note)

        $surface = Get-TypeSurface $Type
        if ($surface.Count -eq 0) { return }

        $script:sections += ''
        $script:sections += "### $Title"
        if ($Note) {
            $script:sections += ''
            $script:sections += $Note
        }
        $script:sections += ''
        $script:sections += '```'
        $script:sections += $surface
        $script:sections += '```'
    }

    Add-Section 'ModeContext — everything a mode can see and do' $sdk.GetType('AutoCnC.Sdk.ModeContext') `
        'One instance per unit. This is the complete public surface; nothing else is reachable from a mode.'

    Add-Section 'UnitAction' $core.GetType('AutoCnC.Core.UnitAction') `
        'The complete set of actions a decision can carry.'

    Add-Section 'UnitDecision' $core.GetType('AutoCnC.Core.UnitDecision') `
        'Returned from `OnTick`. The static factories are the intended way to build one.'

    foreach ($typeName in 'ResourceCell', 'ResourceField', 'ThreatSnapshot', 'ThreatKind') {
        $type = $core.GetType("AutoCnC.Core.$typeName")
        if ($type) { Add-Section $typeName $type $null }
    }

    $coreTypes = $core.GetTypes() |
        Where-Object { $_.IsPublic -and -not $_.IsNested } |
        ForEach-Object { $_.Name } | Sort-Object

    $sections += ''
    $sections += '### Every public AutoCnC.Core type'
    $sections += ''
    $sections += '```'
    $sections += ($coreTypes -join ', ')
    $sections += '```'

    $generated = @($beginMarker, '', '<!-- Generated by scripts/export-agent-api.ps1. Do not edit by hand. -->') +
        $sections + @('', $endMarker)

    if (-not (Test-Path $docPath)) {
        throw "Gospel document not found: $docPath"
    }

    $existing = Get-Content -LiteralPath $docPath -Raw
    $beginIndex = $existing.IndexOf($beginMarker)
    $endIndex = $existing.IndexOf($endMarker)
    if ($beginIndex -lt 0 -or $endIndex -lt 0) {
        throw "Markers not found in $docPath. Expected $beginMarker and $endMarker."
    }

    $newline = if ($existing -match "`r`n") { "`r`n" } else { "`n" }
    $updated = $existing.Substring(0, $beginIndex) +
        ($generated -join $newline) +
        $existing.Substring($endIndex + $endMarker.Length)

    if ($Check) {
        if ($updated -ne $existing) {
            Write-Host 'docs/agent-mechanics.md is out of date.' -ForegroundColor Red
            Write-Host 'Regenerate it with: ./scripts/export-agent-api.ps1' -ForegroundColor Red
            exit 1
        }

        Write-Host 'docs/agent-mechanics.md is up to date.' -ForegroundColor Green
        exit 0
    }

    if ($updated -eq $existing) {
        Write-Host 'docs/agent-mechanics.md already up to date.' -ForegroundColor Green
    }
    else {
        Set-Content -LiteralPath $docPath -Value $updated -NoNewline -Encoding utf8
        Write-Host "Updated $docPath" -ForegroundColor Cyan
    }
}
finally {
    [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
