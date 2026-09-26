#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TrainingLoopScriptTests
	{
		string root;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Training PS Tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path.Combine(root, "scripts"));
			var repo = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			File.Copy(Path.Combine(repo.ScriptsDir, "train-loop.ps1"), Path.Combine(root, "scripts", "train-loop.ps1"));
			var host = Path.Combine(root, "tools", "AutoCnC.Training", "bin", "Release", "net8.0-windows");
			Directory.CreateDirectory(host);
			File.WriteAllText(Path.Combine(host, "AutoCnC.Training.dll"), "fake host intercepted by the test");
			File.WriteAllText(Path.Combine(root, "My bot's project.csproj"), "<Project />");
			File.WriteAllText(Path.Combine(root, "agent.json"), "{}");
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, true);

		[Test]
		public async Task ForwardsTypedOptionsAndPreservesPathsWithSpacesAndQuotes()
		{
			var result = await Invoke("-BattleBot \"./My bot's project.csproj\" -Map 'custom map.oramap' " +
				"-Rounds 3 -Difficulty Normal -Seed 321 -AgentConfiguration ./agent.json -RunsRoot './new runs'");
			Assert.That(result.ExitCode, Is.Zero, result.Output);
			using var captured = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "captured.json")));
			var options = captured.RootElement;
			Assert.Multiple(() =>
			{
				Assert.That(options.GetProperty("BattleBot").GetString(), Is.EqualTo(Path.Combine(root, "My bot's project.csproj")));
				Assert.That(options.GetProperty("Rounds").GetInt32(), Is.EqualTo(3));
				Assert.That(options.GetProperty("Seed").GetInt32(), Is.EqualTo(321));
				Assert.That(options.GetProperty("Map").GetString(), Is.EqualTo("custom map.oramap"));
				Assert.That(options.GetProperty("ExecutionMode").GetString(), Is.EqualTo("Headless"));
				Assert.That(options.GetProperty("AgentConfiguration").GetString(), Is.EqualTo(Path.Combine(root, "agent.json")));
				Assert.That(options.GetProperty("RunsRoot").GetString(), Is.EqualTo(Path.Combine(root, "new runs")));
				Assert.That(options.GetProperty("Benchmark").GetString(), Is.EqualTo("hard-16-9"));
				Assert.That(options.GetProperty("BenchmarkDifficulty").GetString(), Is.EqualTo("Hard"));
				Assert.That(options.GetProperty("Commit").GetBoolean(), Is.True);
				Assert.That(options.GetProperty("Push").GetBoolean(), Is.True,
					"Promotions are published unless the caller opts out.");
				Assert.That(options.GetProperty("Color").GetBoolean(), Is.True);
				Assert.That(options.GetProperty("DeleteBlockingRun").GetBoolean(), Is.False);
			});
			AssertTemporaryOptionsDeleted();
		}

		[Test]
		public async Task DeleteBlockingRunIsForwardedAsABoolean()
		{
			var result = await Invoke("-Rounds 1 -DeleteBlockingRun");
			Assert.That(result.ExitCode, Is.Zero, result.Output);
			using var captured = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "captured.json")));
			Assert.That(captured.RootElement.GetProperty("DeleteBlockingRun").ValueKind, Is.EqualTo(JsonValueKind.True));
			AssertTemporaryOptionsDeleted();
		}

		[Test]
		public async Task NoCommitNoPushAndNoColorAreForwardedAsTheirOppositeOptions()
		{
			var result = await Invoke("-Rounds 1 -NoCommit -NoPush -NoColor");
			Assert.That(result.ExitCode, Is.Zero, result.Output);
			using var captured = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "captured.json")));
			Assert.Multiple(() =>
			{
				Assert.That(captured.RootElement.GetProperty("Commit").GetBoolean(), Is.False);
				Assert.That(captured.RootElement.GetProperty("Push").GetBoolean(), Is.False);
				Assert.That(captured.RootElement.GetProperty("Color").GetBoolean(), Is.False);
			});
			AssertTemporaryOptionsDeleted();
		}

		[Test]
		public async Task PropagatesHostFailuresAndDeletesTemporaryOptions()
		{
			var result = await Invoke("-Rounds 1", hostExitCode: 27);
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.Output, Does.Contain("exited with code 27"));
			AssertTemporaryOptionsDeleted();
		}

		[TestCase("-Rounds -1")]
		[TestCase("-RestoreRun . -Rounds 2")]
		[TestCase("-RestoreRun . -DeleteBlockingRun")]
		public async Task InvalidParametersDoNotStartTheHost(string arguments)
		{
			var result = await Invoke(arguments);
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(File.Exists(Path.Combine(root, "captured.json")), Is.False);
		}

		[TestCase("-Rounds 1 -WhatIf")]
		[TestCase("-RestoreRun . -WhatIf")]
		[TestCase("-DeleteBlockingRun -WhatIf")]
		public async Task WhatIfDoesNotStartTheHost(string arguments)
		{
			var result = await Invoke(arguments);
			Assert.That(result.ExitCode, Is.Zero, result.Output);
			Assert.That(File.Exists(Path.Combine(root, "captured.json")), Is.False);
		}

		[Test]
		public async Task RestoreForwardsAnAbsoluteRunPathWithoutStartingTraining()
		{
			var result = await Invoke("-RestoreRun .");
			Assert.That(result.ExitCode, Is.Zero, result.Output);
			using var captured = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "captured.json")));
			Assert.That(captured.RootElement.GetProperty("RestoreRun").GetString(), Is.EqualTo(root));
			AssertTemporaryOptionsDeleted();
		}

		void AssertTemporaryOptionsDeleted()
		{
			var path = File.ReadAllText(Path.Combine(root, "options-path.txt")).Trim();
			Assert.That(File.Exists(path), Is.False);
		}

		async Task<(int ExitCode, string Output)> Invoke(string arguments, int hostExitCode = 0)
		{
			var script = Path.Combine(root, "invoke.ps1");
			File.WriteAllText(script,
				"$ErrorActionPreference = 'Stop'; Set-Location $PSScriptRoot\n" +
				"function dotnet {\n" +
				"    if ($args[1] -ne '--options') { throw 'Unexpected host invocation' }\n" +
				"    Copy-Item -LiteralPath $args[2] -Destination ./captured.json\n" +
				"    Set-Content -LiteralPath ./options-path.txt -Value $args[2]\n" +
				$"    $global:LASTEXITCODE = {hostExitCode}\n" +
				"}\n" +
				"& ./scripts/train-loop.ps1 -NoBuild " + arguments + "\n", new UTF8Encoding(true));
			var info = new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
					"WindowsPowerShell", "v1.0", "powershell.exe"),
				WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true
			};
			foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
				info.ArgumentList.Add(argument);
			using var process = Process.Start(info);
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
			}
			catch (TimeoutException)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync();
				throw;
			}
			return (process.ExitCode, await stdout + await stderr);
		}
	}
}
