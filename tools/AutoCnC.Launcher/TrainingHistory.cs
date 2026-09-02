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
using System.Linq;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	/// <summary>One completed fight reduced to comparable iteration KPIs.</summary>
	public sealed class TrainingIteration
	{
		public int Number { get; init; }
		public TrainingRun Run { get; init; }
		public TrainingPlayerResult LocalPlayer { get; init; }
		public TrainingPlayerResult Opponents { get; init; }
		public int OpponentCount { get; init; }

		public string Outcome => Run.Manifest.Result?.Outcome ?? LocalPlayer?.Outcome;
		public int DurationSeconds => Run.Manifest.Result?.DurationSeconds ?? 0;
	}

	/// <summary>Chronological battles and comparable KPI summaries for one bot.</summary>
	public sealed class TrainingHistory
	{
		public IReadOnlyList<TrainingRun> Runs { get; }
		public IReadOnlyList<TrainingIteration> Iterations { get; }
		public IReadOnlyList<string> Warnings { get; }

		TrainingHistory(IReadOnlyList<TrainingRun> runs, IReadOnlyList<string> warnings)
		{
			Runs = runs;
			Warnings = warnings;
			Iterations = CreateIterations(runs);
		}

		public static TrainingHistory Empty { get; } = new([], []);

		public TrainingHistory WithRun(TrainingRun run)
		{
			if (run == null)
				return this;

			var runs = Runs.Where(existing => !SamePath(existing.RunDirectory, run.RunDirectory))
				.Append(run)
				.OrderBy(existing => existing.Manifest.CreatedUtc)
				.ToList();
			return new TrainingHistory(runs, Warnings);
		}

		public static TrainingHistory Load(string botPath, string runsRoot = null)
		{
			if (string.IsNullOrWhiteSpace(botPath))
				return Empty;

			var botIdentity = BotIdentity(botPath);
			var directory = TrainingRun.RunsDirectoryForBot(botPath, runsRoot);
			if (!Directory.Exists(directory))
				return Empty;

			var runs = new List<TrainingRun>();
			var warnings = new List<string>();
			foreach (var runDirectory in Directory.EnumerateDirectories(directory))
			{
				try
				{
					var run = TrainingRun.Load(runDirectory);
					if (run != null && SamePath(BotIdentity(run), botIdentity))
						runs.Add(run);
				}
				catch (IOException ex)
				{
					warnings.Add($"{runDirectory}: {ex.Message}");
				}
				catch (UnauthorizedAccessException ex)
				{
					warnings.Add($"{runDirectory}: {ex.Message}");
				}
				catch (JsonException ex)
				{
					warnings.Add($"{runDirectory}: {ex.Message}");
				}
				catch (ArgumentException ex)
				{
					warnings.Add($"{runDirectory}: {ex.Message}");
				}
				catch (NotSupportedException ex)
				{
					warnings.Add($"{runDirectory}: {ex.Message}");
				}
			}

			runs.Sort((left, right) =>
			{
				var created = left.Manifest.CreatedUtc.CompareTo(right.Manifest.CreatedUtc);
				return created != 0
					? created
					: string.Compare(left.Manifest.Id, right.Manifest.Id, StringComparison.Ordinal);
			});

			return new TrainingHistory(runs, warnings);
		}

		internal static TrainingHistory FromRuns(IEnumerable<TrainingRun> runs) =>
			new((runs ?? []).OrderBy(run => run.Manifest.CreatedUtc).ToList(), []);

		static IReadOnlyList<TrainingIteration> CreateIterations(IReadOnlyList<TrainingRun> runs)
		{
			var iterations = new List<TrainingIteration>();
			for (var index = 0; index < runs.Count; index++)
			{
				var run = runs[index];
				var result = run.Manifest.Result;
				if (run.Manifest.CompletedUtc == null || result?.Players is not { Count: > 0 })
					continue;

				var local = result.Players.FirstOrDefault(player =>
					string.Equals(player.Name, result.LocalPlayer, StringComparison.Ordinal));
				local ??= result.Players.FirstOrDefault(player => !player.IsBot);
				local ??= result.Players[0];

				var opponents = result.Players.Where(player => !ReferenceEquals(player, local)).ToList();
				iterations.Add(new TrainingIteration
				{
					Number = index + 1,
					Run = run,
					LocalPlayer = local,
					Opponents = SumOpponents(opponents),
					OpponentCount = opponents.Count
				});
			}

			return iterations;
		}

		static TrainingPlayerResult SumOpponents(IReadOnlyList<TrainingPlayerResult> opponents) =>
			new()
			{
				Name = opponents.Count == 1 ? opponents[0].Name : "Opponents",
				IsBot = true,
				Units = opponents.Sum(player => player.Units),
				ArmyValue = opponents.Sum(player => player.ArmyValue),
				Buildings = opponents.Sum(player => player.Buildings),
				BaseValue = opponents.Sum(player => player.BaseValue),
				Cash = opponents.Sum(player => player.Cash),
				Killed = opponents.Sum(player => player.Killed),
				Lost = opponents.Sum(player => player.Lost)
			};

		static string BotIdentity(string botPath)
		{
			var full = Path.GetFullPath(botPath);
			return BotWorkspace.ResolveProject(full) ?? full;
		}

		static string BotIdentity(TrainingRun run) =>
			!string.IsNullOrEmpty(run.Manifest.BotProject)
				? Path.GetFullPath(run.Manifest.BotProject)
				: Path.GetFullPath(run.Manifest.BotPath);

		static bool SamePath(string left, string right) =>
			string.Equals(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
				StringComparison.OrdinalIgnoreCase);
	}
}
