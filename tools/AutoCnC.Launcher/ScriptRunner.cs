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
	/// <summary>What a queued job is, when that changes how its ending is handled.</summary>
	public enum ScriptJobKind
	{
		Generic,
		Improvement,
		Chat
	}

	/// <summary>One thing to run: a repository script, with arguments.</summary>
	public sealed class ScriptJob
	{
		public string Title { get; init; }
		public string ScriptPath { get; init; }
		public IReadOnlyList<string> Arguments { get; init; } = [];
		public ScriptJobKind Kind { get; init; } = ScriptJobKind.Generic;
		public bool PreserveColor { get; init; }
		public string CancellationFile { get; init; }
		public string WorkerOwnershipFile { get; init; }
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
	/// <para>
	/// Arguments are bound by name from the script's own parameter metadata, aliases included:
	/// <c>CommandInfo.Parameters</c> is keyed by real parameter names only, so matching against it
	/// alone rejected documented aliases such as <c>-Doctrine</c>. That metadata is also null when
	/// PowerShell cannot parse the script at all — Windows PowerShell reads a BOM-less file as ANSI
	/// and chokes on characters Core handles fine — which used to be reported as an unknown
	/// parameter, blaming the button for a syntax error further down the file. Now the parse error
	/// itself is reported.
	/// </para>
	/// </remarks>
	public sealed class ScriptRunner
	{
		/// <summary>How long a closing game gets to write its replay out before it is killed.</summary>
		const int CloseTimeout = 8000;
		const string Runner =
			"$utf8 = New-Object System.Text.UTF8Encoding $false\n" +
			"$OutputEncoding = [Console]::OutputEncoding = $utf8\n" +
			"if (Get-Variable PSStyle -ErrorAction SilentlyContinue) {\n" +
			"    $PSStyle.OutputRendering = if ($env:AUTOCNC_PRESERVE_COLOR -eq '1') { 'Ansi' } else { 'PlainText' }\n" +
			"}\n" +
			"$workerPath = $env:AUTOCNC_WORKER_OWNERSHIP\n" +
			"$workerGate = $env:AUTOCNC_WORKER_GATE\n" +
			"if ($workerGate) { while (-not (Test-Path -LiteralPath $workerGate)) { Start-Sleep -Milliseconds 10 } }\n" +
			"$exitCode = 0\n" +
			"try {\n" +
			"    $job = @((ConvertFrom-Json -InputObject $env:AUTOCNC_SCRIPT_JOB))\n" +
			"    $script = [string]$job[0]\n" +
			"    $command = Get-Command -Name $script -CommandType ExternalScript\n" +
			"    $declared = $command.Parameters\n" +
			"    if ($null -eq $declared -or $declared.Count -eq 0) {\n" +
			"        $parseTokens = $null\n" +
			"        $parseErrors = $null\n" +
			"        [void][System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$parseTokens, [ref]$parseErrors)\n" +
			"        if ($parseErrors -and $parseErrors.Count -gt 0) {\n" +
			"            throw \"$script is not valid PowerShell $($PSVersionTable.PSVersion): $($parseErrors[0].Message) (line $($parseErrors[0].Extent.StartLineNumber))\"\n" +
			"        }\n" +
			"        if ($job.Count -gt 1) { throw \"$script exposes no parameter metadata.\"\n" +
			"        }\n" +
			"        & $script\n" +
			"    } else {\n" +
			"        $parameters = @{}\n" +
			"        for ($i = 1; $i -lt $job.Count; $i++) {\n" +
			"            $name = ([string]$job[$i]).TrimStart('-')\n" +
			"            $metadata = $declared[$name]\n" +
			"            if ($null -eq $metadata) {\n" +
			"                $metadata = @($declared.Values | Where-Object { $_.Aliases -contains $name })[0]\n" +
			"            }\n" +
			"            if ($null -eq $metadata) { throw \"Unknown parameter '-$name' for $script.\" }\n" +
			"            if ($metadata.ParameterType -eq [System.Management.Automation.SwitchParameter]) {\n" +
			"                $parameters[$metadata.Name] = $true\n" +
			"            } else {\n" +
			"                if (++$i -ge $job.Count) { throw \"Parameter '-$name' needs a value.\" }\n" +
			"                $parameters[$metadata.Name] = [string]$job[$i]\n" +
			"            }\n" +
			"        }\n" +
			"        & $command @parameters\n" +
			"    }\n" +
			"    if ($LASTEXITCODE -is [int]) { $exitCode = $LASTEXITCODE }\n" +
			"} catch {\n" +
			"    [Console]::Error.WriteLine(($_ | Out-String))\n" +
			"    $exitCode = 1\n" +
			"} finally {\n" +
			"    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE }\n" +
			"    if ($workerPath) { Remove-Item -LiteralPath $workerPath -Force -ErrorAction SilentlyContinue }\n" +
			"    if ($workerGate) { Remove-Item -LiteralPath $workerGate -Force -ErrorAction SilentlyContinue }\n" +
			"}\n" +
			"exit $exitCode\n";

		readonly object outputLock = new();
		readonly List<string> currentOutput = [];
		readonly TerminalTextParser terminalParser = new();
		Process process;
		WindowsProcessJob processJob;
		string cancellationFile;
		string workerOwnershipFile;
		string workerGateFile;

		/// <summary>Every line of output, in order, from both stdout and stderr.</summary>
		public event Action<TerminalLine> Output;

		/// <summary>Raised when the script finishes, with its exit code.</summary>
		public event Action<int> Finished;

		public bool IsRunning => process != null;
		internal bool HasProcessJob => processJob != null;
		public IReadOnlyList<string> LastOutput { get; private set; } = [];

		public void Start(ScriptJob job, string workingDirectory)
		{
			if (IsRunning)
				throw new InvalidOperationException("Something is already running.");

			string preparedCancellationFile = null;
			(string Ownership, string Gate) preparedWorker = (null, null);
			ProcessStartInfo startInfo;
			try
			{
				preparedCancellationFile = PrepareCancellationFile(job.CancellationFile);
				preparedWorker = PrepareWorkerFiles(job.WorkerOwnershipFile);
				startInfo = CreateStartInfo(job, workingDirectory);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				JsonException)
			{
				CleanupWorkerFiles(preparedWorker.Ownership, preparedWorker.Gate);
				throw new InvalidOperationException(
					"Could not prepare the worker process: " + ex.Message, ex);
			}
			if (preparedCancellationFile != null)
				startInfo.Environment["AUTOCNC_CANCELLATION_PRECLEARED"] = "1";
			if (preparedWorker.Ownership != null)
				startInfo.Environment["AUTOCNC_WORKER_OWNERSHIP"] = preparedWorker.Ownership;
			if (preparedWorker.Gate != null)
				startInfo.Environment["AUTOCNC_WORKER_GATE"] = preparedWorker.Gate;

			lock (outputLock)
			{
				currentOutput.Clear();
				LastOutput = [];
				terminalParser.Reset();
			}

			var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			WindowsProcessJob jobObject = null;
			var startFailed = false;
			process = started;
			cancellationFile = preparedCancellationFile;
			workerOwnershipFile = preparedWorker.Ownership;
			workerGateFile = preparedWorker.Gate;
			started.OutputDataReceived += (_, e) => Emit(e.Data);
			started.ErrorDataReceived += (_, e) => Emit(e.Data);
			started.Exited += (_, _) =>
			{
				if (startFailed)
					return;

				// The asynchronous readers can still have buffered lines at this point; the
				// parameterless wait is what flushes them, so nothing is lost off the end.
				started.WaitForExit();

				var code = started.ExitCode == 0 ? 0 : 1;
				lock (outputLock)
					LastOutput = currentOutput.ToArray();

				if (ReferenceEquals(process, started))
				{
					process = null;
					cancellationFile = null;
					workerOwnershipFile = null;
					workerGateFile = null;
					processJob = null;
				}
				CleanupWorkerFiles(preparedWorker.Ownership, preparedWorker.Gate);
				jobObject?.Dispose();
				started.Dispose();
				Finished?.Invoke(code);
			};

			try
			{
				jobObject = WindowsProcessJob.Create();
				processJob = jobObject;
				if (!started.Start())
					throw new InvalidOperationException(
						"PowerShell did not start the worker process.");
				jobObject.Assign(started);
				if (preparedWorker.Ownership != null)
					WriteWorkerOwnership(preparedWorker.Ownership, started);
				if (preparedWorker.Gate != null)
					File.WriteAllText(preparedWorker.Gate, "go");
				started.BeginOutputReadLine();
				started.BeginErrorReadLine();
			}
			catch (System.ComponentModel.Win32Exception)
			{
				startFailed = true;
				CleanupFailedStart(started, jobObject);
				throw;
			}
			catch (InvalidOperationException)
			{
				startFailed = true;
				CleanupFailedStart(started, jobObject);
				throw;
			}
			catch (IOException ex)
			{
				startFailed = true;
				CleanupFailedStart(started, jobObject);
				throw new InvalidOperationException(
					"Could not start the worker process: " + ex.Message, ex);
			}
			catch (UnauthorizedAccessException ex)
			{
				startFailed = true;
				CleanupFailedStart(started, jobObject);
				throw new InvalidOperationException(
					"Could not start the worker process: " + ex.Message, ex);
			}
			catch (JsonException ex)
			{
				startFailed = true;
				CleanupFailedStart(started, jobObject);
				throw new InvalidOperationException(
					"Could not start the worker process: " + ex.Message, ex);
			}
		}

		internal static ProcessStartInfo CreateStartInfo(ScriptJob job, string workingDirectory)
		{
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

			// -NonInteractive so a script that decides to prompt fails fast instead of hanging
			// behind a window nobody can see.
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
			startInfo.ArgumentList.Add("-OutputFormat");
			startInfo.ArgumentList.Add("Text");
			// Windows PowerShell serializes stderr as CLIXML with -EncodedCommand even when
			// text output is requested. Only this fixed bootstrap is code; job values stay in JSON.
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add(Runner);

			var invocation = new List<string> { job.ScriptPath };
			invocation.AddRange(job.Arguments);
			startInfo.Environment["AUTOCNC_SCRIPT_JOB"] = JsonSerializer.Serialize(invocation);
			return startInfo;
		}

		void CleanupFailedStart(Process started, WindowsProcessJob jobObject)
		{
			if (ReferenceEquals(process, started))
			{
				process = null;
				cancellationFile = null;
				workerOwnershipFile = null;
				workerGateFile = null;
				processJob = null;
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

			started.StartInfo.Environment.TryGetValue(
				"AUTOCNC_WORKER_OWNERSHIP", out var ownership);
			started.StartInfo.Environment.TryGetValue(
				"AUTOCNC_WORKER_GATE", out var gate);
			CleanupWorkerFiles(ownership, gate);
			jobObject?.Dispose();
			started.Dispose();
		}

		static (string Ownership, string Gate) PrepareWorkerFiles(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				var gateDirectory = Path.Combine(Path.GetTempPath(),
					"AutoCnC", "WorkerGates");
				Directory.CreateDirectory(gateDirectory);
				return (null, Path.Combine(gateDirectory,
					Guid.NewGuid().ToString("N") + ".gate"));
			}

			var ownership = Path.GetFullPath(path);
			var gate = ownership + ".gate";
			Directory.CreateDirectory(Path.GetDirectoryName(ownership));
			CleanupWorkerFiles(ownership, gate);
			return (ownership, gate);
		}

		static void WriteWorkerOwnership(string path, Process process)
		{
			var temporary = path + ".tmp";
			File.WriteAllText(temporary,
				JsonSerializer.Serialize(ProcessOwnership.ForProcess(process)));
			File.Move(temporary, path, true);
		}

		static void CleanupWorkerFiles(string ownership, string gate)
		{
			foreach (var path in new[] { ownership, gate })
				if (!string.IsNullOrWhiteSpace(path))
					try
					{
						File.Delete(path);
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
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
				running.WaitForExit();
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
			var installed = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
				"PowerShell", "7", "pwsh.exe");
			if (File.Exists(installed))
				return installed;

			var searchPath = Environment.GetEnvironmentVariable("PATH");
			if (!string.IsNullOrWhiteSpace(searchPath))
				foreach (var directory in searchPath.Split(Path.PathSeparator,
					StringSplitOptions.RemoveEmptyEntries))
				{
					var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
					if (File.Exists(candidate))
						return candidate;
				}

			foreach (var candidate in new[]
			{
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
