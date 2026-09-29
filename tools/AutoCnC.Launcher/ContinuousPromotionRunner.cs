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
using AutoCnC.Evidence;

namespace AutoCnC.Launcher
{
	public enum ContinuousEvaluationArm
	{
		Candidate,
		Control
	}

	public sealed class ContinuousScriptPlan
	{
		public string Title { get; init; }
		public string ScriptPath { get; init; }
		public IReadOnlyList<string> Arguments { get; init; } = [];

		/// <summary>The arm a benchmark step plays, so its result can be captured; null otherwise.</summary>
		public ContinuousEvaluationArm? Arm { get; init; }

		/// <summary>True for one stage of a sequential gate, whose result must be captured.</summary>
		public bool Sequential { get; init; }

		/// <summary>The zero-based stage a sequential step belongs to.</summary>
		public int Stage { get; init; }

		/// <summary>Where the step writes its machine-readable result.</summary>
		public string ResultPath { get; init; }

		/// <summary>The fresh-seed matches a sequential step plays.</summary>
		public IReadOnlyList<SequentialMatch> Matches { get; init; } = [];
	}

	public sealed class ContinuousEvaluationPlan
	{
		public TrainingRun Run { get; init; }
		public string Batch { get; init; }
		public string Benchmark { get; init; }
		public string Difficulty { get; init; }
		public int MaxGameSeconds { get; init; }
		public string CandidateProjectPath { get; init; }
		public string ControlProjectPath { get; init; }
		public string CandidateOutputDirectory { get; init; }
		public string ControlOutputDirectory { get; init; }
		public string CandidateBuildResultPath { get; init; }
		public string ControlBuildResultPath { get; init; }
		public string CandidateAssemblyPath { get; set; }
		public string ControlAssemblyPath { get; set; }
		public string CandidateFingerprint { get; init; }
		public string ChampionFingerprint { get; init; }
		public IReadOnlyList<ContinuousBenchmarkScenario> Scenarios { get; init; } = [];
		public string CandidateAssemblySha256 { get; set; }
		public string ControlAssemblySha256 { get; set; }
		public bool NoChanges { get; init; }

		/// <summary>Set for a sequential set: fresh seeds, stages and Wald's test. Null for a pinned set.</summary>
		public SequentialSetDefinition Sequential { get; init; }

		/// <summary>Where the sequential gate keeps its pool and cached champion games.</summary>
		public string GateStatePath { get; init; }

		/// <summary>Anything worth reporting about the pool, such as a new one being drawn.</summary>
		public string Note { get; set; }

		/// <summary>Set when a step found something the caller should report at once, then cleared by it.</summary>
		public string Warning { get; set; }

		internal ContinuousEvaluationProgress Progress { get; } = new();
	}

	/// <summary>How far one evaluation has got, between the steps its caller runs.</summary>
	internal sealed class ContinuousEvaluationProgress
	{
		public int PinnedArmsBenchmarked { get; set; }
		public int Steps { get; set; }
		public int Stage { get; set; }
		public bool CandidateStageDone { get; set; }
		public bool ControlStageDone { get; set; }
		public bool Finished { get; set; }
		public int StagesPlayed { get; set; }
		public List<BenchmarkMatchResult> Candidate { get; } = [];
		public Dictionary<int, BenchmarkMatchResult> FreshControl { get; } = [];
		public HashSet<int> CachedScenarios { get; } = [];
		public int? CanaryScenario { get; set; }
		public bool? CanaryReproduced { get; set; }
		public SequentialDecision Decision { get; set; }
		public List<string> CandidateBatches { get; } = [];
		public List<string> ControlBatches { get; } = [];
	}

	public sealed class ContinuousBenchmarkScenario
	{
		public int Repeat { get; init; }
		public int Scenario { get; init; }
		public string Map { get; init; }
		public string Faction { get; init; }
		public string BotFaction { get; init; }
		public int Seed { get; init; }
	}

	public sealed class ContinuousEvaluationCompletion
	{
		public PairedBenchmarkEvaluation Evaluation { get; init; }
		public bool RequiresReevaluation { get; init; }
		public string InvalidationReason { get; init; }
	}

	public sealed class ContinuousArmBuildResult
	{
		public int SchemaVersion { get; set; }
		public string TargetPath { get; set; }
	}

	/// <summary>
	/// Materializes immutable arms, captures their built assemblies and evaluates paired results.
	/// </summary>
	public sealed class ContinuousPromotionRunner
	{
		public const string DefaultBenchmark = "hard-16-9";
		public const string DefaultDifficulty = "Hard";

		/// <summary>The sequential fresh-seed gate the unattended loop uses by default.</summary>
		public const string SequentialBenchmark = "hard-16-9-fresh";

		/// <summary>
		/// Benchmark games played at once when nobody says otherwise: half the logical processors,
		/// at most four. A headless game is one busy thread; the other half stays free for the
		/// evidence tool, which summarises each game while the next ones play.
		/// </summary>
		public static int DefaultParallel => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

		/// <summary>Benchmark games to play at once. Zero or less means <see cref="DefaultParallel"/>.</summary>
		public int Parallel { get; set; }

		int EffectiveParallel => Parallel > 0 ? Parallel : DefaultParallel;

		public static void ValidateSelection(RepoLayout repo, string benchmark, string difficulty) =>
			ValidateBenchmarkSelection(repo, benchmark, difficulty);

		static readonly JsonSerializerOptions BuildJsonOptions = new()
		{
			PropertyNameCaseInsensitive = true
		};

		public void CaptureChampion(TrainingRun run)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			if (!File.Exists(run.SnapshotManifestPath))
				WorkspaceSnapshot.Capture(run);
		}

