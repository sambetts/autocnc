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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.Json;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class ScriptRunnerTests
	{
		[Test]
		public void FinishedSeesTheCompleteOutputBuffer()
		{
			var directory = Path.Combine(Path.GetTempPath(), "AutoCnC Script Runner",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			var script = Path.Combine(directory, "output.ps1");
			File.WriteAllText(script,
				"Write-Output \"$([char]27)[31;1m├─ thinking →$([char]27)[0m\"\n" +
				"Write-Output 'AUTOCNC_BOT_PROJECT=C:\\code\\Bot.csproj'\n");

			try
			{
				using var finished = new ManualResetEventSlim();
				var runner = new ScriptRunner();
				var exitCode = -1;
				runner.Finished += code =>
				{
					exitCode = code;
					finished.Set();
				};

				runner.Start(new ScriptJob { ScriptPath = script }, directory);

				Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
				Assert.That(exitCode, Is.Zero);
				Assert.That(runner.LastOutput, Does.Contain("├─ thinking →"));
				Assert.That(runner.LastOutput,
					Does.Contain("AUTOCNC_BOT_PROJECT=C:\\code\\Bot.csproj"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void NamedParametersAndSwitchesRetainTheirBinding()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "parameters.ps1");
			File.WriteAllText(script,
				"[CmdletBinding()]\n" +
				"param([string]$BattleBot, [string]$RunDirectory, [string]$AgentConfiguration, [switch]$Test, " +
				"[ValidateSet('Debug','Release')][string]$Configuration = 'Release')\n" +
				"Write-Output \"$BattleBot|$RunDirectory|$AgentConfiguration|$($Test.IsPresent)|$Configuration\"\n");

			try
			{
				var runner = Run(script, directory,
				[
					"-BattleBot", "bot.csproj",
					"-RunDirectory", @"C:\run path",
					"-AgentConfiguration", @"C:\run path\agent.json",
					"-Test"
				]);

				Assert.That(runner.ExitCode, Is.Zero);
				Assert.That(runner.Output,
					Does.Contain(@"bot.csproj|C:\run path|C:\run path\agent.json|True|Release"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void ErrorsArePlainTextRatherThanCliXml()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "error.ps1");
			File.WriteAllText(script, "throw 'probe failure'\n");

			try
			{
				var runner = Run(script, directory, []);
				var output = string.Join('\n', runner.Output);

				Assert.That(runner.ExitCode, Is.Not.Zero);
				Assert.That(output, Does.Contain("probe failure"));
				Assert.That(output, Does.Not.Contain("#< CLIXML"));
				Assert.That(output, Does.Not.Contain("_x001B_"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[TestCase(0)]
		[TestCase(23)]
		public void ReplayLauncherPropagatesTheGameExitCode(int gameExitCode)
		{
			var directory = TempDirectory();
			var launcher = Path.Combine(directory, "scripts", "launch.ps1");
			var engine = Path.Combine(directory, "engine", "bin");
			Directory.CreateDirectory(Path.GetDirectoryName(launcher));
			Directory.CreateDirectory(engine);
			var repository = RepoLayout.Discover(null, [AppContext.BaseDirectory]);
			File.Copy(repository.LaunchScript, launcher);
			File.WriteAllText(Path.Combine(directory, "scripts", "engine-runtime.ps1"),
				"function Get-EngineRuntime { [pscustomobject]@{ DotNetPath = 'Invoke-TestEngine' } }\n" +
				$"function Invoke-TestEngine {{ $global:LASTEXITCODE = {gameExitCode} }}\n");
			File.WriteAllText(Path.Combine(engine, "OpenRA.dll"), "engine fixture");
			var replay = Path.Combine(directory, "recorded.orarep");
			File.WriteAllText(replay, "replay fixture");
			try
			{
				var result = Run(launcher, directory, ["-Replay", replay]);
				Assert.That(result.ExitCode, Is.EqualTo(gameExitCode == 0 ? 0 : 1),
					"The command host must report a failed replay rather than unlocking feedback.");
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void ColorPreservingJobsExposeAnsiStyles()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "color.ps1");
			File.WriteAllText(script,
				"Write-Output \"$([char]27)[36;1mcolored progress$([char]27)[0m\"\n");

			try
			{
				using var finished = new ManualResetEventSlim();
				var runner = new ScriptRunner();
				var lines = new List<TerminalLine>();
				runner.Output += line => lines.Add(line);
				runner.Finished += _ => finished.Set();
				runner.Start(new ScriptJob
				{
					ScriptPath = script,
					PreserveColor = true
				}, directory);

				Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
				var colored = lines.Find(line => line.PlainText == "colored progress");
				Assert.That(colored, Is.Not.Null);
				Assert.That(colored.Spans[0].Style.Foreground, Is.Not.Null);

				// The console host forwards this string, so colour surviving a real child
				// process is what stops the training loop printing a monochrome transcript.
				Assert.That(colored.AnsiText, Is.EqualTo("\x1B[36;1mcolored progress\x1B[0m\x1B[0m"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void StopSignalsAHeadlessJobBeforeKillingIt()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "cancel.ps1");
			var cancellation = Path.Combine(directory, "cancel.request");
			var ready = Path.Combine(directory, "ready");
			File.WriteAllText(cancellation, "stale");
			File.WriteAllText(script,
				"param([string]$CancellationFile, [string]$ReadyFile)\n" +
				"Set-Content -LiteralPath $ReadyFile -Value ready\n" +
				"while (-not (Test-Path -LiteralPath $CancellationFile)) { Start-Sleep -Milliseconds 10 }\n" +
				"Write-Output 'clean stop observed'\n");

			try
			{
				using var finished = new ManualResetEventSlim();
				var runner = new ScriptRunner();
				var exitCode = -1;
				runner.Finished += code =>
				{
					exitCode = code;
					finished.Set();
				};
				runner.Start(new ScriptJob
				{
					ScriptPath = script,
					Arguments = ["-CancellationFile", cancellation, "-ReadyFile", ready],
					CancellationFile = cancellation
				}, directory);

				Assert.That(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(5)), Is.True);
				runner.Stop();

				Assert.That(finished.Wait(TimeSpan.FromSeconds(5)), Is.True);
				Assert.That(exitCode, Is.Zero);
				Assert.That(runner.LastOutput, Does.Contain("clean stop observed"));
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void WorkerOwnershipIsLiveForTheScriptLifetimeAndRemovedAfterward()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "worker.ps1");
			var ready = Path.Combine(directory, "ready");
			var release = Path.Combine(directory, "release");
			var ownership = Path.Combine(directory, "worker.json");
			File.WriteAllText(script,
				"param([string]$ReadyFile, [string]$ReleaseFile)\n" +
				"Set-Content -LiteralPath $ReadyFile -Value ready\n" +
				"while (-not (Test-Path -LiteralPath $ReleaseFile)) { Start-Sleep -Milliseconds 10 }\n");

			try
			{
				using var finished = new ManualResetEventSlim();
				var runner = new ScriptRunner();
				runner.Finished += _ => finished.Set();
				runner.Start(new ScriptJob
				{
					ScriptPath = script,
					Arguments = ["-ReadyFile", ready, "-ReleaseFile", release],
					WorkerOwnershipFile = ownership
				}, directory);

				Assert.That(SpinWait.SpinUntil(() =>
					File.Exists(ready) && File.Exists(ownership),
					TimeSpan.FromSeconds(10)), Is.True);
				var worker = JsonSerializer.Deserialize<ProcessOwnership>(
					File.ReadAllText(ownership));
				Assert.That(ProcessOwnership.IsLive(worker), Is.True);

				File.WriteAllText(release, "go");
				Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
				Assert.That(File.Exists(ownership), Is.False);
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void ClosingAWorkerJobKillsItsProcess()
		{
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.SystemDirectory,
					"WindowsPowerShell", "v1.0", "powershell.exe"),
				Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
			Assert.That(process, Is.Not.Null);
			using (var job = WindowsProcessJob.Create())
				job.Assign(process);

			Assert.That(process.WaitForExit(5000), Is.True);
		}

		[Test]
		public void WorkerGateWriteFailureKillsWorkerAndResetsRunnerState()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "sleep.ps1");
			var ownership = Path.Combine(directory, "worker.json");
			var gate = ownership + ".gate";
			File.WriteAllText(script,
				"param()\nStart-Sleep -Seconds 30\n");
			Directory.CreateDirectory(gate);
			var runner = new ScriptRunner();

			try
			{
				Assert.That(() => runner.Start(new ScriptJob
				{
					ScriptPath = script,
					WorkerOwnershipFile = ownership
				}, directory), Throws.TypeOf<InvalidOperationException>()
					.With.InnerException.TypeOf<UnauthorizedAccessException>());
				Assert.That(runner.IsRunning, Is.False);
				Assert.That(File.Exists(ownership), Is.False);
			}
			finally
			{
				Directory.Delete(gate);
				Directory.Delete(directory, true);
			}
		}

		[Test]
		public void GenericScriptRunnerProcessAlsoUsesKillOnCloseJob()
		{
			var directory = TempDirectory();
			var script = Path.Combine(directory, "generic-worker.ps1");
			var ready = Path.Combine(directory, "ready");
			var release = Path.Combine(directory, "release");
			File.WriteAllText(script,
				"param([string]$ReadyFile, [string]$ReleaseFile)\n" +
				"Set-Content -LiteralPath $ReadyFile -Value ready\n" +
				"while (-not (Test-Path -LiteralPath $ReleaseFile)) { Start-Sleep -Milliseconds 10 }\n");

			try
			{
				using var finished = new ManualResetEventSlim();
				var runner = new ScriptRunner();
				runner.Finished += _ => finished.Set();
				runner.Start(new ScriptJob
				{
					ScriptPath = script,
					Arguments = ["-ReadyFile", ready, "-ReleaseFile", release]
				}, directory);

				Assert.That(SpinWait.SpinUntil(() => File.Exists(ready),
					TimeSpan.FromSeconds(10)), Is.True);
				Assert.That(runner.HasProcessJob, Is.True);
				File.WriteAllText(release, "go");
				Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		internal static (int ExitCode, System.Collections.Generic.IReadOnlyList<string> Output) Run(
			string script, string directory, string[] arguments)
		{
			using var finished = new ManualResetEventSlim();
			var runner = new ScriptRunner();
			var exitCode = int.MinValue;
			runner.Finished += code =>
			{
				exitCode = code;
				finished.Set();
			};
			runner.Start(new ScriptJob { ScriptPath = script, Arguments = arguments }, directory);

			Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True);
			return (exitCode, runner.LastOutput);
		}

		static string TempDirectory()
		{
			var directory = Path.Combine(Path.GetTempPath(), "AutoCnC Script Runner",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			return directory;
		}
	}
}
