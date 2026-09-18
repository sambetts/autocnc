<#
.SYNOPSIS
	Shared content prerequisites and installation for the pinned OpenRA engine.
#>

function Get-OpenRASupportDirectory([string]$EngineDirectory) {
	$portable = Join-Path $EngineDirectory 'Support'
	if (Test-Path -LiteralPath $portable -PathType Container) { return $portable }

	$homeDirectory = [Environment]::GetFolderPath('UserProfile')
	if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
		$modern = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'OpenRA'
		$legacy = Join-Path ([Environment]::GetFolderPath('Personal')) 'OpenRA'
	}
	elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
		return Join-Path $homeDirectory 'Library/Application Support/OpenRA'
	}
	else {
		$config = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $homeDirectory '.config' }
		$modern = Join-Path $config 'openra'
		$legacy = Join-Path $homeDirectory '.openra'
	}

	if (-not (Test-Path -LiteralPath $modern -PathType Container) -and
		(Test-Path -LiteralPath $legacy -PathType Container)) { return $legacy }
	return $modern
}

function Get-CncRequiredContent([string]$EngineDirectory) {
	$manifest = Join-Path $EngineDirectory 'mods/cnc-content/mod.yaml'
	$inBase = $false
	foreach ($line in Get-Content -LiteralPath $manifest) {
		if ($line -match '^\t\tContentPackage@base:') { $inBase = $true; continue }
		if ($line -match '^\t\t[^\t]') { $inBase = $false }
		if ($inBase -and $line -match '^\t\t\tTestFiles: (.+)$') {
			foreach ($file in ($Matches[1] -split ',')) {
				if ($file.Trim() -notmatch '^\^SupportDir\|Content/cnc/([a-z0-9-]+\.mix)$') {
					throw "Unsupported required content path in $manifest`: $file"
				}
				$Matches[1]
			}
			return
		}
	}
	throw "Required C&C content metadata was not found in $manifest. Run ./scripts/setup.ps1 first."
}

function Assert-CncContent([string]$EngineDirectory) {
	$directory = Join-Path (Get-OpenRASupportDirectory $EngineDirectory) 'Content/cnc'
	$missing = @(Get-CncRequiredContent $EngineDirectory | Where-Object {
		-not (Test-Path -LiteralPath (Join-Path $directory $_) -PathType Leaf)
	})
	if ($missing.Count -gt 0) {
		throw "Required C&C game content is missing from '$directory': $($missing -join ', '). Run ./scripts/install-content.ps1 (no graphics or OpenGL required), then retry the headless battle."
	}
}

function Get-CncContentDownload([string]$EngineDirectory) {
	$manifest = Join-Path $EngineDirectory 'mods/cnc-content/installer/downloads.yaml'
	$fields = @{}
	$files = [ordered]@{}
	$inBase = $false
	foreach ($line in Get-Content -LiteralPath $manifest) {
		if ($line -match '^basefiles:') { $inBase = $true; continue }
		if ($line -match '^\S') { $inBase = $false }
		if (-not $inBase -or [string]::IsNullOrWhiteSpace($line)) { continue }
		if ($line -match '^\t(Type|SHA1|MirrorList): (.+)$') {
			$fields[$Matches[1]] = $Matches[2].Trim()
		}
		elseif ($line -match '^\tExtract:$') { continue }
		elseif ($line -match '^\t\t\^SupportDir\|Content/cnc/([a-z0-9-]+\.mix): ([a-z0-9-]+\.mix)$') {
			$files.Add($Matches[1], $Matches[2])
		}
		else { throw "Unsupported basefiles metadata in $manifest`: $line" }
	}
	if ($fields.Type -ne 'ZipFile' -or $fields.SHA1 -notmatch '^[a-f0-9]{40}$' -or
		$fields.MirrorList -notmatch '^https://' -or $files.Count -eq 0) {
		throw "Invalid basefiles download metadata in $manifest."
	}
	foreach ($file in Get-CncRequiredContent $EngineDirectory) {
		if (-not $files.Contains($file)) { throw "Required file '$file' is absent from $manifest." }
	}
	[pscustomobject]@{ SHA1 = $fields.SHA1; MirrorList = $fields.MirrorList; Files = $files }
}

function Install-CncContentArchive($Download, [string]$Archive, [string]$SupportDirectory) {
	# Use the checksum pinned by OpenRA before opening or installing anything from the archive.
	$actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA1).Hash
	if ($actual -ne $Download.SHA1) { throw "C&C content checksum mismatch: expected $($Download.SHA1), got $actual." }

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
	$staging = Join-Path ([IO.Path]::GetTempPath()) "autocnc-content-$([Guid]::NewGuid().ToString('N'))"
	try {
		[void][IO.Directory]::CreateDirectory($staging)
		foreach ($file in $Download.Files.GetEnumerator()) {
			$entry = $zip.GetEntry($file.Value)
			if ($null -eq $entry -or $entry.Length -eq 0) { throw "C&C content archive is missing '$($file.Value)'." }
			[IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $file.Key))
		}
		$destination = Join-Path $SupportDirectory 'Content/cnc'
		[void][IO.Directory]::CreateDirectory($destination)
		foreach ($file in $Download.Files.Keys) {
			Copy-Item -LiteralPath (Join-Path $staging $file) -Destination (Join-Path $destination $file) -Force
		}
	}
	finally {
		$zip.Dispose()
		if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
	}
}
