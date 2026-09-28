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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Evidence
{
	/// <summary>One faction pairing a sequential set stratifies its fresh seeds across.</summary>
	public sealed class SequentialPairing
	{
		public string Faction { get; set; }
		public string BotFaction { get; set; }
	}

	/// <summary>The <c>sequential</c> block of a benchmark set in <c>scripts/benchmarks.json</c>.</summary>
	/// <remarks>
	/// A sequential set has no fixed list of matches. Its seeds are drawn fresh for every champion,
	/// played in stages, and judged by a sequential test on the games only one arm won. See
	/// <see cref="SequentialGate"/> for why.
	/// </remarks>
	public sealed class SequentialSetDefinition
	{
		public List<string> Maps { get; set; } = [];
		public List<SequentialPairing> Pairings { get; set; } = [];

		/// <summary>The first seed ever allocated. Every pool takes the next unused block.</summary>
		public int SeedBase { get; set; }

		/// <summary>Pairs added per stage. A multiple of the pairing count keeps every stage stratified.</summary>
		public int StagePairs { get; set; } = 16;

		/// <summary>Pairs after which an undecided test stops and the champion is kept.</summary>
		public int MaxPairs { get; set; } = 64;

		/// <summary>The false-promotion rate the test is designed for when a change does nothing.</summary>
		public double Alpha { get; set; } = 0.05;

		/// <summary>The miss rate the test is designed for at <see cref="DiscordantWinShare"/>.</summary>
		public double Beta { get; set; } = 0.2;

		/// <summary>
		/// The share of decisive pairs a worthwhile candidate wins: the alternative hypothesis.
		/// </summary>
		/// <remarks>
		/// A decisive pair is one game only one arm won. 0.75 means three of every four such games
		/// go the candidate's way, which is roughly fifteen points of win rate at this bot's level.
		/// </remarks>
		public double DiscordantWinShare { get; set; } = 0.75;

		public string Validate()
		{
			if (Maps == null || Maps.Count == 0 || Maps.Any(string.IsNullOrWhiteSpace))
				return "it names no maps";
			if (Pairings == null || Pairings.Count == 0 ||
				Pairings.Any(p => p == null || string.IsNullOrWhiteSpace(p.Faction) ||
					string.IsNullOrWhiteSpace(p.BotFaction)))
				return "it names no complete faction pairings";
			if (SeedBase <= 0)
				return "its seedBase must be positive";
			if (StagePairs <= 0 || MaxPairs < StagePairs || MaxPairs % StagePairs != 0)
				return "maxPairs must be a positive multiple of stagePairs";
			if ((long)SeedBase + MaxPairs * 10_000L > int.MaxValue)
				return "its seedBase leaves too little room for fresh pools";
			if (Alpha is <= 0 or >= 0.5 || Beta is <= 0 or >= 0.5)
				return "alpha and beta must lie between 0 and 0.5";
			if (DiscordantWinShare is <= 0.5 or >= 1)
				return "discordantWinShare must lie between 0.5 and 1";
			return null;
		}

		public static SequentialSetDefinition Parse(JsonElement element)
		{
			var definition = JsonSerializer.Deserialize<SequentialSetDefinition>(element.GetRawText(),
				SequentialGate.JsonOptions) ?? throw new InvalidDataException("The sequential block is empty.");
			var problem = definition.Validate();
			if (problem != null)
				throw new InvalidDataException("The sequential benchmark block is invalid: " + problem + ".");
			return definition;
		}
	}

	/// <summary>One match of a pool: a fresh seed on one map and faction pairing.</summary>
	public sealed class SequentialMatch
	{
		public int Scenario { get; set; }
		public string Map { get; set; }
		public string Faction { get; set; }
		public string BotFaction { get; set; }
		public int Seed { get; set; }
	}

	/// <summary>A champion game kept so later candidates in the same pool need not replay it.</summary>
	public sealed class SequentialChampionResult
	{
		public int Scenario { get; set; }
		public int Seed { get; set; }
		public string Outcome { get; set; }
		public string Status { get; set; }
		public double? Fitness { get; set; }
		public int? DurationSeconds { get; set; }
		public double? EarnedPerSecond { get; set; }
		public double? SpentPerSecond { get; set; }
		public double? Exchange { get; set; }
		public double? BuildingsKilled { get; set; }
		public string RulesFingerprint { get; set; }
		public string AssemblySha256 { get; set; }
		public string RunId { get; set; }
		public string Evidence { get; set; }
		public DateTime RecordedUtc { get; set; }

		public static SequentialChampionResult From(BenchmarkMatchResult match, string rules,
			string assemblySha256) => new()
		{
			Scenario = match.Scenario,
			Seed = match.Seed ?? 0,
			Outcome = match.Outcome,
			Status = match.Status,
			Fitness = match.Fitness,
			DurationSeconds = match.DurationSeconds,
			EarnedPerSecond = match.EarnedPerSecond,
			SpentPerSecond = match.SpentPerSecond,
			Exchange = match.Exchange,
			BuildingsKilled = match.BuildingsKilled,
			RulesFingerprint = rules,
			AssemblySha256 = assemblySha256,
			RunId = match.RunId,
			Evidence = match.Evidence,
			RecordedUtc = DateTime.UtcNow
		};

		/// <summary>True when a replay of this game came out exactly as it did the first time.</summary>
		public bool Reproduces(BenchmarkMatchResult replay) =>
			replay != null &&
			string.Equals(Outcome, replay.Outcome, StringComparison.OrdinalIgnoreCase) &&
			DurationSeconds == replay.DurationSeconds &&
			Same(Fitness, replay.Fitness) && Same(Exchange, replay.Exchange) &&
			Same(BuildingsKilled, replay.BuildingsKilled);

		static bool Same(double? left, double? right) =>
			left.HasValue == right.HasValue &&
			(!left.HasValue || Math.Abs(left.Value - right.Value) < 1e-9);

		public BenchmarkMatchResult ToMatch(SequentialMatch match, string benchmark, string batch) => new()
		{
			RunId = RunId,
			Evidence = Evidence,
			Arm = "control",
			Repeat = 1,
			Scenario = Scenario,
			Map = match.Map,
			Faction = match.Faction,
			BotFaction = match.BotFaction,
			Seed = Seed,
			Outcome = Outcome,
			Fitness = Fitness,
			EarnedPerSecond = EarnedPerSecond,
			SpentPerSecond = SpentPerSecond,
			Exchange = Exchange,
			BuildingsKilled = BuildingsKilled,
			DurationSeconds = DurationSeconds,
			Benchmark = benchmark,
			Batch = batch,
			Status = Status,
			Succeeded = true,
			Error = ""
		};
	}

	/// <summary>The fresh seeds one champion is judged on, and its cached games on them.</summary>
	public sealed class SequentialPool
	{
		public int Epoch { get; set; }
		public string ChampionFingerprint { get; set; }
		public string HarnessKey { get; set; }
		public DateTime CreatedUtc { get; set; }
		public string CreatedForRun { get; set; }

		/// <summary>
		/// False once a replayed champion game disagreed with its cached result. The cache is
		/// then never used again for this pool, and every pair is played in both arms.
		/// </summary>
		public bool Reproducible { get; set; } = true;

		public int Evaluations { get; set; }
		public int CanaryChecks { get; set; }
		public string CanaryFailure { get; set; }
		public List<SequentialMatch> Matches { get; set; } = [];
		public List<SequentialChampionResult> Champion { get; set; } = [];

		public SequentialChampionResult Cached(int scenario) =>
			Reproducible ? Champion.FirstOrDefault(result => result.Scenario == scenario) : null;
	}

	/// <summary>A pool that was retired, kept so the seeds it used are never drawn again.</summary>
	public sealed class SequentialRetiredPool
	{
		public int Epoch { get; set; }
		public string ChampionFingerprint { get; set; }
		public int FirstSeed { get; set; }
		public int LastSeed { get; set; }
		public int Evaluations { get; set; }
		public DateTime RetiredUtc { get; set; }
		public string Reason { get; set; }
	}

	/// <summary>Everything a sequential gate remembers between rounds, one file per bot and set.</summary>
	public sealed class SequentialGateState
	{
		public int SchemaVersion { get; set; } = 1;
		public string Benchmark { get; set; }
		public int NextSeed { get; set; }
		public SequentialPool Pool { get; set; }
		public List<SequentialRetiredPool> Retired { get; set; } = [];
	}

	/// <summary>One pair the test decides on.</summary>
	public readonly record struct SequentialOutcomePair(bool CandidateWon, bool ControlWon);

	/// <summary>Where a sequential test stands after the pairs played so far.</summary>
	public sealed class SequentialDecision
	{
		public const string Promote = "Promote";
		public const string Restore = "Restore";
		public const string Continue = "Continue";

		public string Verdict { get; set; }
		public int Pairs { get; set; }
		public int CandidateOnlyWins { get; set; }
		public int ControlOnlyWins { get; set; }
		public int BothWon { get; set; }
		public int NeitherWon { get; set; }
		public double LogLikelihoodRatio { get; set; }
		public double UpperBound { get; set; }
		public double LowerBound { get; set; }

		/// <summary>measured-better, measured-worse, inconclusive or undecided.</summary>
		public string Conclusion { get; set; }

		public string Reason { get; set; }
	}

	/// <summary>
	/// The promotion gate for a bot that already wins most of a fixed benchmark.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Eight pinned seeds stopped working as a gate once the champion had been selected on them. By
	/// 27 September the champion won all eight, a candidate could at best tie, a tie was broken by
	/// a fitness score that caps at 1.0 in a win, and twenty rounds running produced nothing. The
	/// same champion won only four of the seven random-seed fights it provably played. Reusing a
	/// small fixed set for selection had fitted the champion to that set.
	/// </para>
	/// <para>
	/// So every champion is judged on seeds it was never selected on. When a champion changes, a
	/// fresh block of seeds is drawn for it, stratified across the faction pairings. Candidates
	/// are played on that pool a stage at a time, the champion's games on it are cached because
	/// the engine replays a seed exactly, and one cached game is replayed per evaluation as a
	/// canary for that assumption. A promotion retires the pool, so the new champion's record is
	/// never the one it was chosen on.
	/// </para>
	/// <para>
	/// The decision is Wald's sequential probability ratio test on the decisive pairs, the games
	/// exactly one arm won: under "no difference" either arm is equally likely to take one, under
	/// the alternative the candidate takes <see cref="SequentialSetDefinition.DiscordantWinShare"/>
	/// of them. Pairs both arms won or both lost carry no information about which is better and
	/// are ignored, which is also why fitness plays no part: in a win it is saturated. The test is
	/// checked at the end of each stage and truncated at the pool size, both of which only make it
	/// more conservative than its nominal error rates.
	/// </para>
	/// </remarks>
	public static class SequentialGate
	{
		internal static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		/// <summary>The outcomes that count as a result rather than missing evidence.</summary>
		public static bool Decided(BenchmarkMatchResult match) =>
			match != null && match.Succeeded &&
			(string.Equals(match.Outcome, "Won", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(match.Outcome, "Lost", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(match.Outcome, "TimedOut", StringComparison.OrdinalIgnoreCase));

		public static bool Won(string outcome) =>
			string.Equals(outcome, "Won", StringComparison.OrdinalIgnoreCase);

		/// <summary>Draws the next pool of fresh seeds, stratified across maps and pairings.</summary>
		public static SequentialPool Draw(SequentialGateState state, SequentialSetDefinition definition,
			string championFingerprint, string harnessKey, string runId)
		{
			ArgumentNullException.ThrowIfNull(state);
			ArgumentNullException.ThrowIfNull(definition);

			var first = Math.Max(state.NextSeed, definition.SeedBase);
			var pool = new SequentialPool
			{
				Epoch = (state.Pool?.Epoch ?? state.Retired.Select(r => r.Epoch).DefaultIfEmpty(0).Max()) + 1,
				ChampionFingerprint = championFingerprint,
				HarnessKey = harnessKey,
				CreatedUtc = DateTime.UtcNow,
				CreatedForRun = runId
			};

			for (var i = 0; i < definition.MaxPairs; i++)
			{
				var pairing = definition.Pairings[i % definition.Pairings.Count];
				var map = definition.Maps[i / definition.Pairings.Count % definition.Maps.Count];
				pool.Matches.Add(new SequentialMatch
				{
					Scenario = i + 1,
					Map = map,
					Faction = pairing.Faction,
					BotFaction = pairing.BotFaction,
					Seed = first + i
				});
			}

			state.NextSeed = first + definition.MaxPairs;
			return pool;
		}

		/// <summary>
		/// The pool to judge this champion on: the current one if it is still this champion's,
		/// otherwise a freshly drawn one with the old one retired.
		/// </summary>
		/// <remarks>
		/// Only a new champion retires a pool, because only selection spends seeds: the champion
		/// was chosen on the pool it beat, so its record there is biased upwards and must never be
		/// the one it is judged against. A harness change - engine, rules, difficulty, time limit
		/// or scoring - leaves the seeds as fresh as they were but makes the cached games stale,
		/// so it clears the cache and keeps the seeds.
		/// </remarks>
		/// <returns>True when a new pool was drawn.</returns>
		public static bool EnsurePool(SequentialGateState state, SequentialSetDefinition definition,
			string championFingerprint, string harnessKey, string runId, out string note)
		{
			note = null;
			var pool = state.Pool;
			if (pool != null &&
				string.Equals(pool.ChampionFingerprint, championFingerprint, StringComparison.Ordinal) &&
				pool.Matches.Count == definition.MaxPairs &&
				SameDesign(pool, definition))
			{
				if (!string.Equals(pool.HarnessKey, harnessKey, StringComparison.Ordinal))
				{
					note = $"The harness changed, so the {pool.Champion.Count} cached champion game(s) of pool " +
						$"{pool.Epoch} were discarded; its seeds are kept.";
					pool.HarnessKey = harnessKey;
					pool.Champion.Clear();
					pool.Reproducible = true;
					pool.CanaryFailure = null;
				}

				return false;
			}

			if (pool != null)
			{
				var reason = !string.Equals(pool.ChampionFingerprint, championFingerprint, StringComparison.Ordinal)
					? "the champion changed"
					: "the set's pool design changed";
				note = $"Pool {pool.Epoch} was retired because {reason}.";
				state.Retired.Add(new SequentialRetiredPool
				{
					Epoch = pool.Epoch,
					ChampionFingerprint = pool.ChampionFingerprint,
					FirstSeed = pool.Matches.Count > 0 ? pool.Matches.Min(m => m.Seed) : 0,
					LastSeed = pool.Matches.Count > 0 ? pool.Matches.Max(m => m.Seed) : 0,
					Evaluations = pool.Evaluations,
					RetiredUtc = DateTime.UtcNow,
					Reason = reason
				});
			}

			state.Pool = Draw(state, definition, championFingerprint, harnessKey, runId);
			return true;
		}

		static bool SameDesign(SequentialPool pool, SequentialSetDefinition definition)
		{
			for (var i = 0; i < pool.Matches.Count; i++)
			{
				var pairing = definition.Pairings[i % definition.Pairings.Count];
				var map = definition.Maps[i / definition.Pairings.Count % definition.Maps.Count];
				var match = pool.Matches[i];
				if (match.Scenario != i + 1 ||
					!string.Equals(match.Map, map, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(match.Faction, pairing.Faction, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(match.BotFaction, pairing.BotFaction, StringComparison.OrdinalIgnoreCase))
					return false;
			}

			return true;
		}

		/// <summary>The stage's slice of the pool: pairs <c>stage * StagePairs + 1</c> onwards.</summary>
		public static IReadOnlyList<SequentialMatch> Stage(SequentialPool pool, SequentialSetDefinition definition,
			int stage) =>
			pool.Matches.Skip(stage * definition.StagePairs).Take(definition.StagePairs).ToList();

		public static int Stages(SequentialSetDefinition definition) => definition.MaxPairs / definition.StagePairs;

		/// <summary>Wald's test after the pairs played so far.</summary>
		/// <param name="final">True when no further stage can be played, so an undecided test ends.</param>
		public static SequentialDecision Decide(IEnumerable<SequentialOutcomePair> pairs,
			SequentialSetDefinition definition, bool final)
		{
			var decision = new SequentialDecision
			{
				UpperBound = Math.Log((1 - definition.Beta) / definition.Alpha),
				LowerBound = Math.Log(definition.Beta / (1 - definition.Alpha))
			};

			foreach (var pair in pairs)
			{
				decision.Pairs++;
				if (pair.CandidateWon && !pair.ControlWon)
					decision.CandidateOnlyWins++;
				else if (pair.ControlWon && !pair.CandidateWon)
					decision.ControlOnlyWins++;
				else if (pair.CandidateWon)
					decision.BothWon++;
				else
					decision.NeitherWon++;
			}

			var share = definition.DiscordantWinShare;
			decision.LogLikelihoodRatio = decision.CandidateOnlyWins * Math.Log(2 * share) +
				decision.ControlOnlyWins * Math.Log(2 * (1 - share));

			var tally = string.Create(CultureInfo.InvariantCulture,
				$"over {decision.Pairs} fresh paired game(s) the candidate won {decision.CandidateOnlyWins} " +
				$"the champion lost and lost {decision.ControlOnlyWins} the champion won " +
				$"(both won {decision.BothWon}, neither {decision.NeitherWon})");
			var ratio = string.Create(CultureInfo.InvariantCulture,
				$"log-likelihood ratio {decision.LogLikelihoodRatio:0.00}, bounds " +
				$"{decision.LowerBound:0.00} and {decision.UpperBound:0.00}");

			if (decision.LogLikelihoodRatio >= decision.UpperBound)
			{
				decision.Verdict = SequentialDecision.Promote;
				decision.Conclusion = "measured-better";
				decision.Reason = $"Promote: {tally}; the sequential test crossed its upper bound ({ratio}).";
			}
			else if (decision.LogLikelihoodRatio <= decision.LowerBound)
			{
				decision.Verdict = SequentialDecision.Restore;
				decision.Conclusion = decision.ControlOnlyWins > decision.CandidateOnlyWins
					? "measured-worse" : "not-better";
				decision.Reason = $"Restore: {tally}; the sequential test crossed its lower bound ({ratio}), " +
					(decision.ControlOnlyWins > decision.CandidateOnlyWins
						? "so the change measured worse."
						: "so the change is not the improvement the test looks for.");
			}
			else if (final)
			{
				decision.Verdict = SequentialDecision.Restore;
				decision.Conclusion = "inconclusive";
				decision.Reason = $"Restore: {tally}; the test was still undecided when the pool ran out " +
					$"({ratio}). Inconclusive is not refuted: the effect, if any, is smaller than this gate can see.";
			}
			else
			{
				decision.Verdict = SequentialDecision.Continue;
				decision.Conclusion = "undecided";
				decision.Reason = $"Continue: {tally}; {ratio}.";
			}

			return decision;
		}

		/// <summary>A stable key for everything besides the bot that decides how a seed plays out.</summary>
		public static string HarnessKey(IEnumerable<string> parts)
		{
			var text = string.Join("\n", parts.Select(part => part ?? ""));
			return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
				Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
		}

		public static SequentialGateState Read(string path, string benchmark)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return new SequentialGateState { Benchmark = benchmark };

			var state = JsonSerializer.Deserialize<SequentialGateState>(File.ReadAllText(path), JsonOptions)
				?? new SequentialGateState();
			state.Benchmark ??= benchmark;
			state.Retired ??= [];
			if (state.Pool != null)
			{
				state.Pool.Matches ??= [];
				state.Pool.Champion ??= [];
			}

			return state;
		}

		public static void Write(string path, SequentialGateState state)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			var temporary = full + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions) + "\n",
				new UTF8Encoding(false));
			File.Move(temporary, full, true);
		}

		/// <summary>The matches file <c>benchmark-bot.ps1 -MatchesFile</c> reads.</summary>
		public static void WriteMatches(string path, IEnumerable<SequentialMatch> matches)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(new { matches = matches.ToList() }, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}

		/// <summary>One scenario both arms have a row for, in the form the test and the report use.</summary>
		public sealed class Pair
		{
			public SequentialMatch Match { get; init; }
			public BenchmarkMatchResult Candidate { get; init; }
			public BenchmarkMatchResult Control { get; init; }
			public bool Cached { get; init; }

			/// <summary>A candidate game that failed on every attempt counts as a game it did not win.</summary>
			public bool CandidateWon => Decided(Candidate) && Won(Candidate.Outcome);
			public bool ControlWon => Won(Control.Outcome);

			public string CandidateOutcome => Decided(Candidate) ? Candidate.Outcome : "Failed";
		}

		/// <summary>
		/// Pairs the candidate's games with the champion's, dropping only pairs the champion could
		/// not complete. A candidate game that failed is kept as a loss: a change that crashes the
		/// game must not be excused the very games it broke.
		/// </summary>
		public static List<Pair> Pairs(SequentialPool pool, IEnumerable<BenchmarkMatchResult> candidate,
			IReadOnlyDictionary<int, BenchmarkMatchResult> control, ISet<int> cachedScenarios, out int dropped)
		{
			dropped = 0;
			var pairs = new List<Pair>();
			foreach (var row in candidate.Where(row => row != null).OrderBy(row => row.Scenario))
			{
				var match = pool.Matches.FirstOrDefault(m => m.Scenario == row.Scenario);
				if (match == null)
					throw new InvalidDataException($"Candidate scenario {row.Scenario} is not in pool {pool.Epoch}.");
				if (!control.TryGetValue(row.Scenario, out var champion) || !Decided(champion))
				{
					dropped++;
					continue;
				}

				pairs.Add(new Pair
				{
					Match = match,
					Candidate = row,
					Control = champion,
					Cached = cachedScenarios?.Contains(row.Scenario) == true
				});
			}

			return pairs;
		}

		public static SequentialDecision Decide(IEnumerable<Pair> pairs, SequentialSetDefinition definition,
			bool final) =>
			Decide(pairs.Select(pair => new SequentialOutcomePair(pair.CandidateWon, pair.ControlWon)),
				definition, final);

		/// <summary>
		/// The combined benchmark result and the promotion evaluation for a finished sequential
		/// sitting, in the same documents a pinned set produces.
		/// </summary>
		public static (BenchmarkResultDocument Result, PairedBenchmarkEvaluation Evaluation) Compose(
			string benchmark, string batch, string difficulty, int maxGameSeconds,
			SequentialSetDefinition definition, SequentialPool pool, IReadOnlyList<Pair> pairs, int dropped,
			int stagesPlayed, SequentialDecision decision, int? canaryScenario, bool? canaryReproduced, string note)
		{
			var candidateRows = pairs.Select(pair => Arm(pair.Candidate, "candidate", benchmark, batch)).ToList();
			var controlRows = pairs.Select(pair => Arm(pair.Control, "control", benchmark, batch)).ToList();

			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				GeneratedUtc = DateTime.UtcNow,
				Benchmark = benchmark,
				Batch = batch,
				Difficulty = difficulty,
				MaxGameSeconds = maxGameSeconds,
				ExpectedMatchesPerArm = pairs.Count,
				Candidate = Summary("candidate", candidateRows),
				Control = Summary("control", controlRows),
				Matches = [.. candidateRows, .. controlRows]
			};

			foreach (var pair in pairs)
				result.Paired.Add(new BenchmarkPairResult
				{
					Repeat = 1,
					Scenario = pair.Match.Scenario,
					Map = pair.Match.Map,
					Faction = pair.Match.Faction,
					BotFaction = pair.Match.BotFaction,
					Seed = pair.Match.Seed,
					CandidateOutcome = pair.CandidateOutcome,
					ControlOutcome = pair.Control.Outcome,
					FitnessDelta = Delta(pair.Candidate.Fitness, pair.Control.Fitness, 4),
					EarnedPerSecondDelta = Delta(pair.Candidate.EarnedPerSecond, pair.Control.EarnedPerSecond, 3),
					SpentPerSecondDelta = Delta(pair.Candidate.SpentPerSecond, pair.Control.SpentPerSecond, 3),
					ExchangeDelta = Delta(pair.Candidate.Exchange, pair.Control.Exchange, 4),
					BuildingsKilledDelta = Delta(pair.Candidate.BuildingsKilled, pair.Control.BuildingsKilled, 3),
					Benchmark = benchmark,
					Batch = batch
				});

			var evaluation = new PairedBenchmarkEvaluation
			{
				GeneratedUtc = DateTime.UtcNow,
				Benchmark = benchmark,
				Batch = batch,
				Verdict = decision.Verdict == SequentialDecision.Promote
					? PromotionVerdicts.Promote
					: PromotionVerdicts.Restore,
				Basis = "sequential",
				Reason = decision.Reason,
				PairsCompared = pairs.Count,
				ExpectedMatchesPerArm = pairs.Count,
				DroppedPairs = dropped,
				CandidateWins = pairs.Count(pair => pair.CandidateWon),
				ControlWins = pairs.Count(pair => pair.ControlWon),
				Sequential = new SequentialEvaluation
				{
					PoolEpoch = pool.Epoch,
					FirstSeed = pool.Matches.Count > 0 ? pool.Matches.Min(m => m.Seed) : 0,
					LastSeed = pool.Matches.Count > 0 ? pool.Matches.Max(m => m.Seed) : 0,
					StagesPlayed = stagesPlayed,
					StagePairs = definition.StagePairs,
					MaxPairs = definition.MaxPairs,
					Alpha = definition.Alpha,
					Beta = definition.Beta,
					DiscordantWinShare = definition.DiscordantWinShare,
					CandidateOnlyWins = decision.CandidateOnlyWins,
					ControlOnlyWins = decision.ControlOnlyWins,
					BothWon = decision.BothWon,
					NeitherWon = decision.NeitherWon,
					LogLikelihoodRatio = Math.Round(decision.LogLikelihoodRatio, 4),
					UpperBound = Math.Round(decision.UpperBound, 4),
					LowerBound = Math.Round(decision.LowerBound, 4),
					Conclusion = decision.Conclusion,
					CachedControlPairs = pairs.Count(pair => pair.Cached),
					CandidateFailures = pairs.Count(pair => !Decided(pair.Candidate)),
					CandidateTimeouts = pairs.Count(pair =>
						string.Equals(pair.CandidateOutcome, "TimedOut", StringComparison.OrdinalIgnoreCase)),
					CanaryScenario = canaryScenario,
					CanaryReproduced = canaryReproduced,
					Note = note
				}
			};

			var deltas = new List<double>();
			foreach (var pair in pairs)
			{
				var candidateFitness = pair.Candidate.Fitness ?? 0;
				var controlFitness = pair.Control.Fitness ?? 0;
				var delta = candidateFitness - controlFitness;
				if (pair.Candidate.Fitness.HasValue && pair.Control.Fitness.HasValue)
				{
					deltas.Add(delta);
					if (delta > 0.00015)
						evaluation.CandidateFitnessPairs++;
					else if (delta < -0.00015)
						evaluation.ControlFitnessPairs++;
					else
						evaluation.TiedFitnessPairs++;
				}

				evaluation.Scenarios.Add(new PairedScenarioEvaluation
				{
					Repeat = 1,
					Scenario = pair.Match.Scenario,
					Map = pair.Match.Map,
					Faction = pair.Match.Faction,
					BotFaction = pair.Match.BotFaction,
					Seed = pair.Match.Seed,
					CandidateOutcome = pair.CandidateOutcome,
					ControlOutcome = pair.Control.Outcome,
					CandidateFitness = candidateFitness,
					ControlFitness = controlFitness,
					FitnessDelta = Math.Round(delta, 4)
				});
			}

			if (deltas.Count > 0)
			{
				var sorted = deltas.OrderBy(value => value).ToArray();
				var middle = sorted.Length / 2;
				evaluation.MedianPairedFitnessDelta = Math.Round(sorted.Length % 2 == 1
					? sorted[middle]
					: (sorted[middle - 1] + sorted[middle]) / 2d, 4);
			}

			return (result, evaluation);
		}

		static BenchmarkMatchResult Arm(BenchmarkMatchResult source, string arm, string benchmark, string batch) => new()
		{
			RunId = source.RunId,
			Evidence = source.Evidence,
			Arm = arm,
			Repeat = 1,
			Scenario = source.Scenario,
			Map = source.Map,
			Faction = source.Faction,
			BotFaction = source.BotFaction,
			Seed = source.Seed,
			Outcome = Decided(source) ? source.Outcome : "Failed",
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

		static BenchmarkArmResult Summary(string arm, IReadOnlyList<BenchmarkMatchResult> rows)
		{
			var played = rows.Where(Decided).ToList();
			return new BenchmarkArmResult
			{
				Arm = arm,
				Wins = played.Count(row => Won(row.Outcome)),
				Played = played.Count,
				TimedOut = played.Count(row => string.Equals(row.Outcome, "TimedOut", StringComparison.OrdinalIgnoreCase)),
				MedianFitness = Median(played.Select(row => row.Fitness)),
				MedianEarnedPerSecond = Median(played.Select(row => row.EarnedPerSecond)),
				MedianSpentPerSecond = Median(played.Select(row => row.SpentPerSecond)),
				MedianExchange = Median(played.Select(row => row.Exchange)),
				MedianBuildingsKilled = Median(played.Select(row => row.BuildingsKilled))
			};
		}

		static double? Median(IEnumerable<double?> values)
		{
			var sorted = values.Where(value => value.HasValue && double.IsFinite(value.Value))
				.Select(value => value.Value).OrderBy(value => value).ToArray();
			if (sorted.Length == 0)
				return null;
			var middle = sorted.Length / 2;
			return Math.Round(sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d, 4);
		}

		static double? Delta(double? candidate, double? control, int digits) =>
			candidate.HasValue && control.HasValue
				? Math.Round(candidate.Value - control.Value, digits)
				: null;
	}
}
