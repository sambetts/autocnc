// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class BenchmarkBotTests
	{
		string root;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC benchmark bot", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(root, "scripts"));
			var repository = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			File.Copy(Path.Combine(repository.Root, "scripts", "benchmark-bot.ps1"),
				Path.Combine(root, "scripts", "benchmark-bot.ps1"));
			Write("scripts/run-bot.ps1", """
				param($BattleBot, $Configuration, $InstallDirectory, [switch]$NoLaunch)
				if (-not $NoLaunch) { throw 'Arm preparation must not launch a match.' }
				if (-not (Test-Path -LiteralPath $BattleBot)) { throw 'Bot input does not exist.' }
				$global:LASTEXITCODE = 0
				""");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		[TestCase("candidate artifact/AutoCnC.Reference.dll")]
		[TestCase("control artifact/AutoCnC.Reference.DLL")]
		[TestCase("assembly folder")]
		public void PrebuiltArmsKeepTheirOriginalPathWithoutQueryingMsBuild(string input)
		{
			var isAssembly = input.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
			var assembly = Write(isAssembly ? input : input + "/AutoCnC.Reference.dll", "immutable assembly");
			var expected = isAssembly ? assembly : Path.GetDirectoryName(assembly);
			var result = Probe(expected, "unexpected");

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(result.Output, Does.Contain("BOT=" + expected));
			Assert.That(File.ReadAllText(assembly), Is.EqualTo("immutable assembly"));
			Assert.That(Directory.Exists(Path.Combine(root, "output", "artifacts")), Is.False);
		}

		[TestCase("project")]
		[TestCase("directory")]
		[TestCase("name")]
		public void ProjectArmsUseTheReportedTargetPath(string kind)
		{
			var project = Write("bots/Reference/Reference.csproj", "project fixture");
			var target = Write("built output/CustomAssembly.dll", "built assembly");
			var input = kind == "project" ? project : kind == "directory" ? Path.GetDirectoryName(project) : "Reference";
			var result = Probe(input, "success");

			Assert.That(result.ExitCode, Is.Zero, string.Join('\n', result.Output));
			Assert.That(result.Output, Does.Contain("BOT=" + target));
			Assert.That(File.ReadAllText(Path.Combine(root, "queried-project.txt")).Trim(), Is.EqualTo(project));
		}

		[TestCase("failure", "Could not determine the bot target path")]
		[TestCase("empty", "Could not determine the bot target path")]
		[TestCase("missing", "which does not exist")]
		public void TargetQueryErrorsHaveActionableDiagnostics(string behavior, string expectedError)
		{
			var project = Write("bots/Reference/Reference.csproj", "project fixture");
			var result = Probe(project, behavior);
			var output = string.Join('\n', result.Output);

			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(output, Does.Contain(expectedError));
			Assert.That(output, Does.Not.Contain("null-valued expression"));
		}

		(int ExitCode, IReadOnlyList<string> Output) Probe(string input, string behavior)
		{
			var script = Write("probe.ps1", """
				param([string]$InputBot, [string]$Behavior)
				$ErrorActionPreference = 'Stop'
				$repoRoot = $PSScriptRoot
				$OutputDirectory = Join-Path $repoRoot 'output'
				$Configuration = 'Release'
				$tokens = $null
				$errors = $null
				$ast = [System.Management.Automation.Language.Parser]::ParseFile(
					(Join-Path $repoRoot 'scripts/benchmark-bot.ps1'), [ref]$tokens, [ref]$errors)
				if ($errors.Count) { throw ($errors -join '; ') }
				$function = $ast.Find({ param($node)
					$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
						$node.Name -eq 'Build-ArmBot'
				}, $false)
				. ([scriptblock]::Create($function.Extent.Text))
				function global:dotnet {
					if ($Behavior -eq 'unexpected') { throw 'Prebuilt arms must not invoke MSBuild.' }
					if ($args[0] -ne 'msbuild' -or [IO.Path]::GetExtension($args[1]) -ne '.csproj') {
						throw 'Expected a project target-path query.'
					}
					$args[1] | Set-Content -LiteralPath (Join-Path $repoRoot 'queried-project.txt')
					$global:LASTEXITCODE = 0
					if ($Behavior -eq 'failure') { $global:LASTEXITCODE = 1; return }
					if ($Behavior -eq 'empty') { return }
					Join-Path $repoRoot 'built output/CustomAssembly.dll'
				}
				$bot = Build-ArmBot $InputBot 'candidate' 'test-revision'
				"BOT=$bot"
				""");
			return ScriptRunnerTests.Run(script, root, ["-InputBot", input, "-Behavior", behavior]);
		}

		string Write(string relativePath, string content)
		{
			var path = Path.GetFullPath(Path.Combine(root, relativePath));
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, content);
			return path;
		}
	}
}
