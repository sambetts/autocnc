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
using System.Text.RegularExpressions;
using AutoCnC.Evidence;

namespace AutoCnC.Launcher
{
	public sealed class ContinuousBenchmarkPlan
	{
		public string ScriptPath { get; init; }
		public IReadOnlyList<string> Arguments { get; init; } = [];
		public PairedBenchmarkEvaluation ImmediateEvaluation { get; init; }
		public bool CanRun => ImmediateEvaluation == null;
	}

	/// <summary>
	/// Owns the durable candidate, paired benchmark decision and safe snapshot restoration.
	/// </summary>
	public sealed class ContinuousPromotionRunner
	{
		static readonly Regex Commit = new("^[0-9a-f]{7,64}$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

		public void BeginCandidate(TrainingRun run)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			run.BeginContinuousExperiment();
		}

		public ContinuousBenchmarkPlan PrepareEvaluation(RepoLayout repo, TrainingRun run)
		{
			if (repo == null)
				throw new ArgumentNullException(nameof(repo));
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			var controlRevision = ResolveControlRevision(repo, run, out var unavailable);
			run.BeginContinuousEvaluation(controlRevision);

			if (unavailable != null)
			{
				var evaluation = PairedBenchmarkEvaluator.Undefined(unavailable);
				Record(run, evaluation);
				return new ContinuousBenchmarkPlan { ImmediateEvaluation = evaluation };
			}

			return new ContinuousBenchmarkPlan
			{
				ScriptPath = repo.BenchmarkBotScript,
				Arguments =
				[
					"-BattleBot", run.Manifest.BotProject,
					"-Control", controlRevision,
					"-OutputDirectory", run.BenchmarkRunsDirectory,
					"-ResultPath", run.BenchmarkResultPath
				]
			};
		}

		public PairedBenchmarkEvaluation CompleteEvaluation(TrainingRun run, int exitCode)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			var evaluation = exitCode == 0
				? PairedBenchmarkEvaluator.EvaluateFile(run.BenchmarkResultPath)
				: PairedBenchmarkEvaluator.Undefined(
					$"The paired benchmark process exited with code {exitCode}.");
			Record(run, evaluation);
			return evaluation;
		}

		public PairedBenchmarkEvaluation RecordFailedCandidate(TrainingRun run, string reason)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			var evaluation = PairedBenchmarkEvaluator.Undefined(
				string.IsNullOrWhiteSpace(reason)
					? "The candidate improvement failed before paired evaluation."
					: reason.Trim());
			Record(run, evaluation);
			return evaluation;
		}

		public ContinuousEvaluationDecision ApplyDecision(TrainingRun run,
			PairedBenchmarkEvaluation evaluation)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));
			if (evaluation == null)
				throw new ArgumentNullException(nameof(evaluation));

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
				evaluation.Benchmark, evaluation.Batch);
		}

		static string ResolveControlRevision(RepoLayout repo, TrainingRun run, out string unavailable)
		{
			unavailable = null;

			if (!File.Exists(repo.BenchmarkBotScript))
			{
				unavailable = "The paired benchmark script is unavailable.";
				return null;
			}

			if (string.IsNullOrWhiteSpace(run.Manifest.BotProject) ||
				!File.Exists(run.Manifest.BotProject))
			{
				unavailable = "The continuous candidate no longer has an editable bot project.";
				return null;
			}

			var botsRoot = Path.GetFullPath(Path.Combine(repo.Root, "bots"));
			var project = Path.GetFullPath(run.Manifest.BotProject);
			var relative = Path.GetRelativePath(botsRoot, project);
			if (Path.IsPathRooted(relative) || relative == ".." ||
				relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			{
				unavailable =
					"The current benchmark script can only materialize a control for a bot under this checkout's bots directory.";
				return null;
			}

			var revision = run.Manifest.Experiment?.ChampionSourceRevision;
			if (string.IsNullOrWhiteSpace(revision) || !Commit.IsMatch(revision))
			{
				unavailable =
					"The pre-agent champion is not a clean Git revision. Its WorkspaceSnapshot remains authoritative, " +
					"but the current paired benchmark script cannot run that snapshot as a control arm.";
				return null;
			}

			return revision;
		}
	}
}
