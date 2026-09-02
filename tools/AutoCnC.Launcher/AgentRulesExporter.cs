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

namespace AutoCnC.Launcher
{
	/// <summary>Asks OpenRA to export the ruleset it actually resolved for AutoC&amp;C.</summary>
	public static class AgentRulesExporter
	{
		public static void Export(RepoLayout repo, string destination)
		{
			var utility = Path.Combine(repo.EngineBinDir, "OpenRA.Utility.dll");
			var platform = Path.Combine(repo.EngineBinDir, "AutoCnC.Platform.dll");
			if (!File.Exists(utility) || !File.Exists(platform))
				throw new InvalidOperationException(
					"The platform must be built before its resolved game rules can be exported.");

			Directory.CreateDirectory(Path.GetDirectoryName(destination));
			if (File.Exists(destination))
				File.Delete(destination);

			var start = new ProcessStartInfo
			{
				FileName = "dotnet",
				WorkingDirectory = repo.Root,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			start.ArgumentList.Add(utility);
			start.ArgumentList.Add("autocnc");
			start.ArgumentList.Add("--export-agent-rules");
			start.ArgumentList.Add(destination);
			start.Environment["ENGINE_DIR"] = repo.EngineDir;
			start.Environment["MOD_SEARCH_PATHS"] =
				$"{Path.Combine(repo.Root, "mods")},{Path.Combine(repo.EngineDir, "mods")}";
			start.Environment["NO_COLOR"] = "1";

			Process started;
			try
			{
				started = Process.Start(start);
			}
			catch (System.ComponentModel.Win32Exception ex)
			{
				throw new InvalidOperationException(
					$"Could not start OpenRA's resolved-rules exporter: {ex.Message}", ex);
			}

			using var process = started;
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			process.WaitForExit();
			var standardOutput = output.GetAwaiter().GetResult();
			var standardError = error.GetAwaiter().GetResult();

			if (process.ExitCode != 0)
			{
				var detail = (standardError + Environment.NewLine + standardOutput).Trim();
				throw new InvalidOperationException(
					$"OpenRA could not export its resolved game rules (exit {process.ExitCode}): {detail}");
			}

			if (!File.Exists(destination))
				throw new InvalidOperationException(
					"OpenRA reported success but did not create the resolved game-rules snapshot.");
		}
	}
}
