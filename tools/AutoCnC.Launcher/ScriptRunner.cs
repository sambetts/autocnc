#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	/// <summary>One thing to run: a repository script, with arguments.</summary>
	public sealed class ScriptJob
	{
		public string Title { get; init; }
		public string ScriptPath { get; init; }
		public IReadOnlyList<string> Arguments { get; init; } = [];
		public bool PreserveColor { get; init; }
		public string CancellationFile { get; init; }
		public Action<TerminalLine> Output { get; init; }
		public Action<int> Completed { get; init; }
	}

	/// <summary>
	/// Runs the repository's PowerShell scripts and streams their output back line by line.
	/// </summary>
	/// <remarks>
	/// The launcher deliberately owns no build or launch logic of its own: everything it does is
	/// something you could type into a terminal, which keeps the window and the command line
	/// honest about each other and means a fix to a script fixes both.
	/// </remarks>
	public sealed class ScriptRunner
	{
		/// <summary>How long a closing game gets to write its replay out before it is killed.</summary>
		const int CloseTimeout = 8000;
		static readonly string EncodedRunner = Convert.ToBase64String(Encoding.Unicode.GetBytes(
			"$utf8 = New-Object System.Text.UTF8Encoding $false\n" +
			"$OutputEncoding = [Console]::OutputEncoding = $utf8\n" +
			"if (Get-Variable PSStyle -ErrorAction SilentlyContinue) {\n" +
			"    $PSStyle.OutputRendering = if ($env:AUTOCNC_PRESERVE_COLOR -eq '1') { 'Ansi' } else { 'PlainText' }\n" +
			"}\n" +
			"$job = @((ConvertFrom-Json -InputObject $env:AUTOCNC_SCRIPT_JOB))\n" +
			"$script = [string]$job[0]\n" +
			"$command = Get-Command -Name $script -CommandType ExternalScript\n" +
			"$parameters = @{}\n" +
			"for ($i = 1; $i -lt $job.Count; $i++) {\n" +
			"    $name = ([string]$job[$i]).TrimStart('-')\n" +
			"    $metadata = $command.Parameters[$name]\n" +
			"    if ($null -eq $metadata) { throw \"Unknown parameter '-$name' for $script.\" }\n" +
			"    if ($metadata.ParameterType -eq [System.Management.Automation.SwitchParameter]) {\n" +
			"        $parameters[$name] = $true\n" +
			"    } else {\n" +
			"        if (++$i -ge $job.Count) { throw \"Parameter '-$name' needs a value.\" }\n" +
			"        $parameters[$name] = [string]$job[$i]\n" +
			"    }\n" +
			"}\n" +
			"& $command @parameters\n"));

		readonly object outputLock = new();
		readonly List<string> currentOutput = [];
		readonly TerminalTextParser terminalParser = new();
		Process process;
		string cancellationFile;

		/// <summary>Every line of output, in order, from both stdout and stderr.</summary>
		public event Action<TerminalLine> Output;

		/// <summary>Raised when the script finishes, with its exit code.</summary>
		public event Action<int> Finished;

		public bool IsRunning => process != null;
		public IReadOnlyList<string> LastOutput { get; private set; } = [];

		public void Start(ScriptJob job, string workingDirectory)
		{
			if (IsRunning)
				throw new InvalidOperationException("Something is already running.");

			var preparedCancellationFile = PrepareCancellationFile(job.CancellationFile);
			var startInfo = new ProcessStartInfo
			{
				FileName = PowerShellPath(),
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			if (job.PreserveColor)
			{
				startInfo.Environment.Remove("NO_COLOR");
				startInfo.Environment["AUTOCNC_PRESERVE_COLOR"] = "1";
				startInfo.Environment["CLICOLOR_FORCE"] = "1";
				startInfo.Environment["FORCE_COLOR"] = "1";
			}
			else
			{
				startInfo.Environment["NO_COLOR"] = "1";
				startInfo.Environment.Remove("CLICOLOR_FORCE");
				startInfo.Environment.Remove("FORCE_COLOR");
			}

			if (preparedCancellationFile != null)
				startInfo.Environment["AUTOCNC_CANCELLATION_PRECLEARED"] = "1";

			// -NonInteractive so a script that decides to prompt fails fast instead of hanging
			// behind a window nobody can see.
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
			startInfo.ArgumentList.Add("-OutputFormat");
			startInfo.ArgumentList.Add("Text");
			startInfo.ArgumentList.Add("-EncodedCommand");
			startInfo.ArgumentList.Add(EncodedRunner);

			var invocation = new List<string> { job.ScriptPath };
			invocation.AddRange(job.Arguments);
			startInfo.Environment["AUTOCNC_SCRIPT_JOB"] = JsonSerializer.Serialize(invocation);

			lock (outputLock)
			{
				currentOutput.Clear();
				LastOutput = [];
				terminalParser.Reset();
			}

			var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process = started;
			cancellationFile = preparedCancellationFile;
			started.OutputDataReceived += (_, e) => Emit(e.Data);
			started.ErrorDataReceived += (_, e) => Emit(e.Data);
			started.Exited += (_, _) =>
			{
				// The asynchronous readers can still have buffered lines at this point; the
				// parameterless wait is what flushes them, so nothing is lost off the end.
				started.WaitForExit();

				var code = started.ExitCode;
				lock (outputLock)
					LastOutput = currentOutput.ToArray();

				if (ReferenceEquals(process, started))
				{
					process = null;
					cancellationFile = null;
				}
				started.Dispose();
				Finished?.Invoke(code);
			};

			try
			{
				started.Start();
				started.BeginOutputReadLine();
				started.BeginErrorReadLine();
			}
			catch (System.ComponentModel.Win32Exception)
			{
				CleanupFailedStart(started);
				throw;
			}
			catch (InvalidOperationException)
			{
				CleanupFailedStart(started);
				throw;
			}
		}

		void CleanupFailedStart(Process started)
		{
			if (ReferenceEquals(process, started))
			{
				process = null;
				cancellationFile = null;
			}

			try
			{
				if (!started.HasExited)
					started.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
			catch (System.ComponentModel.Win32Exception)
			{
			}

			started.Dispose();
		}

		static string PrepareCancellationFile(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return null;

			try
			{
				var fullPath = Path.GetFullPath(path);
				File.Delete(fullPath);
				return fullPath;
			}
			catch (ArgumentException ex)
			{
				throw new InvalidOperationException($"The cancellation path is invalid: {ex.Message}", ex);
			}
			catch (NotSupportedException ex)
			{
				throw new InvalidOperationException($"The cancellation path is invalid: {ex.Message}", ex);
			}
			catch (IOException ex)
			{
				throw new InvalidOperationException($"Could not clear the cancellation path: {ex.Message}", ex);
			}
			catch (UnauthorizedAccessException ex)
			{
				throw new InvalidOperationException($"Could not clear the cancellation path: {ex.Message}", ex);
			}
		}

		/// <summary>
		/// Stops the script and anything it started — the game is a grandchild process, so
		/// killing only PowerShell would leave it running.
		/// </summary>
		/// <remarks>
		/// The game gets asked to close before it gets killed, because the replay recorder writes
		/// the metadata the engine needs to load a replay back only during a clean shutdown. Kill
		/// it outright and the recording of the match you just watched is unreadable — which is
		/// exactly the match you were most likely to want.
		/// </remarks>
		public void Stop()
		{
			var running = process;
			if (running == null)
				return;

			try
			{
				if (RequestCancellation() && running.WaitForExit(CloseTimeout))
					return;

				if (GameWindows.CloseAllStartedAfter(running.StartTime) && running.WaitForExit(CloseTimeout))
					return;

				running.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
				// It exited on its own between the check and the kill. Nothing to do.
			}
			catch (System.ComponentModel.Win32Exception)
			{
				// It exited on its own between the check and the kill. Nothing to do.
			}
		}

		bool RequestCancellation()
		{
			var path = cancellationFile;
			if (string.IsNullOrWhiteSpace(path))
				return false;

			try
			{
				var fullPath = Path.GetFullPath(path);
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				File.WriteAllText(fullPath, "stop");
				return true;
			}
			catch (IOException ex)
			{
				Emit($"Could not request a clean stop: {ex.Message}");
				return false;
			}
			catch (UnauthorizedAccessException ex)
			{
				Emit($"Could not request a clean stop: {ex.Message}");
				return false;
			}
		}

		void Emit(string line)
		{
			if (line != null)
			{
				TerminalLine parsed;
				lock (outputLock)
				{
					parsed = terminalParser.ParseLine(line);
					currentOutput.Add(parsed.PlainText);
				}

				Output?.Invoke(parsed);
			}
		}

		/// <summary>
		/// Prefers PowerShell 7 when it is installed, since that is what most people have their
		/// terminal set to, and falls back to the Windows PowerShell that is always present.
		/// </summary>
		static string PowerShellPath()
		{
			foreach (var candidate in new[]
			{
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
			})
			{
				if (File.Exists(candidate))
					return candidate;
			}

			return "powershell.exe";
		}
	}
}
