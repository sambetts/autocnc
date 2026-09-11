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
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TrainingRunTests
	{
		string root;
		string workspace;
		string project;
		string runs;
		string gameGuide;
		string gameRules;
		string promptTemplate;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC.Launcher.Tests", Guid.NewGuid().ToString("N"));
			workspace = Path.Combine(root, "My Bot");
			runs = Path.Combine(root, "runs");
			gameGuide = Path.Combine(root, "agent-game-guide.md");
			gameRules = Path.Combine(root, "game-rules.json");
			promptTemplate = string.Join(Environment.NewLine,
			[
				"Improve the bot. Edit only files under {workspace}.",
				"Read {gameGuide} and {gameRules}.",
				"Evidence: {fightManifest}, {battleLog}, {telemetry}, {decisionTrace}.",
				"Fight: {battle}. Result: {result}. Revision: {sourceRevision}.",
				"{nextPromptContract}"
			]);
			project = Path.Combine(workspace, "MyBot.csproj");
			Directory.CreateDirectory(workspace);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "before");
			File.WriteAllText(Path.Combine(workspace, "Strategy.custom"), "custom before");
			File.WriteAllText(Path.Combine(workspace, "Notes.md"), "keep me");
			File.WriteAllText(gameGuide, "# Test game guide\nOnly use visible enemy information.");
			File.WriteAllText(gameRules, "{\"actors\":[{\"id\":\"e1\",\"hitPoints\":50}]}");
			Directory.CreateDirectory(Path.Combine(workspace, "bin"));
			File.WriteAllText(Path.Combine(workspace, "bin", "ignored.txt"), "build output");
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
				Directory.Delete(root, true);
		}

		[Test]
		public void EveryFightGetsAUniqueDurableDirectory()
		{
			var first = NewRun();
			var second = NewRun();

			Assert.That(second.RunDirectory, Is.Not.EqualTo(first.RunDirectory));
			Assert.That(File.Exists(first.ManifestPath), Is.True);
			Assert.That(Directory.Exists(first.EvidenceDirectory), Is.True);
			Assert.That(first.TelemetryPath, Does.StartWith(first.EvidenceDirectory));
			Assert.That(first.Manifest.BotProject, Is.EqualTo(project));
			Assert.That(first.Manifest.SourceRevision, Does.StartWith("tree:"));
		}

		[Test]
		public void DiscoverySkipsAnIncompatibleRememberedCheckout()
		{
			var oldRoot = Path.Combine(root, "old-checkout");
			var currentRoot = Path.Combine(root, "current-checkout");
			CreateCheckout(oldRoot, authoringApi: null);
			CreateCheckout(currentRoot, authoringApi: "5");
			var nestedStart = Path.Combine(currentRoot, "tools", "AutoCnC.Launcher", "bin");
			Directory.CreateDirectory(nestedStart);

			var found = RepoLayout.Discover(oldRoot, [nestedStart]);

			Assert.That(found.Root, Is.EqualTo(currentRoot));
			Assert.That(found.SupportsTraining, Is.True);
			Assert.That(RepoLayout.For(oldRoot).SupportsTraining, Is.False);
		}

		[Test]
		public void SnapshotReportsAndRestoresOnlyEditableSource()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);

			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "after");
			File.WriteAllText(Path.Combine(workspace, "Strategy.custom"), "custom after");
			File.Delete(Path.Combine(workspace, "Notes.md"));
			File.WriteAllText(Path.Combine(workspace, "Added.cs"), "added");
			File.WriteAllText(Path.Combine(workspace, "bin", "ignored.txt"), "changed build output");

			var changes = WorkspaceSnapshot.Compare(run);

			Assert.That(changes.Select(c => (c.Kind, c.RelativePath)), Is.EquivalentTo(new[]
			{
				("added", "Added.cs"),
				("deleted", "Notes.md"),
				("modified", "Strategy.cs"),
				("modified", "Strategy.custom")
			}));

			WorkspaceSnapshot.Restore(run);

			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")), Is.EqualTo("before"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.custom")), Is.EqualTo("custom before"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Notes.md")), Is.EqualTo("keep me"));
			Assert.That(File.Exists(Path.Combine(workspace, "Added.cs")), Is.False);
			Assert.That(File.ReadAllText(Path.Combine(workspace, "bin", "ignored.txt")), Is.EqualTo("changed build output"));
		}

		[Test]
		public void AgentPacketIsProviderNeutralAndReferencesFightEvidence()
		{
			var run = NewRun();

			TrainingAgent.Prepare(run, gameGuide, gameRules,
				promptTemplate,
				"other-agent", ["--input", "{promptFile}"]);

			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.DecisionTracePath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.GameGuidePath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.GameRulesPath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(TrainingAgent.NextPromptBegin));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.Manifest.BotDirectory));
			Assert.That(File.ReadAllText(run.GameGuidePath), Is.EqualTo(File.ReadAllText(gameGuide)));
			Assert.That(File.ReadAllText(run.GameRulesPath), Is.EqualTo(File.ReadAllText(gameRules)));
			Assert.That(File.Exists(run.FightManifestPath), Is.True);
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("other-agent"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("{promptFile}"));

			TrainingAgent.Prepare(run, gameGuide, gameRules,
				promptTemplate, "copilot", TrainingAgent.DefaultArguments);
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("{evidence}"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Not.Contain("--no-color"));
			Assert.That(run.SnapshotDirectory, Does.Not.StartWith(run.EvidenceDirectory));
		}

		[Test]
		public void CompleteNextPromptIsExtractedAndPersistedOnlyWhenAccepted()
		{
			var run = NewRun();
			var nextPrompt = promptTemplate + Environment.NewLine +
				"Start with the economy inflection point.";
			var extracted = TrainingAgent.FindSuggestedNextPrompt(
			[
				"Finished.",
				TrainingAgent.NextPromptBegin,
				.. nextPrompt.Split(Environment.NewLine),
				TrainingAgent.NextPromptEnd
			]);

			run.AgentStarted("agent");
			run.AgentFinished(0, 1, extracted);

			Assert.That(extracted, Is.EqualTo(nextPrompt));
			Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);

			run.AcceptSuggestedNextPrompt(extracted);

			Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.True);
			Assert.That(run.Manifest.Agent.SuggestedNextPrompt, Is.EqualTo(nextPrompt));
			Assert.That(TrainingAgent.FindSuggestedNextPrompt(
				[TrainingAgent.NextPromptBegin, TrainingAgent.NextPromptEnd]), Is.Null);
		}

		[Test]
		public void NestedContractMarkersAndRenderedValuesAreNormalized()
		{
			var run = NewRun();
			var copiedContract = string.Join(Environment.NewLine,
			[
				"## Create the complete prompt for the next round",
				"",
				"Keep these placeholders: " + run.Manifest.BotDirectory +
					", {gameGuide}, {gameRules}, {fightManifest}, {battleLog}, {telemetry}, " +
					"{decisionTrace}, {battle}, {result}, {sourceRevision}, {nextPromptContract}",
				"",
				"Return the template between these marker lines:",
				TrainingAgent.NextPromptBegin,
				"<complete replacement prompt template>",
				TrainingAgent.NextPromptEnd,
				"",
				"Continuous improvement may accept it automatically."
			]);
			var proposal = promptTemplate
				.Replace("{workspace}", run.Manifest.BotDirectory, StringComparison.Ordinal)
				.Replace("{nextPromptContract}", copiedContract, StringComparison.Ordinal);

			var extracted = TrainingAgent.FindSuggestedNextPrompt(
			[
				"Finished.",
				TrainingAgent.NextPromptBegin,
				.. proposal.Split(Environment.NewLine),
				TrainingAgent.NextPromptEnd
			], run);

			Assert.That(extracted, Does.Contain("{workspace}"));
			Assert.That(extracted, Does.Not.Contain(run.Manifest.BotDirectory));
			Assert.That(extracted, Does.Not.Contain("<complete replacement prompt template>"));
			Assert.That(extracted.Split(Environment.NewLine)
				.Count(line => line == "{nextPromptContract}"), Is.EqualTo(1));
			Assert.That(TrainingAgent.ValidatePromptTemplate(extracted, out var error), Is.True,
				error);
		}

		[Test]
		public void NextPromptMustRetainDynamicRunPlaceholders()
		{
			Assert.That(TrainingAgent.ValidatePromptTemplate(promptTemplate, out var validError), Is.True);
			Assert.That(validError, Is.Null);
			Assert.That(TrainingAgent.ValidatePromptTemplate(
				promptTemplate.Replace("{telemetry}", "no telemetry", StringComparison.Ordinal),
				out var invalidError), Is.False);
			Assert.That(invalidError, Does.Contain("{telemetry}"));

			var inlineContract = promptTemplate.Replace(
				"{nextPromptContract}", "Keep {nextPromptContract} here.", StringComparison.Ordinal);
			Assert.That(TrainingAgent.ValidatePromptTemplate(inlineContract, out var contractError),
				Is.False);
			Assert.That(contractError, Does.Contain("on a line by itself"));
		}

		[Test]
		public void ApprovedReplacementTemplateUsesFreshValuesNextRound()
		{
			var first = NewRun();
			TrainingAgent.PrepareContext(first, gameGuide, gameRules, promptTemplate);
			var replacement = promptTemplate + Environment.NewLine +
				"Begin with the earliest economy divergence before reviewing combat.";

			var second = NewRun();
			TrainingAgent.PrepareContext(second, gameGuide, gameRules, replacement);
			var rendered = File.ReadAllText(second.PromptPath);

			Assert.That(rendered, Does.Contain("earliest economy divergence"));
			Assert.That(rendered, Does.Contain(second.TelemetryPath));
			Assert.That(rendered, Does.Not.Contain(first.TelemetryPath));
		}

		[Test]
		public void PlayerFeedbackIsDurableAndIncludedInAgentEvidence()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);

			run.SetPlayerFeedback("I expanded too late and never recovered map control.");
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Contain("never recovered map control"));
			TrainingAgent.PrepareContext(run, gameGuide, gameRules, promptTemplate);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.Result.PlayerFeedback,
				Is.EqualTo("I expanded too late and never recovered map control."));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("I expanded too late"));
			Assert.That(() => run.SetPlayerFeedback(new string('x', TrainingRun.MaxPlayerFeedbackLength + 1)),
				Throws.ArgumentException);
		}

		[Test]
		public void HeadlessFeedbackRequiresSuccessfulPlaybackOfThatBattlesReplay()
		{
			var first = NewRun();
			var second = NewRun();
			foreach (var run in new[] { first, second })
			{
				run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
				Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
				File.WriteAllText(run.ReplayPath, "recorded replay fixture");
			}

			Assert.That(() => first.SetPlayerFeedback("Not observed yet."), Throws.InvalidOperationException);
			Assert.That(first.RecordReplayPlayback(1, cancelled: false), Is.False);
			Assert.That(first.RecordReplayPlayback(0, cancelled: true), Is.False);
			Assert.That(TrainingRun.Load(first.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(first.CanProvideFeedback, Is.False);

			Assert.That(first.RecordReplayPlayback(0, cancelled: false), Is.True);
			var reloaded = TrainingRun.Load(first.RunDirectory);
			Assert.That(reloaded.Manifest.ReplayWatchedUtc, Is.Not.Null);
			Assert.That(reloaded.CanProvideFeedback, Is.True);
			Assert.That(second.CanProvideFeedback, Is.False);
			Assert.That(TrainingRun.Load(second.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);

			reloaded.SetPlayerFeedback("The replay showed the harvester was left exposed.");
			TrainingAgent.PrepareContext(reloaded, gameGuide, gameRules, promptTemplate);
			Assert.That(File.ReadAllText(reloaded.FightManifestPath), Does.Contain("harvester was left exposed"));
			Assert.That(File.ReadAllText(reloaded.PromptPath), Does.Contain("harvester was left exposed"));
			Assert.That(TrainingAgent.RenderPrompt(second, promptTemplate), Does.Not.Contain("harvester was left exposed"));
		}

		[Test]
		public void UnrecordedBattlesAndMissingReplaysCannotUnlockFeedback()
		{
			var run = NewRun();
			Assert.That(() => run.SetPlayerFeedback("Still running."), Throws.InvalidOperationException);
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Result = new TrainingBattleResult();
			Assert.That(() => run.SetPlayerFeedback("No match happened."), Throws.InvalidOperationException);
			Assert.That(BattleFeedback.ShouldPrompt(run, automated: false), Is.False);

			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			Assert.That(() => run.RecordReplayPlayback(0, cancelled: false), Throws.InvalidOperationException);
			Assert.That(run.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(BattleFeedback.CanReview(run), Is.False);
		}

		[TestCase(BattleExecutionModes.Rendered, false, true)]
		[TestCase(BattleExecutionModes.Rendered, true, false)]
		[TestCase(BattleExecutionModes.Headless, false, false)]
		[TestCase(BattleExecutionModes.Headless, true, false)]
		[TestCase(null, false, true)]
		public void OnlyManualRenderedBattlesPromptForMissingFeedback(string execution, bool automated, bool expected)
		{
			var run = NewRun();
			run.Manifest.Battle.ExecutionMode = execution;
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.EqualTo(expected));
			run.Manifest.Result.PlayerFeedback = "Existing feedback.";
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.False);
			run.Manifest.Result.PlayerFeedback = null;
			run.Manifest.Status = "failed";
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.False);
		}

		[Test]
		public void EditingAndClearingFeedbackRefreshesTheSameBattlesEvidence()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.SetPlayerFeedback(new string('x', TrainingRun.MaxPlayerFeedbackLength));
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Result.PlayerFeedback.Length,
				Is.EqualTo(TrainingRun.MaxPlayerFeedbackLength));
			run.SetPlayerFeedback("  An updated observation.  ");
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Contain("An updated observation."));
			Assert.That(TrainingAgent.RenderPrompt(run, promptTemplate), Does.Contain("An updated observation."));
			run.SetPlayerFeedback(" \r\n ");
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Not.Contain("An updated observation."));
			Assert.That(TrainingAgent.RenderPrompt(run, promptTemplate), Does.Contain("No player assessment was provided."));
		}

		[Test]
		public void PrebuiltBotsCanKeepObservedBattleFeedbackWithoutBeingTrainable()
		{
			var assembly = Path.Combine(root, "Prebuilt.dll");
			File.WriteAllText(assembly, "assembly fixture");
			var run = TrainingRun.Create(assembly,
				new TrainingBattleConfiguration { ExecutionMode = BattleExecutionModes.Rendered }, runs);
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Assert.That(run.IsEditable, Is.False);
			run.SetPlayerFeedback("The bot never defended its expansion.");
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.True);
		}

		[Test]
		public void LegacyHeadlessRunsKeepTheirFeedbackButRequireAReplayBeforeEditing()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.Manifest.SchemaVersion = 4;
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			run.Manifest.Result.PlayerFeedback = "An assessment from an older launcher.";
			run.Save();
			var manifest = File.ReadAllText(run.ManifestPath)
				.Replace("  \"ReplayWatchedUtc\": null,\n", "", StringComparison.Ordinal);
			File.WriteAllText(run.ManifestPath, manifest);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.HasPlayerFeedback, Is.True);
			Assert.That(loaded.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(loaded.CanProvideFeedback, Is.False);
			Assert.That(() => loaded.SetPlayerFeedback("An unobserved edit."), Throws.InvalidOperationException);
		}

		[Test]
		public void AFailedManifestWriteCannotClaimFeedbackOrReplayReviewWasSaved()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Directory.CreateDirectory(run.ManifestPath + ".tmp");
			Assert.That(() => run.SetPlayerFeedback("Unsaved feedback."),
				Throws.TypeOf<UnauthorizedAccessException>());
			Assert.That(run.HasPlayerFeedback, Is.False);
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);

			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			File.WriteAllText(run.ReplayPath, "replay fixture");
			Assert.That(() => run.RecordReplayPlayback(0, cancelled: false),
				Throws.TypeOf<UnauthorizedAccessException>());
			Assert.That(run.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);
		}

		[Test]
		public void HistoryKeepsOnlyTheSelectedBotAndBuildsChronologicalKpis()
		{
			var later = NewRun();
			Complete(later, "Won", 140, army: 5200, opponentArmy: 900);
			later.Manifest.CreatedUtc = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
			later.Save();

			var earlier = NewRun();
			Complete(earlier, "Lost", 240, army: 1800, opponentArmy: 3600, opponents: 2);
			earlier.Manifest.CreatedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
			earlier.Save();

			var otherWorkspace = Path.Combine(root, "Other");
			Directory.CreateDirectory(otherWorkspace);
			var otherProject = Path.Combine(otherWorkspace, "MyBot.csproj");
			File.WriteAllText(otherProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			var other = TrainingRun.Create(otherProject, new TrainingBattleConfiguration(), runs);
			Complete(other, "Won", 10, army: 9999, opponentArmy: 0);

			var history = TrainingHistory.Load(project, runs);

			Assert.That(history.Runs.Select(run => run.Manifest.Id),
				Is.EqualTo(new[] { earlier.Manifest.Id, later.Manifest.Id }));
			Assert.That(history.Iterations.Select(iteration => iteration.Number), Is.EqualTo(new[] { 1, 2 }));
			Assert.That(history.Iterations[0].Outcome, Is.EqualTo("Lost"));
			Assert.That(history.Iterations[0].DurationSeconds, Is.EqualTo(240));
			Assert.That(history.Iterations[0].LocalPlayer.ArmyValue, Is.EqualTo(1800));
			Assert.That(history.Iterations[0].Opponents.ArmyValue, Is.EqualTo(7200));
			Assert.That(history.Iterations[1].Outcome, Is.EqualTo("Won"));
		}

		[Test]
		public void FailedAttemptIsArchivedAndRecoveryPromptTargetsItsVerificationError()
		{
			var run = NewRun();
			TrainingAgent.PrepareContext(run, gameGuide, gameRules, promptTemplate);
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent output" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "tests failed");
			run.AgentFinished(1, 4, failurePhase: "verification",
				failureMessage: "Bot tests failed.");

			var transcript = run.ArchiveAgentAttempt();
			var recovery = TrainingAgent.BuildRecoveryContext(run.Manifest.Agent, transcript);
			TrainingAgent.PrepareContext(run, gameGuide, gameRules, promptTemplate, recovery);
			run.AgentStarted("agent", transcript);

			Assert.That(File.Exists(transcript), Is.True);
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("Recovery attempt"));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("Bot tests failed."));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(transcript));
			Assert.That(run.Manifest.Agent.Attempt, Is.EqualTo(2));
			Assert.That(run.Manifest.Agent.RecoveryTranscript, Is.EqualTo(transcript));
		}

		[Test]
		public void LegacyFailedRunInfersVerificationPhaseFromTranscript()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent finished" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "tests failed");
			run.AgentFinished(1, 2);

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.That(loaded.Manifest.Agent.FailurePhase, Is.EqualTo("verification"));
			Assert.That(loaded.Manifest.Agent.FailureMessage,
				Is.EqualTo("Independent bot verification failed."));

			loaded.VerificationStarted();
			Assert.That(loaded.Manifest.Status, Is.EqualTo("verifying"));
			Assert.That(loaded.Manifest.Agent.ExitCode, Is.Null);
		}

		[Test]
		public void PreviousCopilotDefaultsUpgradeToColoredProgressOutput()
		{
			var previous = new[]
			{
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-color",
				"--no-custom-instructions",
				"--no-remote-export",
				"--screen-reader",
				"--add-dir", "{evidence}"
			};

			var upgraded = TrainingAgent.UpgradeDefaultArguments("copilot", previous);

			Assert.That(upgraded, Does.Not.Contain("--screen-reader"));
			Assert.That(upgraded, Does.Not.Contain("--no-color"));
			Assert.That(upgraded, Does.Not.Contain("--silent"));
		}

		[Test]
		public void HeadlessPerformanceIsStoredInTheDurableManifest()
		{
			var run = NewRun();
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			File.WriteAllText(run.TelemetryPath,
				"seconds,player,faction,bot,colour,units,army,buildings,basevalue,assets,cash,killed,lost,buildingskilled,buildingslost,state\n" +
				"42,Commander,gdi,0,C82020,5,2500,3,4200,7000,1000,8,3,2,1,Won\n" +
				"42,Watson,nod,1,12B572,0,0,0,0,0,0,3,8,1,2,Lost\n");
			File.WriteAllText(run.BattleLogPath,
				"seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail\n" +
				"0,player,Commander,,,,,,,,faction=gdi bot=0 colour=C82020 side=you\n" +
				"0,player,Watson,,,,,,,,faction=nod bot=1 colour=12B572 side=enemy\n" +
				"42,over,Commander,,,,,,,,result=Won\n");
			File.WriteAllText(run.PerformancePath,
				"{\"schemaVersion\":1,\"mode\":\"headless\",\"status\":\"completed\"," +
				"\"elapsedMilliseconds\":1000,\"worldTicks\":2500,\"logicAttempts\":2600," +
				"\"ticksPerSecond\":2500,\"simulationSpeed\":100,\"gameSeconds\":100,\"result\":\"won\"}");
			File.WriteAllText(run.CancellationPath, "stop");

			var match = new MatchLog();
			match.Watch(run.TelemetryPath);
			Assert.That(match.Refresh(), Is.True);
			var battle = new BattleEventLog();
			battle.Watch(run.BattleLogPath);
			Assert.That(battle.Refresh(), Is.True);

			run.Finish("finished", match, battle);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.SchemaVersion, Is.EqualTo(5));
			Assert.That(loaded.Manifest.Result.Outcome, Is.EqualTo("Won"));
			Assert.That(loaded.Manifest.Performance.SimulationSpeed, Is.EqualTo(100));
			Assert.That(loaded.Manifest.Performance.TicksPerSecond, Is.EqualTo(2500));
			Assert.That(File.Exists(run.CancellationPath), Is.False);
		}

		/// <remarks>
		/// Stopping kills the process tree, so the exit code is indistinguishable from a crash.
		/// If that is all the next round knows, it opens as an investigation into a failure that
		/// never happened — which is both untrue and slow, because the agent reads a transcript
		/// and reruns the tests before starting the work that was actually wanted.
		/// </remarks>
		[Test]
		public void AStoppedAttemptIsNotTreatedAsAFailureNextRound()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent output" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "half way through");
			run.AgentFinished(-1, 3, failurePhase: "cancelled",
				failureMessage: "The improvement was stopped from the launcher before it finished.",
				cancelled: true);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.True);
				Assert.That(loaded.Manifest.Status, Is.EqualTo("improvement-cancelled"));

				// The transcript names a verification section, which is what the legacy inference
				// reads to relabel an unexplained failure. It must not reinterpret a stop.
				Assert.That(loaded.Manifest.Agent.FailurePhase, Is.EqualTo("cancelled"));
			});

			var transcript = loaded.ArchiveAgentAttempt();
			Assert.That(TrainingAgent.BuildRecoveryContext(loaded.Manifest.Agent, transcript),
				Is.Null, "A stop is not a failure to be recovered from.");

			var cancellation = TrainingAgent.BuildCancellationContext(loaded.Manifest.Agent,
				transcript);
			TrainingAgent.PrepareContext(loaded, gameGuide, gameRules, promptTemplate, cancellation);
			var prompt = File.ReadAllText(loaded.PromptPath);
			Assert.Multiple(() =>
			{
				Assert.That(prompt, Does.Not.Contain("Recovery attempt"));
				Assert.That(prompt, Does.Contain("stopped the previous improvement"));

				// The half-finished edits are the one thing it does have to be warned about.
				Assert.That(prompt, Does.Contain("3 file(s)"));
				Assert.That(prompt, Does.Contain(transcript));
			});
		}

		[Test]
		public void AStopThatChangedNothingSaysNothingToTheNextRound()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(-1, 0, failurePhase: "cancelled", cancelled: true);

			Assert.That(
				TrainingAgent.BuildCancellationContext(run.Manifest.Agent, "transcript.txt"),
				Is.Null, "With no edits left behind there is nothing the next round needs told.");
		}

		/// <remarks>
		/// Covers the guard itself rather than the path production takes. A cancelled run normally
		/// carries the "cancelled" phase, which stops the legacy inference on its own — so without
		/// a run that has no phase at all, the <c>Cancelled</c> clause would never be exercised and
		/// could be dropped by a later edit without any test noticing.
		/// </remarks>
		[Test]
		public void ACancelledRunWithNoPhaseIsStillNotRelabelledAsAFailure()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"=== Verification ===" + Environment.NewLine + "stopped part way");
			run.AgentFinished(-1, 1, cancelled: true);
			Assert.That(run.Manifest.Agent.FailurePhase, Is.Null, "Precondition for this test.");

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.True);
				Assert.That(loaded.Manifest.Agent.FailurePhase, Is.Null);
				Assert.That(loaded.Manifest.Agent.FailureMessage, Is.Null);
			});
		}

		/// <remarks>
		/// The counterpart to the test above. Stopping a *repair* is not the same as stopping an
		/// improvement: whatever it was sent to fix is still broken, so the next attempt must
		/// still be told to fix it rather than being reassured that nothing failed.
		/// </remarks>
		[Test]
		public void StoppingARepairKeepsTheFailureItWasSentToFix()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(1, 2, failurePhase: "verification",
				failureMessage: "Bot tests failed.");
			var firstTranscript = run.ArchiveAgentAttempt();

			// The repair attempt, which the player then stops part way through.
			run.AgentStarted("agent", firstTranscript, repairing: true);
			Assert.That(run.Manifest.Agent.Repairing, Is.True, "Precondition for this test.");
			run.AgentFinished(1, 5, failurePhase: "verification",
				failureMessage: "Bot tests failed.");

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.False);
				Assert.That(loaded.Manifest.Status, Is.EqualTo("improvement-failed"));
			});

			var recovery = TrainingAgent.BuildRecoveryContext(loaded.Manifest.Agent,
				firstTranscript);
			Assert.That(recovery, Is.Not.Null,
				"A stopped repair still has a failure to recover from.");
			Assert.That(recovery, Does.Contain("Bot tests failed."));
		}

		[Test]
		public void AnUninspectableStopIsWarnedAboutRatherThanAssumedClean()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(-1, TrainingAgentResult.UnknownChangeCount,
				failurePhase: "cancelled", cancelled: true);

			var context = TrainingAgent.BuildCancellationContext(run.Manifest.Agent, null);

			Assert.That(context, Is.Not.Null,
				"An unreadable workspace is not the same as a clean one.");
			Assert.That(context, Does.Contain("could not be determined"));
			Assert.That(context, Does.Not.Contain("-1"));
		}

		[Test]
		public void ASuccessfulRunIsNeverRecordedAsCancelled()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(0, 2, cancelled: true);

			Assert.Multiple(() =>
			{
				Assert.That(run.Manifest.Agent.Cancelled, Is.False);
				Assert.That(run.Manifest.Status, Is.EqualTo("improved"));
			});
		}

		TrainingRun NewRun() =>
			TrainingRun.Create(project, new TrainingBattleConfiguration
			{
				Map = "test-map",
				Difficulty = "Normal",
				Opponents = 1,
				Faction = "gdi",
				BotFaction = "nod",
				GameSpeed = "maximum",
				ExecutionMode = BattleExecutionModes.Rendered
			}, runs);

		static void Complete(TrainingRun run, string outcome, int duration, int army,
			int opponentArmy, int opponents = 1)
		{
			run.Manifest.Status = "finished";
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Result = new TrainingBattleResult
			{
				DurationSeconds = duration,
				LocalPlayer = "You",
				Outcome = outcome,
				Players =
				[
					new TrainingPlayerResult
					{
						Name = "You",
						Outcome = outcome,
						Units = army / 100,
						ArmyValue = army,
						Buildings = 4,
						BaseValue = army / 2,
						Killed = army / 200,
						Lost = opponentArmy / 300
					},
					.. Enumerable.Range(1, opponents).Select(index => new TrainingPlayerResult
					{
						Name = "Opponent " + index,
						IsBot = true,
						Outcome = outcome == "Won" ? "Lost" : "Won",
						Units = opponentArmy / 100,
						ArmyValue = opponentArmy,
						Buildings = 5,
						BaseValue = opponentArmy / 2,
						Killed = opponentArmy / 200,
						Lost = army / 300
					})
				]
			};
			run.Save();
		}

		static void CreateCheckout(string checkout, string authoringApi)
		{
			var scripts = Path.Combine(checkout, "scripts");
			Directory.CreateDirectory(scripts);
			File.WriteAllText(Path.Combine(checkout, "AutoCnC.sln"), "");
			File.WriteAllText(Path.Combine(scripts, "run-bot.ps1"), "");
			if (authoringApi != null)
				File.WriteAllText(Path.Combine(scripts, "authoring-api.version"), authoringApi);
		}
	}
}
