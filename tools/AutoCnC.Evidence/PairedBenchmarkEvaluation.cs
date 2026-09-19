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
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Evidence
{
	public static class PromotionVerdicts
	{
		public const string Undefined = "Undefined";
		public const string Promote = "Promote";
		public const string Restore = "Restore";
	}

	/// <summary>One arm summary from <c>benchmark-bot.ps1</c>.</summary>
	public sealed class BenchmarkArmResult
	{
		public string Arm { get; set; }
		public int Wins { get; set; }
		public int Played { get; set; }
		public double MedianFitness { get; set; }
		public double MedianEarnedPerSecond { get; set; }
		public double MedianSpentPerSecond { get; set; }
		public double MedianExchange { get; set; }
		public double MedianBuildingsKilled { get; set; }
	}

	/// <summary>One completed match from the machine-readable benchmark result.</summary>
	public sealed class BenchmarkMatchResult
	{
		public string RunId { get; set; }
		public string Evidence { get; set; }
		public string Arm { get; set; }
		public int Repeat { get; set; }
		public int Scenario { get; set; }
		public string Map { get; set; }
		public string Faction { get; set; }
		public string BotFaction { get; set; }
		public int? Seed { get; set; }
		public string Outcome { get; set; }
		public double Fitness { get; set; }
		public double EarnedPerSecond { get; set; }
		public double SpentPerSecond { get; set; }
		public double Exchange { get; set; }
		public double BuildingsKilled { get; set; }
		public int DurationSeconds { get; set; }

		/// <summary>Optional fields accepted from newer producers without invalidating schema 1.</summary>
		public string Benchmark { get; set; }
		public string Batch { get; set; }
		public string Status { get; set; }
		public bool Succeeded { get; set; }
		public string Error { get; set; }
	}

	/// <summary>The paired row emitted by <c>benchmark-bot.ps1</c>.</summary>
	public sealed class BenchmarkPairResult
	{
		public int Repeat { get; set; }
		public int Scenario { get; set; }
		public string Map { get; set; }
		public string Faction { get; set; }
		public string BotFaction { get; set; }
		public int? Seed { get; set; }
		public string CandidateOutcome { get; set; }
		public string ControlOutcome { get; set; }
		public double FitnessDelta { get; set; }
		public double EarnedPerSecondDelta { get; set; }
		public double SpentPerSecondDelta { get; set; }
		public double ExchangeDelta { get; set; }
		public double BuildingsKilledDelta { get; set; }
		public string Benchmark { get; set; }
		public string Batch { get; set; }
	}

	/// <summary>The schema 1 result written by <c>benchmark-bot.ps1</c>.</summary>
	public sealed class BenchmarkResultDocument
	{
		public int SchemaVersion { get; set; }
		public DateTime GeneratedUtc { get; set; }
		public string Benchmark { get; set; }
		public string Batch { get; set; }
		public string Difficulty { get; set; }
		public int ExpectedMatchesPerArm { get; set; }
		public BenchmarkArmResult Candidate { get; set; }
		public BenchmarkArmResult Control { get; set; }
		public List<BenchmarkPairResult> Paired { get; set; } = [];
		public List<BenchmarkMatchResult> Matches { get; set; } = [];
	}

	/// <summary>Combines two immutable single-arm benchmark runs into one paired batch.</summary>
	public static class PairedBenchmarkResultComposer
	{
		readonly record struct ScenarioKey(int Repeat, int Scenario);

		public static BenchmarkResultDocument Combine(BenchmarkResultDocument candidate,
			BenchmarkResultDocument control, string batch)
		{
			if (candidate == null || control == null)
				throw new InvalidDataException("Both immutable arm results are required.");
			if (string.IsNullOrWhiteSpace(batch))
				throw new InvalidDataException("The combined paired batch needs an identifier.");
			if (string.IsNullOrWhiteSpace(candidate.Batch) ||
				string.IsNullOrWhiteSpace(control.Batch))
				throw new InvalidDataException(
					"Each immutable arm result must identify its raw benchmark batch.");
			if (!string.Equals(candidate.Benchmark, control.Benchmark,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Candidate and control were run against different benchmarks.");
			if (!string.Equals(candidate.Difficulty, control.Difficulty,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Candidate and control were run at different difficulties.");
			if (candidate.ExpectedMatchesPerArm <= 0 ||
				candidate.ExpectedMatchesPerArm != control.ExpectedMatchesPerArm)
				throw new InvalidDataException(
					"Candidate and control do not declare the same expected match count.");

			var candidateRows = SingleArm(candidate, "candidate");
			var controlRows = SingleArm(control, "control");
			var combined = new BenchmarkResultDocument
			{
				SchemaVersion = Math.Max(candidate.SchemaVersion, control.SchemaVersion),
				GeneratedUtc = DateTime.UtcNow,
				Benchmark = candidate.Benchmark,
				Batch = batch,
				Difficulty = candidate.Difficulty,
				ExpectedMatchesPerArm = candidate.ExpectedMatchesPerArm,
				Candidate = Clone(candidate.Candidate, "candidate"),
				Control = Clone(control.Candidate ?? control.Control, "control"),
				Matches =
				[
					.. candidateRows.Select(row => Clone(row, "candidate",
						candidate.Benchmark, batch)),
					.. controlRows.Select(row => Clone(row, "control",
						candidate.Benchmark, batch))
				]
			};

			var controlByScenario = controlRows
				.GroupBy(row => new ScenarioKey(row.Repeat, row.Scenario))
				.Where(group => group.Count() == 1)
				.ToDictionary(group => group.Key, group => group.Single());

			foreach (var candidateRow in candidateRows)
			{
				var key = new ScenarioKey(candidateRow.Repeat, candidateRow.Scenario);
				if (!controlByScenario.TryGetValue(key, out var controlRow))
					continue;

				combined.Paired.Add(new BenchmarkPairResult
				{
					Repeat = key.Repeat,
					Scenario = key.Scenario,
					Map = candidateRow.Map,
					Faction = candidateRow.Faction,
					BotFaction = candidateRow.BotFaction,
					Seed = candidateRow.Seed,
					CandidateOutcome = candidateRow.Outcome,
					ControlOutcome = controlRow.Outcome,
					FitnessDelta = Math.Round(candidateRow.Fitness - controlRow.Fitness, 4),
					EarnedPerSecondDelta = Math.Round(
						candidateRow.EarnedPerSecond - controlRow.EarnedPerSecond, 3),
					SpentPerSecondDelta = Math.Round(
						candidateRow.SpentPerSecond - controlRow.SpentPerSecond, 3),
					ExchangeDelta = Math.Round(candidateRow.Exchange - controlRow.Exchange, 4),
					BuildingsKilledDelta =
						candidateRow.BuildingsKilled - controlRow.BuildingsKilled,
					Benchmark = candidate.Benchmark,
					Batch = batch
				});
			}

			return combined;
		}

		static List<BenchmarkMatchResult> SingleArm(BenchmarkResultDocument document,
			string arm)
		{
			var rows = document.Matches ?? [];
			if (rows.Any(row => row == null ||
				!string.Equals(row.Arm, "candidate", StringComparison.OrdinalIgnoreCase)))
				throw new InvalidDataException(
					$"The immutable {arm} benchmark result is not a single candidate arm.");

			return rows;
		}

		static BenchmarkArmResult Clone(BenchmarkArmResult source, string arm)
		{
			if (source == null)
				return null;

			return new BenchmarkArmResult
			{
				Arm = arm,
				Wins = source.Wins,
				Played = source.Played,
				MedianFitness = source.MedianFitness,
				MedianEarnedPerSecond = source.MedianEarnedPerSecond,
				MedianSpentPerSecond = source.MedianSpentPerSecond,
				MedianExchange = source.MedianExchange,
				MedianBuildingsKilled = source.MedianBuildingsKilled
			};
		}

		static BenchmarkMatchResult Clone(BenchmarkMatchResult source, string arm,
			string benchmark, string batch) => new()
		{
			RunId = source.RunId,
			Evidence = source.Evidence,
			Arm = arm,
			Repeat = source.Repeat,
			Scenario = source.Scenario,
			Map = source.Map,
			Faction = source.Faction,
			BotFaction = source.BotFaction,
			Seed = source.Seed,
			Outcome = source.Outcome,
			Fitness = source.Fitness,
			EarnedPerSecond = source.EarnedPerSecond,
			SpentPerSecond = source.SpentPerSecond,
			Exchange = source.Exchange,
			BuildingsKilled = source.BuildingsKilled,
			DurationSeconds = source.DurationSeconds,
			Benchmark = benchmark,
			Batch = batch,
			Status = source.Status,
			Succeeded = source.Succeeded,
			Error = source.Error ?? ""
		};
	}

	public sealed class PairedScenarioEvaluation
	{
		public int Repeat { get; set; }
		public int Scenario { get; set; }
		public string Map { get; set; }
		public string Faction { get; set; }
		public string BotFaction { get; set; }
		public int? Seed { get; set; }
		public string CandidateOutcome { get; set; }
		public string ControlOutcome { get; set; }
		public double CandidateFitness { get; set; }
		public double ControlFitness { get; set; }
		public double FitnessDelta { get; set; }
	}

	/// <summary>A promotion decision derived only from complete paired benchmark evidence.</summary>
	public sealed class PairedBenchmarkEvaluation
	{
		public int SchemaVersion { get; set; } = 1;
		public DateTime GeneratedUtc { get; set; }
		public string Benchmark { get; set; }
		public string Batch { get; set; }
		public string Verdict { get; set; } = PromotionVerdicts.Undefined;
		public string Basis { get; set; }
		public string Reason { get; set; }
		public int PairsCompared { get; set; }
		public int ExpectedMatchesPerArm { get; set; }
		public int CandidateWins { get; set; }
		public int ControlWins { get; set; }
		public int CandidateFitnessPairs { get; set; }
		public int ControlFitnessPairs { get; set; }
		public int TiedFitnessPairs { get; set; }
		public double MedianPairedFitnessDelta { get; set; }
		public List<PairedScenarioEvaluation> Scenarios { get; set; } = [];

		[JsonIgnore]
		public bool CanPromote =>
			string.Equals(Verdict, PromotionVerdicts.Promote, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Validates and ranks a candidate and its control arm from the same benchmark sitting.
	/// </summary>
	public static class PairedBenchmarkEvaluator
	{
		const double DeltaTolerance = 0.00015;

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		readonly record struct ScenarioKey(int Repeat, int Scenario);

		public static PairedBenchmarkEvaluation EvaluateFile(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return Undefined("The paired benchmark result is missing.");

			try
			{
				var document = ReadResult(path);
				return Evaluate(document);
			}
			catch (JsonException ex)
			{
				return Undefined("The paired benchmark result is invalid JSON: " + ex.Message);
			}
			catch (IOException ex)
			{
				return Undefined("The paired benchmark result could not be read: " + ex.Message);
			}
			catch (UnauthorizedAccessException ex)
			{
				return Undefined("The paired benchmark result could not be read: " + ex.Message);
			}
		}

		public static PairedBenchmarkEvaluation Evaluate(BenchmarkResultDocument document)
		{
			if (document == null)
				return Undefined("The paired benchmark result is empty.");
			if (document.SchemaVersion < 1)
				return Undefined("The paired benchmark result has no supported schema version.");
			if (string.IsNullOrWhiteSpace(document.Benchmark))
				return Undefined("The paired benchmark result has no benchmark name.");
			if (string.IsNullOrWhiteSpace(document.Batch))
				return Undefined("The paired benchmark result has no batch identifier.",
					document.Benchmark);
			if (document.Candidate == null || document.Control == null)
				return Undefined("Both candidate and control summaries are required.",
					document.Benchmark, document.Batch);
			if (document.ExpectedMatchesPerArm <= 0)
				return Undefined("The paired benchmark result has no expected match count.",
					document.Benchmark, document.Batch);

			var matches = document.Matches ?? [];
			if (matches.Any(m => m == null ||
				(!Arm(m, "candidate") && !Arm(m, "control"))))
				return Undefined("Every match must belong to the candidate or control arm.",
					document.Benchmark, document.Batch);

			var candidate = matches.Where(m => Arm(m, "candidate")).ToList();
			var control = matches.Where(m => Arm(m, "control")).ToList();
			if (matches.Count != document.ExpectedMatchesPerArm * 2 ||
				candidate.Count != document.ExpectedMatchesPerArm ||
				control.Count != document.ExpectedMatchesPerArm)
				return Undefined("The serialized match plan is incomplete for one or both arms.",
					document.Benchmark, document.Batch);

			if (!TryIndex(candidate, out var candidateByScenario, out var duplicateReason) ||
				!TryIndex(control, out var controlByScenario, out duplicateReason))
				return Undefined(duplicateReason, document.Benchmark, document.Batch);

			if (!candidateByScenario.Keys.ToHashSet().SetEquals(controlByScenario.Keys))
				return Undefined("Candidate and control did not run the same repeat/scenario set.",
					document.Benchmark, document.Batch);

			if (document.Candidate.Played != document.ExpectedMatchesPerArm ||
				document.Control.Played != document.ExpectedMatchesPerArm)
				return Undefined("Arm summaries do not describe the complete paired match set.",
					document.Benchmark, document.Batch);

			var candidateWins = candidate.Count(Won);
			var controlWins = control.Count(Won);
			if (document.Candidate.Wins != candidateWins || document.Control.Wins != controlWins)
				return Undefined("Arm win totals disagree with the paired match rows.",
					document.Benchmark, document.Batch);

			var pairs = document.Paired ?? [];
			if (!TryIndex(pairs, out var pairedByScenario, out duplicateReason))
				return Undefined(duplicateReason, document.Benchmark, document.Batch);
			if (pairs.Count != document.ExpectedMatchesPerArm ||
				!pairedByScenario.Keys.ToHashSet().SetEquals(candidateByScenario.Keys))
				return Undefined("The paired rows are incomplete or name a different scenario set.",
					document.Benchmark, document.Batch);

			var evaluation = new PairedBenchmarkEvaluation
			{
				GeneratedUtc = DateTime.UtcNow,
				Benchmark = document.Benchmark,
				Batch = document.Batch,
				ExpectedMatchesPerArm = document.ExpectedMatchesPerArm,
				PairsCompared = candidate.Count,
				CandidateWins = candidateWins,
				ControlWins = controlWins
			};

			foreach (var key in candidateByScenario.Keys.OrderBy(k => k.Repeat).ThenBy(k => k.Scenario))
			{
				var candidateMatch = candidateByScenario[key];
				var controlMatch = controlByScenario[key];
				var pair = pairedByScenario[key];

				var invalid = ValidateScenario(document, candidateMatch, controlMatch, pair);
				if (invalid != null)
					return Undefined(invalid, document.Benchmark, document.Batch);

				var delta = candidateMatch.Fitness - controlMatch.Fitness;
				if (delta > DeltaTolerance)
					evaluation.CandidateFitnessPairs++;
				else if (delta < -DeltaTolerance)
					evaluation.ControlFitnessPairs++;
				else
					evaluation.TiedFitnessPairs++;

				evaluation.Scenarios.Add(new PairedScenarioEvaluation
				{
					Repeat = key.Repeat,
					Scenario = key.Scenario,
					Map = candidateMatch.Map,
					Faction = candidateMatch.Faction,
					BotFaction = candidateMatch.BotFaction,
					Seed = candidateMatch.Seed,
					CandidateOutcome = candidateMatch.Outcome,
					ControlOutcome = controlMatch.Outcome,
					CandidateFitness = candidateMatch.Fitness,
					ControlFitness = controlMatch.Fitness,
					FitnessDelta = Math.Round(delta, 4)
				});
			}

			evaluation.MedianPairedFitnessDelta = Math.Round(
				Median(evaluation.Scenarios.Select(s => s.FitnessDelta).ToArray()), 4);

			if (evaluation.CandidateWins > evaluation.ControlWins)
			{
				evaluation.Verdict = PromotionVerdicts.Promote;
				evaluation.Basis = "wins";
				evaluation.Reason =
					$"Candidate won {evaluation.CandidateWins} paired matches; control won {evaluation.ControlWins}.";
			}
			else if (evaluation.CandidateWins < evaluation.ControlWins)
			{
				evaluation.Verdict = PromotionVerdicts.Restore;
				evaluation.Basis = "wins";
				evaluation.Reason =
					$"Candidate won {evaluation.CandidateWins} paired matches; control won {evaluation.ControlWins}.";
			}
			else
			{
				evaluation.Basis = "paired-fitness";
				evaluation.Verdict = evaluation.MedianPairedFitnessDelta > DeltaTolerance
					? PromotionVerdicts.Promote
					: PromotionVerdicts.Restore;
				evaluation.Reason =
					$"Wins were tied at {evaluation.CandidateWins}; median paired fitness delta was " +
					$"{evaluation.MedianPairedFitnessDelta:+0.####;-0.####;0}.";
			}

			return evaluation;
		}

		public static PairedBenchmarkEvaluation Undefined(string reason, string benchmark = null,
			string batch = null) => new()
		{
			GeneratedUtc = DateTime.UtcNow,
			Benchmark = benchmark,
			Batch = batch,
			Verdict = PromotionVerdicts.Undefined,
			Reason = reason
		};

		public static void Write(string path, PairedBenchmarkEvaluation evaluation)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(evaluation, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}

		public static BenchmarkResultDocument ReadResult(string path) =>
			JsonSerializer.Deserialize<BenchmarkResultDocument>(File.ReadAllText(path), JsonOptions);

		public static void WriteResult(string path, BenchmarkResultDocument result)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(result, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}

		static string ValidateScenario(BenchmarkResultDocument document,
			BenchmarkMatchResult candidate, BenchmarkMatchResult control, BenchmarkPairResult pair)
		{
			if (!Complete(candidate) || !Complete(control))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} contains a failed or incomplete run.";
			if (!ValidOutcome(candidate.Outcome) || !ValidOutcome(control.Outcome))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} has an Undefined outcome.";
			if (!double.IsFinite(candidate.Fitness) || !double.IsFinite(control.Fitness) ||
				!double.IsFinite(pair.FitnessDelta))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} has non-finite fitness.";
			if (!SameConfiguration(candidate, control) || !SameConfiguration(candidate, pair))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} was not paired on the same configuration.";
			if (!SameOptional(document.Benchmark, candidate.Benchmark) ||
				!SameOptional(document.Benchmark, control.Benchmark) ||
				!SameOptional(document.Benchmark, pair.Benchmark))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} names a different benchmark.";
			if (!SameOptional(document.Batch, candidate.Batch) ||
				!SameOptional(document.Batch, control.Batch) ||
				!SameOptional(document.Batch, pair.Batch))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} names a different batch.";
			if (!string.Equals(pair.CandidateOutcome, candidate.Outcome, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(pair.ControlOutcome, control.Outcome, StringComparison.OrdinalIgnoreCase))
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} has inconsistent paired outcomes.";
			if (Math.Abs(pair.FitnessDelta - (candidate.Fitness - control.Fitness)) > DeltaTolerance)
				return $"Repeat {candidate.Repeat}, scenario {candidate.Scenario} has an inconsistent fitness delta.";

			return null;
		}

		static bool TryIndex(IReadOnlyList<BenchmarkMatchResult> matches,
			out Dictionary<ScenarioKey, BenchmarkMatchResult> indexed, out string reason)
		{
			indexed = [];
			foreach (var match in matches)
			{
				var key = new ScenarioKey(match.Repeat, match.Scenario);
				if (match.Repeat <= 0 || match.Scenario <= 0 || !indexed.TryAdd(key, match))
				{
					reason = "Each arm must contain one positive, unique repeat/scenario key.";
					return false;
				}
			}

			reason = null;
			return true;
		}

		static bool TryIndex(IReadOnlyList<BenchmarkPairResult> pairs,
			out Dictionary<ScenarioKey, BenchmarkPairResult> indexed, out string reason)
		{
			indexed = [];
			foreach (var pair in pairs)
			{
				if (pair == null)
				{
					reason = "The paired rows contain an empty entry.";
					return false;
				}

				var key = new ScenarioKey(pair.Repeat, pair.Scenario);
				if (pair.Repeat <= 0 || pair.Scenario <= 0 || !indexed.TryAdd(key, pair))
				{
					reason = "Paired rows must contain one positive, unique repeat/scenario key.";
					return false;
				}
			}

			reason = null;
			return true;
		}

		static bool Complete(BenchmarkMatchResult match) =>
			!string.IsNullOrWhiteSpace(match.RunId) &&
			match.DurationSeconds > 0 &&
			match.Succeeded &&
			string.IsNullOrWhiteSpace(match.Error) &&
			(!string.IsNullOrWhiteSpace(match.Status) &&
				(match.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
				match.Status.Equals("finished", StringComparison.OrdinalIgnoreCase) ||
				match.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)));

		static bool ValidOutcome(string outcome) =>
			string.Equals(outcome, "Won", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(outcome, "Lost", StringComparison.OrdinalIgnoreCase);

		static bool Won(BenchmarkMatchResult match) =>
			string.Equals(match.Outcome, "Won", StringComparison.OrdinalIgnoreCase);

		static bool Arm(BenchmarkMatchResult match, string arm) =>
			string.Equals(match?.Arm, arm, StringComparison.OrdinalIgnoreCase);

		static bool SameConfiguration(BenchmarkMatchResult left, BenchmarkMatchResult right) =>
			string.Equals(left.Map, right.Map, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(left.Faction, right.Faction, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(left.BotFaction, right.BotFaction, StringComparison.OrdinalIgnoreCase) &&
			left.Seed == right.Seed &&
			!string.IsNullOrWhiteSpace(left.Map) &&
			!string.IsNullOrWhiteSpace(left.Faction) &&
			!string.IsNullOrWhiteSpace(left.BotFaction);

		static bool SameConfiguration(BenchmarkMatchResult match, BenchmarkPairResult pair) =>
			string.Equals(match.Map, pair.Map, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(match.Faction, pair.Faction, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(match.BotFaction, pair.BotFaction, StringComparison.OrdinalIgnoreCase) &&
			match.Seed == pair.Seed;

		static bool SameOptional(string expected, string actual) =>
			string.IsNullOrWhiteSpace(actual) ||
			string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

		static double Median(double[] values)
		{
			var sorted = values.OrderBy(v => v).ToArray();
			var middle = sorted.Length / 2;
			return sorted.Length % 2 == 1
				? sorted[middle]
				: (sorted[middle - 1] + sorted[middle]) / 2d;
		}
	}
}
