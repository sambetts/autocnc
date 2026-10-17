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
		Func<int, string> proposal;
		int improvements;
		bool candidateWins;

		string PromptHistoryRoot => Path.Combine(root, "prompt-history");

		string RepoTemplate => Path.Combine(repo.Root, "docs", "agent-prompt-template.md");

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
			proposal = null;
			improvements = 0;
			candidateWins = true;
			stageOutcome = null;
			assemblyHash = null;
			fightOutcome = null;
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
		public void ACandidateThatWinsWhatTheChampionLosesIsPromotedOnFreshSeeds()
		{
			UseSequentialGate();
			stageOutcome = (candidate, _, _) => candidate ? "Won" : "Lost";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var run = TrainingRun.Load(runDirectories.Single());
			var state = SequentialGate.Read(GatePath, "hard-16-9-fresh");
			var evaluation = JsonSerializer.Deserialize<PairedBenchmarkEvaluation>(
				File.ReadAllText(run.PromotionEvaluationPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
			Assert.Multiple(() =>
			{
				Assert.That(run.Manifest.Status, Is.EqualTo("promoted"));
				Assert.That(evaluation.Basis, Is.EqualTo("sequential"));
				Assert.That(evaluation.Sequential.CandidateOnlyWins, Is.EqualTo(8),
					"four decisive wins are not enough; the second stage settles it");
				Assert.That(StageJobs(candidate: true).Select(MatchCount), Is.EqualTo(new[] { 4, 4 }));
				Assert.That(StageJobs(candidate: false).Select(MatchCount), Is.EqualTo(new[] { 4, 4 }));
				Assert.That(StageJobs(candidate: true).All(job => Argument(job, "-Parallel") == "2"), Is.True);
				Assert.That(jobs.Where(job => job.ScriptPath == repo.BenchmarkBotScript)
					.All(job => job.Arguments.Contains("-AllowIncomplete")), Is.True,
					"the gate judges each pair, so a step with a crashed game must not fail whole");
				Assert.That(state.Pool.Matches.Select(m => m.Seed), Is.EqualTo(Enumerable.Range(1000001, 8)));
				Assert.That(state.Pool.Champion, Has.Count.EqualTo(8), "every champion game is cached");
				Assert.That(File.ReadAllText(source), Is.EqualTo("candidate-1"));
			});

			var record = ExperimentLedger.Read(LedgerPath).Single();
			Assert.That(record.Conclusion, Is.EqualTo("promoted"));
			Assert.That(record.ChangedFiles, Does.Contain("Strategy.cs"));
			Assert.That(record.CandidateGames, Is.EqualTo(8));
		}

		[Test]
		public void APromotedChampionIsAuditedOnTheHoldoutAndTheBaselineOnlyOnce()
		{
			UseSequentialGate();
			options.Rounds = 2;
			stageOutcome = (candidate, _, _) => candidate ? "Won" : "Lost";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var audits = jobs.Where(job => job.ScriptPath == repo.BenchmarkBotScript &&
				Path.GetFileName(Argument(job, "-ResultPath")).StartsWith("audit-", StringComparison.Ordinal)).ToList();
			Assert.That(audits.Select(job => Path.GetFileName(Argument(job, "-ResultPath"))),
				Is.EqualTo(new[] { "audit-baseline-result.json", "audit-champion-result.json", "audit-champion-result.json" }));
			Assert.That(audits.All(job => Argument(job, "-Benchmark") == "hard-holdout"), Is.True);
			var ledger = File.ReadAllLines(Path.Combine(options.RunsRoot, "ReferenceBot", "audit.jsonl"));
			Assert.That(ledger, Has.Length.EqualTo(3));
			Assert.That(messages, Has.Some.StartsWith("Holdout audit on hard-holdout, never used for promotion"));
		}

		[Test]
		public void TheSameChampionsCachedGamesAreReusedAndOneIsReplayedAsACanary()
		{
			UseSequentialGate();
			options.Rounds = 2;
			stageOutcome = (candidate, _, _) => candidate ? "Lost" : "Won";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			Assert.That(runDirectories.Select(path => TrainingRun.Load(path).Manifest.Status),
				Is.All.EqualTo("restored"));
			Assert.That(StageJobs(candidate: true).Select(MatchCount), Is.EqualTo(new[] { 4, 4 }),
				"losing four decisive games ends each round after one stage");
			Assert.That(StageJobs(candidate: false).Select(MatchCount), Is.EqualTo(new[] { 4, 1 }),
				"the second round replays one cached champion game, not the stage");
			var state = SequentialGate.Read(GatePath, "hard-16-9-fresh");
			Assert.That(state.Pool.Epoch, Is.EqualTo(1));
			Assert.That(state.Pool.CanaryChecks, Is.EqualTo(1));
			Assert.That(state.Pool.Reproducible, Is.True);
			Assert.That(ExperimentLedger.Read(LedgerPath).Select(r => r.Conclusion),
				Is.All.EqualTo("measured-worse"));
			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
		}

		[Test]
		public void AChampionGameThatDoesNotReplayAsCachedAbandonsTheCache()
		{
			UseSequentialGate();
			options.Rounds = 2;
			stageOutcome = (candidate, _, round) => candidate ? "Lost" : round == 1 ? "Won" : "Lost";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var state = SequentialGate.Read(GatePath, "hard-16-9-fresh");
			Assert.That(state.Pool.Reproducible, Is.False);
			Assert.That(state.Pool.CanaryFailure, Does.Contain("cached as Won"));
			Assert.That(StageJobs(candidate: false).Select(MatchCount), Is.EqualTo(new[] { 4, 1, 3, 4 }),
				"after the failed canary the rest of the stage, and the next, are played rather than read");
			Assert.That(messages, Has.Some.Contains("did not reproduce its cached result"));
		}

		[Test]
		public void ACanaryWhoseGameFailedToRunLeavesTheCacheTrusted()
		{
			UseSequentialGate();
			options.Rounds = 2;
			stageOutcome = (candidate, _, round) => candidate ? "Lost" : round == 1 ? "Won" : "Failed";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var state = SequentialGate.Read(GatePath, "hard-16-9-fresh");
			Assert.That(state.Pool.Reproducible, Is.True, "a crash is not evidence the cache is wrong");
			Assert.That(state.Pool.CanaryChecks, Is.Zero);
			Assert.That(StageJobs(candidate: false).Select(MatchCount), Is.EqualTo(new[] { 4, 1 }));
			Assert.That(ExperimentLedger.Read(LedgerPath).Select(r => r.Conclusion), Is.All.EqualTo("measured-worse"),
				"the cached champion game stood in for the one that failed to replay");
		}

		[Test]
		public void APromotionRetiresThePoolSoTheNewChampionIsJudgedOnSeedsNobodyPlayed()
		{
			UseSequentialGate();
			options.Rounds = 2;
			stageOutcome = (candidate, _, round) => candidate == (round == 1) ? "Won" : "Lost";
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var state = SequentialGate.Read(GatePath, "hard-16-9-fresh");
			Assert.That(state.Pool.Epoch, Is.EqualTo(2));
			Assert.That(state.Pool.Matches.Min(m => m.Seed), Is.EqualTo(1000009));
			Assert.That(state.Retired.Single().Reason, Is.EqualTo("the champion changed"));
			Assert.That(messages, Has.Some.Contains("Pool 1 was retired"));
		}

		[Test]
		public void TheCandidatesOwnChecksAreEvaluatedInItsOwnGames()
		{
			UseSequentialGate();
			stageOutcome = (candidate, _, _) => candidate ? "Lost" : "Won";
			improve = () =>
			{
				File.WriteAllText(source, "candidate-" + improvements);
				File.WriteAllText(Path.Combine(Path.GetDirectoryName(source), "checks.json"),
					"{\"hypothesis\":\"Pushes wait for siege\",\"checks\":[]}");
			};
			Run();

			var candidateStage = StageJobs(candidate: true).Single();
			Assert.That(Argument(candidateStage, "-ChecksFile"),
				Does.EndWith(Path.Combine("candidate-source", "checks.json")));
			Assert.That(StageJobs(candidate: false).Single().Arguments, Does.Not.Contain("-ChecksFile"));
			Assert.That(ExperimentLedger.Read(LedgerPath).Single().Hypothesis, Is.EqualTo("Pushes wait for siege"));
		}

		[Test]
		public void AWonFightIsFollowedByFreshSeedsUntilTheBotLosesAndTheAgentStudiesTheLoss()
		{
			options.MaxFightsPerRound = 4;
			fightOutcome = fight => fight < 3 ? "Won" : "Lost";
			Run();

			var studied = TrainingRun.Load(runDirectories[2]);
			Assert.Multiple(() =>
			{
				Assert.That(runDirectories, Has.Count.EqualTo(3), "it stops at the first loss");
				Assert.That(messages.Count(m => m.StartsWith("AUTOCNC_TRAINING_RUN=", StringComparison.Ordinal)),
					Is.EqualTo(3), "every fight is announced, so the nightly pause can cancel the one in progress");
				Assert.That(jobs.Where(job => job.ScriptPath == repo.TrainBotScript).Select(job => Argument(job, "-RunDirectory")),
					Is.EqualTo(new[] { runDirectories[2] }));
				Assert.That(studied.Manifest.Result.Outcome, Is.EqualTo("Lost"));
				Assert.That(studied.Manifest.EarlierFights.Select(fight => fight.RunId),
					Is.EqualTo(runDirectories.Take(2).Select(path => TrainingRun.Load(path).Manifest.Id)));
				Assert.That(studied.Manifest.EarlierFights.Select(fight => fight.Outcome), Is.All.EqualTo("Won"));
				Assert.That(File.ReadAllText(studied.PromptPath), Does.Contain("This is the first loss this round found"));
				Assert.That(runDirectories.Take(2).Select(path => TrainingRun.Load(path).Manifest.Experiment), Is.All.Null,
					"the wins passed over are finished fights, not experiments");
			});

			var record = ExperimentLedger.Read(LedgerPath).Single();
			Assert.That(record.FightOutcome, Is.EqualTo("Lost"));
			Assert.That(record.EarlierFights, Is.EqualTo(new[] { "Won", "Won" }));
			Assert.That(ExperimentLedger.ChampionRecord([record]), Does.Contain("won 2 of the 3"),
				"the champion's record still counts the wins the round passed over");
		}

		[Test]
		public void WhenEveryFightIsWonTheRoundStudiesTheLastAndSaysSo()
		{
			options.MaxFightsPerRound = 3;
			Run();

			Assert.That(runDirectories, Has.Count.EqualTo(3));
			var studied = TrainingRun.Load(runDirectories[2]);
			Assert.That(File.ReadAllText(studied.PromptPath), Does.Contain("The bot won all 3 fights"));
			Assert.That(messages, Has.Some.EqualTo("No loss in 3 fights; the round studies the last of them."));
		}

		[Test]
		public void APinnedSeedFightsOnceBecauseEveryFightWouldBeTheSame()
		{
			options.MaxFightsPerRound = 4;
			options.Seed = 42;
			Run();

			Assert.That(runDirectories, Has.Count.EqualTo(1));
			Assert.That(TrainingRun.Load(runDirectories[0]).Manifest.EarlierFights, Is.Null);
		}

		[Test]
		public void AFightStoppedWhileLookingForALossStopsTheLoopBeforeTheAgent()
		{
			options.MaxFightsPerRound = 4;
			var fights = 0;
			intercept = job => job.ScriptPath == repo.RunBotScript && ++fights == 2 ? 1 : null;

			Assert.That(() => Run(), Throws.Exception);
			Assert.That(runDirectories, Has.Count.EqualTo(2));
			Assert.That(jobs.Where(job => job.ScriptPath == repo.TrainBotScript), Is.Empty);
			Assert.That(messages, Has.Some.EqualTo("Run saved: " + runDirectories[1]));
		}

		[Test]
		public void TheNextPromptIsAdoptedOnlyAfterTheVerdict()
		{
			proposal = round => Template("lesson " + round);
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			var evaluation = messages.FindIndex(message => message.StartsWith("Evaluation:", StringComparison.Ordinal));
			var adoption = messages.FindIndex(message => message.StartsWith("Next prompt:", StringComparison.Ordinal));
			Assert.That(evaluation, Is.GreaterThanOrEqualTo(0));
			Assert.That(adoption, Is.GreaterThan(evaluation));
		}

		[Test]
		public void AFightWhoseAssemblyChangedUnderTheSameSourceIsFlagged()
		{
			options.Rounds = 2;
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			assemblyHash = () => "stale-" + runDirectories.Count;
			Run();

			Assert.That(messages, Has.Some.StartsWith("WARNING: this fight's bot assembly differs"),
				"the restored champion fought with bytes it did not produce before");
		}

		[Test]
		public void TheSameSourceFightingWithTheSameAssemblyIsNotFlagged()
		{
			options.Rounds = 2;
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			Run();

			Assert.That(messages, Has.None.StartsWith("WARNING: this fight's bot assembly differs"));
		}

		[Test]
		public void ARestoredFileIsNewerThanTheCandidateItReplaced()
		{
			File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			candidateWins = false;
			DateTime candidateWritten = default;
			improve = () =>
			{
				File.WriteAllText(source, "candidate");
				candidateWritten = File.GetLastWriteTimeUtc(source);
			};
			Run();

			Assert.That(File.ReadAllText(source), Is.EqualTo("champion"));
			Assert.That(File.GetLastWriteTimeUtc(source), Is.GreaterThanOrEqualTo(candidateWritten),
				"an incremental build must see the restored source as newer than the candidate's output");
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
		public void APromotionIsPushedToTheBranchItTracks()
		{
			RequireGitCheckout();
			var remote = RequireRemote();
			File.WriteAllText(Path.Combine(repo.Root, "docs", "agent-game-guide.md"), "edited while training ran");
			improve = () => File.WriteAllText(source, "candidate-" + improvements);

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "-1", "--format=%s"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(RemoteHead(remote), Is.EqualTo(Git("rev-parse", "HEAD")),
					"A promotion must reach the remote as soon as it is committed.");
				Assert.That(Git("status", "--porcelain", "--", "docs"), Does.Contain("agent-game-guide.md"),
					"Only commits are published; uncommitted work elsewhere stays where it was.");
				Assert.That(messages, Has.Some.StartsWith("Pushed "));
			});
		}

		[Test]
		public void AHandMadeCommitTravelsWithTheNextPromotion()
		{
			RequireGitCheckout();
			var remote = RequireRemote();
			File.WriteAllText(Path.Combine(Path.GetDirectoryName(project), "Opening.cs"), "adopted by hand");
			Git("add", "--", "bots/Reference/Opening.cs");
			Git("commit", "--quiet", "--message", "Adopted by hand");
			var handMade = Git("rev-parse", "HEAD");
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(RemoteHead(remote), Is.EqualTo(Git("rev-parse", "HEAD")));
				Assert.That(Git("merge-base", "--is-ancestor", handMade, RemoteHead(remote)), Is.Not.Null,
					"The whole branch is published, so nothing committed before the promotion is left behind.");
			});
		}

		[Test]
		public void ARestoredCandidateIsNeverPushed()
		{
			RequireGitCheckout();
			var remote = RequireRemote();
			var before = RemoteHead(remote);
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.That(RemoteHead(remote), Is.EqualTo(before));
			Assert.That(messages, Has.None.StartsWith("Pushed "));
		}

		[Test]
		public void NoPushCommitsButLeavesTheRemoteAlone()
		{
			RequireGitCheckout();
			var remote = RequireRemote();
			var before = RemoteHead(remote);
			options.Push = false;
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "-1", "--format=%s"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(RemoteHead(remote), Is.EqualTo(before));
				Assert.That(messages, Has.None.StartsWith("Pushed "));
			});
		}

		[Test]
		public void APushThatFailsIsReportedAndTrainingContinues()
		{
			RequireGitCheckout();
			var branch = Git("symbolic-ref", "--short", "HEAD");
			Git("remote", "add", "origin", Path.Combine(root, "missing remote.git"));
			Git("config", $"branch.{branch}.remote", "origin");
			Git("config", $"branch.{branch}.merge", "refs/heads/" + branch);
			options.Rounds = 2;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(improvements, Is.EqualTo(2), "A push is bookkeeping; it cannot stop training.");
				Assert.That(messages.Count(message => message.Contains("but did not push it to origin/", StringComparison.Ordinal)),
					Is.EqualTo(2));
				Assert.That(messages, Has.Some.Contains("It stays committed locally"));
				Assert.That(Git("log", "--format=%s").Split('\n')
					.Count(line => line.Trim() == "Promote the bot hard-16-9 preferred"), Is.EqualTo(2));
			});
		}

		[Test]
		public void ABranchThatTracksNothingIsCommittedAndReportedAsUnpushed()
		{
			RequireGitCheckout();
			improve = () => File.WriteAllText(source, "candidate");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "-1", "--format=%s"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(messages, Has.Some.Contains("does not track a remote branch"));
			});
		}

		[Test]
		public void APromotionCommitsThePromptInForceAndPushesItWithTheBot()
		{
			RequireGitCheckout();
			var remote = RequireRemote();
			improve = () => File.WriteAllText(source, "candidate");
			proposal = _ => Template("Lesson from a promoted round.");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "-1", "--format=%s", "HEAD~1"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(Git("log", "-1", "--format=%s"),
					Is.EqualTo($"Record the training prompt in force when {Git("rev-parse", "--short", "HEAD~1")} was promoted"));
				Assert.That(Git("show", "--name-only", "--format=", "HEAD"), Is.EqualTo("docs/agent-prompt-template.md"),
					"The prompt goes in its own commit, and nothing else rides along with it.");
				Assert.That(Git("show", "HEAD:docs/agent-prompt-template.md"), Does.Contain("Lesson from a promoted round."));
				Assert.That(Git("status", "--porcelain", "--", "docs/agent-prompt-template.md"), Is.Empty);
				Assert.That(RemoteHead(remote), Is.EqualTo(Git("rev-parse", "HEAD")));
				Assert.That(messages, Has.Some.StartsWith("Pushed ").And.Contains("and the prompt"));
			});
		}

		[Test]
		public void ARestoredRoundsPromptIsAdoptedButNotCommitted()
		{
			RequireGitCheckout();
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate");
			proposal = _ => Template("Lesson from a restored round.");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "--format=%s"), Is.EqualTo("checkout"));
				Assert.That(File.ReadAllText(RepoTemplate), Does.Contain("Lesson from a restored round."));
				Assert.That(Git("status", "--porcelain", "--", "docs/agent-prompt-template.md"),
					Does.Contain("agent-prompt-template.md"),
					"An adoption is committed with the next promotion, not on its own.");
			});
		}

		[Test]
		public void APromptTemplateOutsideTheCheckoutIsNeverCommitted()
		{
			RequireGitCheckout();
			var custom = Path.Combine(root, "custom prompt.md");
			File.WriteAllText(custom, Template("Custom baseline."));
			options.PromptTemplate = custom;
			improve = () => File.WriteAllText(source, "candidate");
			proposal = _ => Template("Custom lineage.");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(Git("log", "-1", "--format=%s"), Is.EqualTo("Promote the bot hard-16-9 preferred"));
				Assert.That(File.ReadAllText(custom), Does.Contain("Custom lineage."));
				Assert.That(messages, Has.None.Contains("prompt in force"));
			});
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

		[Test]
		public void EachRoundIsGivenThePromptTheRoundBeforeItProposed()
		{
			options.Rounds = 2;
			proposal = round => Template($"Round {round} lesson.");

			Run();

			var runs = runDirectories.Select(TrainingRun.Load).ToList();
			var revisions = new PromptHistory(PromptHistoryRoot).Read().Revisions;
			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(runs[0].PromptPath), Does.Not.Contain("lesson."),
					"The first round renders the template the loop started with.");
				Assert.That(File.ReadAllText(runs[1].PromptPath), Does.Contain("Round 1 lesson."),
					"The second round is given the first round's proposal.");
				Assert.That(File.ReadAllText(runs[1].PromptPath), Does.Contain("Nobody reviews it."),
					"A round must be told its proposal is adopted unreviewed.");
				Assert.That(File.ReadAllText(RepoTemplate), Does.Contain("Round 2 lesson."),
					"The template file carries the latest adoption into the next start.");
				Assert.That(File.ReadAllText(RepoTemplate), Does.Not.Contain("\r\n"),
					"An LF template stays LF, so its diff shows what the round changed.");
				Assert.That(runs.Select(run => run.Manifest.Agent.SuggestedNextPromptAccepted), Is.All.True);
				Assert.That(runs.Select(run => run.Manifest.Experiment.PromptRewriteFrozen), Is.All.False);
				Assert.That(revisions.Select(revision => revision.Origin),
					Is.EqualTo(new[] { "baseline", "continuous", "continuous" }));
				Assert.That(revisions.Skip(1).Select(revision => revision.RunId),
					Is.EqualTo(runs.Select(run => run.Manifest.Id)));
				Assert.That(messages.Count(message => message.StartsWith("Next prompt: adopted", StringComparison.Ordinal)),
					Is.EqualTo(2));
			});
		}

		[Test]
		public void ARestoredCandidateStillHandsItsProposalToTheNextRound()
		{
			options.Rounds = 2;
			candidateWins = false;
			improve = () => File.WriteAllText(source, "candidate-" + improvements);
			proposal = round => round == 1 ? Template("Lesson from a restored round.") : null;

			Run();

			var runs = runDirectories.Select(TrainingRun.Load).ToList();
			Assert.Multiple(() =>
			{
				Assert.That(runs[0].Manifest.Status, Is.EqualTo("restored"));
				Assert.That(runs[0].Manifest.Agent.SuggestedNextPromptAccepted, Is.True);
				Assert.That(File.ReadAllText(runs[1].PromptPath), Does.Contain("Lesson from a restored round."));
				Assert.That(runs[1].Manifest.Experiment.PromptRewriteFrozen, Is.True,
					"A round that proposed nothing leaves the prompt in force.");
				Assert.That(File.ReadAllText(RepoTemplate), Does.Contain("Lesson from a restored round."));
				Assert.That(messages, Has.Some.Contains("proposed none"));
			});
		}

		[Test]
		public void AProposalThatFailsValidationLeavesThePromptInForce()
		{
			var before = File.ReadAllText(RepoTemplate);
			proposal = _ => Template("No workspace rule.").Replace("Edit only", "Change", StringComparison.Ordinal);

			Run();

			var run = TrainingRun.Load(runDirectories.Single());
			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(RepoTemplate), Is.EqualTo(before));
				Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);
				Assert.That(run.Manifest.Experiment.PromptRewriteFrozen, Is.True);
				Assert.That(new PromptHistory(PromptHistoryRoot).Read().Revisions, Is.Empty);
				Assert.That(messages, Has.Some.Contains("the proposal is invalid"));
				Assert.That(run.Manifest.Status, Is.EqualTo("promoted"),
					"An unusable proposal is the prompt's problem, not a reason to stop training.");
			});
		}

		[Test]
		public void AnExplicitTemplateFileIsTheOneThatEvolvesAndKeepsItsLineEndings()
		{
			var custom = Path.Combine(root, "custom prompt.md");
			File.WriteAllText(custom, Template("Custom baseline.").Replace("\n", "\r\n") + "\r\n");
			options.PromptTemplate = custom;
			var repoBefore = File.ReadAllText(RepoTemplate);
			proposal = _ => Template("Custom lineage.");

			Run();

			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(custom), Does.Contain("Custom lineage."));
				Assert.That(File.ReadAllText(custom), Does.Contain("\r\n"));
				Assert.That(File.ReadAllText(custom).Replace("\r\n", "", StringComparison.Ordinal), Does.Not.Contain("\n"));
				Assert.That(File.ReadAllText(RepoTemplate), Is.EqualTo(repoBefore));
				Assert.That(File.Exists(custom + ".tmp"), Is.False);
			});
		}

		void Run(CancellationToken token = default) =>
			new TrainingLoopRunner(options, messages.Add, Execute, new PromptHistory(PromptHistoryRoot)).Run(token);

		/// <summary>A valid template carrying a line the rendered prompt can be searched for.</summary>
		static string Template(string lesson) =>
			"Edit only {workspace}. " + lesson + " {gameMechanics} {gameGuide} {gameRules} {fightManifest} " +
			"{battleLog} {telemetry} {decisionTrace} {summary} {units} {mapFacts} {checks} " +
			"{checkResults} {trend} {checkReport} {trendReport} {botAudit} {battle} {result} {sourceRevision}\n" +
			"{nextPromptContract}";

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
				var outcome = fightOutcome?.Invoke(runDirectories.Count) ?? "Won";
				var enemy = outcome == "Won" ? "Lost" : "Won";
				File.WriteAllText(Argument(job, "-Telemetry"),
					"seconds,player,bot,state,units,army,buildings,basevalue,cash,killed,lost\n" +
					$"600,You,0,{outcome},3,300,2,400,100,2,1\n600,Enemy,1,{enemy},0,0,0,0,0,1,2\n");
				File.WriteAllText(Argument(job, "-BattleLog"),
					$"seconds,event,player,detail\n0,player,You,side=you;faction=gdi\n600,over,You,result={outcome}\n");
				File.WriteAllText(Argument(job, "-DecisionTrace"), "{}\n");
				File.WriteAllText(Path.Combine(Path.GetDirectoryName(Argument(job, "-Telemetry")), "bot-assembly.json"),
					"{\"Assemblies\":[{\"Sha256\":\"" + (assemblyHash?.Invoke() ?? "same") + "\"}]}");
			}
			else if (job.ScriptPath == repo.ExportAgentRulesScript)
				File.WriteAllText(Argument(job, "-Output"), "{\"actors\":[]}");
			else if (job.ScriptPath == repo.TrainBotScript)
			{
				improvements++;
				improve?.Invoke();
				var proposed = proposal?.Invoke(improvements);
				if (proposed != null)
					return new TrainingScriptResult(0,
					[
						"Diagnosis, changes and checks.",
						TrainingAgent.NextPromptBegin,
						.. proposed.Split('\n'),
						TrainingAgent.NextPromptEnd
					]);
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
				var candidate = Path.GetFileName(Argument(job, "-ResultPath")).StartsWith("candidate", StringComparison.Ordinal);
				if (job.Arguments.Contains("-MatchesFile"))
					WriteStage(job, candidate);
				else
					WriteBenchmark(Argument(job, "-ResultPath"), candidate == candidateWins);
			}
			return new TrainingScriptResult(0, []);
		}

		/// <summary>How a fake sequential stage plays: (candidate arm?, scenario, round) to outcome.</summary>
		Func<bool, int, int, string> stageOutcome;

		/// <summary>The assembly hash a fake fight reports; null reports the same one every time.</summary>
		Func<string> assemblyHash;

		/// <summary>How the nth fake training fight (1-based, across the whole run) ends; null wins them all.</summary>
		Func<int, string> fightOutcome;

		void WriteStage(ScriptJob job, bool candidate)
		{
			using var document = JsonDocument.Parse(File.ReadAllText(Argument(job, "-MatchesFile")));
			var batch = Guid.NewGuid().ToString("N");
			var output = Argument(job, "-OutputDirectory");
			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1, Benchmark = Argument(job, "-Benchmark"), Batch = batch,
				Difficulty = Argument(job, "-Difficulty"), MaxGameSeconds = int.Parse(Argument(job, "-MaxGameSeconds")),
				Candidate = new BenchmarkArmResult { Arm = "candidate" }
			};
			foreach (var match in document.RootElement.GetProperty("matches").EnumerateArray())
			{
				var scenario = match.GetProperty("scenario").GetInt32();
				var outcome = stageOutcome(candidate, scenario, improvements);
				var failed = outcome == "Failed";
				var evidence = Path.Combine(output, $"{batch}-{scenario}", "evidence");
				Directory.CreateDirectory(evidence);
				ExperimentLedger.WriteReasonIdCounts(evidence, new System.Collections.Generic.Dictionary<string, int>
				{
					["assault.new-rule"] = candidate && scenario % 2 == 0 ? 3 : 0
				});
				result.Matches.Add(new BenchmarkMatchResult
				{
					RunId = $"{batch}-{scenario}", Evidence = evidence, Arm = "candidate", Repeat = 1, Scenario = scenario,
					Map = match.GetProperty("map").GetString(), Faction = match.GetProperty("faction").GetString(),
					BotFaction = match.GetProperty("botFaction").GetString(), Seed = match.GetProperty("seed").GetInt32(),
					Outcome = failed ? "Undefined" : outcome, Fitness = outcome == "Won" ? 1 : 0.4, EarnedPerSecond = 60,
					SpentPerSecond = 60, Exchange = 2, BuildingsKilled = 10, DurationSeconds = failed ? 0 : 900,
					Succeeded = !failed, Status = failed ? "Undefined" : "Completed",
					Error = failed ? "Battle process failed with exit code 1." : ""
				});
			}

			result.ExpectedMatchesPerArm = result.Matches.Count;
			result.Candidate.Played = result.Matches.Count;
			result.Candidate.Wins = result.Matches.Count(m => m.Outcome == "Won");
			PairedBenchmarkEvaluator.WriteResult(Argument(job, "-ResultPath"), result);
		}

		/// <summary>Switches the loop to a small sequential set: stages of 4, a pool of 8.</summary>
		void UseSequentialGate(int stagePairs = 4, int maxPairs = 8)
		{
			File.WriteAllText(Path.Combine(repo.ScriptsDir, "benchmarks.json"),
				"{\"sets\":[{\"name\":\"hard-16-9\",\"matches\":[" +
				"{\"map\":\"map\",\"faction\":\"gdi\",\"botFaction\":\"nod\",\"seed\":123}]}," +
				"{\"name\":\"hard-holdout\",\"difficulty\":\"Hard\",\"matches\":[" +
				"{\"map\":\"other\",\"faction\":\"gdi\",\"botFaction\":\"nod\",\"seed\":400001}]}," +
				"{\"name\":\"hard-16-9-fresh\",\"difficulty\":\"Hard\",\"maxGameSeconds\":2400,\"sequential\":{" +
				"\"maps\":[\"map\"],\"pairings\":[{\"faction\":\"gdi\",\"botFaction\":\"nod\"}," +
				"{\"faction\":\"nod\",\"botFaction\":\"gdi\"},{\"faction\":\"gdi\",\"botFaction\":\"gdi\"}," +
				"{\"faction\":\"nod\",\"botFaction\":\"nod\"}],\"seedBase\":1000001," +
				$"\"stagePairs\":{stagePairs},\"maxPairs\":{maxPairs}" + "}}]}");
			options.Benchmark = "hard-16-9-fresh";
			options.Parallel = 2;
		}

		string GatePath => Path.Combine(options.RunsRoot, "ReferenceBot", "gate", "hard-16-9-fresh.json");

		string LedgerPath => Path.Combine(options.RunsRoot, "ReferenceBot", ExperimentLedger.FileName);

		List<ScriptJob> StageJobs(bool candidate) => jobs.Where(job => job.ScriptPath == repo.BenchmarkBotScript &&
			Path.GetFileName(Argument(job, "-ResultPath")).StartsWith(candidate ? "candidate-stage" : "control-stage",
				StringComparison.Ordinal)).ToList();

		static int MatchCount(ScriptJob job)
		{
			using var document = JsonDocument.Parse(File.ReadAllText(Argument(job, "-MatchesFile")));
			return document.RootElement.GetProperty("matches").GetArrayLength();
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

		/// <summary>A bare repository the fake checkout's branch tracks, standing in for GitHub.</summary>
		string RequireRemote()
		{
			var remote = Path.Combine(root, "remote.git");
			if (Git("init", "--quiet", "--bare", remote) == null)
				Assert.Ignore("Git could not create a bare repository in this environment.");

			Git("remote", "add", "origin", remote);
			if (Git("push", "--quiet", "--set-upstream", "origin", "HEAD") == null)
				Assert.Ignore("Git could not push to a local bare repository in this environment.");
			return remote;
		}

		string RemoteHead(string remote) =>
			Git("ls-remote", remote, "refs/heads/" + Git("symbolic-ref", "--short", "HEAD"))?.Split('\t')[0];

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
