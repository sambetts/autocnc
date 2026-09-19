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
	public sealed class AgentProcessTests
	{
		string directory;

		[SetUp]
		public void SetUp()
		{
			directory = Path.Combine(Path.GetTempPath(), "AutoCnC Agent Process", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(directory, true);

		[Test]
		public async Task NativeStderrIsTextAndTheActualExitCodeControlsSuccess(
			[Values("train-bot.ps1", "chat-bot.ps1")] string scriptName,
			[Values(false, true)] bool withInput,
			[Values(0, 23)] int agentExitCode)
		{
			var child = "$utf8 = [Text.UTF8Encoding]::new($false); " +
				"[Console]::InputEncoding = [Console]::OutputEncoding = $utf8; " +
				(withInput ? "Write-Output ('input=' + [Console]::In.ReadToEnd().Trim()); " : "") +
				"[Console]::Error.WriteLine('agent diagnostic'); " +
				"[Console]::Error.WriteLine('second diagnostic'); " +
				"Start-Sleep -Milliseconds 100; Write-Output 'agent completed'; " +
				$"exit {agentExitCode}";
			File.WriteAllText(Path.Combine(directory, "agent.ps1"), child);
			File.WriteAllText(Path.Combine(directory, "agent.cmd"),
				"@echo off\r\npowershell.exe -NoProfile -NonInteractive -File \"%~dp0agent.ps1\"\r\nexit /b %errorlevel%\r\n");
			var script = PrepareInvocation(scriptName,
				"$agent = [pscustomobject]@{ command = 'cmd.exe' }\n" +
				"$agentArguments = @('/d', '/c', 'agent.cmd')\n" +
				(withInput ? "$agentInput = 'prompt café'\n" : "$agentInput = $null\n"));

			var result = await RunWindowsPowerShell(script);
			var transcript = File.ReadAllText(Path.Combine(directory, "transcript.txt"));
			Assert.That(result.ExitCode, Is.EqualTo(agentExitCode == 0 ? 0 : 1), result.Output);
			Assert.That(transcript, Does.Contain("agent diagnostic"));
			Assert.That(transcript, Does.Contain("second diagnostic"));
			Assert.That(transcript, Does.Contain("agent completed"));
			Assert.That(transcript, Does.Not.Contain("NativeCommandError"));
			Assert.That(result.Output, Does.Not.Contain("<Objs"));
			Assert.That(result.Output, Does.Not.Contain("#< CLIXML"));
			if (withInput)
				Assert.That(transcript, Does.Contain("input=prompt café"));
			if (agentExitCode == 0)
				Assert.That(result.Output, Does.Contain("preference=Stop"));
			else
				Assert.That(result.Output, Does.Contain("exited with code 23"));

			if (scriptName == "train-bot.ps1")
			{
				Assert.That(File.Exists(Path.Combine(directory, "verified.txt")), Is.EqualTo(agentExitCode == 0));
				if (agentExitCode != 0)
				{
					using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "status.json")));
					Assert.That(status.RootElement.GetProperty("State").GetString(), Is.EqualTo("failed"));
					Assert.That(status.RootElement.GetProperty("AgentExitCode").GetInt32(), Is.EqualTo(agentExitCode));
				}
			}
		}

		[TestCase("train-bot.ps1")]
		[TestCase("chat-bot.ps1")]
		public async Task MissingAgentFailsWithoutUsingAStaleExitCode(string scriptName)
		{
			var script = PrepareInvocation(scriptName,
				"$agent = [pscustomobject]@{ command = (Join-Path $PSScriptRoot 'missing-agent.exe') }\n" +
				"$agentArguments = @(); $agentInput = $null; $LASTEXITCODE = 79\n");

			var result = await RunWindowsPowerShell(script);
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.Output, Does.Contain("missing-agent.exe"));
			Assert.That(result.Output, Does.Not.Contain("exited with code 79"));
			Assert.That(File.Exists(Path.Combine(directory, "verified.txt")), Is.False);
		}

		[Test]
		public async Task WindowsPowerShellHostMessagesAndErrorsArePlainText()
		{
			var script = Path.Combine(directory, "output.ps1");
			File.WriteAllText(script,
				"[CmdletBinding()]\nparam()\nWrite-Host 'host message'\n" +
				"Write-Warning 'warning message'\nthrow 'probe failure'\n");
			var result = await RunWindowsPowerShell(script);
			Assert.That(result.ExitCode, Is.Not.Zero);
			Assert.That(result.Output, Does.Contain("host message"));
			Assert.That(result.Output, Does.Contain("warning message"));
			Assert.That(result.Output, Does.Contain("probe failure"));
			Assert.That(result.Output, Does.Not.Contain("<Objs"));
			Assert.That(result.Output, Does.Not.Contain("#< CLIXML"));
		}

		string PrepareInvocation(string scriptName, string agentSetup)
		{
			var repository = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(repository.LaunchScript), scriptName));
			var start = source.IndexOf(scriptName == "train-bot.ps1" ? "\"=== Agent: " : "\"=== You ===\"",
				StringComparison.Ordinal);
			Assert.That(start, Is.GreaterThanOrEqualTo(0));

			// Execute the production invocation, replacing only expensive setup and deployment.
			var script = Path.Combine(directory, "invoke.ps1");
			File.WriteAllText(script,
				"[CmdletBinding()]\nparam()\n$ErrorActionPreference = 'Stop'\n" +
				"$workspace = $run = $PSScriptRoot; $project = 'Bot.csproj'; $Configuration = 'Release'\n" +
				"$transcript = Join-Path $PSScriptRoot 'transcript.txt'\n" +
				"$Message = 'test message'\n" +
				"function Write-AgentStatus { param($State, $Phase, $AgentExitCode, $Message) " +
				"$PSBoundParameters | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'status.json') }\n" +
				agentSetup + source[start..] + "\nWrite-Output \"preference=$ErrorActionPreference\"\n",
				new UTF8Encoding(true));
			File.WriteAllText(Path.Combine(directory, "verify-bot.ps1"),
				"param($BattleBot, $RunDirectory, $Configuration)\n" +
				"Set-Content (Join-Path $PSScriptRoot 'verified.txt') 'verified'\n");
			return script;
		}

		async Task<(int ExitCode, string Output)> RunWindowsPowerShell(string script)
		{
			var info = ScriptRunner.CreateStartInfo(new ScriptJob { ScriptPath = script }, directory);
			info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
				"WindowsPowerShell", "v1.0", "powershell.exe");
			using var process = Process.Start(info);
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
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
