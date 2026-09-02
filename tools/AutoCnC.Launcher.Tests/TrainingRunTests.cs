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
			CreateCheckout(currentRoot, authoringApi: "3");
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

		TrainingRun NewRun() =>
			TrainingRun.Create(project, new TrainingBattleConfiguration
			{
				Map = "test-map",
				Difficulty = "Normal",
				Opponents = 1,
				Faction = "gdi",
				BotFaction = "nod",
				GameSpeed = "maximum"
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
