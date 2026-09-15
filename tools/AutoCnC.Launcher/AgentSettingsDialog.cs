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
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Edits the provider-neutral command used for optional AI improvement.</summary>
	public sealed class AgentSettingsDialog : Form
	{
		readonly TextBox commandBox;
		readonly TextBox argumentsBox;
		readonly TextBox stdinBox;

		public string AgentCommand => commandBox.Text.Trim();
		public string[] AgentArguments => argumentsBox.Lines
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.ToArray();

		public string AgentStdin => stdinBox.Text.Trim() is { Length: > 0 } stdin ? stdin : null;

		public AgentSettingsDialog(string command, string[] arguments, string stdin)
		{
			Text = "Improvement agent";
			Font = CommandTheme.Body;
			FormBorderStyle = FormBorderStyle.Sizable;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			MinimumSize = new Size(620, 480);
			Size = new Size(720, 550);
			Padding = new Padding(12);

			commandBox = new TextBox { Dock = DockStyle.Fill, Text = command ?? "copilot" };
			argumentsBox = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ScrollBars = ScrollBars.Both,
				WordWrap = false,
				Font = new Font(FontFamily.GenericMonospace, 9f),
				Lines = arguments is { Length: > 0 } ? arguments : TrainingAgent.DefaultArguments
			};
			stdinBox = new TextBox
			{
				Dock = DockStyle.Fill,
				Font = new Font(FontFamily.GenericMonospace, 9f),
				Text = stdin ?? ""
			};

			var reset = new ActionButton { Text = "Use GitHub Copilot CLI defaults", AutoSize = true };
			reset.Click += (_, _) =>
			{
				commandBox.Text = "copilot";
				argumentsBox.Lines = TrainingAgent.DefaultArguments;
				stdinBox.Text = TrainingAgent.DefaultStdin;
			};

			var ok = new ActionButton { Text = "Save", Primary = true, AutoSize = true, DialogResult = DialogResult.OK };
			ok.Click += Validate;
			var cancel = new ActionButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

			var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.Controls.Add(new Label { Text = "Command:", AutoSize = true }, 0, 0);
			grid.Controls.Add(commandBox, 0, 1);
			grid.Controls.Add(new Label
			{
				Text = "Arguments (one per line):",
				AutoSize = true,
				Margin = new Padding(0, 10, 0, 3)
			}, 0, 2);
			grid.Controls.Add(argumentsBox, 0, 3);
			grid.Controls.Add(new Label
			{
				Text = "Standard input:",
				AutoSize = true,
				Margin = new Padding(0, 10, 0, 3)
			}, 0, 4);
			grid.Controls.Add(stdinBox, 0, 5);
			grid.Controls.Add(new Label
			{
				Text = "Use {prompt} or {promptFile}. Also available: {project}, {workspace}, " +
					"{evidence}, {sessionId}, and {run}." + Environment.NewLine +
					"A rendered prompt is far larger than Windows allows on a command line, so send it in on standard input." +
					Environment.NewLine +
					"{sessionId} keeps every round and every chat message in one agent conversation; " +
					"drop it and the agent forgets between turns.",
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(0, 6, 0, 6)
			}, 0, 6);

			var buttons = new FlowLayoutPanel
			{
				AutoSize = true,
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.RightToLeft
			};
			buttons.Controls.Add(cancel);
			buttons.Controls.Add(ok);
			buttons.Controls.Add(reset);
			grid.Controls.Add(buttons, 0, 7);

			Controls.Add(grid);
			AcceptButton = ok;
			CancelButton = cancel;
			CommandTheme.Apply(this);
		}

		void Validate(object sender, EventArgs e)
		{
			if (AgentCommand.Length > 0 &&
				(TrainingAgent.CarriesPrompt(AgentArguments) ||
					TrainingAgent.CarriesPrompt([AgentStdin])))
				return;

			MessageBox.Show(this,
				"Set a command and include {prompt} or {promptFile} in its arguments or standard input.",
				"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			DialogResult = DialogResult.None;
		}
	}
}
