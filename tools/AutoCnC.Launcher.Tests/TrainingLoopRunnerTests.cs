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
using System.Threading;
using AutoCnC.Evidence;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TrainingLoopRunnerTests
	{
		string root;
		string project;
		string source;
		RepoLayout repo;
		TrainingLoopOptions options;
		readonly List<ScriptJob> jobs = [];
		readonly List<string> runDirectories = [];
		readonly List<string> foughtSources = [];
		readonly List<string> messages = [];
		Func<ScriptJob, int?> intercept;
		Action improve;
		int improvements;
		bool candidateWins;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC Training Loop Tests", Guid.NewGuid().ToString("N"));
			var checkout = Path.Combine(root, "checkout");
			var scripts = Path.Combine(checkout, "scripts");
			var tools = Path.Combine(checkout, "tools", "AutoCnC.Launcher");
			var docs = Path.Combine(checkout, "docs");
			var workspace = Path.Combine(checkout, "bots", "Reference");
			foreach (var directory in new[] { scripts, tools, docs, workspace, Path.Combine(checkout, "engine", "bin") })
				Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(checkout, "AutoCnC.sln"), "");
			File.WriteAllText(Path.Combine(checkout, "engine", "OpenRA.sln"), "");
			File.WriteAllText(Path.Combine(checkout, "engine", "bin", "OpenRA.dll"), "engine");
			File.WriteAllText(Path.Combine(scripts, "authoring-api.version"), RepoLayout.RequiredAuthoringApiVersion.ToString());
			foreach (var name in new[] { "run-bot.ps1", "train-bot.ps1", "benchmark-bot.ps1", "export-agent-rules.ps1" })
				File.WriteAllText(Path.Combine(scripts, name), "param()");
			File.WriteAllText(Path.Combine(tools, "build-experiment-arm.ps1"), "param()");
			File.WriteAllText(Path.Combine(scripts, "difficulties.json"),
				"{\"default\":\"Hard\",\"levels\":[{\"name\":\"Hard\"},{\"name\":\"Normal\"}]}");
			File.WriteAllText(Path.Combine(scripts, "benchmarks.json"),
				"{\"sets\":[{\"name\":\"hard-16-9\",\"matches\":[" +
				"{\"map\":\"map\",\"faction\":\"gdi\",\"botFaction\":\"nod\",\"seed\":123}]}]}");
			File.WriteAllText(Path.Combine(docs, "agent-game-guide.md"), "guide");
			File.WriteAllText(Path.Combine(docs, "agent-mechanics.md"), "mechanics");
			File.WriteAllText(Path.Combine(docs, "agent-prompt-template.md"),
				"Edit only {workspace}. {gameMechanics} {gameGuide} {gameRules} {fightManifest} " +
				"{battleLog} {telemetry} {decisionTrace} {summary} {units} {mapFacts} {checks} " +
				"{checkResults} {trend} {checkReport} {trendReport} {botAudit} {battle} {result} {sourceRevision}\n" +
				"{nextPromptContract}\n");
			project = Path.Combine(workspace, "ReferenceBot.csproj");
			source = Path.Combine(workspace, "Strategy.cs");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(source, "champion");
			repo = RepoLayout.For(checkout);
			options = new TrainingLoopOptions { RepoRoot = checkout, RunsRoot = Path.Combine(root, "runs"), Rounds = 1 };
			jobs.Clear();
			runDirectories.Clear();
			foughtSources.Clear();
			messages.Clear();
			intercept = null;
			improve = null;
			improvements = 0;
			candidateWins = true;
		}

		[TearDown]
		public void TearDown()
		{
			foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
			Directory.Delete(root, true);
		}

		[Test]
		public void RepeatsWithFreshEvidenceAndSnapshotsAndHonorsRoundLimit()
		{
			options.Rounds = 3;
			Run();

			Assert.That(improvements, Is.EqualTo(3));
			Assert.That(runDirectories.Distinct().Count(), Is.EqualTo(3));
			Assert.That(jobs.Count(job => job.ScriptPath == repo.BenchmarkBotScript), Is.Zero,
				"No-change rounds retain the champion without unnecessary benchmarks.");
			foreach (var directory in runDirectories)
			{
				var run = TrainingRun.Load(directory);
				Assert.Multiple(() =>
				{
					Assert.That(run.Manifest.Status, Is.EqualTo("promoted"));
					Assert.That(run.Manifest.Result.Outcome, Is.EqualTo("Won"));
					Assert.That(File.Exists(run.SnapshotManifestPath), Is.True);
					Assert.That(File.Exists(run.FightManifestPath), Is.True);
					Assert.That(run.Manifest.Experiment.PromptRewriteFrozen, Is.True);
					Assert.That(run.IsBusy, Is.False);
				});
			}
			Assert.That(jobs.Where(job => job.ScriptPath == repo.TrainBotScript)
				.Select(job => Argument(job, "-RunDirectory")), Is.EqualTo(runDirectories));
		}

		[TestCase(true, "promoted")]
		[TestCase(false, "restored")]
		public void BenchmarksBothImmutableArmsAndFightsTheSelectedChampion(bool wins, string status)
		{
			options.Rounds = 2;
			candidateWins = wins;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			Assert.That(foughtSources, Is.EqualTo(new[] { "champion", wins ? "candidate-1" : "champion" }));
			Assert.That(File.ReadAllText(source), Is.EqualTo(wins ? "candidate-2" : "champion"));
			Assert.That(runDirectories.Select(path => TrainingRun.Load(path).Manifest.Status), Is.All.EqualTo(status));
			Assert.That(jobs.Count(job => job.ScriptPath == repo.BenchmarkBotScript), Is.EqualTo(4));
			foreach (var job in jobs.Where(job => job.ScriptPath == repo.BenchmarkBotScript))
			{
				Assert.That(Argument(job, "-BattleBot"), Does.EndWith("Bot.dll"));
				Assert.That(Argument(job, "-BattleBot"), Does.Not.StartWith(Path.GetDirectoryName(project)));
				Assert.That(Argument(job, "-Benchmark"), Is.EqualTo("hard-16-9"));
				Assert.That(Argument(job, "-Difficulty"), Is.EqualTo("Hard"));
			}
		}

		[Test]
		public void PromotionIsCommittedToTheBotWorkspaceAndNothingElse()
		{
			RequireGitCheckout();
			var outside = Path.Combine(repo.Root, "docs", "agent-game-guide.md");
			File.WriteAllText(outside, "edited while training ran");
			improve = () => File.WriteAllText(source, "candidate-" + improvements);

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("show", "--name-only", "--format=", "HEAD").Split('\n')
					.Select(line => line.Trim()).Where(line => line.Length > 0),
					Is.EqualTo(new[] { "bots/Reference/Strategy.cs" }),
					"An applied engine patch or an unrelated edit must not ride along with a promotion.");
				Assert.That(Git("log", "-1", "--format=%s"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(Git("show", "HEAD:bots/Reference/Strategy.cs"), Is.EqualTo("candidate-1"));
				Assert.That(Git("status", "--porcelain", "--", "docs"), Does.Contain("agent-game-guide.md"));
				Assert.That(messages, Has.Some.Contains("Committed the promoted bot as"));
			});
		}

		[Test]
		public void ARestoredCandidateIsNeverCommitted()
		{
			RequireGitCheckout();
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.That(Git("log", "--format=%s"), Is.EqualTo("checkout"));
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
		}

		[Test]
		public void NoCommitLeavesVersionControlAloneButStillPromotes()
		{
			RequireGitCheckout();
			options.Commit = false;
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.That(Git("log", "--format=%s"), Is.EqualTo("checkout"));
			Assert.That(File.ReadAllText(source), Is.EqualTo("candidate"));
			Assert.That(messages, Has.None.Contains("Committed"));
		}

		[Test]
		public void APromotionThatCannotBeCommittedIsReportedAndTrainingContinues()
		{
			options.Rounds = 2;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);

			Run();

			Assert.That(improvements, Is.EqualTo(2),
				"Bookkeeping cannot stop a loop whose promotion was already measured and applied.");
			Assert.That(messages, Has.Some.Contains("Promoted, but not committed, because"));
		}

		[Test]
		public void ThePromotionCommitMessageCarriesTheEvidenceThatAllowedIt()
		{
			var message = TrainingLoopRunner.PromotionCommitMessage(new PairedBenchmarkEvaluation
			{
				Benchmark = "hard-16-9", Batch = "promotion-01",
				Verdict = PromotionVerdicts.Promote,
				Reason = "Candidate won 2 paired matches; control won 1, with no paired fitness regression.",
				CandidateWins = 2, ControlWins = 1,
				CandidateFitnessPairs = 5, ControlFitnessPairs = 2, TiedFitnessPairs = 1,
				MedianPairedFitnessDelta = 0.0126, PairsCompared = 8, DroppedPairs = 1
			});

			Assert.Multiple(() =>
			{
				Assert.That(message, Does.StartWith("Promote the bot hard-16-9 preferred\n\n"));
				Assert.That(message, Does.Contain("Candidate won 2 paired matches"));
				Assert.That(message, Does.Contain("wins             2 - 1"));
				Assert.That(message, Does.Contain("median delta     +0.0126"));
				Assert.That(message, Does.Contain("5 better, 2 worse, 1 tied"));
				Assert.That(message, Does.Contain("compared         8, 1 dropped"));
				Assert.That(message, Does.Contain("promotion-01"));
			});
		}

		[TestCase(true)]
		[TestCase(false)]
		public void ScriptJobsCarryTheChosenColourSetting(bool color)
		{
			options.Color = color;

			Run();

			Assert.That(jobs, Is.Not.Empty);
			Assert.That(jobs.Select(job => job.PreserveColor), Is.All.EqualTo(color));
		}

		[Test]
		public void FailedImprovementRestoresChampionAndDoesNotFightAgain()
		{
			options.Rounds = 3;
			intercept = job =>
			{
				if (job.ScriptPath != repo.TrainBotScript)
					return null;
				File.WriteAllText(source, "broken candidate");
				return 1;
			};
			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("Improvement failed"));
			Assert.That(runDirectories, Has.Count.EqualTo(1));
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			Assert.That(TrainingRun.Load(runDirectories[0]).Manifest.Status, Is.EqualTo("restored"));
		}

		[TestCase("run-bot.ps1")]
		[TestCase("export-agent-rules.ps1")]
		public void FailedPrerequisiteNeverInvokesTheAgent(string failedScript)
		{
			intercept = job => Path.GetFileName(job.ScriptPath) == failedScript ? 1 : null;
			Assert.That(() => Run(), Throws.InvalidOperationException);
			Assert.That(improvements, Is.Zero);
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			Assert.That(TrainingRun.Load(runDirectories.Single()).Manifest.CompletedUtc, Is.Not.Null);
		}

		[Test]
		public void MissingBattleEvidenceNeverInvokesTheAgent()
		{
			intercept = job => job.ScriptPath == repo.RunBotScript ? 0 : null;
			Assert.That(() => Run(), Throws.TypeOf<InvalidDataException>());
			Assert.That(improvements, Is.Zero);
		}

		[TestCase("build-experiment-arm.ps1")]
		[TestCase("benchmark-bot.ps1")]
		public void FailedEvaluationRestoresAndStops(string failedScript)
		{
			improve = () => File.WriteAllText(source, "candidate");
			intercept = job => Path.GetFileName(job.ScriptPath) == failedScript ? 1 : null;
			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("invalid"));
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			var run = TrainingRun.Load(runDirectories.Single());
			Assert.That(run.Manifest.Status, Is.EqualTo("restored"));
			Assert.That(run.Manifest.Experiment.Decision, Is.EqualTo("Undefined"));
		}

		[Test]
		public void MissingEvaluationResultIsNotPromoted()
		{
			improve = () => File.WriteAllText(source, "candidate");
			intercept = job => job.ScriptPath == repo.BenchmarkBotScript ? 0 : null;
			Assert.That(() => Run(), Throws.InvalidOperationException);
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			Assert.That(TrainingRun.Load(runDirectories.Single()).Manifest.Status, Is.EqualTo("restored"));
		}

		[Test]
		public void CancellationDuringImprovementPreservesEditsUntilExplicitRestore()
		{
			using var stop = new CancellationTokenSource();
			improve = () =>
			{
				File.WriteAllText(source, "unfinished candidate");
				stop.Cancel();
			};
			Assert.That(() => Run(stop.Token), Throws.TypeOf<OperationCanceledException>());
			var run = TrainingRun.Load(runDirectories.Single());
			Assert.That(run.Manifest.Agent.Cancelled, Is.True);
			Assert.That(run.Manifest.Status, Is.EqualTo("experiment-aborted"));
			Assert.That(run.CanResumeContinuousEvaluation, Is.False);
			Assert.That(File.ReadAllText(source), Is.EqualTo("unfinished candidate"));

			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("unfinished experiment")
				.And.Message.Contains("-DeleteBlockingRun"));
			File.Delete(project);
			options.RestoreRun = run.RunDirectory;
			Run();
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			Assert.That(File.Exists(project), Is.True, "Recovery also works when the candidate removed its project.");
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Status, Is.EqualTo("restored"));
		}

		[Test]
		public void DeleteBlockingRunDiscardsAnInterruptedImprovementAndKeepsTraining()
		{
			using var stop = new CancellationTokenSource();
			var added = Path.Combine(Path.GetDirectoryName(project), "Added.cs");
			improve = () =>
			{
				File.WriteAllText(source, "unfinished candidate");
				File.WriteAllText(added, "half-written by a cancelled agent");
				stop.Cancel();
			};
			Assert.That(() => Run(stop.Token), Throws.TypeOf<OperationCanceledException>());
			var blocking = runDirectories.Single();
			Assert.That(messages, Has.Some.Contains("-DeleteBlockingRun"));

			improve = null;
			messages.Clear();
			options.DeleteBlockingRun = true;
			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Directory.Exists(blocking), Is.False);
				Assert.That(File.Exists(added), Is.False);
				Assert.That(foughtSources.Last(), Is.EqualTo("champion"),
					"The next round must fight the champion, not the cancelled agent's edits.");
				Assert.That(runDirectories, Has.Count.EqualTo(2));
				Assert.That(TrainingRun.Load(runDirectories.Last()).Manifest.Status, Is.EqualTo("promoted"));
				Assert.That(messages, Has.Some.Contains("Discarding 2 source edit(s)"));
				Assert.That(messages, Has.Some.EqualTo("  added Added.cs"));
				Assert.That(messages, Has.Some.EqualTo("  modified Strategy.cs"));
				Assert.That(messages, Has.Some.Contains("Deleted the blocking run"));
			});
		}

		[TestCase(false)]
		[TestCase(true)]
		public void VerifiedCandidateResumesEvaluationBeforeAnotherFight(bool deleteBlockingRun)
		{
			using var stop = new CancellationTokenSource();
			improve = () => File.WriteAllText(source, "candidate");
			intercept = job =>
			{
				if (job.ScriptPath == repo.BuildExperimentArmScript)
				{
					stop.Cancel();
					return 0;
				}
				return null;
			};
			Assert.That(() => Run(stop.Token), Throws.TypeOf<OperationCanceledException>());
			var interrupted = TrainingRun.Load(runDirectories.Single());
			Assert.That(interrupted.CanResumeContinuousEvaluation, Is.True);
			jobs.Clear();
			intercept = null;
			improve = null;
			options.DeleteBlockingRun = deleteBlockingRun;
			Run();
			Assert.That(jobs[0].ScriptPath, Is.EqualTo(repo.BuildExperimentArmScript));
			Assert.That(TrainingRun.Load(interrupted.RunDirectory).Manifest.Status, Is.EqualTo("promoted"));
			Assert.That(runDirectories, Has.Count.EqualTo(2));
			Assert.That(foughtSources.Last(), Is.EqualTo("candidate"));
		}

		[Test]
		public void UnlimitedRoundsContinueUntilCancelledAndReleaseTheWorkspaceLock()
		{
			options.Rounds = 0;
			using var stop = new CancellationTokenSource();
			improve = () => { if (improvements == 3) stop.Cancel(); };
			Assert.That(() => Run(stop.Token), Throws.TypeOf<OperationCanceledException>());
			Assert.That(improvements, Is.EqualTo(3));
			using var exclusive = new FileStream(TrainingWorkspaceMutation.LockPathFor(Path.GetDirectoryName(project)),
				FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			Assert.That(exclusive.CanWrite, Is.True);
		}

		[Test]
		public void ActiveExperimentCannotBeRestoredOrReplaced()
		{
			var run = TrainingRun.Create(project, options.Battle(), options.RunsRoot);
			new ContinuousPromotionRunner().CaptureChampion(run);
			run.ContinuousAgentStarted("fake");
			Assert.That(() => Run(), Throws.InvalidOperationException);
			Assert.That(jobs, Is.Empty);
			options.DeleteBlockingRun = true;
			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("still running"));
			Assert.That(Directory.Exists(run.RunDirectory), Is.True);
			Assert.That(jobs, Is.Empty);
			options.RestoreRun = run.RunDirectory;
			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("active worker"));
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
		}

		[TestCase(-1)]
		[TestCase(-10)]
		public void InvalidRoundLimitFailsBeforeAnyWorker(int rounds)
		{
			options.Rounds = rounds;
			Assert.That(() => Run(), Throws.ArgumentException);
			Assert.That(jobs, Is.Empty);
		}

		[Test]
		public void InvalidBenchmarkFailsBeforeAnyWorker()
		{
			options.Benchmark = "missing";
			Assert.That(() => Run(), Throws.InvalidOperationException.With.Message.Contains("not defined"));
			Assert.That(jobs, Is.Empty);
		}

		[Test]
		public void EvidenceCannotBeWrittenIntoItsOwnSnapshotWorkspace()
		{
			options.RunsRoot = Path.Combine(Path.GetDirectoryName(project), "runs");
			Assert.That(() => Run(), Throws.ArgumentException.With.Message.Contains("outside"));
			Assert.That(jobs, Is.Empty);
		}

		[TestCase(true)]
		[TestCase(false)]
		public void BattleAndCustomAgentSettingsAreForwarded(bool standardInput)
		{
			options.Map = "custom.oramap";
			options.Difficulty = "Normal";
			options.Faction = "nod";
			options.BotFaction = "gdi";
			options.Opponents = 2;
			options.Seed = 4321;
			options.MaxGameSeconds = 900;
			options.AgentConfiguration = Path.Combine(root, "custom agent.json");
			string[] arguments = standardInput ? ["--model", "custom"] : ["--input", "{promptFile}"];
			File.WriteAllText(options.AgentConfiguration, JsonSerializer.Serialize(new
			{
				command = "other-agent", arguments, stdin = "{prompt}"
			}));
			Run();
			var fight = jobs.Single(job => job.ScriptPath == repo.RunBotScript);
			Assert.Multiple(() =>
			{
				Assert.That(Argument(fight, "-Map"), Is.EqualTo(options.Map));
				Assert.That(Argument(fight, "-Difficulty"), Is.EqualTo("Normal"));
				Assert.That(Argument(fight, "-Opponents"), Is.EqualTo("2"));
				Assert.That(Argument(fight, "-Seed"), Is.EqualTo("4321"));
				Assert.That(Argument(fight, "-MaxGameSeconds"), Is.EqualTo("900"));
				Assert.That(Argument(fight, "-ExecutionMode"), Is.EqualTo("Headless"));
				Assert.That(fight.CancellationFile, Is.Not.Null);
				Assert.That(fight.WorkerOwnershipFile, Is.Not.Null);
			});
			var run = TrainingRun.Load(runDirectories.Single());
			var agent = JsonSerializer.Deserialize<TrainingAgentConfiguration>(File.ReadAllText(run.AgentConfigurationPath));
			Assert.That(agent.Command, Is.EqualTo("other-agent"));
			Assert.That(agent.Arguments, Is.EqualTo(arguments));
			Assert.That(agent.Stdin, Is.EqualTo(standardInput ? "{prompt}" : null));
		}

		[Test]
		public void EditsDuringEvaluationAreReevaluatedWithoutBeingOverwritten()
		{
			improve = () => File.WriteAllText(source, "candidate");
			var edited = false;
			intercept = job =>
			{
				if (!edited && job.ScriptPath == repo.BenchmarkBotScript &&
					Argument(job, "-ResultPath").Contains("control-result", StringComparison.Ordinal))
				{
					edited = true;
					File.WriteAllText(source, "manual edit during evaluation");
				}
				return null;
			};
			Run();
			Assert.That(File.ReadAllText(source), Is.EqualTo("manual edit during evaluation"));
			Assert.That(jobs.Count(job => job.ScriptPath == repo.BenchmarkBotScript), Is.EqualTo(4));
			var run = TrainingRun.Load(runDirectories.Single());
			Assert.That(run.Manifest.Status, Is.EqualTo("promoted"));
			Assert.That(run.Manifest.Experiment.EvaluationAttempt, Is.EqualTo(2));
		}

		[Test]
		public void RenderedFightsKeepTheirSpeedAndDoNotPassHeadlessOnlyOptions()
		{
			options.ExecutionMode = BattleExecutionModes.Rendered;
			options.GameSpeed = "fast";
			Run();
			var fight = jobs.Single(job => job.ScriptPath == repo.RunBotScript);
			Assert.That(Argument(fight, "-ExecutionMode"), Is.EqualTo("Rendered"));
			Assert.That(Argument(fight, "-GameSpeed"), Is.EqualTo("fast"));
			Assert.That(fight.CancellationFile, Is.Null);
			Assert.That(fight.Arguments, Does.Not.Contain("-PerformanceReport"));
		}

		void Run(CancellationToken token = default) =>
			new TrainingLoopRunner(options, messages.Add, Execute).Run(token);

		TrainingScriptResult Execute(ScriptJob job, CancellationToken token)
		{
			jobs.Add(job);
			if (job.ScriptPath == repo.RunBotScript)
			{
				var directory = Path.GetDirectoryName(Path.GetDirectoryName(Argument(job, "-Telemetry")));
				runDirectories.Add(directory);
				foughtSources.Add(File.ReadAllText(source));
				Assert.That(File.Exists(TrainingRun.Load(directory).SnapshotManifestPath), Is.True);
			}
			if (intercept?.Invoke(job) is int code)
				return new TrainingScriptResult(code, []);

			if (job.ScriptPath == repo.RunBotScript)
			{
				File.WriteAllText(Argument(job, "-Telemetry"),
					"seconds,player,bot,state,units,army,buildings,basevalue,cash,killed,lost\n" +
					"600,You,0,Won,3,300,2,400,100,2,1\n600,Enemy,1,Lost,0,0,0,0,0,1,2\n");
				File.WriteAllText(Argument(job, "-BattleLog"),
					"seconds,event,player,detail\n0,player,You,side=you;faction=gdi\n600,over,You,result=Won\n");
				File.WriteAllText(Argument(job, "-DecisionTrace"), "{}\n");
			}
			else if (job.ScriptPath == repo.ExportAgentRulesScript)
				File.WriteAllText(Argument(job, "-Output"), "{\"actors\":[]}");
			else if (job.ScriptPath == repo.TrainBotScript)
			{
				improvements++;
				improve?.Invoke();
			}
			else if (job.ScriptPath == repo.BuildExperimentArmScript)
			{
				var output = Argument(job, "-OutputDirectory");
				Directory.CreateDirectory(output);
				var assembly = Path.Combine(output, "Bot.dll");
				File.WriteAllText(assembly, output);
				File.WriteAllText(Argument(job, "-ResultPath"), JsonSerializer.Serialize(new { SchemaVersion = 1, TargetPath = assembly }));
			}
			else if (job.ScriptPath == repo.BenchmarkBotScript)
			{
				var candidate = Argument(job, "-ResultPath").Contains("candidate-result", StringComparison.Ordinal);
				WriteBenchmark(Argument(job, "-ResultPath"), candidate == candidateWins);
			}
			return new TrainingScriptResult(0, []);
		}

		static string Argument(ScriptJob job, string name) =>
			job.Arguments[job.Arguments.ToList().IndexOf(name) + 1];

		/// <summary>Turns the fake checkout into a real repository, so commits can be asserted.</summary>
		void RequireGitCheckout()
		{
			if (Git("init", "--quiet") == null)
				Assert.Ignore("Git is not available on this machine.");

			Git("config", "user.email", "training@autocnc.test");
			Git("config", "user.name", "AutoC&C Training Tests");
			Git("config", "commit.gpgsign", "false");
			Git("add", "--all", "--", ".");
			if (Git("commit", "--message", "checkout") == null)
				Assert.Ignore("Git could not commit in this environment.");
		}

		string Git(params string[] arguments)
		{
			try
			{
				var start = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "git", WorkingDirectory = repo.Root, UseShellExecute = false,
					CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
				};
				start.ArgumentList.Add("-C");
				start.ArgumentList.Add(repo.Root);
				foreach (var argument in arguments)
					start.ArgumentList.Add(argument);

				using var process = System.Diagnostics.Process.Start(start);
				var error = process.StandardError.ReadToEndAsync();
				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				error.GetAwaiter().GetResult();
				return process.ExitCode == 0 ? output.Trim() : null;
			}
			catch (System.ComponentModel.Win32Exception)
			{
				return null;
			}
		}

		static void WriteBenchmark(string path, bool won)
		{
			var batch = Guid.NewGuid().ToString("N");
			PairedBenchmarkEvaluator.WriteResult(path, new BenchmarkResultDocument
			{
				SchemaVersion = 1, Benchmark = "hard-16-9", Batch = batch, Difficulty = "Hard",
				MaxGameSeconds = 5400, ExpectedMatchesPerArm = 1,
				Candidate = new BenchmarkArmResult { Arm = "candidate", Wins = won ? 1 : 0, Played = 1, MedianFitness = won ? 0.7 : 0.3 },
				Matches =
				[
					new BenchmarkMatchResult
					{
						RunId = batch, Arm = "candidate", Repeat = 1, Scenario = 1, Map = "map",
						Faction = "gdi", BotFaction = "nod", Seed = 123, Outcome = won ? "Won" : "Lost",
						Fitness = won ? 0.7 : 0.3, EarnedPerSecond = 12, SpentPerSecond = 10,
						Exchange = 1.2, BuildingsKilled = 2, DurationSeconds = 600,
						Succeeded = true, Status = "completed", Error = ""
					}
				]
			});
		}
	}
}
