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
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class ImprovementWindowTests
	{
		[Test]
		public void DedicatedWindowStartsOnLiveProgressAndExposesItsInputs()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Output Window",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			var guide = Path.Combine(root, "guide.md");
			var mechanics = Path.Combine(root, "mechanics.md");
			var rules = Path.Combine(root, "rules.json");
			var template = string.Join(Environment.NewLine,
			[
				"Edit only files under {workspace}.", "{gameMechanics}", "{gameGuide}", "{gameRules}",
				"{fightManifest}", "{battleLog}",
				"{telemetry}", "{decisionTrace}", "{battle}", "{result}", "{sourceRevision}",
				"{summary}", "{units}", "{mapFacts}", "{checks}", "{checkResults}", "{trend}",
				"{checkReport}", "{trendReport}", "{botAudit}",
				"{nextPromptContract}"
			]);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(guide, "# Rules\nUse visible information.");
			File.WriteAllText(mechanics, "# Mechanics\nModeContext exposes FindResourceFields.");
			File.WriteAllText(rules, "{\"actors\":[{\"id\":\"mtnk\",\"hitPoints\":45000}]}");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				TrainingAgent.PrepareContext(run, guide, mechanics, rules, template);
				run.AgentStarted("fake-agent");

				using var window = new ImprovementWindow();
				window.StartAgentRun(run);
				var parser = new TerminalTextParser();
				var progress = parser.ParseLine(
					"\x1B[36;1m├─ inspecting resolved unit rules →\x1B[0m");
				window.AppendAgentOutput(progress);

				Assert.That(window.SelectedView, Is.EqualTo("Progress"));
				Assert.That(window.AgentProgressText, Does.Contain("inspecting resolved unit rules"));
				Assert.That(window.ProgressColorAt(0), Is.EqualTo(progress.Spans[0].Style.Foreground));
				Assert.That(window.AgentPromptText, Does.Contain(run.GameGuidePath));
				Assert.That(window.GameGuideText, Does.Contain("visible information"));
				Assert.That(window.GameRulesText, Does.Contain("\"mtnk\""));

				var nextPrompt = template + Environment.NewLine + "Inspect economy first.";
				run.AgentFinished(0, 1, nextPrompt);
				window.CurrentPromptTemplate = template;
				window.CompleteAgentRun(run);
				string accepted = null;
				window.NextPromptAccepted += (_, value) => accepted = value;
				window.SubmitNextPrompt();

				Assert.That(window.NextPromptText, Is.EqualTo(nextPrompt));
				Assert.That(window.CanAcceptNextPrompt, Is.True);
				Assert.That(accepted, Is.EqualTo(nextPrompt));
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Test]
		public void AFinishedRoundOffersItsProposedPromptAsADifferenceToApproveOrReject()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Prompt Review",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				var current = Template("Report what changed.");
				var proposed = Template("Report what changed, economy first.");
				run.AgentStarted("fake-agent");
				run.AgentFinished(0, 1, proposed);

				using var window = new ImprovementWindow();
				window.ShowInTaskbar = false;
				window.CurrentPromptTemplate = current;
				window.CompleteAgentRun(run, reviewNextPrompt: true);

				Assert.That(window.SelectedView, Is.EqualTo("Next prompt *"),
					"a proposal nobody is shown is a proposal nobody approved");
				Assert.That(window.NextPromptDiffText,
					Does.Contain("- Report what changed.").And
						.Contain("+ Report what changed, economy first."));
				Assert.That(window.NextPromptDiffText, Does.Not.Contain("- Edit only {workspace}."),
					"lines the proposal left alone are not changes");
				Assert.That(window.NextPromptStatusText,
					Does.Contain("1 line(s) added, 1 line(s) removed."));
				Assert.That(window.CanAcceptNextPrompt, Is.True);
				Assert.That(window.CanRejectNextPrompt, Is.True);

				TrainingRun rejected = null;
				window.NextPromptRejected += value => rejected = value;
				window.RejectNextPromptDraft();

				Assert.That(rejected, Is.SameAs(run));

				// What the launcher does with it, and what reopening the session then shows.
				run.RejectSuggestedNextPrompt();
				window.MarkNextPromptRejected();
				Assert.That(window.CanAcceptNextPrompt, Is.False);
				Assert.That(window.CanRejectNextPrompt, Is.False);
				Assert.That(window.NextPromptStatusText, Does.Contain("Rejected"));

				window.ShowAgentRun(TrainingRun.Load(run.RunDirectory));
				Assert.That(window.NextPromptStatusText, Does.Contain("Rejected"));
				Assert.That(window.CanAcceptNextPrompt, Is.False);
				Assert.That(window.NextPromptText, Is.EqualTo(proposed),
					"the proposal is evidence about the round that made it");

				// Editing the draft reopens the decision rather than stranding it as rejected.
				window.NextPromptText = Template("Report what changed, air power first.");
				Assert.That(window.CanAcceptNextPrompt, Is.True);
				Assert.That(window.CanRejectNextPrompt, Is.True);
				Assert.That(window.NextPromptDiffText,
					Does.Contain("+ Report what changed, air power first."));

				// A tab that lays out to nothing is a decision the player cannot see to take,
				// and every assertion above would still pass.
				window.Size = new System.Drawing.Size(900, 600);
				window.Show();
				var tabs = Descendants(window).OfType<TabControl>().Single();
				tabs.SelectedTab = tabs.TabPages.Cast<TabPage>()
					.Single(page => page.Text.StartsWith("Next prompt", StringComparison.Ordinal));
				window.PerformLayout();

				var split = Descendants(window).OfType<SplitContainer>().Single();
				var diffPane = Descendants(split.Panel1).OfType<RichTextBox>().Single();
				var draft = Descendants(split.Panel2).OfType<TextBox>().Single();
				Assert.That(diffPane.Height, Is.GreaterThan(diffPane.Font.Height * 3));
				Assert.That(draft.Height, Is.GreaterThan(draft.Font.Height * 3));
				Assert.That(diffPane.Height, Is.GreaterThan(draft.Height),
					"the comparison is the part being judged");
				foreach (var button in Descendants(window).OfType<ActionButton>()
					.Where(button => button.Text is "Use this prompt next round"
						or "Keep the current prompt"))
					Assert.That(button.Width, Is.GreaterThan(0), button.Text);
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Test]
		public void AContinuousRoundDoesNotStealTheProgressViewToShowADiffNobodyIsReading()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Prompt Review Quiet",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				run.AgentStarted("fake-agent");
				run.AgentFinished(0, 1, Template("Report what changed, economy first."));

				using var window = new ImprovementWindow();
				window.ShowInTaskbar = false;
				window.CurrentPromptTemplate = Template("Report what changed.");
				window.StartAgentRun(run);
				window.CompleteAgentRun(run, reviewNextPrompt: false);

				Assert.That(window.SelectedView, Is.EqualTo("Progress"));
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Test]
		public void RebindRunReplacesAStaleModelessWindowReference()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Rebind Window",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				run.AgentStarted("agent");
				run.AgentFinished(0, 1, Template("draft"));
				var stale = TrainingRun.Load(run.RunDirectory);
				var current = TrainingRun.Load(run.RunDirectory);
				current.AcceptSuggestedNextPrompt(Template("draft"));

				using var window = new ImprovementWindow();
				window.ShowAgentRun(stale);
				window.RebindRun(stale, current);

				Assert.That(window.ShownRun, Is.SameAs(current));
				Assert.That(window.NextPromptStatusText, Does.Contain("Saved"));
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		/// <summary>A prompt that satisfies the template contract, with one line to vary.</summary>
		static string Template(string tail) => string.Join(Environment.NewLine,
		[
			"Improve the bot. Edit only files under {workspace}.",
			"{gameMechanics}", "{gameGuide}", "{gameRules}", "{fightManifest}", "{battleLog}",
			"{telemetry}", "{decisionTrace}", "{battle}", "{result}", "{sourceRevision}",
			"{summary}", "{units}", "{mapFacts}", "{checks}", "{checkResults}", "{trend}",
			"{checkReport}", "{trendReport}", "{botAudit}",
			"{nextPromptContract}",
			tail
		]);

		[Test]
		public void TheChatTabShowsWhatWasSaidAndKeepsAcceptingWhileTheAgentWorks()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Chat Window",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				var conversation = new AgentConversation(run);

				using var window = new ImprovementWindow();
				window.ShowInTaskbar = false;
				window.Size = new System.Drawing.Size(900, 600);
				window.Show();
				window.ShowAgentRun(run);
				window.ShowConversation(conversation);

				Assert.That(window.CanSendChat, Is.False, "an empty box has nothing to send");

				string sent = null;
				window.MessageSent += (_, message) => sent = message;
				window.ChatInputText = "Focus on the economy.";
				Assert.That(window.CanSendChat, Is.True);
				window.SubmitChatMessage();

				Assert.That(sent, Is.EqualTo("Focus on the economy."));
				Assert.That(window.ChatInputText, Is.Empty, "the box clears once it is sent");

				// What the launcher does with it: queued behind a round that is still running.
				conversation.Post(sent, agentBusy: true);
				window.RefreshChat();

				Assert.That(window.ConversationText, Does.Contain("Focus on the economy."));
				Assert.That(window.ConversationText, Does.Contain("Waiting to send"));
				Assert.That(window.ChatStatusText, Does.Contain("1 message(s) will be delivered"));

				conversation.TryStartNext(out _);
				window.BeginChatTurn();
				Assert.That(window.ChatStatusText, Does.Contain("answering"));

				conversation.Complete("Economy first; it never built a refinery.");
				window.EndChatTurn();

				Assert.That(window.ConversationText, Does.Contain("never built a refinery"));
				Assert.That(window.ConversationText, Does.Not.Contain("Waiting to send"));

				// A composer that lays out to nothing is a chat tab you cannot type into, and
				// every assertion above would still pass.
				var tabs = Descendants(window).OfType<TabControl>().Single();
				tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Chat");
				window.PerformLayout();

				var box = Descendants(window).OfType<TextBox>()
					.Single(text => text.MaxLength == AgentConversation.MaxMessageLength);
				var send = Descendants(window).OfType<ActionButton>()
					.Single(button => button.Text == "Send");
				Assert.That(box.Width, Is.GreaterThan(80));
				Assert.That(box.Height, Is.GreaterThan(box.Font.Height));
				Assert.That(send.Width, Is.GreaterThan(0));
				Assert.That(tabs.SelectedTab.ClientRectangle.Width,
					Is.GreaterThan(box.Width), "the composer must sit inside the tab");
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		static IEnumerable<Control> Descendants(Control control)
		{
			foreach (Control child in control.Controls)
			{
				yield return child;
				foreach (var grandchild in Descendants(child))
					yield return grandchild;
			}
		}
	}
}
