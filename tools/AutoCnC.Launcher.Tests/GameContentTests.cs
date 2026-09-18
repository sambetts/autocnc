// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class GameContentTests
	{
		string root;
		RepoLayout repository;

		string ContentDirectory => Path.Combine(root, "engine", "Support", "Content", "cnc");

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC content with spaces", Guid.NewGuid().ToString("N"));
			repository = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			Directory.CreateDirectory(Path.Combine(root, "engine", "Support"));
			foreach (var script in new[] { "game-content.ps1", "install-content.ps1", "run-bot.ps1" })
				Write(Path.Combine("scripts", script), File.ReadAllText(Path.Combine(repository.ScriptsDir, script)));

			Write("engine/mods/cnc-content/mod.yaml",
				"ModContent:\n\tPackages:\n\t\tContentPackage@base:\n" +
				"\t\t\tTestFiles: ^SupportDir|Content/cnc/conquer.mix, ^SupportDir|Content/cnc/sounds.mix\n" +
				"\t\tContentPackage@music:\n\t\t\tTestFiles: ^SupportDir|Content/cnc/scores.mix\n");
			PrepareArchive();
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		[Test]
		public void PinnedOpenRaMetadataIsSupported()
		{
			var engine = Path.Combine(Path.GetDirectoryName(repository.ScriptsDir), "engine");
			foreach (var path in new[] { "mods/cnc-content/mod.yaml", "mods/cnc-content/installer/downloads.yaml" })
				Write(Path.Combine("engine", path), File.ReadAllText(Path.Combine(engine, path)));

			var result = Probe("'REQUIRED=' + @(Get-CncRequiredContent $engine).Count\n" +
				"$download = Get-CncContentDownload $engine\n'EXTRACT=' + $download.Files.Count");

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.Output, Does.Contain("REQUIRED=7"));
			Assert.That(result.Output, Does.Contain("EXTRACT=9"));
		}

		[Test]
		public void PortableProfileTakesPrecedence()
		{
			var result = Probe("Get-OpenRASupportDirectory $engine");
			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.Output, Does.Contain(Path.Combine(root, "engine", "Support")));
		}

		[Test]
		public void MissingContentStopsHeadlessBeforeBuildOrEngineStartup()
		{
			var result = Run("run-bot.ps1", ["-ExecutionMode", "Headless", "-Map", "fixture.oramap"]);

			Assert.That(result.ExitCode, Is.Not.Zero);
			var output = string.Join('\n', result.Output);
			Assert.That(output, Does.Contain("game content is missing"));
			Assert.That(output, Does.Contain("conquer.mix"));
			Assert.That(output, Does.Contain("install-content.ps1"));
			Assert.That(output, Does.Not.Contain("Building"));
		}

		[Test]
		public void VerifiedOfflineArchiveInstallsOnlyListedFiles()
		{
			var result = Run("install-content.ps1", ["-Archive", Path.Combine(root, "basefiles.zip")]);

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(File.ReadAllText(Path.Combine(ContentDirectory, "conquer.mix")), Is.EqualTo("conquer"));
			Assert.That(File.ReadAllText(Path.Combine(ContentDirectory, "sounds.mix")), Is.EqualTo("sounds"));
			Assert.That(Directory.GetFiles(ContentDirectory), Has.Length.EqualTo(2));
			Assert.That(File.Exists(Path.Combine(root, "engine", "Support", "Content", "unexpected.txt")), Is.False);
		}

		[Test]
		public void ChecksumFailureDoesNotOverwriteInstalledContent()
		{
			Write("engine/Support/Content/cnc/conquer.mix", "keep existing");
			File.AppendAllText(Path.Combine(root, "basefiles.zip"), "corrupt");
			var result = Run("install-content.ps1", ["-Archive", Path.Combine(root, "basefiles.zip")]);

			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("checksum mismatch"));
			Assert.That(File.ReadAllText(Path.Combine(ContentDirectory, "conquer.mix")), Is.EqualTo("keep existing"));
			Assert.That(File.Exists(Path.Combine(ContentDirectory, "sounds.mix")), Is.False);
		}

		[Test]
		public void IncompleteArchiveDoesNotInstallPartialContent()
		{
			PrepareArchive(includeSounds: false);
			var result = Run("install-content.ps1", ["-Archive", Path.Combine(root, "basefiles.zip")]);

			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("archive is missing 'sounds.mix'"));
			Assert.That(Directory.Exists(ContentDirectory), Is.False);
		}

		[Test]
		public void UnsafeExtractionMetadataIsRejected()
		{
			var manifest = Path.Combine(root, "engine", "mods", "cnc-content", "installer", "downloads.yaml");
			File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("Content/cnc/conquer.mix:", "Content/cnc/../conquer.mix:"));
			var result = Run("install-content.ps1", ["-Archive", Path.Combine(root, "basefiles.zip")]);

			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("Unsupported basefiles metadata"));
			Assert.That(Directory.Exists(ContentDirectory), Is.False);
		}

		[Test]
		public void InstalledBaseContentDoesNotDownloadOrRequireOptionalMusic()
		{
			InstallFixtureContent();
			var result = Probe("function Invoke-WebRequest { throw 'Network must not be used.' }\n" +
				"& (Join-Path $PSScriptRoot 'scripts/install-content.ps1')\nAssert-CncContent $engine");

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(string.Join('\n', result.Output), Does.Contain("already installed"));
		}

		[Test]
		public void DownloadRetriesMirrorsWithoutUsingTheNetwork()
		{
			var result = Probe("""
				function Invoke-WebRequest($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec) {
					if (-not $OutFile) { return [pscustomobject]@{ Content = "# mirrors`nhttps://broken.invalid/content.zip`nhttp://fixture.invalid/content.zip" } }
					if ($Uri -like '*broken*') { throw 'fixture mirror unavailable' }
					if ($Uri -ne 'https://fixture.invalid/content.zip') { throw 'Expected HTTPS.' }
					Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'basefiles.zip') -Destination $OutFile
				}
				& (Join-Path $PSScriptRoot 'scripts/install-content.ps1')
				Assert-CncContent $engine
				""");

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(string.Join('\n', result.Output), Does.Contain("fixture mirror unavailable"));
			Assert.That(File.Exists(Path.Combine(ContentDirectory, "sounds.mix")), Is.True);
		}

		[TestCase("Rendered", false, false)]
		[TestCase("Headless", true, false)]
		[TestCase("Headless", false, true)]
		public void RenderedBuildOnlyAndInstalledHeadlessLaunchesRemainAvailable(string mode, bool noLaunch, bool installed)
		{
			if (installed)
				InstallFixtureContent();
			foreach (var name in new[] { "AutoCnC.Platform.dll", "OpenRA.Platforms.Headless.dll" })
				Write(Path.Combine("engine", "bin", name), "");
			var bot = Write("bot.dll", "");
			Write("scripts/engine-runtime.ps1", """
				function Get-EngineRuntime { [pscustomobject]@{ DotNetPath = 'Invoke-FixtureEngine' } }
				function Invoke-FixtureEngine {
					Write-Output 'ENGINE_STARTED'
					foreach ($argument in $args) {
						if ($argument -like 'Launch.HeadlessReport=*') {
							Set-Content -LiteralPath $argument.Substring('Launch.HeadlessReport='.Length) -Value '{"status":"completed"}'
						}
					}
					$global:LASTEXITCODE = 0
				}
				""");
			var arguments = new List<string>
			{
				"-BattleBot", bot, "-ExecutionMode", mode, "-Map", "fixture.oramap", "-Opponents", "0",
				"-PerformanceReport", Path.Combine(root, "performance.json")
			};
			if (noLaunch)
				arguments.Add("-NoLaunch");
			var result = Run("run-bot.ps1", arguments.ToArray());

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(string.Join('\n', result.Output), noLaunch
				? Does.Not.Contain("ENGINE_STARTED") : Does.Contain("ENGINE_STARTED"));
		}

		void InstallFixtureContent()
		{
			Write("engine/Support/Content/cnc/conquer.mix", "conquer");
			Write("engine/Support/Content/cnc/sounds.mix", "sounds");
		}

		void PrepareArchive(bool includeSounds = true)
		{
			var path = Path.Combine(root, "basefiles.zip");
			File.Delete(path);
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			{
				using (var writer = new StreamWriter(zip.CreateEntry("conquer.mix").Open()))
					writer.Write("conquer");
				if (includeSounds)
					using (var writer = new StreamWriter(zip.CreateEntry("sounds.mix").Open()))
						writer.Write("sounds");
				using (var writer = new StreamWriter(zip.CreateEntry("../unexpected.txt").Open()))
					writer.Write("not installed");
			}
			var hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));
			Write("engine/mods/cnc-content/installer/downloads.yaml",
				$"basefiles: Base Freeware Content\n\tType: ZipFile\n\tSHA1: {hash}\n" +
				"\tMirrorList: https://fixture.invalid/mirrors.txt\n\tExtract:\n" +
				"\t\t^SupportDir|Content/cnc/conquer.mix: conquer.mix\n" +
				"\t\t^SupportDir|Content/cnc/sounds.mix: sounds.mix\n");
		}

		(int ExitCode, IReadOnlyList<string> Output) Probe(string code) => ScriptRunnerTests.Run(
			Write("probe.ps1", "[CmdletBinding()]\nparam()\n$ErrorActionPreference = 'Stop'\n" +
				". (Join-Path $PSScriptRoot 'scripts/game-content.ps1')\n" +
				"$engine = Join-Path $PSScriptRoot 'engine'\n" + code), root, []);

		(int ExitCode, IReadOnlyList<string> Output) Run(string script, string[] arguments) =>
			ScriptRunnerTests.Run(Path.Combine(root, "scripts", script), root, arguments);

		string Write(string path, string contents)
		{
			var target = Path.Combine(root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.WriteAllText(target, contents);
			return target;
		}
	}
}
