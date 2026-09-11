// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	internal sealed class BattleFeedbackDialog : Form
	{
		readonly TrainingRun run;
		readonly Action<string> save;
		readonly TextBox feedback;
		readonly Label message;
		readonly Label count;
		readonly Button submit;

		internal string FeedbackText
		{
			get => feedback.Text;
			set => feedback.Text = value;
		}
		internal bool CanSubmit => submit.Enabled;
		internal bool CanEditFeedback => feedback.Enabled;
		internal string MessageText => message.Text;

		internal BattleFeedbackDialog(TrainingRun run, Action<string> save, bool beforeImprovement = false)
		{
			this.run = run ?? throw new ArgumentNullException(nameof(run));
			this.save = save ?? throw new ArgumentNullException(nameof(save));
			SuspendLayout();
			Text = beforeImprovement ? "AutoC&C - Feedback before improvement" : "AutoC&C - Battle feedback";
			Font = CommandTheme.Body;
			AutoScaleMode = AutoScaleMode.Dpi;
			AutoScaleDimensions = new SizeF(96, 96);
			StartPosition = FormStartPosition.CenterParent;
			MinimumSize = new Size(560, 380);
			ClientSize = new Size(720, 420);
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;

			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			var outcome = run.Manifest.Result?.Outcome;
			layout.Controls.Add(new Label
			{
				Text = string.Equals(outcome, "Lost", StringComparison.OrdinalIgnoreCase)
					? "Why do you think you lost?"
					: string.Equals(outcome, "Won", StringComparison.OrdinalIgnoreCase)
						? "What helped your bot win?" : "What would you change about this battle?",
				Font = CommandTheme.Heading, ForeColor = CommandTheme.Green,
				AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10)
			}, 0, 0);
			layout.Controls.Add(new Label
			{
				Text = $"{run.Manifest.CreatedUtc.ToLocalTime():g} / {outcome ?? run.Manifest.Status} / " +
					$"{Csv.Clock(run.Manifest.Result?.DurationSeconds ?? 0)} / " +
					(run.IsHeadless ? "Headless" : "Rendered"),
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = CommandTheme.Muted,
				UseMnemonic = false, Margin = new Padding(0, 0, 0, 10)
			}, 0, 1);
			layout.Controls.Add(new Label
			{
				Text = run.NeedsReplayForFeedback
					? BattleFeedback.Description(run) + (beforeImprovement
						? " You can also improve using the logs and stats without personal feedback." : "")
					: "Add the moments you noticed: a late expansion, an exposed harvester, or an attack that went wrong. " +
						"Your feedback is optional and will be included when this battle is analyzed.",
				AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10)
			}, 0, 2);
			feedback = new TextBox
			{
				Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
				Dock = DockStyle.Fill, MaxLength = TrainingRun.MaxPlayerFeedbackLength,
				Text = run.Manifest.Result?.PlayerFeedback ?? "", Enabled = run.CanProvideFeedback,
				AccessibleName = "Battle feedback",
				AccessibleDescription = "Your observations about why this battle won or lost.",
				Margin = new Padding(0)
			};
			layout.Controls.Add(feedback, 0, 3);
			count = new Label
			{
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = CommandTheme.Muted,
				TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 4, 0, 0)
			};
			layout.Controls.Add(count, 0, 4);
			message = new Label
			{
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = CommandTheme.Danger,
				AccessibleName = "Feedback save error", UseMnemonic = false
			};
			layout.Controls.Add(message, 0, 5);

			var actions = new FlowLayoutPanel
			{
				AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
				Margin = new Padding(0, 12, 0, 0)
			};
			var cancel = new ActionButton
			{
				Text = beforeImprovement ? "Cancel" : "Not now", DialogResult = DialogResult.Cancel
			};
			actions.Controls.Add(cancel);
			if (beforeImprovement)
				actions.Controls.Add(new ActionButton
				{
					Text = "Improve without feedback", DialogResult = DialogResult.Ignore
				});
			submit = new ActionButton
			{
				Text = run.NeedsReplayForFeedback ? "Watch replay && add feedback"
					: beforeImprovement ? "Save feedback && improve" : "Save feedback",
				Primary = true,
				AccessibleName = run.NeedsReplayForFeedback ? "Watch replay and add feedback"
					: beforeImprovement ? "Save feedback and improve" : "Save feedback"
			};
			submit.Click += (_, _) => Submit();
			actions.Controls.Add(submit);
			layout.Controls.Add(actions, 0, 6);
			Controls.Add(layout);
			AcceptButton = submit;
			CancelButton = cancel;
			feedback.TextChanged += (_, _) => UpdateState();
			Shown += (_, _) =>
			{
				if (feedback.Enabled)
					feedback.Focus();
			};
			UpdateState();
			CommandTheme.Apply(this);
			ResumeLayout(true);
		}

		void UpdateState()
		{
			count.Text = $"{feedback.Text.Length:N0} / {TrainingRun.MaxPlayerFeedbackLength:N0} characters";
			submit.Enabled = run.NeedsReplayForFeedback
				? run.HasRecordedBattle && File.Exists(run.ReplayPath)
				: run.CanProvideFeedback && !string.Equals(feedback.Text.Trim(),
					run.Manifest.Result?.PlayerFeedback ?? "", StringComparison.Ordinal);
		}

		internal void Submit()
		{
			if (!submit.Enabled)
				return;
			if (run.NeedsReplayForFeedback)
			{
				DialogResult = DialogResult.Retry;
				Close();
				return;
			}

			try
			{
				save(feedback.Text);
				DialogResult = DialogResult.OK;
				Close();
			}
			catch (ArgumentException ex)
			{
				message.Text = ex.Message;
			}
			catch (InvalidOperationException ex)
			{
				message.Text = ex.Message;
			}
			catch (IOException ex)
			{
				message.Text = "Could not save feedback. Your draft is still here. " + ex.Message;
			}
			catch (UnauthorizedAccessException ex)
			{
				message.Text = "Could not save feedback. Check access to the run folder. " + ex.Message;
			}
		}
	}
}
