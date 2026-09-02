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
using System.IO;
using System.Threading;
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
			}
			finally
			{
				Directory.Delete(directory, true);
			}
		}

		static (int ExitCode, System.Collections.Generic.IReadOnlyList<string> Output) Run(
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
