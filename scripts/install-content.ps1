<#
.SYNOPSIS
	Installs the free C&C base game content without a window or OpenGL.

.DESCRIPTION
	Uses the mirror list, checksum and extraction list from the pinned OpenRA engine.
	Installs into the same support directory as the game. This is an explicit setup step;
	launching a battle never downloads content automatically.

.PARAMETER Archive
	Optional local copy of the pinned base-content ZIP, for offline installation.
	It must match the checksum in engine/mods/cnc-content/installer/downloads.yaml.
#>
[CmdletBinding()]
param([string]$Archive)

$ErrorActionPreference = 'Stop'
$engineDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'engine'
. (Join-Path $PSScriptRoot 'game-content.ps1')

$support = Get-OpenRASupportDirectory $engineDir
$required = @(Get-CncRequiredContent $engineDir)
$missing = @($required | Where-Object {
	-not (Test-Path -LiteralPath (Join-Path $support "Content/cnc/$_") -PathType Leaf)
})
if ($missing.Count -eq 0 -and -not $Archive) {
	Write-Host "C&C base content is already installed in $support." -ForegroundColor Green
	return
}

$download = Get-CncContentDownload $engineDir
if ($Archive) {
	Install-CncContentArchive $download ([IO.Path]::GetFullPath($Archive)) $support
}
else {
	$temporary = Join-Path ([IO.Path]::GetTempPath()) "autocnc-download-$([Guid]::NewGuid().ToString('N'))"
	$previousTls = [Net.ServicePointManager]::SecurityProtocol
	$previousProgress = $ProgressPreference
	try {
		[void][IO.Directory]::CreateDirectory($temporary)
		$package = Join-Path $temporary 'basefiles.zip'
		[Net.ServicePointManager]::SecurityProtocol = $previousTls -bor [Net.SecurityProtocolType]::Tls12
		$ProgressPreference = 'SilentlyContinue'
		Write-Host '==> Finding C&C base-content mirrors' -ForegroundColor Cyan
		$response = Invoke-WebRequest -Uri $download.MirrorList -UseBasicParsing -TimeoutSec 60
		$mirrors = @(([string]$response.Content -split '\r?\n') | ForEach-Object { $_.Trim() } | Where-Object {
			$_ -and -not $_.StartsWith('#')
		})
		$verified = $false
		foreach ($mirror in $mirrors) {
			# Some older mirror lists use HTTP. Try the same endpoint over HTTPS instead.
			$url = $mirror -replace '^http://', 'https://'
			if ($url -notmatch '^https://') { continue }
			try {
				Write-Host "==> Downloading $url" -ForegroundColor Cyan
				Invoke-WebRequest -Uri $url -OutFile $package -UseBasicParsing -TimeoutSec 300
				if ((Get-FileHash -LiteralPath $package -Algorithm SHA1).Hash -ne $download.SHA1) {
					throw 'C&C content checksum mismatch.'
				}
				$verified = $true
				break
			}
			catch {
				Write-Warning "Content mirror failed: $($_.Exception.Message)"
			}
		}
		if (-not $verified) { throw 'No C&C content mirror supplied a verified package. Retry later, or use -Archive with a local copy of the pinned ZIP.' }
		Install-CncContentArchive $download $package $support
	}
	finally {
		[Net.ServicePointManager]::SecurityProtocol = $previousTls
		$ProgressPreference = $previousProgress
		if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
	}
}

Assert-CncContent $engineDir
Write-Host "C&C base content installed in $support. Headless battles can now run without OpenGL." -ForegroundColor Green