		public ContinuousEvaluationPlan PrepareEvaluation(RepoLayout repo, TrainingRun run,
			string benchmark = DefaultBenchmark, string difficulty = DefaultDifficulty)
		{
			if (repo == null)
				throw new ArgumentNullException(nameof(repo));
			if (run == null)
				throw new ArgumentNullException(nameof(run));
			using var workspaceMutation = TrainingRun.AcquireWorkspaceMutation(run);
			using var mutation = TrainingRun.AcquireMutation(run);
			run = mutation.Run;
			if (run.IsBusy && !ProcessOwnership.IsCurrent(run.Manifest.Owner))
				throw new InvalidOperationException(
					"Another launcher still owns this continuous experiment.");
			if (!File.Exists(repo.BuildExperimentArmScript) ||
				!File.Exists(repo.BenchmarkBotScript))
				throw new InvalidOperationException(
					"The build and benchmark scripts are required for paired evaluation.");
			if (!run.IsEditable)
				throw new InvalidOperationException(
					"The continuous candidate no longer has an editable bot project.");
			var selection = ValidateBenchmarkSelection(repo, benchmark, difficulty);
			benchmark = selection.Benchmark;
			difficulty = selection.Difficulty;

			var relativeProject = Path.GetRelativePath(run.Manifest.BotDirectory,
				run.Manifest.BotProject);
			if (Path.IsPathRooted(relativeProject) || relativeProject == ".." ||
				relativeProject.StartsWith(".." + Path.DirectorySeparatorChar,
					StringComparison.Ordinal))
				throw new InvalidDataException(
					"The bot project must stay inside the workspace captured for evaluation.");

			var championFingerprint = WorkspaceSnapshot.MaterializeImmutableChampion(run,
				run.ControlSourceDirectory, run.ControlSourceManifestPath);
			var recordedChampion = run.Manifest.Experiment?.ChampionFingerprint;
			if (!string.IsNullOrWhiteSpace(recordedChampion) &&
				!string.Equals(championFingerprint, recordedChampion, StringComparison.Ordinal))
				throw new InvalidDataException(
					"The materialized champion does not match the pre-agent workspace snapshot.");

			var candidateFingerprint = WorkspaceSnapshot.CaptureImmutableCurrent(run,
				run.CandidateSourceDirectory, run.CandidateSourceManifestPath);
			if (string.Equals(candidateFingerprint, championFingerprint, StringComparison.Ordinal))
			{
				PrepareDirectory(run.CandidateArtifactDirectory);
				PrepareDirectory(run.ControlArtifactDirectory);
				PrepareDirectory(run.CandidateBenchmarkRunsDirectory);
				PrepareDirectory(run.ControlBenchmarkRunsDirectory);
				DeleteIfExists(run.CandidateBenchmarkResultPath);
				DeleteIfExists(run.ControlBenchmarkResultPath);
				DeleteIfExists(run.BenchmarkResultPath);
				DeleteIfExists(run.PromotionEvaluationPath);
				run.BeginContinuousEvaluation(candidateFingerprint, championFingerprint,
					benchmark, difficulty);
				var noChangeExperiment = run.Manifest.Experiment;
				Record(run, new PairedBenchmarkEvaluation
				{
					GeneratedUtc = DateTime.UtcNow,
					Benchmark = benchmark,
					Batch = $"promotion-{noChangeExperiment.Id}-{noChangeExperiment.EvaluationAttempt:D2}-no-change",
					Verdict = PromotionVerdicts.Promote,
					Basis = "No source changes",
					Reason = "The agent made no source changes; the existing champion remains active.",
					ExpectedMatchesPerArm = selection.Scenarios.Count
				});
				run.MarkContinuousPromoted();
				return new ContinuousEvaluationPlan
				{
					Run = run,
					Benchmark = benchmark,
					Difficulty = difficulty,
					MaxGameSeconds = selection.MaxGameSeconds,
					CandidateFingerprint = candidateFingerprint,
					ChampionFingerprint = championFingerprint,
					Scenarios = selection.Scenarios,
					NoChanges = true
				};
			}
			var candidateProject = Under(run.CandidateSourceDirectory, relativeProject);
			var controlProject = Under(run.ControlSourceDirectory, relativeProject);
			if (!File.Exists(candidateProject) || !File.Exists(controlProject))
				throw new InvalidDataException(
					"The immutable candidate and control copies do not contain the bot project.");

			PrepareDirectory(run.CandidateArtifactDirectory);
			PrepareDirectory(run.ControlArtifactDirectory);
			PrepareDirectory(run.CandidateBenchmarkRunsDirectory);
			PrepareDirectory(run.ControlBenchmarkRunsDirectory);
			DeleteIfExists(run.CandidateBenchmarkResultPath);
			DeleteIfExists(run.ControlBenchmarkResultPath);
			DeleteIfExists(run.BenchmarkResultPath);
			foreach (var stageFile in Directory.EnumerateFiles(run.ExperimentDirectory, "*-stage-*.json"))
				DeleteIfExists(stageFile);
			var candidateBuildResult = Path.Combine(
				run.ExperimentDirectory, "candidate-build.json");
			var controlBuildResult = Path.Combine(
				run.ExperimentDirectory, "control-build.json");
			DeleteIfExists(candidateBuildResult);
			DeleteIfExists(controlBuildResult);

			var sharedBotDirectory = Path.Combine(repo.EngineBinDir, "bots");
			if (string.Equals(Path.GetFullPath(run.CandidateArtifactDirectory),
					Path.GetFullPath(run.ControlArtifactDirectory),
					StringComparison.OrdinalIgnoreCase) ||
				IsUnder(sharedBotDirectory, run.CandidateArtifactDirectory) ||
				IsUnder(sharedBotDirectory, run.ControlArtifactDirectory))
				throw new InvalidDataException(
					"Candidate and control must use distinct immutable paths outside engine/bin/bots.");

			run.BeginContinuousEvaluation(candidateFingerprint, championFingerprint,
				benchmark, difficulty);
			var experiment = run.Manifest.Experiment;

			// A sequential set judges this champion on seeds it was never selected on. The pool
			// is drawn the first time a champion is evaluated and kept, with its cached games,
			// until a promotion retires it.
			string gateStatePath = null;
			string note = null;
			if (selection.Sequential != null)
			{
				gateStatePath = GateStatePath(run, benchmark);
				var state = SequentialGate.Read(gateStatePath, benchmark);
				SequentialGate.EnsurePool(state, selection.Sequential, championFingerprint,
					HarnessKey(repo, run, difficulty, selection), run.Manifest.Id, out note);
				SequentialGate.Write(gateStatePath, state);
			}

			return new ContinuousEvaluationPlan
			{
				Run = run,
				Batch = $"promotion-{experiment.Id}-{experiment.EvaluationAttempt:D2}",
				Benchmark = benchmark,
				Difficulty = difficulty,
				MaxGameSeconds = selection.MaxGameSeconds,
				CandidateProjectPath = candidateProject,
				ControlProjectPath = controlProject,
				CandidateOutputDirectory = run.CandidateArtifactDirectory,
				ControlOutputDirectory = run.ControlArtifactDirectory,
				CandidateBuildResultPath = candidateBuildResult,
				ControlBuildResultPath = controlBuildResult,
				CandidateFingerprint = candidateFingerprint,
				ChampionFingerprint = championFingerprint,
				Scenarios = selection.Scenarios,
				Sequential = selection.Sequential,
				GateStatePath = gateStatePath,
				Note = note
			};
		}

		public ContinuousScriptPlan BuildArm(RepoLayout repo, ContinuousEvaluationPlan plan,
			ContinuousEvaluationArm arm)
		{
			var project = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateProjectPath
				: plan.ControlProjectPath;
			var output = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateOutputDirectory
				: plan.ControlOutputDirectory;
			var result = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateBuildResultPath
				: plan.ControlBuildResultPath;
			return new ContinuousScriptPlan
			{
				Title = $"Building immutable {arm.ToString().ToLowerInvariant()} arm",
				ScriptPath = repo.BuildExperimentArmScript,
				Arguments =
				[
					"-Project", project,
					"-OutputDirectory", output,
					"-AutoCnCPath", repo.Root,
					"-ResultPath", result
				]
			};
		}

		public void CaptureBuiltArm(TrainingRun run, ContinuousEvaluationPlan plan,
			ContinuousEvaluationArm arm)
		{
			run = plan?.Run ?? run;
			var resultPath = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateBuildResultPath
				: plan.ControlBuildResultPath;
			var outputDirectory = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateOutputDirectory
				: plan.ControlOutputDirectory;
			if (!File.Exists(resultPath))
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} build did not report its evaluated TargetPath.");

