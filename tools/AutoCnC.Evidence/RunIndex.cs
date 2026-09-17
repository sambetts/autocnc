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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Evidence
{
	/// <summary>One finished run, as the cross-run index remembers it.</summary>
	public sealed class RunHistoryEntry
	{
		public string RunId { get; set; }
		public DateTime CompletedUtc { get; set; }
		public string SourceRevision { get; set; }
		public string Map { get; set; }
		public string Faction { get; set; }
		public string OpponentFaction { get; set; }
		public string Difficulty { get; set; }
		public int? Seed { get; set; }

		/// <summary>The named benchmark configuration, when the run was part of one.</summary>
		public string Benchmark { get; set; }

		/// <summary>Which single invocation of that benchmark this run belonged to.</summary>
		public string Batch { get; set; }

		/// <summary>
		/// <c>candidate</c> or <c>control</c>.
		/// </summary>
		/// <remarks>
		/// A control arm plays the previous revision on the identical configuration, which is the
		/// only way a win rate means anything: without it, a change that wins four of five has not
		/// been compared with anything, and the map and faction it drew are free to explain the
		/// whole result.
		/// </remarks>
		public string Arm { get; set; }

		public string Outcome { get; set; }
		public int DurationSeconds { get; set; }
		public double Fitness { get; set; }
		public Dictionary<string, double> Components { get; set; } = [];
		public Dictionary<string, double> Headline { get; set; } = [];
		public int ChecksPassed { get; set; }
		public int ChecksFailed { get; set; }
	}

	public sealed class RunHistory
	{
		public int SchemaVersion { get; set; } = 1;
		public string Bot { get; set; }
		public List<RunHistoryEntry> Runs { get; set; } = [];
	}

	public sealed class TrendMetric
	{
		public string Name { get; set; }
		public double Latest { get; set; }
		public double Previous { get; set; }
		public double Change { get; set; }
		public double ChangePercent { get; set; }

		/// <summary>Median of the runs before the latest, which variance moves far less than a pair.</summary>
		public double PriorMedian { get; set; }

		public string Direction { get; set; }

		/// <summary>True when this metric fell far enough to be worth a round's attention.</summary>
		public bool Regression { get; set; }

		public double[] Recent { get; set; } = [];
	}

	public sealed class TrendReport
	{
		public int SchemaVersion { get; set; } = 1;
		public string Bot { get; set; }
		public DateTime GeneratedUtc { get; set; }
		public int RunsCompared { get; set; }
		public string LatestRunId { get; set; }
		public string LatestRevision { get; set; }
		public List<TrendMetric> Metrics { get; set; } = [];
		public List<string> Regressions { get; set; } = [];
		public BenchmarkComparison Benchmark { get; set; }

		/// <summary>The block the next prompt injects verbatim.</summary>
		public string Rendered { get; set; }
	}

	/// <summary>Candidate against control on one named benchmark.</summary>
	public sealed class BenchmarkComparison
	{
		public string Name { get; set; }
		public string Batch { get; set; }
		public int CandidateRuns { get; set; }
		public int CandidateWins { get; set; }
		public int ControlRuns { get; set; }
		public int ControlWins { get; set; }
		public double CandidateMedianFitness { get; set; }
		public double ControlMedianFitness { get; set; }
		public string Verdict { get; set; }
	}

	/// <summary>
	/// The per-bot memory the improvement loop never had.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Cross-round comparison used to exist only as prose an agent hand-carried in a prompt it
	/// also rewrote, so a metric stopped being tracked the moment a round forgot to quote it. This
	/// keeps every run's headline numbers whether or not anybody thought to mention them, which is
	/// what makes a regression something the harness reports rather than something a later round
	/// happens to notice.
	/// </para>
	/// <para>
	/// <b>This is for the improvement agent, between matches, and for nothing else.</b> It lives in
	/// the training-run tree, outside every bot workspace; the assembly that writes it is not
	/// packed into any NuGet package a bot can reference; and no bot-visible API exposes it. A bot
	/// that could read it would be reading the answers to a match it is still playing, and an
	/// agent that is handed one will cheerfully hardcode the enemy's spawn — which is why
	/// <see cref="BotSourceAudit"/> exists and why the prompt templates forbid it in as many words.
	/// </para>
	/// </remarks>
	public static class RunIndex
	{
		/// <summary>How many recent runs the trend artifact compares.</summary>
		public const int TrendWindow = 10;

		/// <summary>A fall of this fraction against the prior median is called a regression.</summary>
		public const double RegressionThreshold = 0.15;

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		/// <summary>Metrics the trend tracks, and whether more of each is better.</summary>
		static readonly (string Name, bool HigherIsBetter)[] Tracked =
		[
			("fitness", true),
			("creditsEarnedPerSecond", true),
			("creditsSpentPerSecond", true),
			("valueExchangeRatio", true),
			("meanArmyValue", true),
			("buildingsKilled", true),
			("creditsKilled", true),
			("creditsLost", false),
			("durationSeconds", true),
			("cellsExplored", true),
			("idleUnitSeconds", false)
		];

		public static RunHistory Read(string path)
		{
			if (!File.Exists(path))
				return new RunHistory();

			try
			{
				return JsonSerializer.Deserialize<RunHistory>(File.ReadAllText(path), JsonOptions)
					?? new RunHistory();
			}
			catch (JsonException)
			{
				return new RunHistory();
			}
		}

		public static RunHistoryEntry Entry(FightSummary summary, CheckReport checks)
		{
			var entry = new RunHistoryEntry
			{
				RunId = summary.RunId,
				CompletedUtc = summary.GeneratedUtc,
				SourceRevision = summary.SourceRevision,
				Map = summary.Fight.Map,
				Faction = summary.Fight.Faction,
				OpponentFaction = summary.Fight.OpponentFaction,
				Difficulty = summary.Fight.Difficulty,
				Seed = summary.Fight.Seed,
				Benchmark = summary.Fight.Benchmark,
				Batch = summary.Fight.Batch,
				Arm = string.IsNullOrEmpty(summary.Fight.Arm) ? "candidate" : summary.Fight.Arm,
				Outcome = summary.Fight.Outcome,
				DurationSeconds = summary.Fight.DurationSeconds,
				Fitness = summary.Fitness?.Total ?? 0,
				ChecksPassed = checks?.Passed ?? 0,
				ChecksFailed = checks?.Failed ?? 0
			};

			foreach (var component in summary.Fitness?.Components ?? [])
				entry.Components[component.Name] = component.Score;

			var headline = summary.Headline;

			// Metrics the evidence could not answer are omitted rather than stored as zero. The
			// trend then skips them, instead of reporting an economic collapse that is really a
			// run recorded before the earned/spent columns existed.
			if (summary.Provenance?.HasEconomyFlows != false)
			{
				entry.Headline["creditsEarnedPerSecond"] = headline.CreditsEarnedPerSecond;
				entry.Headline["creditsSpentPerSecond"] = headline.CreditsSpentPerSecond;
			}

			entry.Headline["valueExchangeRatio"] = headline.ValueExchangeRatio;
			entry.Headline["meanArmyValue"] = headline.MeanArmyValue;
			entry.Headline["buildingsKilled"] = headline.BuildingsKilled;
			entry.Headline["creditsKilled"] = headline.CreditsKilled;
			entry.Headline["creditsLost"] = headline.CreditsLost;
			entry.Headline["durationSeconds"] = headline.DurationSeconds;
			entry.Headline["cellsExplored"] = headline.CellsExplored;
			entry.Headline["idleUnitSeconds"] = headline.IdleUnitSeconds;
			entry.Headline["fitness"] = entry.Fitness;
			return entry;
		}

		/// <summary>Adds or replaces this run, keeping the index ordered and bounded.</summary>
		public static RunHistory Record(RunHistory history, string bot, RunHistoryEntry entry)
		{
			history ??= new RunHistory();
			history.Bot ??= bot;
			history.Runs.RemoveAll(r => string.Equals(r.RunId, entry.RunId, StringComparison.Ordinal));
			history.Runs.Add(entry);
			history.Runs = history.Runs.OrderBy(r => r.CompletedUtc).ToList();
			return history;
		}

		public static TrendReport Trend(RunHistory history, int window = TrendWindow)
		{
			var runs = history.Runs
				.Where(r => !string.Equals(r.Arm, "control", StringComparison.OrdinalIgnoreCase))
				.TakeLast(Math.Max(2, window))
				.ToList();

			var report = new TrendReport
			{
				Bot = history.Bot,
				GeneratedUtc = DateTime.UtcNow,
				RunsCompared = runs.Count,
				LatestRunId = runs.Count > 0 ? runs[^1].RunId : null,
				LatestRevision = runs.Count > 0 ? runs[^1].SourceRevision : null
			};

			if (runs.Count >= 2)
				foreach (var (name, higherIsBetter) in Tracked)
				{
					// A metric no longer reaches the trend unless every run in the window recorded
					// it. Substituting zero for a run that never measured it would manufacture a
					// cliff, which is precisely the false alarm this report exists to avoid.
					if (!runs.All(r => r.Headline.ContainsKey(name)))
						continue;

					var series = runs.Select(r => r.Headline[name]).ToArray();
					var latest = series[^1];
					var previous = series[^2];
					var priorMedian = Median(series[..^1]);
					var change = Math.Round(latest - previous, 3);

					var metric = new TrendMetric
					{
						Name = name,
						Latest = Math.Round(latest, 3),
						Previous = Math.Round(previous, 3),
						Change = change,
						ChangePercent = previous != 0 ? Math.Round(100d * change / Math.Abs(previous), 1) : 0,
						PriorMedian = Math.Round(priorMedian, 3),
						Direction = change > 0 ? "up" : change < 0 ? "down" : "flat",
						Recent = series.Select(v => Math.Round(v, 3)).ToArray()
					};

					// Measured against the prior median rather than the previous run, because a
					// single noisy match either side would otherwise be enough to raise or hide an
					// alarm at n=1.
					if (priorMedian != 0)
					{
						var drift = (latest - priorMedian) / Math.Abs(priorMedian);
						metric.Regression = higherIsBetter
							? drift <= -RegressionThreshold
							: drift >= RegressionThreshold;
					}

					report.Metrics.Add(metric);
					if (metric.Regression)
						report.Regressions.Add(string.Create(CultureInfo.InvariantCulture,
							$"{name}: prior median {metric.PriorMedian:0.###} -> {metric.Latest:0.###} " +
							$"({100d * (latest - priorMedian) / Math.Abs(priorMedian):0.#}% against that median)"));
				}

			report.Benchmark = Compare(history);
			report.Rendered = Render(report);
			return report;
		}

		/// <summary>Candidate against control within the most recent benchmark sitting.</summary>
		/// <remarks>
		/// Scoped to one batch, not to a benchmark name. Aggregating every run that ever used the
		/// name would fold a previous revision's candidates and a stale control arm into the
		/// current win count, which is precisely the confounding this artifact exists to remove.
		/// </remarks>
		static BenchmarkComparison Compare(RunHistory history)
		{
			var latest = history.Runs.LastOrDefault(r => !string.IsNullOrEmpty(r.Benchmark));
			if (latest == null)
				return null;

			// A run recorded before batches existed has no batch id; those fall back to matching
			// on name alone rather than vanishing from the comparison entirely.
			var runs = string.IsNullOrEmpty(latest.Batch)
				? history.Runs.Where(r =>
					string.Equals(r.Benchmark, latest.Benchmark, StringComparison.OrdinalIgnoreCase) &&
					string.IsNullOrEmpty(r.Batch)).ToList()
				: history.Runs.Where(r =>
					string.Equals(r.Batch, latest.Batch, StringComparison.OrdinalIgnoreCase)).ToList();

			var candidate = runs.Where(r => !string.Equals(r.Arm, "control", StringComparison.OrdinalIgnoreCase)).ToList();
			var control = runs.Where(r => string.Equals(r.Arm, "control", StringComparison.OrdinalIgnoreCase)).ToList();

			var comparison = new BenchmarkComparison
			{
				Name = latest.Benchmark,
				Batch = latest.Batch,
				CandidateRuns = candidate.Count,
				CandidateWins = candidate.Count(Won),
				ControlRuns = control.Count,
				ControlWins = control.Count(Won),
				CandidateMedianFitness = Math.Round(Median(candidate.Select(r => r.Fitness).ToArray()), 4),
				ControlMedianFitness = Math.Round(Median(control.Select(r => r.Fitness).ToArray()), 4)
			};

			comparison.Verdict = control.Count == 0
				? "no control arm was run, so this win rate is not attributable"
				: string.Create(CultureInfo.InvariantCulture,
					$"candidate {comparison.CandidateWins}/{comparison.CandidateRuns} " +
					$"against control {comparison.ControlWins}/{comparison.ControlRuns}");

			return comparison;
		}

		static bool Won(RunHistoryEntry entry) =>
			string.Equals(entry.Outcome, "Won", StringComparison.OrdinalIgnoreCase);

		static double Median(double[] values)
		{
			if (values.Length == 0)
				return 0;

			var sorted = values.OrderBy(v => v).ToArray();
			var middle = sorted.Length / 2;
			return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
		}

		public static string Render(TrendReport report)
		{
			if (report.RunsCompared < 2)
				return "Not enough history yet to show a trend.";

			var text = new StringBuilder();
			text.Append(CultureInfo.InvariantCulture,
				$"Trend across the last {report.RunsCompared} runs of {report.Bot}:\n");

			foreach (var metric in report.Metrics)
				text.Append(CultureInfo.InvariantCulture,
					$"- {metric.Name}: {metric.Previous:0.###} -> {metric.Latest:0.###} " +
					$"({metric.ChangePercent:+0.#;-0.#;0}%), prior median {metric.PriorMedian:0.###}" +
					$"{(metric.Regression ? "  <== REGRESSION" : "")}\n");

			if (report.Benchmark != null)
				text.Append(CultureInfo.InvariantCulture,
					$"Benchmark {report.Benchmark.Name}" +
					$"{(report.Benchmark.Batch != null ? " batch " + report.Benchmark.Batch : "")}: " +
					$"{report.Benchmark.Verdict}; " +
					$"median fitness {report.Benchmark.CandidateMedianFitness:0.###} " +
					$"against {report.Benchmark.ControlMedianFitness:0.###}.\n");

			if (report.Regressions.Count > 0)
				text.Append("Regressions to explain before doing anything else:\n")
					.Append("- ").Append(string.Join("\n- ", report.Regressions)).Append('\n');

			return text.ToString().TrimEnd('\n');
		}

		public static void WriteHistory(string path, RunHistory history) =>
			Write(path, history);

		public static void WriteTrend(string path, TrendReport report) =>
			Write(path, report);

		static void Write(string path, object value)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(value, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}
	}
}
