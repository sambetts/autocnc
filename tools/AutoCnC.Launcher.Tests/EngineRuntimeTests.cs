// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class EngineRuntimeTests
	{
		string root;
		RepoLayout repository;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC engine runtime", Guid.NewGuid().ToString("N"));
			repository = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			Directory.CreateDirectory(Path.Combine(root, "scripts"));
			CopyScript("engine-runtime.ps1");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		[TestCase("win", "x64", "win-x64")]
		[TestCase("win", "x86", "win-x86")]
		[TestCase("linux", "x64", "linux-x64")]
		[TestCase("linux", "arm64", "linux-arm64")]
		[TestCase("osx", "x64", "osx-x64")]
		[TestCase("osx", "arm64", "osx-arm64")]
		public void NativeLibrariesMatchTheDotNetHost(string os, string architecture, string target)
		{
			var result = Probe($$"""
				function Get-EngineOperatingSystem { '{{os}}' }
				function Get-EngineDotNetInfo($DotNetPath) {
				    [pscustomobject]@{ DotNetPath = $DotNetPath; Architecture = '{{architecture}}'; HasNet8Runtime = $true }
				}
				$expectedHost = (Get-Command dotnet -CommandType Application).Source
				$runtime = Get-EngineRuntime
				"$($runtime.TargetPlatform)|$($runtime.DotNetPath -eq $expectedHost)"
				""");

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.Output, Does.Contain($"{target}|True"));
		}

		[Test]
		public void WindowsArm64SelectsAnX64RuntimeWithoutReplacingTheSdk()
		{
			Write("x64 runtime\\dotnet.exe", "");
			var result = Probe(WindowsArm64Probe("x64", true) + """

				$expectedSdk = (Get-Command dotnet -CommandType Application).Source
				$runtime = Get-EngineRuntime
				"$($runtime.TargetPlatform)|$($runtime.DotNetPath -eq $script:x64Host)"
				"SDK_UNCHANGED=$((Get-Command dotnet -CommandType Application).Source -eq $expectedSdk)"
				""");

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.Output, Does.Contain("win-x64|True"));
			Assert.That(result.Output, Does.Contain("SDK_UNCHANGED=True"));
			Assert.That(string.Join('\n', result.Output), Does.Contain("x64 compatibility on Windows ARM64"));
		}

		[TestCase(false, "x64", true)]
		[TestCase(true, "arm64", true)]
		[TestCase(true, "x64", false)]
		public void MissingCompatibleRuntimeHasAnActionableError(bool exists, string architecture, bool net8)
		{
			if (exists)
				Write("x64 runtime\\dotnet.exe", "");

			var result = Probe(WindowsArm64Probe(architecture, net8) + "\nGet-EngineRuntime\n");

			Assert.That(result.ExitCode, Is.Not.Zero);
			var output = string.Join('\n', result.Output);
			Assert.That(output, Does.Contain(".NET 8 x64 runtime"));
			Assert.That(output, Does.Contain("DOTNET_ROOT_X64"));
		}

		[Test]
		public void UnsupportedArchitecturesDoNotSilentlyUseX64Libraries()
		{
			var result = Probe("""
				function Get-EngineOperatingSystem { 'linux' }
				function Get-EngineDotNetInfo($DotNetPath) {
				    [pscustomobject]@{ DotNetPath = $DotNetPath; Architecture = 'arm'; HasNet8Runtime = $true }
				}
				Get-EngineRuntime
				""");

			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("do not support 'linux-arm'"));
		}

		[TestCase("arm64", "8.0.30", true)]
		[TestCase("x64", "8.0.0", true)]
		[TestCase("x64", "10.0.11", false)]
		public void HostInspectionUsesInvariantOutputAndRestoresTheLanguage(
			string architecture, string runtimeVersion, bool hasNet8)
		{
			var result = Probe($$"""
				function Invoke-DotNetProbe {
				    if ($env:DOTNET_CLI_UI_LANGUAGE -ne 'en') { throw 'Expected invariant .NET output.' }
				    "Host:`n  Architecture: {{architecture}}`n.NET runtimes installed:"
				    '  Microsoft.NETCore.App {{runtimeVersion}} [C:\Runtime with spaces]'
				    $global:LASTEXITCODE = 0
				}
				$env:DOTNET_CLI_UI_LANGUAGE = 'fr'
				$runtime = Get-EngineDotNetInfo Invoke-DotNetProbe
				"$($runtime.Architecture)|$($runtime.HasNet8Runtime)|$env:DOTNET_CLI_UI_LANGUAGE"
				""");

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(result.Output, Does.Contain($"{architecture}|{hasNet8}|fr"));
		}

		[TestCase(23, "Could not inspect .NET host")]
		[TestCase(0, "Could not determine the architecture")]
		public void HostInspectionDoesNotHideFailures(int exitCode, string expectedError)
		{
			var result = Probe($$"""
				function Invoke-DotNetProbe {
				    'unrecognized host output'
				    $global:LASTEXITCODE = {{exitCode}}
				}
				$env:DOTNET_CLI_UI_LANGUAGE = 'fr'
				try { Get-EngineDotNetInfo Invoke-DotNetProbe } catch { $_.Exception.Message }
				"LANGUAGE=$env:DOTNET_CLI_UI_LANGUAGE"
				""");

			Assert.That(result.Output, Does.Contain("LANGUAGE=fr"));
			Assert.That(string.Join('\n', result.Output), Does.Contain(expectedError));
		}

		[TestCase("win-x64")]
		[TestCase("linux-arm64")]
		[TestCase("osx-arm64")]
		public void EngineBuildUsesTheSdkButPassesTheSelectedNativeTarget(string target)
		{
			CopyScript("build.ps1");
			Write("engine\\OpenRA.sln", "");
			Write("scripts\\engine-runtime.ps1", $$"""
				function Get-EngineRuntime {
				    [pscustomobject]@{ DotNetPath = 'Invoke-EngineOnly'; TargetPlatform = '{{target}}' }
				}
				function Invoke-EngineOnly { throw 'An engine-only runtime cannot compile projects.' }
				""");

			var result = Probe("""
				function global:dotnet {
				    "SDK_ARGS=$($args -join '|')"
				    $global:LASTEXITCODE = 0
				}
				$env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'test-package-cache'
				& (Join-Path $PSScriptRoot 'scripts\build.ps1') -SkipBots
				""");

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain($"|-p:TargetPlatform={target}"));
			Assert.That(string.Join('\n', result.Output), Does.Contain("SDK_ARGS=pack|"));
		}

		[TestCase("launch.ps1", 0)]
		[TestCase("launch.ps1", 23)]
		[TestCase("run-bot.ps1", 0)]
		[TestCase("run-bot.ps1", 23)]
		[TestCase("lint.ps1", 0)]
		[TestCase("lint.ps1", 23)]
		[TestCase("export-agent-rules.ps1", 0)]
		[TestCase("export-agent-rules.ps1", 23)]
		public void EngineEntryPointsUseTheSelectedHostAndPropagateFailure(string script, int engineExitCode)
		{
			var path = PrepareEngineScript(script, engineExitCode);
			string[] arguments = script switch
			{
				"run-bot.ps1" => ["-BattleBot", Write("fixture-bot.dll", "")],
				"export-agent-rules.ps1" => ["-Output", Path.Combine(root, "game-rules.json")],
				_ => []
			};
			var result = ScriptRunnerTests.Run(path, root, arguments);

			Assert.That(result.ExitCode, engineExitCode == 0 ? Is.Zero : Is.Not.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("ENGINE_ARGS="));
			Assert.That(string.Join('\n', result.Output), Does.Not.Contain("must not run OpenRA"));
		}

		[Test]
		public void DeployingABotDoesNotRequireAnEngineRuntime()
		{
			var script = PrepareEngineScript("run-bot.ps1", 0);
			File.Delete(Path.Combine(root, "scripts", "engine-runtime.ps1"));
			var result = ScriptRunnerTests.Run(script, root,
				["-BattleBot", Write("fixture-bot.dll", ""), "-NoLaunch"]);

			Assert.That(result.ExitCode, Is.Zero);
			Assert.That(string.Join('\n', result.Output), Does.Contain("Built and installed."));
		}

		[Test]
		public void LauncherRuleExportUsesTheSameRuntimeAwareScript()
		{
			PrepareEngineScript("export-agent-rules.ps1", 0);
			Write("AutoCnC.sln", "");
			Write("scripts\\run-bot.ps1", "");
			var destination = Path.Combine(root, "run with spaces & 'quotes'", "game-rules.json");

			AgentRulesExporter.Export(RepoLayout.For(root), destination);

			Assert.That(File.ReadAllText(destination).Trim(), Is.EqualTo("{\"exported\":true}"));
		}

		[Test]
		public void LauncherRuleExportSurfacesEngineFailure()
		{
			PrepareEngineScript("export-agent-rules.ps1", 23);
			Write("AutoCnC.sln", "");
			Write("scripts\\run-bot.ps1", "");

			var error = Assert.Throws<InvalidOperationException>(() =>
				AgentRulesExporter.Export(RepoLayout.For(root), Path.Combine(root, "game-rules.json")));

			Assert.That(error.Message, Does.Contain("Game-rules export failed with exit code 23"));
		}

		[Test]
		public void LauncherRuleExportRejectsSuccessWithoutASnapshot()
		{
			PrepareEngineScript("export-agent-rules.ps1", 0, writeSnapshot: false);
			Write("AutoCnC.sln", "");
			Write("scripts\\run-bot.ps1", "");

			var error = Assert.Throws<InvalidOperationException>(() =>
				AgentRulesExporter.Export(RepoLayout.For(root), Path.Combine(root, "game-rules.json")));

			Assert.That(error.Message, Does.Contain("did not create the resolved game-rules snapshot"));
		}

		string PrepareEngineScript(string script, int exitCode, bool writeSnapshot = true)
		{
			foreach (var assembly in new[]
			{
				"OpenRA.dll", "OpenRA.Utility.dll", "AutoCnC.Platform.dll", "OpenRA.Platforms.Headless.dll"
			})
				Write(Path.Combine("engine", "bin", assembly), "");

			Write("scripts\\engine-runtime.ps1", $$"""
				function global:dotnet { throw 'The SDK host must not run OpenRA.' }
				function Get-EngineRuntime {
				    [pscustomobject]@{ DotNetPath = 'Invoke-EngineFixture'; TargetPlatform = 'win-x64' }
				}
				function Invoke-EngineFixture {
				    "ENGINE_ARGS=$($args -join '|')"
				    if ({{exitCode}} -eq 0 -and ${{writeSnapshot.ToString().ToLowerInvariant()}} -and $args[2] -eq '--export-agent-rules') {
				        Set-Content -LiteralPath $args[3] -Value '{"exported":true}'
				    }
				    $global:LASTEXITCODE = {{exitCode}}
				}
				""");
			return CopyScript(script);
		}

		static string WindowsArm64Probe(string candidateArchitecture, bool candidateHasNet8) => $$"""
			$script:x64Host = Join-Path $PSScriptRoot 'x64 runtime\dotnet.exe'
			function Get-EngineOperatingSystem { 'win' }
			function Get-WindowsX64DotNetPaths {
			    Join-Path $PSScriptRoot 'missing runtime\dotnet.exe'
			    $script:x64Host
			}
			function Get-EngineDotNetInfo($DotNetPath) {
			    $architecture = if ($DotNetPath -eq $script:x64Host) { '{{candidateArchitecture}}' } else { 'arm64' }
			    [pscustomobject]@{
			        DotNetPath = $DotNetPath
			        Architecture = $architecture
			        HasNet8Runtime = ${{candidateHasNet8.ToString().ToLowerInvariant()}}
			    }
			}
			""";

		(int ExitCode, IReadOnlyList<string> Output) Probe(string code)
		{
			var script = Write("probe.ps1",
				"$ErrorActionPreference = 'Stop'\n. (Join-Path $PSScriptRoot 'scripts\\engine-runtime.ps1')\n" + code);
			return ScriptRunnerTests.Run(script, root, []);
		}

		string CopyScript(string name)
		{
			var target = Path.Combine(root, "scripts", name);
			File.Copy(Path.Combine(repository.ScriptsDir, name), target, overwrite: true);
			return target;
		}

		string Write(string path, string contents)
		{
			var target = Path.Combine(root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.WriteAllText(target, contents);
			return target;
		}
	}
}