			var result = JsonSerializer.Deserialize<ContinuousArmBuildResult>(
				File.ReadAllText(resultPath), BuildJsonOptions);
			if (result == null)
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} build result is null.");
			if (result.SchemaVersion < 1 ||
				string.IsNullOrWhiteSpace(result.TargetPath))
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} build reported no evaluated TargetPath.");

			var destination = Path.GetFullPath(result.TargetPath);
			if (!IsUnder(outputDirectory, destination))
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} TargetPath escaped its isolated output directory.");
			if (!File.Exists(destination))
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} build did not produce " +
					$"'{destination}'.");

			MakeReadOnly(Path.GetDirectoryName(destination));
			var sha256 = BotWorkspace.Sha256(destination);

			if (arm == ContinuousEvaluationArm.Candidate)
			{
				plan.CandidateAssemblyPath = destination;
				plan.CandidateAssemblySha256 = sha256;
			}
			else
			{
				plan.ControlAssemblyPath = destination;
				plan.ControlAssemblySha256 = sha256;
			}

			EnsureImmutableSources(plan);
			if (arm == ContinuousEvaluationArm.Control)
			{
				EnsureDistinctArtifacts(plan);
				run.RecordContinuousArmArtifacts(plan.CandidateAssemblyPath,
					plan.CandidateAssemblySha256, plan.ControlAssemblyPath,
					plan.ControlAssemblySha256);
			}
		}

		public ContinuousScriptPlan BenchmarkArm(RepoLayout repo, TrainingRun run,
			ContinuousEvaluationPlan plan, ContinuousEvaluationArm arm)
		{
			run = plan?.Run ?? run;
			EnsureReadyForBenchmark(run, plan);
			var candidate = arm == ContinuousEvaluationArm.Candidate;
			var arguments = new List<string>
			{
				"-BattleBot", candidate
					? plan.CandidateAssemblyPath
					: plan.ControlAssemblyPath,
				"-OutputDirectory", candidate
					? run.CandidateBenchmarkRunsDirectory
					: run.ControlBenchmarkRunsDirectory,
				"-ResultPath", candidate
					? run.CandidateBenchmarkResultPath
					: run.ControlBenchmarkResultPath,
				"-Benchmark", plan.Benchmark,
				"-Difficulty", plan.Difficulty,
				"-MaxGameSeconds", plan.MaxGameSeconds.ToString(),
				"-Parallel", EffectiveParallel.ToString()
			};
			AddCandidateChecks(run, arm, arguments);
			return new ContinuousScriptPlan
			{
				Title = $"Benchmarking immutable {arm.ToString().ToLowerInvariant()} arm",
				ScriptPath = repo.BenchmarkBotScript,
				Arguments = arguments,
				Arm = arm,
				ResultPath = candidate ? run.CandidateBenchmarkResultPath : run.ControlBenchmarkResultPath
			};
		}

		/// <summary>
		/// The candidate's own checks, evaluated in the games that judge it. Before this they were
		/// only ever evaluated against the next round's opening fight, which runs the champion
		/// whenever the candidate is restored - so a rejected candidate's claims were never tested.
		/// </summary>
		static void AddCandidateChecks(TrainingRun run, ContinuousEvaluationArm arm, List<string> arguments)
		{
			var checks = Path.Combine(run.CandidateSourceDirectory, "checks.json");
			if (arm == ContinuousEvaluationArm.Candidate && File.Exists(checks))
				arguments.AddRange(["-ChecksFile", checks]);
		}

		/// <summary>
		/// The next benchmark script to run for this evaluation, or null once the evidence for a
		/// verdict is complete and <see cref="CompleteEvaluation"/> can be called.
		/// </summary>
		/// <remarks>
		/// A pinned set is two steps, the candidate arm and then the control arm. A sequential set
		/// is a stage at a time: the candidate on the stage's fresh seeds, then the champion on
		/// whichever of them the pool has not cached, then Wald's test, which either decides or
		/// asks for the next stage. Every step must be followed by <see cref="CaptureBenchmarkStep"/>.
		/// </remarks>
		public ContinuousScriptPlan NextBenchmarkStep(RepoLayout repo, TrainingRun run,
			ContinuousEvaluationPlan plan)
		{
			ArgumentNullException.ThrowIfNull(plan);
			run = plan.Run ?? run;
			if (plan.NoChanges)
				return null;

			var progress = plan.Progress;
			if (plan.Sequential == null)
				return progress.PinnedArmsBenchmarked switch
				{
					0 => BenchmarkArm(repo, run, plan, ContinuousEvaluationArm.Candidate),
					1 => BenchmarkArm(repo, run, plan, ContinuousEvaluationArm.Control),
					_ => null
				};

			EnsureReadyForBenchmark(run, plan);
			var state = SequentialGate.Read(plan.GateStatePath, plan.Benchmark);
			var pool = RequirePool(state, plan);
			while (!progress.Finished)
			{
				var stage = SequentialGate.Stage(pool, plan.Sequential, progress.Stage);
				if (!progress.CandidateStageDone)
					return SequentialStep(repo, run, plan, ContinuousEvaluationArm.Candidate, stage);

				if (!progress.ControlStageDone)
				{
					var needed = ControlNeeded(pool, progress, stage);
					if (needed.Count > 0)
						return SequentialStep(repo, run, plan, ContinuousEvaluationArm.Control, needed);
					progress.ControlStageDone = true;
				}

				FinishStage(plan, pool);
			}

			return null;
		}

		/// <summary>Takes in what a step from <see cref="NextBenchmarkStep"/> played.</summary>
		public void CaptureBenchmarkStep(TrainingRun run, ContinuousEvaluationPlan plan,
			ContinuousScriptPlan step)
		{
			ArgumentNullException.ThrowIfNull(plan);
			ArgumentNullException.ThrowIfNull(step);
			run = plan.Run ?? run;
			var progress = plan.Progress;
			if (!step.Sequential)
			{
				progress.PinnedArmsBenchmarked++;
				return;
			}

			var result = PairedBenchmarkEvaluator.ReadResult(step.ResultPath);
			var arm = step.Arm == ContinuousEvaluationArm.Candidate ? "candidate" : "control";
			var rows = ValidateStageResult(plan, result, step, arm);
			if (step.Arm == ContinuousEvaluationArm.Candidate)
			{
				progress.Candidate.RemoveAll(row => rows.Any(added => added.Scenario == row.Scenario));
				progress.Candidate.AddRange(rows);
				progress.CandidateBatches.Add(result.Batch);
				progress.CandidateStageDone = true;
				return;
			}

			progress.ControlBatches.Add(result.Batch);
			var state = SequentialGate.Read(plan.GateStatePath, plan.Benchmark);
			var pool = RequirePool(state, plan);
			var canaryFailed = false;
			foreach (var row in rows)
			{
				var cached = pool.Champion.FirstOrDefault(result => result.Scenario == row.Scenario);
				if (row.Scenario == progress.CanaryScenario && cached != null)
				{
					if (!SequentialGate.Decided(row))
					{
						// A replay whose game never reached a result says nothing about whether
						// the cache reproduces. The cached game stands in for it, and the cache
						// is checked again next evaluation.
						progress.CachedScenarios.Add(row.Scenario);
						plan.Note = (plan.Note == null ? "" : plan.Note + " ") +
							$"The canary replay of seed {row.Seed} did not complete, so the cache was not checked this time.";
						continue;
					}

					pool.CanaryChecks++;
					progress.CanaryReproduced = cached.Reproduces(row);
					if (progress.CanaryReproduced == false)
					{
						canaryFailed = true;
						pool.Reproducible = false;
						pool.CanaryFailure = string.Create(System.Globalization.CultureInfo.InvariantCulture,
							$"Seed {row.Seed} replayed as {row.Outcome} in {row.DurationSeconds}s at fitness {row.Fitness}, " +
							$"but was cached as {cached.Outcome} in {cached.DurationSeconds}s at fitness {cached.Fitness} " +
							$"({run.Manifest.Id}).");
					}
				}
				else if (cached == null && pool.Reproducible && SequentialGate.Decided(row))
					pool.Champion.Add(SequentialChampionResult.From(row, RulesFingerprint(row.Evidence),
						plan.ControlAssemblySha256));

				progress.FreshControl[row.Scenario] = row;
			}

			SequentialGate.Write(plan.GateStatePath, state);
			if (canaryFailed)
			{
				// The cache is not to be trusted, so every champion game this evaluation relied on
				// is played again rather than taken from it.
				progress.CachedScenarios.Clear();
				plan.Warning = "A replayed champion game did not reproduce its cached result, so the cache is no " +
					"longer used for this pool: " + pool.CanaryFailure;
				plan.Note = (plan.Note == null ? "" : plan.Note + " ") + plan.Warning;
				progress.ControlStageDone = false;
				return;
			}

			progress.ControlStageDone = true;
		}

		ContinuousScriptPlan SequentialStep(RepoLayout repo, TrainingRun run, ContinuousEvaluationPlan plan,
			ContinuousEvaluationArm arm, IReadOnlyList<SequentialMatch> matches)
		{
			var progress = plan.Progress;
			var candidate = arm == ContinuousEvaluationArm.Candidate;
			progress.Steps++;
			var name = $"{(candidate ? "candidate" : "control")}-stage-{progress.Stage + 1}-{progress.Steps:D2}";
			var matchesPath = Path.Combine(run.ExperimentDirectory, name + "-matches.json");
			var resultPath = Path.Combine(run.ExperimentDirectory, name + "-result.json");
			SequentialGate.WriteMatches(matchesPath, matches);
			DeleteIfExists(resultPath);

			var arguments = new List<string>
			{
				"-BattleBot", candidate ? plan.CandidateAssemblyPath : plan.ControlAssemblyPath,
				"-OutputDirectory", Path.Combine(candidate
					? run.CandidateBenchmarkRunsDirectory
					: run.ControlBenchmarkRunsDirectory, $"stage-{progress.Stage + 1}"),
				"-ResultPath", resultPath,
				"-Benchmark", plan.Benchmark,
				"-MatchesFile", matchesPath,
				"-Difficulty", plan.Difficulty,
				"-MaxGameSeconds", plan.MaxGameSeconds.ToString(),
				"-Parallel", EffectiveParallel.ToString(),

				// The gate judges every pair itself - a crashed candidate game is a loss, a crashed
				// champion game drops its pair - so a step of a few games must not fail as a whole.
				"-AllowIncomplete"
			};
			AddCandidateChecks(run, arm, arguments);

			var stages = SequentialGate.Stages(plan.Sequential);
			return new ContinuousScriptPlan
			{
				Title = candidate
					? $"Benchmarking the candidate on fresh seeds, stage {progress.Stage + 1} of at most {stages}"
					: $"Benchmarking the champion on {matches.Count} fresh seed(s) not yet cached, stage {progress.Stage + 1}",
				ScriptPath = repo.BenchmarkBotScript,
				Arguments = arguments,
				Arm = arm,
				Sequential = true,
				Stage = progress.Stage,
				ResultPath = resultPath,
				Matches = matches
			};
		}

		/// <summary>
		/// The champion games a stage still needs: those the pool has not cached, plus one cached
		/// game replayed per evaluation to show the cache still reproduces.
		/// </summary>
		static List<SequentialMatch> ControlNeeded(SequentialPool pool, ContinuousEvaluationProgress progress,
			IReadOnlyList<SequentialMatch> stage)
		{
			if (progress.Stage == 0 && progress.CanaryScenario == null && pool.Reproducible)
			{
				var cached = pool.Champion.OrderBy(result => result.Scenario).ToList();
				if (cached.Count > 0)
					progress.CanaryScenario = cached[pool.Evaluations % cached.Count].Scenario;
			}

			var needed = new List<SequentialMatch>();
			foreach (var match in stage)
			{
				if (progress.FreshControl.ContainsKey(match.Scenario))
					continue;
				if (match.Scenario != progress.CanaryScenario && pool.Cached(match.Scenario) != null)
				{
					progress.CachedScenarios.Add(match.Scenario);
					continue;
				}

				needed.Add(match);
			}

			if (progress.Stage == 0 && progress.CanaryScenario is int canary &&
				!progress.FreshControl.ContainsKey(canary) && needed.All(match => match.Scenario != canary))
			{
				var match = pool.Matches.FirstOrDefault(m => m.Scenario == canary);
				if (match != null)
					needed.Add(match);
			}

			return needed;
		}

		static void FinishStage(ContinuousEvaluationPlan plan, SequentialPool pool)
		{
			var progress = plan.Progress;
			var pairs = SequentialGate.Pairs(pool, progress.Candidate, ControlRows(pool, progress),
				progress.CachedScenarios, out _);
			var final = progress.Stage + 1 >= SequentialGate.Stages(plan.Sequential);
			progress.Decision = SequentialGate.Decide(pairs, plan.Sequential, final);
			progress.StagesPlayed = progress.Stage + 1;
			if (progress.Decision.Verdict == SequentialDecision.Continue)
			{
				progress.Stage++;
				progress.CandidateStageDone = false;
				progress.ControlStageDone = false;
			}
			else
				progress.Finished = true;
		}

		/// <summary>The champion's row for every scenario played so far: fresh first, then cached.</summary>
		static Dictionary<int, BenchmarkMatchResult> ControlRows(SequentialPool pool,
			ContinuousEvaluationProgress progress)
		{
			var rows = new Dictionary<int, BenchmarkMatchResult>(progress.FreshControl);
			foreach (var scenario in progress.CachedScenarios)
			{
				if (rows.ContainsKey(scenario))
					continue;
				var cached = pool.Cached(scenario);
				var match = pool.Matches.FirstOrDefault(m => m.Scenario == scenario);
				if (cached != null && match != null)
					rows[scenario] = cached.ToMatch(match, null, null);
			}

			return rows;
		}

		static SequentialPool RequirePool(SequentialGateState state, ContinuousEvaluationPlan plan)
		{
			var pool = state.Pool;
			if (pool == null ||
				!string.Equals(pool.ChampionFingerprint, plan.ChampionFingerprint, StringComparison.Ordinal))
				throw new InvalidDataException(
					"The sequential gate's pool no longer belongs to this champion; evaluate the candidate again.");
			return pool;
		}

		static List<BenchmarkMatchResult> ValidateStageResult(ContinuousEvaluationPlan plan,
			BenchmarkResultDocument result, ContinuousScriptPlan step, string arm)
		{
			if (result == null)
				throw new InvalidDataException($"The {arm} stage result is empty.");
			if (!string.Equals(result.Benchmark, plan.Benchmark, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(result.Difficulty, plan.Difficulty, StringComparison.OrdinalIgnoreCase) ||
				result.MaxGameSeconds != plan.MaxGameSeconds)
				throw new InvalidDataException(
					$"The {arm} stage result was not played on {plan.Benchmark} at {plan.Difficulty} " +
					$"with a {plan.MaxGameSeconds}-second limit.");

			var rows = result.Matches ?? [];
			if (result.ExpectedMatchesPerArm != step.Matches.Count || rows.Count != step.Matches.Count)
				throw new InvalidDataException(
					$"The {arm} stage result has {rows.Count} row(s) for the {step.Matches.Count} match(es) it was given.");

			foreach (var expected in step.Matches)
			{
				var row = rows.SingleOrDefault(r => r?.Scenario == expected.Scenario);
				if (row == null ||
					!string.Equals(row.Arm, "candidate", StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(row.Map, expected.Map, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(row.Faction, expected.Faction, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(row.BotFaction, expected.BotFaction, StringComparison.OrdinalIgnoreCase) ||
					row.Seed != expected.Seed)
					throw new InvalidDataException(
						$"The {arm} stage result does not match fresh-seed scenario {expected.Scenario}.");
			}

			return rows;
		}

		static string GateStatePath(TrainingRun run, string benchmark) =>
			Path.Combine(Path.GetDirectoryName(run.RunDirectory) ?? run.RunDirectory, "gate",
				string.Concat(benchmark.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')) + ".json");

		/// <summary>
		/// Everything besides the bot that decides how a seed plays out. When it changes the
		/// cached champion games are discarded; see <see cref="SequentialGate.EnsurePool"/>.
		/// </summary>
		static string HarnessKey(RepoLayout repo, TrainingRun run, string difficulty, BenchmarkSelection selection)
		{
			var parts = new List<string>
			{
				"difficulty=" + difficulty,
				"limit=" + selection.MaxGameSeconds,
				"rules=" + (RulesFingerprint(run.EvidenceDirectory) ?? "unrecorded"),
				"difficulties=" + FileHash(repo.DifficultiesFile),
				"fitness=" + FitnessScore.CurrentScaleVersion
			};
			foreach (var name in new[]
			{
				"OpenRA.Game.dll", "OpenRA.Mods.Common.dll", "OpenRA.Mods.Cnc.dll",
				"AutoCnC.Platform.dll", "AutoCnC.Core.dll", "AutoCnC.Sdk.dll"
			})
				parts.Add(name + "=" + FileHash(Path.Combine(repo.EngineBinDir, name)));
			return SequentialGate.HarnessKey(parts);
		}

		static string FileHash(string path) => File.Exists(path) ? BotWorkspace.Sha256(path) : "absent";

		/// <summary>The rules a fight or benchmark game recorded beside its battle log.</summary>
		static string RulesFingerprint(string evidenceDirectory)
		{
			var path = Path.Combine(evidenceDirectory ?? "", "rules-fingerprint.json");
			if (!File.Exists(path))
				return null;
			try
			{
				using var document = JsonDocument.Parse(File.ReadAllText(path));
				return document.RootElement.TryGetProperty("fingerprint", out var value) &&
					value.ValueKind == JsonValueKind.String
					? value.GetString()
					: null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public ContinuousEvaluationCompletion CompleteEvaluation(TrainingRun run,
			ContinuousEvaluationPlan plan, int exitCode)
		{
			run = plan?.Run ?? run;
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			PairedBenchmarkEvaluation evaluation;
			if (exitCode != 0)
				evaluation = PairedBenchmarkEvaluator.Undefined(
					$"The immutable control benchmark exited with code {exitCode}.");
			else if (plan?.Sequential != null)
				try
				{
					EnsureReadyForBenchmark(run, plan);
					evaluation = CompleteSequential(run, plan);
				}
				catch (Exception ex) when (ex is InvalidDataException or IOException or
					UnauthorizedAccessException or System.Text.Json.JsonException or
					InvalidOperationException)
				{
					evaluation = PairedBenchmarkEvaluator.Undefined(
						"The sequential evaluation could not be completed: " + ex.Message,
						plan.Benchmark, plan.Batch);
				}
			else
				try
				{
					EnsureReadyForBenchmark(run, plan);
					var candidate = PairedBenchmarkEvaluator.ReadResult(
						run.CandidateBenchmarkResultPath);
					var control = PairedBenchmarkEvaluator.ReadResult(
						run.ControlBenchmarkResultPath);
					ValidateBenchmarkResult(plan, candidate, "candidate");
					ValidateBenchmarkResult(plan, control, "control");
					run.RecordContinuousBenchmarkBatches(candidate?.Batch, control?.Batch);
					var combined = PairedBenchmarkResultComposer.Combine(
						candidate, control, plan.Batch);
					PairedBenchmarkEvaluator.WriteResult(run.BenchmarkResultPath, combined);
					evaluation = PairedBenchmarkEvaluator.Evaluate(combined);
				}
				catch (Exception ex) when (ex is InvalidDataException or IOException or
					UnauthorizedAccessException or System.Text.Json.JsonException or
					InvalidOperationException)
				{
					evaluation = PairedBenchmarkEvaluator.Undefined(
						"The immutable arm results could not be paired: " + ex.Message);
				}

			Record(run, evaluation);
			var invalidation = LiveCandidateInvalidation(run, plan);
			if (invalidation == null)
				return new ContinuousEvaluationCompletion { Evaluation = evaluation };

			Invalidate(run, invalidation);
			return new ContinuousEvaluationCompletion
			{
				Evaluation = evaluation,
				RequiresReevaluation = true,
				InvalidationReason = invalidation
			};
		}

		/// <summary>
		/// Composes a finished sequential sitting into the same documents a pinned set leaves: the
		/// per-arm and paired benchmark results, and the promotion evaluation.
		/// </summary>
		PairedBenchmarkEvaluation CompleteSequential(TrainingRun run, ContinuousEvaluationPlan plan)
		{
			var progress = plan.Progress;
			if (!progress.Finished || progress.Decision == null)
				throw new InvalidDataException("The sequential gate had not reached a verdict.");

			var state = SequentialGate.Read(plan.GateStatePath, plan.Benchmark);
			var pool = RequirePool(state, plan);
			var pairs = SequentialGate.Pairs(pool, progress.Candidate, ControlRows(pool, progress),
				progress.CachedScenarios, out var dropped);
			if (pairs.Count == 0)
				throw new InvalidDataException("No fresh-seed pair completed in both arms.");

			var final = progress.StagesPlayed >= SequentialGate.Stages(plan.Sequential);
			var decision = SequentialGate.Decide(pairs, plan.Sequential, final);
			var (result, evaluation) = SequentialGate.Compose(plan.Benchmark, plan.Batch, plan.Difficulty,
				plan.MaxGameSeconds, plan.Sequential, pool, pairs, dropped, progress.StagesPlayed, decision,
				progress.CanaryScenario, progress.CanaryReproduced, plan.Note);

			PairedBenchmarkEvaluator.WriteResult(run.BenchmarkResultPath, result);
			PairedBenchmarkEvaluator.WriteResult(run.CandidateBenchmarkResultPath,
				SingleArm(result, "candidate", string.Join(",", progress.CandidateBatches)));
			PairedBenchmarkEvaluator.WriteResult(run.ControlBenchmarkResultPath,
				SingleArm(result, "control", string.Join(",", progress.ControlBatches)));
			run.RecordContinuousBenchmarkBatches(string.Join(",", progress.CandidateBatches),
				string.Join(",", progress.ControlBatches));

			pool.Evaluations++;
			SequentialGate.Write(plan.GateStatePath, state);
			return evaluation;
		}

		/// <summary>One arm of a composed sequential result, in the single-arm form a benchmark writes.</summary>
		static BenchmarkResultDocument SingleArm(BenchmarkResultDocument combined, string arm, string batch) => new()
		{
			SchemaVersion = 1,
			GeneratedUtc = combined.GeneratedUtc,
			Benchmark = combined.Benchmark,
			Batch = string.IsNullOrWhiteSpace(batch) ? combined.Batch : batch,
			Difficulty = combined.Difficulty,
			MaxGameSeconds = combined.MaxGameSeconds,
			ExpectedMatchesPerArm = combined.ExpectedMatchesPerArm,
			Candidate = arm == "candidate" ? combined.Candidate : combined.Control,
			Matches = combined.Matches
				.Where(row => string.Equals(row.Arm, arm, StringComparison.OrdinalIgnoreCase))
				.Select(row => new BenchmarkMatchResult
				{
					RunId = row.RunId, Evidence = row.Evidence, Arm = "candidate", Repeat = row.Repeat,
					Scenario = row.Scenario, Map = row.Map, Faction = row.Faction, BotFaction = row.BotFaction,
					Seed = row.Seed, Outcome = row.Outcome, Fitness = row.Fitness,
					EarnedPerSecond = row.EarnedPerSecond, SpentPerSecond = row.SpentPerSecond,
					Exchange = row.Exchange, BuildingsKilled = row.BuildingsKilled,
					DurationSeconds = row.DurationSeconds, Benchmark = row.Benchmark, Batch = row.Batch,
					Status = row.Status, Succeeded = row.Succeeded, Error = row.Error
				}).ToList()
		};

		/// <summary>
		/// Appends what this round tried and what the gate concluded to the bot's experiment ledger.
		/// </summary>
		/// <remarks>
		/// Called once the decision has been applied. Everything in the record comes from the
		/// immutable candidate and control copies and from the candidate's own benchmark games, so
		/// it is right whether or not the live workspace has since been restored.
		/// </remarks>
		public ExperimentRecord RecordExperiment(TrainingRun run, ContinuousEvaluationPlan plan,
			PairedBenchmarkEvaluation evaluation, ContinuousEvaluationDecision decision)
		{
			ArgumentNullException.ThrowIfNull(run);
			if (decision == ContinuousEvaluationDecision.Reevaluate || evaluation == null)
				return null;

			var experiment = run.Manifest.Experiment;
			var candidateSource = run.CandidateSourceDirectory;
			var controlSource = run.ControlSourceDirectory;
			var evidence = CandidateEvidence(run, plan);
			var newIds = ExperimentLedger.NewReasonIds(candidateSource, controlSource);
			var fight = run.Manifest.Result;
			var record = new ExperimentRecord
			{
				RunId = run.Manifest.Id,
				RecordedUtc = DateTime.UtcNow,
				Benchmark = evaluation.Benchmark ?? plan?.Benchmark,
				Basis = evaluation.Basis,
				Verdict = evaluation.Verdict,
				Conclusion = plan?.NoChanges == true
					? "no-change"
					: decision == ContinuousEvaluationDecision.Undefined
						? "undefined"
						: decision == ContinuousEvaluationDecision.Promote
							? "promoted"
							: evaluation.Sequential?.Conclusion ?? "restored",
				Reason = evaluation.Reason,
				ChampionRevision = experiment?.ChampionSourceRevision ?? run.Manifest.SourceRevision,
				ChampionFingerprint = experiment?.ChampionFingerprint,
				CandidateFingerprint = experiment?.CandidateFingerprint,
				Hypothesis = Hypothesis(candidateSource),
				FightOutcome = fight?.Outcome,
				FightMatchup = run.Manifest.Battle == null ? null : Matchup(run),
				EarlierFights = run.Manifest.EarlierFights?.Select(earlier => earlier.Outcome).ToList() ?? [],
				ChangedFiles = ChangedFiles(candidateSource, controlSource),
				PairsCompared = evaluation.PairsCompared,
				CandidateWins = evaluation.CandidateWins,
				ControlWins = evaluation.ControlWins,
				CandidateOnlyWins = evaluation.Sequential?.CandidateOnlyWins,
				ControlOnlyWins = evaluation.Sequential?.ControlOnlyWins,
				LogLikelihoodRatio = evaluation.Sequential?.LogLikelihoodRatio,
				CandidateGames = evidence.Count,
				NewReasonIds = ExperimentLedger.Activations(newIds, evidence),
				Checks = ExperimentLedger.TallyChecks(evidence)
			};

			ExperimentLedger.Append(LedgerPath(run), record);
			return record;
		}

		public static string LedgerPath(TrainingRun run) =>
			Path.Combine(Path.GetDirectoryName(run.RunDirectory) ?? run.RunDirectory, ExperimentLedger.FileName);

		static List<string> CandidateEvidence(TrainingRun run, ContinuousEvaluationPlan plan)
		{
			if (plan?.Sequential != null)
				return plan.Progress.Candidate.Select(row => row.Evidence)
					.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)).ToList();

			if (!File.Exists(run.CandidateBenchmarkResultPath))
				return [];
			try
			{
				return (PairedBenchmarkEvaluator.ReadResult(run.CandidateBenchmarkResultPath)?.Matches ?? [])
					.Select(row => row.Evidence)
					.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)).ToList();
			}
			catch (Exception ex) when (ex is JsonException or IOException)
			{
				return [];
			}
		}

		static string Hypothesis(string candidateSource)
		{
			try
			{
				return Checks.Read(Path.Combine(candidateSource, "checks.json"))?.Hypothesis;
			}
			catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
			{
				return null;
			}
		}

		static string Matchup(TrainingRun run)
		{
			try
			{
				var summary = Path.Combine(run.EvidenceDirectory, "summary.json");
				if (File.Exists(summary))
				{
					using var document = JsonDocument.Parse(File.ReadAllText(summary));
					if (document.RootElement.TryGetProperty("fight", out var fight) &&
						fight.TryGetProperty("faction", out var faction) &&
						fight.TryGetProperty("opponentFaction", out var opponent))
						return $"{faction.GetString()} against {opponent.GetString()}";
				}
			}
			catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
			{
			}

			return null;
		}

		/// <summary>Source files that differ between the immutable candidate and champion copies.</summary>
		static List<string> ChangedFiles(string candidateSource, string championSource)
		{
			if (!Directory.Exists(candidateSource) || !Directory.Exists(championSource))
				return [];

			static Dictionary<string, string> Index(string root) =>
				Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
					.Select(file => (Relative: Path.GetRelativePath(root, file).Replace('\\', '/'), File: file))
					.Where(entry => !entry.Relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
						!entry.Relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
					.ToDictionary(entry => entry.Relative, entry => BotWorkspace.Sha256(entry.File),
						StringComparer.OrdinalIgnoreCase);

			var candidate = Index(candidateSource);
			var champion = Index(championSource);
			return candidate.Keys.Union(champion.Keys, StringComparer.OrdinalIgnoreCase)
				.Where(path => !candidate.TryGetValue(path, out var left) || !champion.TryGetValue(path, out var right) ||
					!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		public ContinuousEvaluationCompletion FailEvaluation(TrainingRun run,
			ContinuousEvaluationPlan plan, string reason)
		{
			run = plan?.Run ?? run;
			var evaluation = PairedBenchmarkEvaluator.Undefined(reason);
			Record(run, evaluation);
			var invalidation = plan == null ? null : LiveCandidateInvalidation(run, plan);
			if (invalidation == null)
				return new ContinuousEvaluationCompletion { Evaluation = evaluation };

			run.InvalidateContinuousEvaluation(invalidation);
			return new ContinuousEvaluationCompletion
			{
				Evaluation = evaluation,
				RequiresReevaluation = true,
				InvalidationReason = invalidation
			};
		}

		public ContinuousEvaluationCompletion RecordFailedCandidate(TrainingRun run,
			string reason)
		{
			run.RecordContinuousCandidateFingerprint(
				BotWorkspace.Fingerprint(run.Manifest.BotDirectory));
			return FailEvaluation(run, null,
				string.IsNullOrWhiteSpace(reason)
					? "The candidate improvement failed before paired evaluation."
					: reason.Trim());
		}

		public ContinuousEvaluationDecision ApplyDecision(TrainingRun run,
			ContinuousEvaluationPlan plan, PairedBenchmarkEvaluation evaluation)
		{
			run = plan?.Run ?? run;
			if (run == null)
				throw new ArgumentNullException(nameof(run));
			if (evaluation == null)
				throw new ArgumentNullException(nameof(evaluation));

			var invalidation = LiveCandidateInvalidation(run, plan);
			if (invalidation != null)
			{
				Invalidate(run, invalidation);
				return ContinuousEvaluationDecision.Reevaluate;
			}

			var decision = DecisionFor(evaluation);
			if (decision == ContinuousEvaluationDecision.Promote)
			{
				run.MarkContinuousPromoted();
				return decision;
			}

			run.MarkContinuousRestoring();
			WorkspaceSnapshot.Restore(run);
			return decision;
		}

		public bool ValidateForNextFight(TrainingRun run, out string invalidation)
		{
			invalidation = null;
			var experiment = run?.Manifest.Experiment;
			if (experiment?.Continuous != true)
				return true;
			if (experiment.RequiresReevaluation == true)
			{
				invalidation = experiment.InvalidationReason ??
					"The experiment was invalidated and must be evaluated again.";
				return false;
			}

			var expected = experiment.ExpectedLiveFingerprint;
			if (string.IsNullOrWhiteSpace(expected))
			{
				invalidation = "The experiment has no evaluated live-workspace fingerprint.";
				return false;
			}

			var current = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			if (string.Equals(current, expected, StringComparison.Ordinal))
				return true;

			invalidation =
				"The live workspace changed after its benchmark decision and must be evaluated again.";
			Invalidate(run, invalidation);
			return false;
		}

		public string CaptureAgentChatFingerprint(TrainingRun run)
		{
			var experiment = run?.Manifest.Experiment;
			if (experiment?.Continuous != true || experiment.EvaluationStartedUtc == null ||
				string.Equals(experiment.State, TrainingExperimentStates.Candidate,
					StringComparison.OrdinalIgnoreCase))
				return null;

			return BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
		}

		public bool InvalidateAfterAgentChat(TrainingRun run, string beforeFingerprint)
		{
			if (string.IsNullOrWhiteSpace(beforeFingerprint))
				return false;

			var after = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			if (string.Equals(beforeFingerprint, after, StringComparison.Ordinal))
				return false;

			Invalidate(run,
				"Agent chat changed the workspace after the immutable candidate snapshot was captured.");
			return true;
		}

		public void InvalidateEvaluation(TrainingRun run, string reason) =>
			Invalidate(run, reason);

		public static ContinuousEvaluationDecision DecisionFor(PairedBenchmarkEvaluation evaluation)
		{
			if (evaluation?.CanPromote == true)
				return ContinuousEvaluationDecision.Promote;

			return string.Equals(evaluation?.Verdict, PromotionVerdicts.Restore,
				StringComparison.OrdinalIgnoreCase)
				? ContinuousEvaluationDecision.Restore
				: ContinuousEvaluationDecision.Undefined;
		}

		static void Record(TrainingRun run, PairedBenchmarkEvaluation evaluation)
		{
			Directory.CreateDirectory(run.ExperimentDirectory);
			PairedBenchmarkEvaluator.Write(run.PromotionEvaluationPath, evaluation);
			run.RecordContinuousEvaluation(evaluation.Verdict, evaluation.Reason,
				evaluation.Benchmark, evaluation.Batch,
				evaluation.ExpectedMatchesPerArm > 0
					? evaluation.ExpectedMatchesPerArm
					: null);
		}

		static void Invalidate(TrainingRun run, string reason)
		{
			var experiment = run.Manifest.Experiment;
			var evaluation = PairedBenchmarkEvaluator.Undefined(reason,
				experiment?.Benchmark, experiment?.Batch);
			evaluation.ExpectedMatchesPerArm = experiment?.ExpectedMatchesPerArm ?? 0;
			Directory.CreateDirectory(run.ExperimentDirectory);
			PairedBenchmarkEvaluator.Write(run.PromotionEvaluationPath, evaluation);
			run.InvalidateContinuousEvaluation(reason);
		}

		sealed class BenchmarkSelection
		{
			public string Benchmark { get; init; }
			public string Difficulty { get; init; }
			public int MaxGameSeconds { get; init; }
			public IReadOnlyList<ContinuousBenchmarkScenario> Scenarios { get; init; } = [];
			public SequentialSetDefinition Sequential { get; init; }
		}

		static BenchmarkSelection ValidateBenchmarkSelection(
			RepoLayout repo, string benchmark, string difficulty)
		{
			benchmark = string.IsNullOrWhiteSpace(benchmark)
				? DefaultBenchmark
				: benchmark.Trim();
			difficulty = string.IsNullOrWhiteSpace(difficulty)
				? DefaultDifficulty
				: difficulty.Trim();

			if (!File.Exists(repo.BenchmarksFile))
				throw new InvalidOperationException("The benchmark catalogue is unavailable.");
			using var document = JsonDocument.Parse(File.ReadAllText(repo.BenchmarksFile));
			if (!document.RootElement.TryGetProperty("sets", out var sets) ||
				sets.ValueKind != JsonValueKind.Array)
				throw new InvalidDataException("The benchmark catalogue defines no sets.");

			JsonElement selectedSet = default;
			var found = false;
			foreach (var set in sets.EnumerateArray())
				if (set.TryGetProperty("name", out var name) &&
					name.ValueKind == JsonValueKind.String &&
					string.Equals(name.GetString(), benchmark,
						StringComparison.OrdinalIgnoreCase))
				{
					selectedSet = set;
					found = true;
					break;
				}

			if (!found)
				throw new InvalidOperationException(
					$"Continuous benchmark '{benchmark}' is not defined.");

			var difficulties = DifficultyTable.Load(repo.DifficultiesFile);
			var resolvedDifficulty = difficulties.Levels
				.FirstOrDefault(level => string.Equals(level.Name, difficulty,
					StringComparison.OrdinalIgnoreCase))?.Name;
			if (resolvedDifficulty == null)
				throw new InvalidOperationException(
					$"Continuous difficulty '{difficulty}' is not defined.");

			var maxGameSeconds = 5400;
			if (selectedSet.TryGetProperty("maxGameSeconds", out var maximum))
			{
				if (maximum.ValueKind != JsonValueKind.Number ||
					!maximum.TryGetInt32(out maxGameSeconds) ||
					maxGameSeconds < 0 ||
					maxGameSeconds > 86400)
					throw new InvalidDataException(
						$"Continuous benchmark '{benchmark}' has an invalid match time limit.");
			}

			// A sequential set has no fixed matches: its seeds are drawn per champion.
			if (selectedSet.TryGetProperty("sequential", out var sequential) &&
				sequential.ValueKind == JsonValueKind.Object)
				return new BenchmarkSelection
				{
					Benchmark = Text(selectedSet, "name"),
					Difficulty = resolvedDifficulty,
					MaxGameSeconds = maxGameSeconds,
					Sequential = SequentialSetDefinition.Parse(sequential)
				};

			if (!selectedSet.TryGetProperty("matches", out var matches) ||
				matches.ValueKind != JsonValueKind.Array ||
				matches.GetArrayLength() == 0)
				throw new InvalidDataException(
					$"Continuous benchmark '{benchmark}' defines no scenarios.");

			var scenarios = new List<ContinuousBenchmarkScenario>();
			var index = 0;
			foreach (var match in matches.EnumerateArray())
			{
				index++;
				var map = Text(match, "map");
				var faction = Text(match, "faction");
				var botFaction = Text(match, "botFaction");
				var seed = match.TryGetProperty("seed", out var seedValue) &&
					seedValue.TryGetInt32(out var parsedSeed)
					? parsedSeed
					: 0;
				if (string.IsNullOrWhiteSpace(map) ||
					string.IsNullOrWhiteSpace(faction) ||
					string.IsNullOrWhiteSpace(botFaction) ||
					seed == 0)
					throw new InvalidDataException(
						$"Continuous benchmark '{benchmark}' scenario {index} is incomplete.");

				scenarios.Add(new ContinuousBenchmarkScenario
				{
					Repeat = 1,
					Scenario = index,
					Map = map,
					Faction = faction,
					BotFaction = botFaction,
					Seed = seed
				});
			}

			return new BenchmarkSelection
			{
				Benchmark = Text(selectedSet, "name"),
				Difficulty = resolvedDifficulty,
				MaxGameSeconds = maxGameSeconds,
				Scenarios = scenarios
			};
		}

		static void ValidateBenchmarkResult(ContinuousEvaluationPlan plan,
			BenchmarkResultDocument result, string arm)
		{
			if (result == null)
				throw new InvalidDataException(
					$"The immutable {arm} benchmark result is empty.");
			if (!string.Equals(result.Benchmark, plan.Benchmark,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					$"The immutable {arm} result reported benchmark '{result.Benchmark}' " +
					$"instead of '{plan.Benchmark}'.");
			if (!string.Equals(result.Difficulty, plan.Difficulty,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					$"The immutable {arm} result reported difficulty '{result.Difficulty}' " +
					$"instead of '{plan.Difficulty}'.");
			if (!result.MaxGameSeconds.HasValue ||
				result.MaxGameSeconds.Value != plan.MaxGameSeconds)
				throw new InvalidDataException(
					$"The immutable {arm} result reported a {result.MaxGameSeconds}-second limit " +
					$"instead of {plan.MaxGameSeconds}.");
			if (result.ExpectedMatchesPerArm != plan.Scenarios.Count)
				throw new InvalidDataException(
					$"The immutable {arm} result declared {result.ExpectedMatchesPerArm} match(es), " +
					$"but benchmark '{plan.Benchmark}' requires {plan.Scenarios.Count}.");

			var rows = result.Matches ?? [];
			if (rows.Count != plan.Scenarios.Count)
				throw new InvalidDataException(
					$"The immutable {arm} result serialized {rows.Count} match row(s), " +
					$"but benchmark '{plan.Benchmark}' requires {plan.Scenarios.Count}.");
			var byScenario = new Dictionary<(int Repeat, int Scenario), BenchmarkMatchResult>();
			foreach (var row in rows)
			{
				if (row == null ||
					!string.Equals(row.Arm, "candidate",
						StringComparison.OrdinalIgnoreCase) ||
					row.Seed is null or 0 ||
					!byScenario.TryAdd((row.Repeat, row.Scenario), row))
					throw new InvalidDataException(
						$"The immutable {arm} result contains an invalid or duplicate scenario row.");
			}

			foreach (var expected in plan.Scenarios)
			{
				if (!byScenario.TryGetValue((expected.Repeat, expected.Scenario),
					out var actual) ||
					!string.Equals(actual.Map, expected.Map,
						StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(actual.Faction, expected.Faction,
						StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(actual.BotFaction, expected.BotFaction,
						StringComparison.OrdinalIgnoreCase) ||
					actual.Seed != expected.Seed)
					throw new InvalidDataException(
						$"The immutable {arm} result does not match benchmark scenario " +
						$"{expected.Repeat}:{expected.Scenario}.");
			}
		}

		static string Text(JsonElement element, string property) =>
			element.TryGetProperty(property, out var value) &&
			value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static string LiveCandidateInvalidation(TrainingRun run, ContinuousEvaluationPlan plan)
		{
			if (run.Manifest.Experiment?.RequiresReevaluation == true)
				return run.Manifest.Experiment.InvalidationReason ??
					"The experiment was invalidated and must be evaluated again.";
			if (plan == null)
			{
				var expected = run.Manifest.Experiment?.CandidateFingerprint;
				if (string.IsNullOrWhiteSpace(expected))
					return "The failed candidate has no stable workspace fingerprint.";
				var current = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
				return string.Equals(current, expected, StringComparison.Ordinal)
					? null
					: "The live workspace changed after the failed candidate was recorded.";
			}

			var live = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			return string.Equals(live, plan.CandidateFingerprint, StringComparison.Ordinal)
				? null
				: "The live workspace changed after the immutable candidate snapshot was captured.";
		}

		static void EnsureReadyForBenchmark(TrainingRun run, ContinuousEvaluationPlan plan)
		{
			EnsureImmutableSources(plan);
			EnsureDistinctArtifacts(plan);
			var live = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			if (!string.Equals(live, plan.CandidateFingerprint, StringComparison.Ordinal))
				throw new InvalidOperationException(
					"The live workspace changed after the immutable candidate snapshot was captured.");
		}

		static void EnsureImmutableSources(ContinuousEvaluationPlan plan)
		{
			if ((File.GetAttributes(plan.CandidateProjectPath) & FileAttributes.ReadOnly) == 0 ||
				(File.GetAttributes(plan.ControlProjectPath) & FileAttributes.ReadOnly) == 0)
				throw new InvalidDataException(
					"Candidate and control source snapshots must be immutable.");
			if (!string.Equals(BotWorkspace.Fingerprint(
					Path.GetDirectoryName(plan.CandidateProjectPath)),
				plan.CandidateFingerprint, StringComparison.Ordinal) ||
				!string.Equals(BotWorkspace.Fingerprint(
					Path.GetDirectoryName(plan.ControlProjectPath)),
				plan.ChampionFingerprint, StringComparison.Ordinal))
				throw new InvalidDataException(
					"An immutable candidate or control source snapshot changed.");
		}

		static void EnsureDistinctArtifacts(ContinuousEvaluationPlan plan)
		{
			if (!File.Exists(plan.CandidateAssemblyPath) ||
				!File.Exists(plan.ControlAssemblyPath))
				throw new InvalidDataException(
					"Both immutable arm assemblies are required before benchmarking.");
			if ((File.GetAttributes(plan.CandidateAssemblyPath) & FileAttributes.ReadOnly) == 0 ||
				(File.GetAttributes(plan.ControlAssemblyPath) & FileAttributes.ReadOnly) == 0)
				throw new InvalidDataException(
					"Candidate and control assemblies must be immutable before benchmarking.");
			if (string.Equals(Path.GetFullPath(plan.CandidateAssemblyPath),
				Path.GetFullPath(plan.ControlAssemblyPath),
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Candidate and control assemblies resolved to the same immutable path.");

			var candidateHash = BotWorkspace.Sha256(plan.CandidateAssemblyPath);
			var controlHash = BotWorkspace.Sha256(plan.ControlAssemblyPath);
			if (!string.Equals(candidateHash, plan.CandidateAssemblySha256,
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(controlHash, plan.ControlAssemblySha256,
					StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"An immutable arm assembly changed after it was captured.");
			if (string.Equals(candidateHash, controlHash, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Candidate and control assemblies are byte-identical; no distinct candidate can be evaluated.");
		}

		static void PrepareDirectory(string directory)
		{
			if (Directory.Exists(directory))
			{
				foreach (var file in Directory.EnumerateFiles(directory, "*",
					SearchOption.AllDirectories))
					File.SetAttributes(file,
						File.GetAttributes(file) & ~FileAttributes.ReadOnly);
				Directory.Delete(directory, recursive: true);
			}

			Directory.CreateDirectory(directory);
		}

		static void DeleteIfExists(string path)
		{
			if (!File.Exists(path))
				return;
			File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}

		static void MakeReadOnly(string directory)
		{
			foreach (var file in Directory.EnumerateFiles(directory, "*",
				SearchOption.AllDirectories))
				File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
		}

		static string Under(string root, string relative)
		{
			var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
			if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					$"'{relative}' points outside the immutable arm directory.");
			return full;
		}

		static bool IsUnder(string root, string path)
		{
			var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var full = Path.GetFullPath(path);
			return full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase);
		}
	}
}
