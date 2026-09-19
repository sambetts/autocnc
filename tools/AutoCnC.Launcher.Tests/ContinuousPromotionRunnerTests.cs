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
		string checkout;
		string workspace;
		string project;
		string strategy;
		RepoLayout repo;
		ContinuousPromotionRunner promotion;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Promotion Tests",
				Guid.NewGuid().ToString("N"));
			checkout = Path.Combine(root, "checkout");
			var scripts = Path.Combine(checkout, "scripts");
			var launcherTools = Path.Combine(checkout, "tools", "AutoCnC.Launcher");
			workspace = Path.Combine(checkout, "bots", "Bot");
			Directory.CreateDirectory(scripts);
			Directory.CreateDirectory(launcherTools);
			Directory.CreateDirectory(Path.Combine(checkout, "engine", "bin", "bots"));
			Directory.CreateDirectory(workspace);
			File.WriteAllText(Path.Combine(checkout, "AutoCnC.sln"), "");
			File.WriteAllText(Path.Combine(scripts, "run-bot.ps1"), "param()");
			File.WriteAllText(Path.Combine(scripts, "benchmark-bot.ps1"), "param()");
			File.WriteAllText(Path.Combine(scripts, "benchmarks.json"),
				"{\"default\":\"smoke\",\"sets\":[" +
				"{\"name\":\"smoke\",\"difficulty\":\"Normal\",\"matches\":[{}]}," +
				"{\"name\":\"hard-16-9\",\"difficulty\":\"Hard\",\"matches\":[{}]}]}");
			File.WriteAllText(Path.Combine(scripts, "difficulties.json"),
				"{\"default\":\"Normal\",\"levels\":[" +
				"{\"name\":\"Normal\"},{\"name\":\"Hard\"}]}");
			File.WriteAllText(Path.Combine(launcherTools, "build-experiment-arm.ps1"), "param()");
			project = Path.Combine(workspace, "Bot.csproj");
			strategy = Path.Combine(workspace, "Strategy.cs");
			File.WriteAllText(project,
				"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
				"<AssemblyName>AutoCnC.TestBot</AssemblyName></PropertyGroup></Project>");
			File.WriteAllText(strategy, "champion");
			repo = RepoLayout.For(checkout);
			promotion = new ContinuousPromotionRunner();
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
			{
				foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
				Directory.Delete(root, true);
			}
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
				Assert.That(run.Manifest.Experiment.ChampionFingerprint, Is.Not.Empty);
				Assert.That(run.Manifest.Agent.SuggestedNextPrompt, Is.EqualTo("agent draft"));
				Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);
			});

			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Candidate),
				"a live owner must not be reconciled as an interrupted experiment");
		}

		[Test]
		public void ContinuousEvaluationDefaultsToHardTrainingBenchmark()
		{
			var settings = new LauncherSettings();

			Assert.That(settings.ContinuousBenchmark, Is.EqualTo("hard-16-9"));
			Assert.That(settings.ContinuousBenchmarkDifficulty, Is.EqualTo("Hard"));
		}

		[Test]
		public void ChampionIsCapturedBeforeQueuedChatCanEditTheCandidate()
		{
			var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
				Path.Combine(root, "chat-runs"));
			promotion.CaptureChampion(run);
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Experiment, Is.Null,
				"capturing the champion must not persist a Prepared experiment");

			File.WriteAllText(strategy, "queued chat edit");
			run.ContinuousAgentStarted("agent");
			Assert.That(run.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Improving),
				"the experiment is created only with the prepared agent attempt");
			run.AgentFinished(0, 1);

			promotion.PrepareEvaluation(repo, run);

			Assert.That(File.ReadAllText(
				Path.Combine(run.ControlSourceDirectory, "Strategy.cs")),
				Is.EqualTo("champion"));
			Assert.That(File.ReadAllText(
				Path.Combine(run.CandidateSourceDirectory, "Strategy.cs")),
				Is.EqualTo("queued chat edit"));
		}

		[Test]
		public void MaterializedDirtyChampionAndCandidateAreDistinctImmutableSources()
		{
			var run = Candidate();

			var plan = promotion.PrepareEvaluation(repo, run);

			Assert.Multiple(() =>
			{
				Assert.That(plan.CandidateProjectPath, Is.Not.EqualTo(plan.ControlProjectPath));
				Assert.That(File.ReadAllText(Path.Combine(run.CandidateSourceDirectory, "Strategy.cs")),
					Is.EqualTo("candidate"));
				Assert.That(File.ReadAllText(Path.Combine(run.ControlSourceDirectory, "Strategy.cs")),
					Is.EqualTo("champion"));
				Assert.That(File.GetAttributes(plan.CandidateProjectPath) & FileAttributes.ReadOnly,
					Is.EqualTo(FileAttributes.ReadOnly));
				Assert.That(plan.ChampionFingerprint,
					Is.EqualTo(run.Manifest.Experiment.ChampionFingerprint));
			});
		}

		[Test]
		public void UnknownContinuousBenchmarkOrDifficultyIsRejected()
		{
			var run = Candidate();

			Assert.That(() => promotion.PrepareEvaluation(repo, run,
				"not-a-benchmark", "Hard"), Throws.InvalidOperationException);
			Assert.That(() => promotion.PrepareEvaluation(repo, run,
				"hard-16-9", "Impossible"), Throws.InvalidOperationException);
		}

		[Test]
		public void BuiltArmsUseDistinctPathsAndMustHaveDifferentHashes()
		{
			var run = Candidate();
			var plan = promotion.PrepareEvaluation(repo, run);
			var candidateBuild = promotion.BuildArm(repo, plan, ContinuousEvaluationArm.Candidate);
			var controlBuild = promotion.BuildArm(repo, plan, ContinuousEvaluationArm.Control);

			WriteBuilt(plan, ContinuousEvaluationArm.Candidate, "candidate binary");
			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Candidate);
			WriteBuilt(plan, ContinuousEvaluationArm.Control, "control binary");
			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Control);
			var candidateBenchmark = promotion.BenchmarkArm(
				repo, run, plan, ContinuousEvaluationArm.Candidate);
			var controlBenchmark = promotion.BenchmarkArm(
				repo, run, plan, ContinuousEvaluationArm.Control);

			Assert.Multiple(() =>
			{
				Assert.That(plan.CandidateAssemblyPath, Is.Not.EqualTo(plan.ControlAssemblyPath));
				Assert.That(Path.GetFileName(plan.CandidateAssemblyPath),
					Is.EqualTo("Expanded.AutoCnC.TestBot.dll"));
				Assert.That(candidateBuild.Arguments,
					Does.Contain(run.CandidateArtifactDirectory));
				Assert.That(controlBuild.Arguments,
					Does.Contain(run.ControlArtifactDirectory));
				Assert.That(candidateBuild.Arguments,
					Does.Contain(plan.CandidateBuildResultPath));
				Assert.That(controlBuild.Arguments,
					Does.Contain(plan.ControlBuildResultPath));
				Assert.That(candidateBuild.Arguments,
					Does.Not.Contain(Path.Combine(repo.EngineBinDir, "bots")));
				Assert.That(candidateBenchmark.Arguments,
					Does.Contain(plan.CandidateAssemblyPath).And.Not.Contain("-Control"));
				Assert.That(controlBenchmark.Arguments,
					Does.Contain(plan.ControlAssemblyPath).And.Not.Contain("-Control"));
				Assert.That(plan.Benchmark, Is.EqualTo("hard-16-9"));
				Assert.That(plan.Difficulty, Is.EqualTo("Hard"));
				Assert.That(run.Manifest.Experiment.RequestedBenchmark,
					Is.EqualTo("hard-16-9"));
				Assert.That(run.Manifest.Experiment.RequestedDifficulty,
					Is.EqualTo("Hard"));
				Assert.That(candidateBenchmark.Arguments,
					Does.Contain("-Benchmark").And.Contain("hard-16-9"));
				Assert.That(candidateBenchmark.Arguments,
					Does.Contain("-Difficulty").And.Contain("Hard"));
				Assert.That(plan.CandidateAssemblySha256,
					Is.Not.EqualTo(plan.ControlAssemblySha256));
				Assert.That(File.ReadAllText(plan.CandidateAssemblyPath),
					Is.EqualTo("candidate binary"));
				Assert.That(File.ReadAllText(plan.ControlAssemblyPath),
					Is.EqualTo("control binary"));
				Assert.That(run.Manifest.Experiment.CandidateAssemblyFile, Is.Not.Null);
				Assert.That(run.Manifest.Experiment.ControlAssemblyFile, Is.Not.Null);
			});
		}

		[Test]
		public void ByteIdenticalArmBuildsAreRejectedBeforeBenchmarking()
		{
			var run = Candidate();
			var plan = promotion.PrepareEvaluation(repo, run);

			WriteBuilt(plan, ContinuousEvaluationArm.Candidate, "same binary");
			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Candidate);
			WriteBuilt(plan, ContinuousEvaluationArm.Control, "same binary");

			Assert.That(
				() => promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Control),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("byte-identical"));
		}

		[Test]
		public void EvaluatedTargetPathSupportsImportedConditionalPropertyExpandedNames()
		{
			File.WriteAllText(Path.Combine(workspace, "Directory.Build.props"),
				"<Project><PropertyGroup><ImportedPrefix>Imported</ImportedPrefix>" +
				"</PropertyGroup></Project>");
			File.WriteAllText(project,
				"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
				"<AssemblyName Condition=\"'$(Configuration)' == 'Release'\">" +
				"$(ImportedPrefix).Conditional</AssemblyName>" +
				"</PropertyGroup></Project>");
			var run = Candidate();
			var plan = promotion.PrepareEvaluation(repo, run);
			File.WriteAllText(Path.Combine(plan.CandidateOutputDirectory, "Bot.dll"),
				"guessed project name");
			File.WriteAllText(Path.Combine(plan.CandidateOutputDirectory,
				"AutoCnC.TestBot.dll"), "unevaluated assembly name");
			WriteBuilt(plan, ContinuousEvaluationArm.Candidate, "candidate binary",
				"Imported.Conditional.dll");

			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Candidate);

			Assert.That(Path.GetFileName(plan.CandidateAssemblyPath),
				Is.EqualTo("Imported.Conditional.dll"));
			Assert.That(File.ReadAllText(plan.CandidateAssemblyPath),
				Is.EqualTo("candidate binary"));
		}

		[Test]
		public void PairedWinPromotesTheSnapshottedCandidate()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Won", controlOutcome: "Lost",
				candidateFitness: 0.6, controlFitness: 0.5);

			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			var decision = promotion.ApplyDecision(run, plan, completion.Evaluation);

			Assert.Multiple(() =>
			{
				Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Promote));
				Assert.That(File.ReadAllText(strategy), Is.EqualTo("candidate"));
				Assert.That(run.Manifest.Status, Is.EqualTo("promoted"));
				Assert.That(run.Manifest.Experiment.Batch, Is.EqualTo(plan.Batch));
				Assert.That(run.Manifest.Experiment.CandidateBatch,
					Is.EqualTo("candidate-batch-20260919-110459"));
				Assert.That(run.Manifest.Experiment.ControlBatch,
					Is.EqualTo("control-batch-20260919-110459"));
				Assert.That(run.Manifest.Experiment.ExpectedMatchesPerArm, Is.EqualTo(1));
				Assert.That(File.Exists(run.BenchmarkResultPath), Is.True);
			});
		}

		[TestCase("smoke", "Hard")]
		[TestCase("hard-16-9", "Normal")]
		public void ReturnedBenchmarkIdentityMustMatchTheConfiguredHardEvaluation(
			string benchmark, string difficulty)
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			PairedBenchmarkEvaluator.WriteResult(run.CandidateBenchmarkResultPath,
				SingleArm("candidate-batch-20260919-110459", "Won", 0.7,
					benchmark, difficulty));
			PairedBenchmarkEvaluator.WriteResult(run.ControlBenchmarkResultPath,
				SingleArm("control-batch-20260919-110459", "Lost", 0.5));

			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);

			Assert.That(completion.Evaluation.Verdict,
				Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(completion.Evaluation.CanPromote, Is.False);
			Assert.That(completion.Evaluation.Reason, Does.Contain("instead of"));
		}

		[Test]
		public void PairedLossRestoresTheSnapshotChampion()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Lost", controlOutcome: "Won",
				candidateFitness: 0.7, controlFitness: 0.4);

			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			var decision = promotion.ApplyDecision(run, plan, completion.Evaluation);

			Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Restore));
			Assert.That(File.ReadAllText(strategy), Is.EqualTo("champion"),
				"wins outrank the candidate's higher fitness");
		}

		[Test]
		public void IncompleteArmResultsAreUndefinedAndRestoreTheChampion()
		{
			var run = Candidate();
			var plan = PreparedArms(run);

			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			var decision = promotion.ApplyDecision(run, plan, completion.Evaluation);

			Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Undefined));
			Assert.That(File.ReadAllText(strategy), Is.EqualTo("champion"));
		}

		[Test]
		public void LiveWorkspaceChangeInvalidatesEvaluationWithoutRestoringUserEdits()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Lost", controlOutcome: "Won",
				candidateFitness: 0.3, controlFitness: 0.7);
			File.WriteAllText(strategy, "user edit after snapshot");

			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);

			Assert.Multiple(() =>
			{
				Assert.That(completion.RequiresReevaluation, Is.True);
				Assert.That(File.ReadAllText(strategy), Is.EqualTo("user edit after snapshot"));
				Assert.That(File.ReadAllText(
					Path.Combine(run.CandidateSourceDirectory, "Strategy.cs")),
					Is.EqualTo("candidate"));
				Assert.That(run.Manifest.Status, Is.EqualTo("candidate"));
				Assert.That(run.Manifest.Experiment.RequiresReevaluation, Is.True);
			});
		}

		[Test]
		public void ChangeBetweenEvaluationAndRestoreIsNeverOverwritten()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Lost", controlOutcome: "Won",
				candidateFitness: 0.3, controlFitness: 0.7);
			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			File.WriteAllText(strategy, "late user edit");

			var decision = promotion.ApplyDecision(run, plan, completion.Evaluation);

			Assert.That(decision, Is.EqualTo(ContinuousEvaluationDecision.Reevaluate));
			Assert.That(File.ReadAllText(strategy), Is.EqualTo("late user edit"));
			Assert.That(run.Manifest.Experiment.RequiresReevaluation, Is.True);
		}

		[Test]
		public void ChatAfterDecisionRequiresReevaluationBeforeTheNextFight()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Won", controlOutcome: "Lost",
				candidateFitness: 0.6, controlFitness: 0.5);
			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			promotion.ApplyDecision(run, plan, completion.Evaluation);

			var beforeChat = promotion.CaptureAgentChatFingerprint(run);
			File.WriteAllText(strategy, "chat edit");
			Assert.That(promotion.InvalidateAfterAgentChat(run, beforeChat), Is.True);

			Assert.That(promotion.ValidateForNextFight(run, out var reason), Is.False);
			Assert.That(reason, Does.Contain("Agent chat"));
			Assert.That(run.Manifest.Experiment.RequiresReevaluation, Is.True);
			Assert.That(File.ReadAllText(run.PromotionEvaluationPath),
				Does.Contain("\"verdict\": \"Undefined\""));
		}

		[Test]
		public void NoEditChatAfterRestorationKeepsTheRestoredChampion()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Lost", controlOutcome: "Won",
				candidateFitness: 0.3, controlFitness: 0.7);
			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			promotion.ApplyDecision(run, plan, completion.Evaluation);
			var beforeChat = promotion.CaptureAgentChatFingerprint(run);

			var invalidated = promotion.InvalidateAfterAgentChat(run, beforeChat);

			Assert.That(invalidated, Is.False);
			Assert.That(run.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Restored));
			Assert.That(promotion.ValidateForNextFight(run, out _), Is.True);
			Assert.That(File.ReadAllText(strategy), Is.EqualTo("champion"));
		}

		[Test]
		public void WorkspaceChangeAfterPromotionBlocksTheNextFight()
		{
			var run = Candidate();
			var plan = PreparedArms(run);
			WriteArmResults(run, candidateOutcome: "Won", controlOutcome: "Lost",
				candidateFitness: 0.6, controlFitness: 0.5);
			var completion = promotion.CompleteEvaluation(run, plan, exitCode: 0);
			promotion.ApplyDecision(run, plan, completion.Evaluation);
			File.WriteAllText(strategy, "changed after promotion");

			var valid = promotion.ValidateForNextFight(run, out var reason);

			Assert.That(valid, Is.False);
			Assert.That(reason, Does.Contain("changed after"));
			Assert.That(run.Manifest.Experiment.RequiresReevaluation, Is.True);
		}

		[Test]
		public void NextIterationCanUseADirtyPromotedChampionSnapshotAsControl()
		{
			var first = Candidate();
			var firstPlan = PreparedArms(first);
			WriteArmResults(first, "Won", "Lost", 0.6, 0.5);
			var firstResult = promotion.CompleteEvaluation(first, firstPlan, 0);
			promotion.ApplyDecision(first, firstPlan, firstResult.Evaluation);

			var second = TrainingRun.Create(project, new TrainingBattleConfiguration(),
				Path.Combine(root, "second-runs"));
			promotion.CaptureChampion(second);
			second.ContinuousAgentStarted("agent");
			File.WriteAllText(strategy, "second candidate");
			second.AgentFinished(0, 1);

			var secondPlan = promotion.PrepareEvaluation(repo, second);

			Assert.That(File.ReadAllText(
				Path.Combine(second.ControlSourceDirectory, "Strategy.cs")),
				Is.EqualTo("candidate"));
			Assert.That(secondPlan.ChampionFingerprint,
				Is.EqualTo(second.Manifest.Experiment.ChampionFingerprint));
		}

		TrainingRun Candidate(string suggestedPrompt = null)
		{
			var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
				Path.Combine(root, "runs"));
			promotion.CaptureChampion(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(strategy, "candidate");
			run.AgentFinished(0, 1, suggestedPrompt);
			return run;
		}

		ContinuousEvaluationPlan PreparedArms(TrainingRun run)
		{
			var plan = promotion.PrepareEvaluation(repo, run);
			WriteBuilt(plan, ContinuousEvaluationArm.Candidate, "candidate binary");
			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Candidate);
			WriteBuilt(plan, ContinuousEvaluationArm.Control, "control binary");
			promotion.CaptureBuiltArm(run, plan, ContinuousEvaluationArm.Control);
			return plan;
		}

		static void WriteBuilt(ContinuousEvaluationPlan plan, ContinuousEvaluationArm arm,
			string content, string fileName = "Expanded.AutoCnC.TestBot.dll")
		{
			var output = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateOutputDirectory
				: plan.ControlOutputDirectory;
			var result = arm == ContinuousEvaluationArm.Candidate
				? plan.CandidateBuildResultPath
				: plan.ControlBuildResultPath;
			var path = Path.Combine(output, fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, content);
			File.WriteAllText(result, JsonSerializer.Serialize(new
			{
				SchemaVersion = 1,
				TargetPath = path
			}));
		}

		static void WriteArmResults(TrainingRun run, string candidateOutcome,
			string controlOutcome, double candidateFitness, double controlFitness)
		{
			PairedBenchmarkEvaluator.WriteResult(run.CandidateBenchmarkResultPath,
				SingleArm("candidate-batch-20260919-110459", candidateOutcome,
					candidateFitness));
			PairedBenchmarkEvaluator.WriteResult(run.ControlBenchmarkResultPath,
				SingleArm("control-batch-20260919-110459", controlOutcome,
					controlFitness));
		}

		static BenchmarkResultDocument SingleArm(string batch, string outcome, double fitness)
			=> SingleArm(batch, outcome, fitness, "hard-16-9", "Hard");

		static BenchmarkResultDocument SingleArm(string batch, string outcome, double fitness,
			string benchmark, string difficulty)
		{
			return new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				Benchmark = benchmark,
				Batch = batch,
				Difficulty = difficulty,
				ExpectedMatchesPerArm = 1,
				Candidate = new BenchmarkArmResult
				{
					Arm = "candidate",
					Wins = outcome == "Won" ? 1 : 0,
					Played = 1,
					MedianFitness = fitness
				},
				Matches =
				[
					new BenchmarkMatchResult
					{
						RunId = batch,
						Arm = "candidate",
						Repeat = 1,
						Scenario = 1,
						Map = "map",
						Faction = "gdi",
						BotFaction = "nod",
						Seed = 123,
						Outcome = outcome,
						Fitness = fitness,
						EarnedPerSecond = 12,
						SpentPerSecond = 10,
						Exchange = 1.2,
						BuildingsKilled = 2,
						DurationSeconds = 600,
						Succeeded = true,
						Status = "completed",
						Error = ""
					}
				]
			};
		}
	}
}
