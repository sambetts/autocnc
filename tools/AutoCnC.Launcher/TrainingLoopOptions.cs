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
using System.IO;
using System.Linq;

namespace AutoCnC.Launcher
{
	public sealed class TrainingLoopOptions
	{
		public string RepoRoot { get; set; }
		public string BattleBot { get; set; } = "Reference";
		public string Map { get; set; } = "16-9.oramap";
		public string Difficulty { get; set; } = "Hard";
		public string Faction { get; set; } = "Random";
		public string BotFaction { get; set; } = "Random";
		public int Opponents { get; set; } = 1;
		public int Seed { get; set; }
		public int Rounds { get; set; }
		public int MaxGameSeconds { get; set; } = 5400;
		public string ExecutionMode { get; set; } = BattleExecutionModes.Headless;
		public string GameSpeed { get; set; } = "maximum";
		public string Benchmark { get; set; } = ContinuousPromotionRunner.DefaultBenchmark;
		public string BenchmarkDifficulty { get; set; } = ContinuousPromotionRunner.DefaultDifficulty;
		public string RunsRoot { get; set; }
		public string AgentConfiguration { get; set; }
		public string PromptTemplate { get; set; }
		public string RestoreRun { get; set; }

		/// <summary>
		/// Restore and delete an unfinished experiment that would otherwise block training, then
		/// carry on. A resumable candidate is still resumed, and a live worker's run is refused.
		/// </summary>
		public bool DeleteBlockingRun { get; set; }

		/// <summary>Commit the bot workspace whenever the paired gate promotes a candidate.</summary>
		public bool Commit { get; set; } = true;

		/// <summary>Forward the agent's own colours to the console.</summary>
		public bool Color { get; set; } = true;

		internal string Validate(RepoLayout repo)
		{
			if (repo?.SupportsTraining != true)
				throw new ArgumentException("Select an AutoC&C checkout with the current authoring API.");
			if (Rounds < 0 || MaxGameSeconds < 0 || Opponents < 1)
				throw new ArgumentException("Rounds and MaxGameSeconds must be nonnegative; Opponents must be positive.");
			if (string.IsNullOrWhiteSpace(Map))
				throw new ArgumentException("A map is required for unattended training.");
			if (ExecutionMode != BattleExecutionModes.Headless && ExecutionMode != BattleExecutionModes.Rendered)
				throw new ArgumentException("ExecutionMode must be Headless or Rendered.");
			if (!new[] { "gdi", "nod", "Random" }.Contains(Faction, StringComparer.OrdinalIgnoreCase) ||
				!new[] { "gdi", "nod", "Random" }.Contains(BotFaction, StringComparer.OrdinalIgnoreCase))
				throw new ArgumentException("Factions must be gdi, nod or Random.");
			if (!DifficultyTable.Load(repo.DifficultiesFile).Levels.Any(level =>
				string.Equals(level.Name, Difficulty, StringComparison.OrdinalIgnoreCase)))
				throw new ArgumentException($"Unknown difficulty: {Difficulty}");
			ContinuousPromotionRunner.ValidateSelection(repo, Benchmark, BenchmarkDifficulty);

			if (string.IsNullOrWhiteSpace(BattleBot))
				throw new ArgumentException("An editable battle bot is required.");
			var path = File.Exists(BattleBot) || Directory.Exists(BattleBot)
				? BattleBot : Path.Combine(repo.Root, "bots", BattleBot);
			var project = BotWorkspace.ResolveProject(path) ??
				throw new ArgumentException($"No editable bot project found: {BattleBot}");
			var workspace = Path.GetDirectoryName(project);
			var runsRoot = Path.GetFullPath(RunsRoot ?? TrainingRun.DefaultRoot);
			var relative = Path.GetRelativePath(workspace, runsRoot);
			if (!Path.IsPathRooted(relative) && relative != ".." &&
				!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				throw new ArgumentException("RunsRoot must be outside the bot workspace so snapshots cannot include their own evidence.");
			return project;
		}

		internal TrainingBattleConfiguration Battle() => new()
		{
			Map = Map,
			Difficulty = Difficulty,
			Faction = Faction,
			BotFaction = BotFaction,
			Opponents = Opponents,
			ExecutionMode = ExecutionMode,
			GameSpeed = BattleExecutionModes.EffectiveGameSpeed(ExecutionMode, GameSpeed)
		};
	}
}
