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
using System.Text.Json;
using AutoCnC.Evidence;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class ContinuousPromotionRunnerTests
	{
		string root;
		string workspace;
		string project;
		string strategy;
		ContinuousPromotionRunner promotion;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Promotion Tests",
				Guid.NewGuid().ToString("N"));
			workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			project = Path.Combine(workspace, "Bot.csproj");
			strategy = Path.Combine(workspace, "Strategy.cs");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(strategy, "champion");
			promotion = new ContinuousPromotionRunner();
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
				Directory.Delete(root, true);
		}

		[Test]
		public void ContinuousSuccessPersistsACandidateAndFreezesPromptRewriting()
		{
			var run = Candidate("agent draft");

			Assert.Multiple(() =>
			{
				Assert.That(run.Manifest.Status, Is.EqualTo("candidate"));
				Assert.That(run.Manifest.Experiment.State,
					Is.EqualTo(TrainingExperimentStates.Candidate));
				Assert.That(run.Manifest.Experiment.PromptRewriteFrozen, Is.True);
				Assert.That(run.Manifest.Experiment.ChampionSnapshotFile,
					Is.EqualTo(Path.GetRelativePath(run.RunDirectory, run.SnapshotManifestPath)));
				Assert.That(run.Manifest.Agent.SuggestedNextPrompt, Is.EqualTo("agent draft"));
				Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);
			});

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Candidate));
			Assert.That(loaded.Manifest.Experiment.PromptRewriteFrozen, Is.True);
		}

		[Test]
		public void UndefinedEvaluationRestoresTheWorkspaceSnapshot()
		{
			var run = Candidate();
			run.BeginContinuousEvaluation(null);

			var evaluation = promotion.CompleteEvaluation(run, exitCode: 0);
			var decision = promotion.ApplyDecision(run, evaluation);

			Assert.Multiple(() =>
			{
				Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Undefined));
				Assert.That(evaluation.CanPromote, Is.False);
				Assert.That(File.ReadAllText(strategy), Is.EqualTo("champion"));
				Assert.That(run.Manifest.Status, Is.EqualTo("restored"));
				Assert.That(run.Manifest.Experiment.State,
					Is.EqualTo(TrainingExperimentStates.Restored));
				Assert.That(File.Exists(run.PromotionEvaluationPath), Is.True);
				Assert.That(File.ReadAllText(run.PromotionEvaluationPath),
					Does.Contain("\"verdict\": \"Undefined\""));
			});
		}

		[Test]
		public void PairedWinPromotesWithoutRestoringTheCandidate()
		{
			var run = Candidate();
			run.BeginContinuousEvaluation("0123456789abcdef0123456789abcdef01234567");
			WriteBenchmarkResult(run, candidateOutcome: "Won", controlOutcome: "Lost",
				candidateFitness: 0.6, controlFitness: 0.5);

			var evaluation = promotion.CompleteEvaluation(run, exitCode: 0);
			var decision = promotion.ApplyDecision(run, evaluation);

			Assert.Multiple(() =>
			{
				Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Promote));
				Assert.That(File.ReadAllText(strategy), Is.EqualTo("candidate"));
				Assert.That(run.Manifest.Status, Is.EqualTo("promoted"));
				Assert.That(run.Manifest.Experiment.State,
					Is.EqualTo(TrainingExperimentStates.Promoted));
				Assert.That(run.Manifest.Experiment.Batch,
					Is.EqualTo("standard-20260919-083633-test"));
			});
		}

		[Test]
		public void PairedLossRestoresTheChampion()
		{
			var run = Candidate();
			run.BeginContinuousEvaluation("0123456789abcdef0123456789abcdef01234567");
			WriteBenchmarkResult(run, candidateOutcome: "Lost", controlOutcome: "Won",
				candidateFitness: 0.7, controlFitness: 0.4);

			var evaluation = promotion.CompleteEvaluation(run, exitCode: 0);
			var decision = promotion.ApplyDecision(run, evaluation);

			Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Restore));
			Assert.That(File.ReadAllText(strategy), Is.EqualTo("champion"),
				"wins outrank the candidate's higher fitness");
			Assert.That(run.Manifest.Experiment.Decision, Is.EqualTo(PromotionVerdicts.Restore));
		}

		[Test]
		public void CleanChampionProducesABenchmarkScriptPlanWithoutRunningIt()
		{
			var checkout = Path.Combine(root, "checkout");
			var scripts = Path.Combine(checkout, "scripts");
			var bot = Path.Combine(checkout, "bots", "Bot");
			Directory.CreateDirectory(scripts);
			Directory.CreateDirectory(bot);
			File.WriteAllText(Path.Combine(checkout, "AutoCnC.sln"), "");
			File.WriteAllText(Path.Combine(scripts, "run-bot.ps1"), "param()");
			File.WriteAllText(Path.Combine(scripts, "benchmark-bot.ps1"), "param()");
			var botProject = Path.Combine(bot, "Bot.csproj");
			File.WriteAllText(botProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(Path.Combine(bot, "Strategy.cs"), "champion");

			var run = TrainingRun.Create(botProject, new TrainingBattleConfiguration(),
				Path.Combine(root, "planned-runs"));
			WorkspaceSnapshot.Capture(run);
			promotion.BeginCandidate(run);
			run.Manifest.Experiment.ChampionSourceRevision =
				"0123456789abcdef0123456789abcdef01234567";
			run.Save();
			run.AgentStarted("agent");
			run.AgentFinished(0, 1);

			var plan = promotion.PrepareEvaluation(RepoLayout.For(checkout), run);

			Assert.Multiple(() =>
			{
				Assert.That(plan.CanRun, Is.True);
				Assert.That(plan.ScriptPath, Is.EqualTo(Path.Combine(scripts, "benchmark-bot.ps1")));
				Assert.That(plan.Arguments, Does.Contain("-Control"));
				Assert.That(plan.Arguments, Does.Contain(run.BenchmarkResultPath));
				Assert.That(run.Manifest.Status, Is.EqualTo("evaluating"));
			});
		}

		TrainingRun Candidate(string suggestedPrompt = null)
		{
			var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
				Path.Combine(root, "runs"));
			WorkspaceSnapshot.Capture(run);
			promotion.BeginCandidate(run);
			run.AgentStarted("agent");
			File.WriteAllText(strategy, "candidate");
			run.AgentFinished(0, 1, suggestedPrompt);
			return run;
		}

		static void WriteBenchmarkResult(TrainingRun run, string candidateOutcome,
			string controlOutcome, double candidateFitness, double controlFitness)
		{
			const string batch = "standard-20260919-083633-test";
			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				Benchmark = "standard",
				Batch = batch,
				Candidate = new BenchmarkArmResult
				{
					Arm = "candidate",
					Wins = candidateOutcome == "Won" ? 1 : 0,
					Played = 1,
					MedianFitness = candidateFitness
				},
				Control = new BenchmarkArmResult
				{
					Arm = "control",
					Wins = controlOutcome == "Won" ? 1 : 0,
					Played = 1,
					MedianFitness = controlFitness
				},
				Matches =
				[
					new BenchmarkMatchResult
					{
						RunId = "candidate",
						Arm = "candidate",
						Repeat = 1,
						Scenario = 1,
						Map = "map",
						Faction = "gdi",
						BotFaction = "nod",
						Seed = 123,
						Outcome = candidateOutcome,
						Fitness = candidateFitness,
						DurationSeconds = 600
					},
					new BenchmarkMatchResult
					{
						RunId = "control",
						Arm = "control",
						Repeat = 1,
						Scenario = 1,
						Map = "map",
						Faction = "gdi",
						BotFaction = "nod",
						Seed = 123,
						Outcome = controlOutcome,
						Fitness = controlFitness,
						DurationSeconds = 600
					}
				],
				Paired =
				[
					new BenchmarkPairResult
					{
						Repeat = 1,
						Scenario = 1,
						Map = "map",
						Faction = "gdi",
						BotFaction = "nod",
						Seed = 123,
						CandidateOutcome = candidateOutcome,
						ControlOutcome = controlOutcome,
						FitnessDelta = Math.Round(candidateFitness - controlFitness, 4)
					}
				]
			};

			Directory.CreateDirectory(run.ExperimentDirectory);
			File.WriteAllText(run.BenchmarkResultPath, JsonSerializer.Serialize(result));
		}
	}
}
