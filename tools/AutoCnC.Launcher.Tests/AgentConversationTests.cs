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
	public sealed class AgentConversationTests
	{
		string root;
		string workspace;
		string project;
		string runs;
		string gameGuide;
		string mechanics;
		string gameRules;
		string promptTemplate;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC.Launcher.Tests",
				Guid.NewGuid().ToString("N"));
			workspace = Path.Combine(root, "My Bot");
			runs = Path.Combine(root, "runs");
			gameGuide = Path.Combine(root, "agent-game-guide.md");
			mechanics = Path.Combine(root, "agent-mechanics.md");
			gameRules = Path.Combine(root, "game-rules.json");
			promptTemplate = string.Join(Environment.NewLine,
			[
				"Improve the bot. Edit only files under {workspace}.",
				"Mechanics: {gameMechanics}",
				"Read {gameGuide} and {gameRules}.",
				"Evidence: {fightManifest}, {battleLog}, {telemetry}, {decisionTrace}.",
				"Derived: {summary}, {units}, {mapFacts}, {checks}, {checkResults}, {trend}.",
				"{checkReport}",
				"{trendReport}",
				"{botAudit}",
				"Fight: {battle}. Result: {result}. Revision: {sourceRevision}.",
				"{nextPromptContract}"
			]);
			project = Path.Combine(workspace, "MyBot.csproj");
			Directory.CreateDirectory(workspace);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(gameGuide, "# Guide\nOnly use visible enemy information.");
			File.WriteAllText(mechanics, "# Mechanics\nModeContext exposes FindResourceFields.");
			File.WriteAllText(gameRules, "{\"actors\":[]}");
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
				Directory.Delete(root, true);
		}

		TrainingRun NewRun() =>
			TrainingRun.Create(project, new TrainingBattleConfiguration(), runs);

		[Test]
		public void OneSessionIdIsPinnedForTheWholeFightAndGivenToTheAgent()
		{
			var run = NewRun();

			var first = run.EnsureAgentSessionId();
			Assert.That(first, Is.Not.Empty);
			Assert.That(run.EnsureAgentSessionId(), Is.EqualTo(first));

			TrainingAgent.Prepare(run, gameGuide, mechanics, gameRules, promptTemplate,
				"copilot", TrainingAgent.DefaultArguments);
			run.AgentStarted("copilot");

			// The repair that follows a failure has to reach the same agent, or it arrives with no
			// memory of the change it was sent to fix.
			run.AgentFinished(1, 2, failurePhase: "verification");
			run.AgentStarted("copilot", repairing: true);

			var reloaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(reloaded.Manifest.AgentSessionId, Is.EqualTo(first));
			Assert.That(reloaded.EnsureAgentSessionId(), Is.EqualTo(first));
			Assert.That(TrainingAgent.DefaultArguments, Does.Contain("--session-id"));
			Assert.That(TrainingAgent.DefaultArguments, Does.Contain("{sessionId}"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("{sessionId}"));
		}

		[Test]
		public void DefaultsWithoutASessionUpgradeToAPinnedOne()
		{
			// The stdin-era default, which is what a player who improved a bot before conversations
			// existed has saved. Without the upgrade every turn would open a new session.
			var withoutSession = new[]
			{
				"--allow-all-tools",
				"--no-ask-user",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			};

			var upgraded = TrainingAgent.UpgradeDefaultArguments("copilot", withoutSession);

			Assert.That(upgraded, Does.Contain("--session-id"));
			Assert.That(upgraded, Does.Contain("{sessionId}"));

			// The prompt stays off the command line, where it no longer fits.
			Assert.That(upgraded, Does.Not.Contain("-p"));
			Assert.That(TrainingAgent.UpgradePromptChannel(upgraded, null),
				Is.EqualTo(TrainingAgent.DefaultStdin));
		}

		[Test]
		public void APromptEraDefaultAlsoUpgradesToAPinnedSession()
		{
			var promptOnCommandLine = new[]
			{
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			};

			Assert.That(TrainingAgent.UpgradeDefaultArguments("copilot", promptOnCommandLine),
				Does.Contain("--session-id"));
		}

		[Test]
		public void ACustomisedAgentIsNotRewrittenToPinASession()
		{
			var custom = new[] { "-p", "{prompt}", "--model", "gpt-5.4" };

			Assert.That(TrainingAgent.UpgradeDefaultArguments("copilot", custom),
				Is.EqualTo(custom));
		}

		[Test]
		public void MessagesTypedWhileTheAgentWorksAreKeptAndDeliveredInOrder()
		{
			var conversation = new AgentConversation(NewRun());

			Assert.That(conversation.Post("Focus on the economy.", agentBusy: true), Is.False);
			Assert.That(conversation.Post("And check the harvesters.", agentBusy: true), Is.False);
			Assert.That(conversation.PendingCount, Is.EqualTo(2));
			Assert.That(conversation.TryStartNext(out _), Is.True);

			// Nothing else may start while one turn is being answered: two processes resuming the
			// same session would interleave into it.
			Assert.That(conversation.TryStartNext(out _), Is.False);

			conversation.Complete("Economy first, then harvesters.");
			Assert.That(conversation.TryStartNext(out var second), Is.True);
			Assert.That(second, Is.EqualTo("And check the harvesters."));

			conversation.Complete("Harvesters are idling at the refinery.");
			Assert.That(conversation.PendingCount, Is.Zero);
			Assert.That(conversation.IsBusy, Is.False);

			var said = conversation.History.Select(entry => entry.Text).ToArray();
			Assert.That(said, Is.EqualTo(new[]
			{
				"Focus on the economy.",
				"And check the harvesters.",
				"Economy first, then harvesters.",
				"Harvesters are idling at the refinery."
			}));
			Assert.That(conversation.History[0].Deferred, Is.True);
			Assert.That(conversation.History[0].Speaker, Is.EqualTo(AgentChatSpeaker.Player));
			Assert.That(conversation.History[2].Speaker, Is.EqualTo(AgentChatSpeaker.Agent));
		}

		[Test]
		public void AMessageSentToAFreeAgentGoesStraightOut()
		{
			var conversation = new AgentConversation(NewRun());

			Assert.That(conversation.Post("What did you change?", agentBusy: false), Is.True);
			Assert.That(conversation.History.Single().Deferred, Is.False);
		}

		[Test]
		public void AFailedTurnAbandonsWhatWasQueuedBehindIt()
		{
			var conversation = new AgentConversation(NewRun());
			conversation.Post("First.", agentBusy: true);
			conversation.Post("Second.", agentBusy: true);
			conversation.Post("Third.", agentBusy: true);
			conversation.TryStartNext(out _);

			conversation.Fail("The agent could not answer.");

			Assert.That(conversation.PendingCount, Is.Zero);
			Assert.That(conversation.IsBusy, Is.False);
			Assert.That(conversation.History[^1].Speaker, Is.EqualTo(AgentChatSpeaker.Launcher));
			Assert.That(conversation.History[^1].Text, Does.Contain("2 queued message(s)"));
		}

		[Test]
		public void WhatWasSaidSurvivesClosingTheLauncher()
		{
			var run = NewRun();
			var conversation = new AgentConversation(run);
			conversation.Post("Why did it lose?", agentBusy: false);
			conversation.TryStartNext(out _);
			conversation.Complete("It never built a refinery.");

			var reopened = new AgentConversation(TrainingRun.Load(run.RunDirectory));

			Assert.That(reopened.History.Select(entry => entry.Text),
				Is.EqualTo(new[] { "Why did it lose?", "It never built a refinery." }));
			Assert.That(reopened.PendingCount, Is.Zero);
		}

		[Test]
		public void TheReplyIsTakenFromBelowTheLastHeadingOfTheTurnTranscript()
		{
			var run = NewRun();
			File.WriteAllText(run.ChatTranscriptPath, string.Join(Environment.NewLine,
			[
				"=== You ===",
				"Why did it lose?",
				"=== Agent ===",
				"It never built a refinery."
			]));

			Assert.That(AgentConversation.ReadReply(run.ChatTranscriptPath),
				Is.EqualTo("It never built a refinery."));
			Assert.That(AgentConversation.ReadReply(run.ChatMessagePath), Is.Null);
		}

		[Test]
		public void AnAgentThatSaidNothingUsefulIsStillQuotedRatherThanLostSilently()
		{
			var run = NewRun();
			File.WriteAllText(run.ChatTranscriptPath, "command not found: copilot");

			Assert.That(AgentConversation.ReadReply(run.ChatTranscriptPath),
				Is.EqualTo("command not found: copilot"));
		}

		[Test]
		public void AnEmptyMessageIsNotAMessage()
		{
			var conversation = new AgentConversation(NewRun());

			Assert.That(conversation.Post("   ", agentBusy: false), Is.False);
			Assert.That(conversation.History, Is.Empty);
			Assert.That(conversation.PendingCount, Is.Zero);
		}

		[Test]
		public void TalkingIsAllowedWhereStartingAnotherRoundIsNot()
		{
			var run = NewRun();
			run.AgentStarted("copilot");
			run.AgentFinished(0, 3);

			// The agent's changes are in the workspace, so there is nothing left to improve from
			// this fight — but asking it what it just did is exactly when you want to.
			Assert.That(run.CanImprove, Is.False);
			Assert.That(run.CanChat, Is.True);
		}

		[Test]
		public void ConversationCanRebindToFreshStateForTheSameRun()
		{
			var run = NewRun();
			var stale = TrainingRun.Load(run.RunDirectory);
			var current = TrainingRun.Load(run.RunDirectory);
			var conversation = new AgentConversation(stale);

			conversation.Rebind(current);

			Assert.That(conversation.Run, Is.SameAs(current));
		}
	}
}
