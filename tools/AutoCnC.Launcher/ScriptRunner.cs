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

namespace AutoCnC.Launcher
{
	/// <summary>One thing to run: a repository script, with arguments.</summary>
	public sealed class ScriptJob
	{
		public string Title { get; init; }
		public string ScriptPath { get; init; }
		public IReadOnlyList<string> Arguments { get; init; } = [];
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

		Process process;

		/// <summary>Every line of output, in order, from both stdout and stderr.</summary>
		public event Action<string> Output;

		/// <summary>Raised when the script finishes, with its exit code.</summary>
		public event Action<int> Finished;

		public bool IsRunning => process != null;

		public void Start(ScriptJob job, string workingDirectory)
		{
			if (IsRunning)
				throw new InvalidOperationException("Something is already running.");

			var startInfo = new ProcessStartInfo
			{
				FileName = PowerShellPath(),
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};

			// -NonInteractive so a script that decides to prompt fails fast instead of hanging
			// behind a window nobody can see.
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(job.ScriptPath);

			foreach (var argument in job.Arguments)
				startInfo.ArgumentList.Add(argument);

			process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process.OutputDataReceived += (_, e) => Emit(e.Data);
			process.ErrorDataReceived += (_, e) => Emit(e.Data);
			process.Exited += (_, _) =>
			{
				var exited = process;

				// The asynchronous readers can still have buffered lines at this point; the
				// parameterless wait is what flushes them, so nothing is lost off the end.
				exited.WaitForExit();

				var code = exited.ExitCode;
				exited.Dispose();
				process = null;
				Finished?.Invoke(code);
			};

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
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
				if (GameWindows.CloseAllStartedAfter(running.StartTime) && running.WaitForExit(CloseTimeout))
					return;

				running.Kill(entireProcessTree: true);
			}
			catch (Exception)
			{
				// It exited on its own between the check and the kill. Nothing to do.
			}
		}

		void Emit(string line)
		{
			if (line != null)
				Output?.Invoke(line);
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
