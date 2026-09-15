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
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Live agent progress and every input and output of one improvement iteration.</summary>
	public sealed class ImprovementWindow : BattleWindow
	{
		readonly TabControl views;
		readonly TabPage progressTab;
		readonly TabPage chatTab;
		readonly TabPage promptTab;
		readonly TabPage nextPromptTab;
		readonly RichTextBox progress;
		readonly RichTextBox conversation;
		readonly TextBox chatInput;
		readonly Button sendChat;
		readonly Label chatStatus;
		readonly TextBox prompt;
		readonly TextBox gameGuide;
		readonly JsonTreeView gameRules;
		readonly JsonTreeView fightManifest;
		readonly ListView changes;
		readonly Label summary;
		readonly TextBox nextPrompt;
		readonly Label nextPromptStatus;
		readonly Button applyNextPrompt;
		readonly Font boldProgressFont;
		readonly TerminalTextParser transcriptParser = new();
		readonly TerminalTextParser chatParser = new();
		readonly TaskbarProgress taskbarProgress = new();

		bool liveOutputStarted;
		bool chatStreaming;
		TrainingRun shownRun;
		AgentConversation chat;
		internal TrainingRun ShownRun => shownRun;

		public event Action<TrainingRun, string> NextPromptAccepted;

		/// <summary>Raised when the window begins showing a different run.</summary>
		/// <remarks>
		/// The conversation belongs to the run, not to the window, and the window is handed a run
		/// from four different directions. Announcing the change is what keeps the Chat tab from
		/// showing one fight's history beside another fight's transcript.
		/// </remarks>
		public event Action<TrainingRun> ShownRunChanged;

		/// <summary>Raised when the player has something to say to the agent.</summary>
		public event Action<TrainingRun, string> MessageSent;

		internal string AgentProgressText => progress.Text;
		internal string AgentPromptText => prompt.Text;
		internal string GameGuideText => gameGuide.Text;
		internal string GameRulesText => gameRules.SourceText;
		internal string SelectedView => views.SelectedTab?.Text;
		internal string NextPromptText => nextPrompt.Text;
		internal bool CanAcceptNextPrompt => applyNextPrompt.Enabled;
		internal string ConversationText => conversation.Text;
		internal string ChatStatusText => chatStatus.Text;
		internal string ChatInputText { get => chatInput.Text; set => chatInput.Text = value; }
		internal bool CanSendChat => sendChat.Enabled;
		internal Color ProgressColorAt(int index)
		{
			progress.Select(index, 1);
			var color = progress.SelectionColor;
			progress.Select(progress.TextLength, 0);
			return color;
		}

		public ImprovementWindow()
			: base("AutoC&C — Improvement")
		{
			progress = new RichTextBox
			{
				Dock = DockStyle.Fill,
				ReadOnly = true,
				DetectUrls = false,
				WordWrap = false,
				BorderStyle = BorderStyle.None,
				BackColor = Paper,
				ForeColor = Ink,
				Font = new Font(FontFamily.GenericMonospace, 9f),
				HideSelection = false
			};
			boldProgressFont = new Font(progress.Font, FontStyle.Bold);

			summary = new Label
			{
				Dock = DockStyle.Bottom,
				AutoSize = false,
				Height = Font.Height + 12,
				Padding = new Padding(8, 6, 8, 0),
				ForeColor = Faded
			};

			progressTab = new TabPage("Progress") { BackColor = Paper, Padding = new Padding(2) };
			progressTab.Controls.Add(progress);
			progressTab.Controls.Add(summary);

			conversation = new RichTextBox
			{
				Dock = DockStyle.Fill,
				ReadOnly = true,
				DetectUrls = false,
				BorderStyle = BorderStyle.None,
				BackColor = Paper,
				ForeColor = Ink,
				Font = new Font(FontFamily.GenericMonospace, 9f),
				HideSelection = false
			};

			chatInput = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ScrollBars = ScrollBars.Vertical,
				BackColor = CommandTheme.Field,
				ForeColor = Ink,
				MaxLength = AgentConversation.MaxMessageLength,
				Font = new Font(FontFamily.GenericMonospace, 9f)
			};
			chatInput.TextChanged += (_, _) => UpdateChatControls();
			chatInput.KeyDown += ChatInputKeyDown;

			sendChat = new ActionButton { Text = "Send", Enabled = false };
			sendChat.Click += (_, _) => SubmitChatMessage();

			chatStatus = new Label
			{
				Dock = DockStyle.Fill,
				AutoSize = false,
				ForeColor = Faded,
				TextAlign = ContentAlignment.MiddleLeft,
				Padding = new Padding(4, 0, 4, 0)
			};

			var chatComposer = new TableLayoutPanel
			{
				Dock = DockStyle.Bottom,
				ColumnCount = 2,
				RowCount = 2,
				Height = 104,
				Padding = new Padding(6, 4, 6, 4),
				BackColor = CommandTheme.Surface
			};
			chatComposer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
			chatComposer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			chatComposer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
			chatComposer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			chatComposer.Controls.Add(chatInput, 0, 0);
			chatComposer.Controls.Add(sendChat, 1, 0);
			chatComposer.Controls.Add(chatStatus, 0, 1);
			chatComposer.SetColumnSpan(chatStatus, 2);

			chatTab = new TabPage("Chat") { BackColor = Paper, Padding = new Padding(2) };
			chatTab.Controls.Add(conversation);
			chatTab.Controls.Add(chatComposer);
			chatTab.Controls.Add(Heading(
				"Talk to this fight's agent. It remembers every round and every message. " +
				"Enter sends, Shift+Enter starts a line."));

			prompt = TextPane();
			gameGuide = TextPane();
			gameRules = new JsonTreeView { Dock = DockStyle.Fill };
			fightManifest = new JsonTreeView { Dock = DockStyle.Fill };
			changes = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				BorderStyle = BorderStyle.None,
				BackColor = Paper,
				ForeColor = Ink
			};
			changes.Columns.Add("Change", 90);
			changes.Columns.Add("File", 600);
			changes.Resize += (_, _) => changes.Columns[^1].Width =
				Math.Max(200, changes.ClientSize.Width - changes.Columns[0].Width);

			promptTab = Page("Prompt", prompt);
			nextPrompt = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ScrollBars = ScrollBars.Both,
				WordWrap = false,
				BackColor = Paper,
				ForeColor = Ink,
				Font = new Font(FontFamily.GenericMonospace, 9f)
			};
			nextPrompt.TextChanged += (_, _) => ValidateNextPromptDraft();

			applyNextPrompt = new ActionButton
			{
				Text = "Use this prompt next round",
				Enabled = false
			};
			applyNextPrompt.Click += (_, _) => SubmitNextPrompt();

			nextPromptStatus = new Label
			{
				AutoSize = true,
				ForeColor = Faded,
				Margin = new Padding(10, 8, 3, 3)
			};
			var nextPromptFooter = new FlowLayoutPanel
			{
				Dock = DockStyle.Bottom,
				AutoSize = true,
				Padding = new Padding(6, 4, 6, 4),
				BackColor = CommandTheme.Surface
			};
			nextPromptFooter.Controls.Add(applyNextPrompt);
			nextPromptFooter.Controls.Add(nextPromptStatus);

			nextPromptTab = new TabPage("Next prompt") { BackColor = Paper, Padding = new Padding(8) };
			nextPromptTab.Controls.Add(nextPrompt);
			nextPromptTab.Controls.Add(nextPromptFooter);
			nextPromptTab.Controls.Add(Heading(
				"Complete replacement prompt for the next round. Edit it before saving if needed."));

			views = new CommandTabs { Dock = DockStyle.Fill };
			views.TabPages.Add(progressTab);
			views.TabPages.Add(chatTab);
			views.TabPages.Add(promptTab);
			views.TabPages.Add(Page("Game guide", gameGuide));
			views.TabPages.Add(Page("Units & weapons", gameRules));
			views.TabPages.Add(Page("Fight", fightManifest));
			views.TabPages.Add(Page("Changes", changes));
			views.TabPages.Add(nextPromptTab);
			Controls.Add(views);
		}

		public void StartAgentRun(TrainingRun run)
		{
			SetShownRun(run);
			LoadInputs(run);
			changes.Items.Clear();
			progress.Clear();
			transcriptParser.Reset();
			liveOutputStarted = false;
			summary.Text = "Starting the improvement agent…";
			nextPrompt.Clear();
			nextPromptTab.Text = "Next prompt";
			nextPromptStatus.Text = "The agent will draft the complete next-round prompt when it finishes.";
			applyNextPrompt.Enabled = false;
			views.SelectedTab = progressTab;
			Text = "AutoC&C — Improvement (running)";
			taskbarProgress.SetBusy(Handle, busy: true);
			UpdateChatControls();
		}

		public void StartVerificationRun(TrainingRun run)
		{
			SetShownRun(run);
			LoadInputs(run);
			progress.Clear();
			transcriptParser.Reset();
			liveOutputStarted = false;
			summary.Text = "Cleaning generated output and retrying independent verification…";
			views.SelectedTab = progressTab;
			Text = "AutoC&C — Improvement (verifying)";
			taskbarProgress.SetBusy(Handle, busy: true);
			UpdateChatControls();
		}

		public void AppendAgentOutput(TerminalLine line)
		{
			if (!liveOutputStarted)
			{
				progress.Clear();
				liveOutputStarted = true;
			}

			Append(line, scroll: true);
		}

		public void CompleteAgentRun(TrainingRun run)
		{
			SetShownRun(run);
			LoadInputs(run);
			LoadChanges(run);

			if (!liveOutputStarted)
			{
				if (File.Exists(run.AgentTranscriptPath))
					LoadTranscript(run.AgentTranscriptPath);
				else
					progress.Text =
						$"No transcript was produced. The agent process exited with code {run.Manifest.Agent?.ExitCode}.";
			}

			summary.Text = AgentSummary(run);
			LoadNextPrompt(run);
			Text = "AutoC&C — Improvement (finished)";
			taskbarProgress.SetBusy(Handle, busy: false);
			UpdateChatControls();
		}

		public void ShowAgentRun(TrainingRun run, bool promptFirst = false)
		{
			SetShownRun(run);
			LoadInputs(run);
			LoadChanges(run);

			var result = run.Manifest.Agent;
			if (File.Exists(run.AgentTranscriptPath))
				LoadTranscript(run.AgentTranscriptPath);
			else
			{
				progress.Text = result?.CompletedUtc != null
					? $"No transcript was produced. The agent process exited with code {result.ExitCode}."
					: result?.StartedUtc != null
						? "The improvement agent is running…"
						: "The agent has not run. Review its inputs before starting it.";
				liveOutputStarted = false;
			}

			summary.Text = AgentSummary(run);
			LoadNextPrompt(run);
			views.SelectedTab = promptFirst ? promptTab : progressTab;
			taskbarProgress.SetBusy(Handle, result?.ExitCode == null && result?.StartedUtc != null);
			UpdateChatControls();
		}

		public void MarkNextPromptSaved()
		{
			nextPromptTab.Text = "Next prompt";
			nextPromptStatus.Text = "Saved. This complete prompt will be rendered with fresh evidence next round.";
			applyNextPrompt.Enabled = false;
		}

		internal void SubmitNextPrompt()
		{
			var suggestion = nextPrompt.Text.Trim();
			if (shownRun != null && suggestion.Length > 0)
				NextPromptAccepted?.Invoke(shownRun, suggestion);
		}

		/// <summary>Shows the conversation belonging to the run on display.</summary>
		public void ShowConversation(AgentConversation conversation)
		{
			chat = conversation;
			chatStreaming = false;
			RefreshChat();
		}

		void SetShownRun(TrainingRun run)
		{
			var changed = !ReferenceEquals(shownRun, run);
			shownRun = run;
			if (changed)
				ShownRunChanged?.Invoke(run);
		}

		/// <summary>Redraws what has been said and what is still waiting to be said.</summary>
		public void RefreshChat()
		{
			RenderConversation();
			UpdateChatControls();
		}

		/// <summary>Starts a live block for the turn now being answered.</summary>
		public void BeginChatTurn()
		{
			chatStreaming = true;
			chatParser.Reset();
			RenderConversation();
			UpdateChatControls();
		}

		public void AppendChatOutput(TerminalLine line)
		{
			if (!chatStreaming)
				return;

			AppendTo(conversation, line, scroll: true);
		}

		/// <summary>Closes the live block once the finished turn has been recorded.</summary>
		public void EndChatTurn()
		{
			chatStreaming = false;
			RefreshChat();
		}

		internal void SubmitChatMessage()
		{
			var message = AgentConversation.Normalize(chatInput.Text);
			if (shownRun == null || message.Length == 0)
				return;

			chatInput.Clear();
			MessageSent?.Invoke(shownRun, message);
		}

		void ChatInputKeyDown(object sender, KeyEventArgs e)
		{
			if (e.KeyCode != Keys.Enter || e.Shift)
				return;

			// Suppressed as well as handled: without this the newline still reaches the box and
			// the next message starts with a blank line nobody typed.
			e.Handled = true;
			e.SuppressKeyPress = true;
			if (sendChat.Enabled)
				SubmitChatMessage();
		}

		void UpdateChatControls()
		{
			var hasRun = shownRun?.CanChat == true;
			var typed = AgentConversation.Normalize(chatInput.Text).Length > 0;
			chatInput.Enabled = hasRun;
			sendChat.Enabled = hasRun && typed;

			if (!hasRun)
			{
				chatStatus.Text = shownRun == null
					? "Select a battle to talk about its bot."
					: "This session has no editable bot workspace to talk about.";
				return;
			}

			var waiting = chat?.PendingCount ?? 0;
			chatStatus.Text = chat?.IsBusy == true
				? waiting == 0
					? "The agent is answering…"
					: $"The agent is answering — {waiting} message(s) waiting behind it."
				: waiting > 0
					? $"{waiting} message(s) will be delivered when the agent is free."
					: "The agent answers when it is free; it is safe to type while it works.";
		}

		void RenderConversation()
		{
			conversation.Clear();
			if (chat == null)
			{
				AppendChatNote("Nothing has been said yet.");
				return;
			}

			foreach (var entry in chat.History)
				AppendChatEntry(entry);

			foreach (var waiting in chat.Pending)
				AppendChatNote("Waiting to send: " + waiting);

			if (chatStreaming)
				AppendChatHeading("Agent", CommandTheme.Green);

			conversation.SelectionStart = conversation.TextLength;
			conversation.ScrollToCaret();
		}

		void AppendChatEntry(AgentChatEntry entry)
		{
			var (who, color) = entry.Speaker switch
			{
				AgentChatSpeaker.Player => ("You", CommandTheme.Amber),
				AgentChatSpeaker.Agent => ("Agent", CommandTheme.Green),
				_ => ("Launcher", Faded)
			};

			AppendChatHeading(
				entry.Deferred && entry.Speaker == AgentChatSpeaker.Player
					? who + " (sent once the agent was free)"
					: who,
				color);
			AppendChatBody(entry.Text, entry.Speaker == AgentChatSpeaker.Launcher ? Faded : Ink);
		}

		void AppendChatNote(string text) => AppendChatBody(text, Faded);

		void AppendChatHeading(string text, Color color)
		{
			conversation.SelectionStart = conversation.TextLength;
			conversation.SelectionLength = 0;
			conversation.SelectionColor = color;
			conversation.SelectionFont = boldProgressFont;
			conversation.AppendText(text + Environment.NewLine);
		}

		void AppendChatBody(string text, Color color)
		{
			conversation.SelectionStart = conversation.TextLength;
			conversation.SelectionLength = 0;
			conversation.SelectionColor = color;
			conversation.SelectionFont = conversation.Font;
			conversation.AppendText((text ?? "") + Environment.NewLine + Environment.NewLine);
		}

		void ValidateNextPromptDraft()
		{
			var candidate = nextPrompt.Text.Trim();
			if (candidate.Length == 0)
			{
				applyNextPrompt.Enabled = false;
				return;
			}

			if (TrainingAgent.ValidatePromptTemplate(candidate, out var error))
			{
				applyNextPrompt.Enabled = true;
				nextPromptStatus.Text = "Complete replacement prompt is valid and ready to save.";
			}
			else
			{
				applyNextPrompt.Enabled = false;
				nextPromptStatus.Text = "Needs editing: " + error;
			}
		}

		void LoadInputs(TrainingRun run)
		{
			prompt.Text = Read(run.PromptPath, "The prompt has not been prepared yet.");
			gameGuide.Text = Read(run.GameGuidePath, "The game guide has not been prepared yet.");
			gameRules.LoadJsonFile(run.GameRulesPath,
				"The resolved unit and weapon rules have not been exported yet.");
			fightManifest.LoadJsonFile(run.FightManifestPath,
				"The fight manifest has not been prepared yet.");
		}

		void LoadChanges(TrainingRun run)
		{
			changes.Items.Clear();
			foreach (var change in WorkspaceSnapshot.ReadChanges(run))
			{
				var item = new ListViewItem(change.Kind);
				item.SubItems.Add(change.RelativePath);
				changes.Items.Add(item);
			}
		}

		void LoadNextPrompt(TrainingRun run)
		{
			var agent = run.Manifest.Agent;
			nextPrompt.Text = agent?.SuggestedNextPrompt ?? "";
			if (agent?.SuggestedNextPromptAccepted == true)
			{
				nextPromptTab.Text = "Next prompt";
				nextPromptStatus.Text = "Saved. This complete prompt will be rendered with fresh evidence next round.";
				applyNextPrompt.Enabled = false;
			}
			else if (!string.IsNullOrEmpty(agent?.SuggestedNextPrompt))
			{
				nextPromptTab.Text = "Next prompt *";
				ValidateNextPromptDraft();
			}
			else if (agent?.CompletedUtc != null)
			{
				nextPromptTab.Text = "Next prompt";
				nextPromptStatus.Text = "No complete prompt was returned. You can paste or write one here.";
				applyNextPrompt.Enabled = false;
			}
			else
			{
				nextPromptTab.Text = "Next prompt";
				nextPromptStatus.Text = "The agent will draft the complete next-round prompt when it finishes.";
				applyNextPrompt.Enabled = false;
			}
		}

		void LoadTranscript(string path)
		{
			progress.Clear();
			transcriptParser.Reset();
			foreach (var line in File.ReadLines(path))
				Append(transcriptParser.ParseLine(line), scroll: false);

			liveOutputStarted = true;
			progress.SelectionStart = progress.TextLength;
			progress.ScrollToCaret();
		}

		void Append(TerminalLine line, bool scroll) => AppendTo(progress, line, scroll);

		void AppendTo(RichTextBox target, TerminalLine line, bool scroll)
		{
			target.SelectionStart = target.TextLength;
			target.SelectionLength = 0;
			var hasAnsiStyle = line.Spans.Any(span =>
				span.Style.Foreground.HasValue || span.Style.Bold);
			var fallback = hasAnsiStyle ? default : SemanticStyle(line.PlainText);

			foreach (var span in line.Spans)
			{
				target.SelectionColor = span.Style.Foreground ?? fallback.Foreground ?? Ink;
				target.SelectionFont = span.Style.Bold || fallback.Bold ? boldProgressFont : target.Font;
				target.AppendText(span.Text);
			}

			target.SelectionColor = Ink;
			target.SelectionFont = target.Font;
			target.AppendText(Environment.NewLine);

			if (scroll)
			{
				target.SelectionStart = target.TextLength;
				target.ScrollToCaret();
			}
		}

		static TerminalStyle SemanticStyle(string text)
		{
			var value = text?.TrimStart() ?? "";
			var lower = value.ToLowerInvariant();

			if (lower.Contains("error", StringComparison.Ordinal) ||
				lower.Contains("failed", StringComparison.Ordinal) ||
				lower.Contains("exception", StringComparison.Ordinal) ||
				lower.Contains("exit code", StringComparison.Ordinal))
				return new TerminalStyle(Color.FromArgb(231, 72, 86), Bold: false);

			if (lower.Contains("passed", StringComparison.Ordinal) ||
				lower.Contains("succeeded", StringComparison.Ordinal) ||
				lower.Contains("verified", StringComparison.Ordinal) ||
				lower.Contains("complete", StringComparison.Ordinal))
				return new TerminalStyle(Color.FromArgb(22, 198, 12), Bold: false);

			if (value.StartsWith(TrainingAgent.NextPromptBegin, StringComparison.OrdinalIgnoreCase) ||
				value.StartsWith(TrainingAgent.NextPromptEnd, StringComparison.OrdinalIgnoreCase))
				return new TerminalStyle(Color.FromArgb(180, 100, 220), Bold: true);

			if (value.StartsWith("===", StringComparison.Ordinal) ||
				value.StartsWith("==>", StringComparison.Ordinal) ||
				value.StartsWith("#", StringComparison.Ordinal))
				return new TerminalStyle(Color.FromArgb(97, 214, 214), Bold: true);

			if (value.StartsWith("├", StringComparison.Ordinal) ||
				value.StartsWith("└", StringComparison.Ordinal) ||
				value.StartsWith("│", StringComparison.Ordinal) ||
				value.Contains("(shell)", StringComparison.OrdinalIgnoreCase))
				return new TerminalStyle(Color.FromArgb(58, 150, 221), Bold: false);

			return default;
		}

		static string AgentSummary(TrainingRun run)
		{
			var result = run.Manifest.Agent;
			if (result == null)
				return "Review the inputs, then choose Analyze & improve when ready.";

			if (result.RestoredUtc != null)
				return result.ChangeCount < 0
					? "The agent's changes were restored to the pre-agent snapshot."
					: $"The {result.ChangeCount} agent change(s) were restored to the pre-agent snapshot.";

			if (result.ExitCode == null)
				return "Improvement agent running — progress is streaming above.";

			if (result.Cancelled)
			{
				var stopped = result.ChangeCount switch
				{
					> 0 => $"It had already changed {result.ChangeCount} file(s), which are still in the workspace.",
					0 => "It had not changed anything yet.",
					_ => "Whether it changed anything could not be determined."
				};
				return $"Stopped before it finished. {stopped} " +
					"Choose Analyze & improve to start another round, or Restore previous iteration.";
			}

			if (result.ExitCode != 0)
			{
				var phase = string.Equals(result.FailurePhase, "verification",
					StringComparison.OrdinalIgnoreCase)
					? "Independent verification failed"
					: "The coding agent failed";
				var detail = string.IsNullOrWhiteSpace(result.FailureMessage)
					? ""
					: " — " + result.FailureMessage;
				return $"{phase}{detail}. Choose Fix failed improvement to repair the current changes, or Restore previous iteration.";
			}

			return result.ExitCode == 0
				? (result.ChangeCount < 0
						? "The agent's changes could not be inspected. Verification passed."
						: $"{result.ChangeCount} source file(s) changed. Verification passed.") +
					(!string.IsNullOrEmpty(result.SuggestedNextPrompt) && !result.SuggestedNextPromptAccepted
						? " Review Next prompt * to choose the complete prompt for the next round."
						: "")
				: "";
		}

		static TextBox TextPane() => new()
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			WordWrap = false,
			MaxLength = int.MaxValue,
			BorderStyle = BorderStyle.None,
			BackColor = Paper,
			ForeColor = Ink,
			Font = new Font(FontFamily.GenericMonospace, 8.5f)
		};

		static TabPage Page(string title, Control content)
		{
			var page = new TabPage(title) { BackColor = Paper, Padding = new Padding(2) };
			page.Controls.Add(content);
			return page;
		}

		static string Read(string path, string missing) =>
			File.Exists(path) ? File.ReadAllText(path) : missing;

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (IsHandleCreated)
					taskbarProgress.SetBusy(Handle, busy: false);
				taskbarProgress.Dispose();
				boldProgressFont?.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
