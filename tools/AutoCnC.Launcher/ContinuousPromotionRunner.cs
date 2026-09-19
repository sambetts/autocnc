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
	}

	public sealed class ContinuousEvaluationPlan
	{
		public string Batch { get; init; }
		public string CandidateProjectPath { get; init; }
		public string ControlProjectPath { get; init; }
		public string CandidateAssemblyPath { get; init; }
		public string ControlAssemblyPath { get; init; }
		public string CandidateFingerprint { get; init; }
		public string ChampionFingerprint { get; init; }
		public string CandidateAssemblySha256 { get; set; }
		public string ControlAssemblySha256 { get; set; }
	}

	public sealed class ContinuousEvaluationCompletion
	{
		public PairedBenchmarkEvaluation Evaluation { get; init; }
		public bool RequiresReevaluation { get; init; }
		public string InvalidationReason { get; init; }
	}

	/// <summary>
	/// Materializes immutable arms, captures their built assemblies and evaluates paired results.
	/// </summary>
	public sealed class ContinuousPromotionRunner
	{
		public void CaptureChampion(TrainingRun run)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			if (!File.Exists(run.SnapshotManifestPath))
				WorkspaceSnapshot.Capture(run);
		}

		public ContinuousEvaluationPlan PrepareEvaluation(RepoLayout repo, TrainingRun run)
		{
			if (repo == null)
				throw new ArgumentNullException(nameof(repo));
			if (run == null)
				throw new ArgumentNullException(nameof(run));
			if (!File.Exists(repo.BuildExperimentArmScript) ||
				!File.Exists(repo.BenchmarkBotScript))
				throw new InvalidOperationException(
					"The build and benchmark scripts are required for paired evaluation.");
			if (!run.IsEditable)
				throw new InvalidOperationException(
					"The continuous candidate no longer has an editable bot project.");

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
				throw new InvalidDataException(
					"The candidate source is identical to the pre-agent champion.");
			var candidateProject = Under(run.CandidateSourceDirectory, relativeProject);
			var controlProject = Under(run.ControlSourceDirectory, relativeProject);
			if (!File.Exists(candidateProject) || !File.Exists(controlProject))
				throw new InvalidDataException(
					"The immutable candidate and control copies do not contain the bot project.");

			var candidateAssemblyName = BotWorkspace.AssemblyName(candidateProject);
			var controlAssemblyName = BotWorkspace.AssemblyName(controlProject);
			if (!string.Equals(candidateAssemblyName, controlAssemblyName,
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Candidate and control materialized different assembly names.");

			PrepareDirectory(run.CandidateArtifactDirectory);
			PrepareDirectory(run.ControlArtifactDirectory);
			PrepareDirectory(run.CandidateBenchmarkRunsDirectory);
			PrepareDirectory(run.ControlBenchmarkRunsDirectory);
			DeleteIfExists(run.CandidateBenchmarkResultPath);
			DeleteIfExists(run.ControlBenchmarkResultPath);
			DeleteIfExists(run.BenchmarkResultPath);

			run.BeginContinuousEvaluation(candidateFingerprint, championFingerprint);
			var experiment = run.Manifest.Experiment;
			var assemblyFile = candidateAssemblyName + ".dll";
			var candidateAssembly = Path.Combine(run.CandidateArtifactDirectory, assemblyFile);
			var controlAssembly = Path.Combine(run.ControlArtifactDirectory, assemblyFile);
			var sharedBotDirectory = Path.Combine(repo.EngineBinDir, "bots");
			if (string.Equals(Path.GetFullPath(candidateAssembly),
					Path.GetFullPath(controlAssembly), StringComparison.OrdinalIgnoreCase) ||
				IsUnder(sharedBotDirectory, candidateAssembly) ||
				IsUnder(sharedBotDirectory, controlAssembly))
				throw new InvalidDataException(
					"Candidate and control must use distinct immutable paths outside engine/bin/bots.");

			return new ContinuousEvaluationPlan
			{
				Batch = $"promotion-{experiment.Id}-{experiment.EvaluationAttempt:D2}",
				CandidateProjectPath = candidateProject,
				ControlProjectPath = controlProject,
				CandidateAssemblyPath = candidateAssembly,
				ControlAssemblyPath = controlAssembly,
				CandidateFingerprint = candidateFingerprint,
				ChampionFingerprint = championFingerprint
			};
		}

		public ContinuousScriptPlan BuildArm(RepoLayout repo, ContinuousEvaluationPlan plan,
			ContinuousEvaluationArm arm)
		{
			var project = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateProjectPath
				: plan.ControlProjectPath;
			return new ContinuousScriptPlan
			{
				Title = $"Building immutable {arm.ToString().ToLowerInvariant()} arm",
				ScriptPath = repo.BuildExperimentArmScript,
				Arguments =
				[
					"-Project", project,
					"-OutputDirectory", arm == ContinuousEvaluationArm.Candidate
						? Path.GetDirectoryName(plan.CandidateAssemblyPath)
						: Path.GetDirectoryName(plan.ControlAssemblyPath),
					"-AutoCnCPath", repo.Root
				]
			};
		}

		public void CaptureBuiltArm(TrainingRun run, ContinuousEvaluationPlan plan,
			ContinuousEvaluationArm arm)
		{
			var destination = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateAssemblyPath
				: plan.ControlAssemblyPath;
			if (!File.Exists(destination))
				throw new InvalidDataException(
					$"The {arm.ToString().ToLowerInvariant()} build did not produce " +
					$"'{destination}'.");

			MakeReadOnly(Path.GetDirectoryName(destination));
			var sha256 = BotWorkspace.Sha256(destination);

			if (arm == ContinuousEvaluationArm.Candidate)
				plan.CandidateAssemblySha256 = sha256;
			else
				plan.ControlAssemblySha256 = sha256;

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
			EnsureReadyForBenchmark(run, plan);
			var candidate = arm == ContinuousEvaluationArm.Candidate;
			return new ContinuousScriptPlan
			{
				Title = $"Benchmarking immutable {arm.ToString().ToLowerInvariant()} arm",
				ScriptPath = repo.BenchmarkBotScript,
				Arguments =
				[
					"-BattleBot", candidate
						? plan.CandidateAssemblyPath
						: plan.ControlAssemblyPath,
					"-OutputDirectory", candidate
						? run.CandidateBenchmarkRunsDirectory
						: run.ControlBenchmarkRunsDirectory,
					"-ResultPath", candidate
						? run.CandidateBenchmarkResultPath
						: run.ControlBenchmarkResultPath
				]
			};
		}

		public ContinuousEvaluationCompletion CompleteEvaluation(TrainingRun run,
			ContinuousEvaluationPlan plan, int exitCode)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			PairedBenchmarkEvaluation evaluation;
			if (exitCode != 0)
				evaluation = PairedBenchmarkEvaluator.Undefined(
					$"The immutable control benchmark exited with code {exitCode}.");
			else
				try
				{
					EnsureReadyForBenchmark(run, plan);
					var candidate = PairedBenchmarkEvaluator.ReadResult(
						run.CandidateBenchmarkResultPath);
					var control = PairedBenchmarkEvaluator.ReadResult(
						run.ControlBenchmarkResultPath);
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

		public ContinuousEvaluationCompletion FailEvaluation(TrainingRun run,
			ContinuousEvaluationPlan plan, string reason)
		{
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

		public void InvalidateForAgentChat(TrainingRun run)
		{
			var experiment = run?.Manifest.Experiment;
			if (experiment?.Continuous != true || experiment.EvaluationStartedUtc == null ||
				string.Equals(experiment.State, TrainingExperimentStates.Candidate,
					StringComparison.OrdinalIgnoreCase))
				return;

			Invalidate(run,
				"An edit-capable agent chat started after the immutable candidate snapshot was captured.");
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
