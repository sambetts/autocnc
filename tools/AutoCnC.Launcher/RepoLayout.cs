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
using System.IO;
using System.Linq;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Where everything lives in an AutoC&amp;C checkout, and whether it has been built yet.
	/// </summary>
	public sealed class RepoLayout
	{
		public const int RequiredAuthoringApiVersion = 3;

		public string Root { get; }
		public int AuthoringApiVersion { get; }
		public bool SupportsTraining => AuthoringApiVersion >= RequiredAuthoringApiVersion;

		RepoLayout(string root)
		{
			Root = root;
			AuthoringApiVersion = ReadAuthoringApiVersion(root);
		}

		public string ScriptsDir => Path.Combine(Root, "scripts");
		public string EngineDir => Path.Combine(Root, "engine");
		public string EngineBinDir => Path.Combine(EngineDir, "bin");
		public string PackagesDir => Path.Combine(Root, "packages");

		public string RunBotScript => Path.Combine(ScriptsDir, "run-bot.ps1");
		public string NewBotScript => Path.Combine(ScriptsDir, "new-bot.ps1");
		public string TrainBotScript => Path.Combine(ScriptsDir, "train-bot.ps1");
		public string LaunchScript => Path.Combine(ScriptsDir, "launch.ps1");
		public string BuildScript => Path.Combine(ScriptsDir, "build.ps1");
		public string DifficultiesFile => Path.Combine(ScriptsDir, "difficulties.json");
		public string AuthoringApiFile => Path.Combine(ScriptsDir, "authoring-api.version");
		public string AgentGameGuide => Path.Combine(Root, "docs", "agent-game-guide.md");
		public string AgentPromptTemplate => Path.Combine(Root, "docs", "agent-prompt-template.md");

		public string ReferenceBot => Path.Combine(Root, "bots", "Reference", "ReferenceBot.csproj");

		/// <summary>The engine is a submodule, so a fresh clone may not have fetched it yet.</summary>
		public bool EngineFetched => File.Exists(Path.Combine(EngineDir, "OpenRA.sln"));

		/// <summary>Building the engine takes minutes, so it is worth knowing before we start.</summary>
		public bool EngineBuilt => File.Exists(Path.Combine(EngineBinDir, "OpenRA.dll"));

		public bool PackagesBuilt =>
			Directory.Exists(PackagesDir) &&
			Directory.EnumerateFiles(PackagesDir, "AutoCnC.Sdk.*.nupkg").Any() &&
			Directory.EnumerateFiles(PackagesDir, "AutoCnC.Core.*.nupkg").Any();

		public static bool LooksLikeRoot(string directory) =>
			!string.IsNullOrWhiteSpace(directory)
			&& File.Exists(Path.Combine(directory, "AutoCnC.sln"))
			&& File.Exists(Path.Combine(directory, "scripts", "run-bot.ps1"));

		public static RepoLayout For(string directory) =>
			LooksLikeRoot(directory) ? new RepoLayout(Path.GetFullPath(directory)) : null;

		/// <summary>
		/// Finds the checkout this launcher belongs to, walking up from wherever it was built or
		/// copied to. Returns null when it is running somewhere unrelated, in which case the user
		/// gets to point at the repository themselves.
		/// </summary>
		public static RepoLayout Discover(string hint = null)
			=> Discover(hint, [AppContext.BaseDirectory, Directory.GetCurrentDirectory()]);

		public static RepoLayout Discover(string hint, IEnumerable<string> starts)
		{
			var hinted = For(hint);
			if (hinted?.SupportsTraining == true)
				return hinted;

			RepoLayout fallback = hinted;

			foreach (var start in starts)
			{
				var directory = start;
				while (!string.IsNullOrEmpty(directory))
				{
					var found = For(directory);
					if (found != null)
					{
						fallback ??= found;
						if (found.SupportsTraining)
							return found;
					}

					directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
				}
			}

			return fallback;
		}

		static int ReadAuthoringApiVersion(string root)
		{
			try
			{
				var path = Path.Combine(root, "scripts", "authoring-api.version");
				return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var version)
					? version
					: 0;
			}
			catch (IOException)
			{
				return 0;
			}
			catch (UnauthorizedAccessException)
			{
				return 0;
			}
		}
	}
}
