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
		readonly TabPage promptTab;
		readonly TabPage nextPromptTab;
		readonly RichTextBox progress;
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
		readonly TaskbarProgress taskbarProgress = new();

		bool liveOutputStarted;
		TrainingRun shownRun;

		public event Action<TrainingRun, string> NextPromptAccepted;

		internal string AgentProgressText => progress.Text;
		internal string AgentPromptText => prompt.Text;
		internal string GameGuideText => gameGuide.Text;
		internal string GameRulesText => gameRules.SourceText;
		internal string SelectedView => views.SelectedTab?.Text;
		internal string NextPromptText => nextPrompt.Text;
		internal bool CanAcceptNextPrompt => applyNextPrompt.Enabled;
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
				BackColor = Color.FromArgb(38, 38, 38)
			};
			nextPromptFooter.Controls.Add(applyNextPrompt);
			nextPromptFooter.Controls.Add(nextPromptStatus);

			nextPromptTab = new TabPage("Next prompt") { BackColor = Paper, Padding = new Padding(8) };
			nextPromptTab.Controls.Add(nextPrompt);
			nextPromptTab.Controls.Add(nextPromptFooter);
			nextPromptTab.Controls.Add(Heading(
				"Complete replacement prompt for the next round. Edit it before saving if needed."));

			views = new TabControl { Dock = DockStyle.Fill };
			views.TabPages.Add(progressTab);
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
			shownRun = run;
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
		}

		public void StartVerificationRun(TrainingRun run)
		{
			shownRun = run;
			LoadInputs(run);
			progress.Clear();
			transcriptParser.Reset();
			liveOutputStarted = false;
			summary.Text = "Cleaning generated output and retrying independent verification…";
			views.SelectedTab = progressTab;
			Text = "AutoC&C — Improvement (verifying)";
			taskbarProgress.SetBusy(Handle, busy: true);
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
			shownRun = run;
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
		}

		public void ShowAgentRun(TrainingRun run, bool promptFirst = false)
		{
			shownRun = run;
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

		void Append(TerminalLine line, bool scroll)
		{
			progress.SelectionStart = progress.TextLength;
			progress.SelectionLength = 0;
			var hasAnsiStyle = line.Spans.Any(span =>
				span.Style.Foreground.HasValue || span.Style.Bold);
			var fallback = hasAnsiStyle ? default : SemanticStyle(line.PlainText);

			foreach (var span in line.Spans)
			{
				progress.SelectionColor = span.Style.Foreground ?? fallback.Foreground ?? Ink;
				progress.SelectionFont = span.Style.Bold || fallback.Bold ? boldProgressFont : progress.Font;
				progress.AppendText(span.Text);
			}

			progress.SelectionColor = Ink;
			progress.SelectionFont = progress.Font;
			progress.AppendText(Environment.NewLine);

			if (scroll)
			{
				progress.SelectionStart = progress.TextLength;
				progress.ScrollToCaret();
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
