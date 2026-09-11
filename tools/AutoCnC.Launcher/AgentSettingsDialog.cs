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

		public string AgentCommand => commandBox.Text.Trim();
		public string[] AgentArguments => argumentsBox.Lines
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.ToArray();

		public AgentSettingsDialog(string command, string[] arguments)
		{
			Text = "Improvement agent";
			Font = CommandTheme.Body;
			FormBorderStyle = FormBorderStyle.Sizable;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			MinimumSize = new Size(620, 430);
			Size = new Size(720, 500);
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

			var reset = new ActionButton { Text = "Use GitHub Copilot CLI defaults", AutoSize = true };
			reset.Click += (_, _) =>
			{
				commandBox.Text = "copilot";
				argumentsBox.Lines = TrainingAgent.DefaultArguments;
			};

			var ok = new ActionButton { Text = "Save", Primary = true, AutoSize = true, DialogResult = DialogResult.OK };
			ok.Click += Validate;
			var cancel = new ActionButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

			var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
				Text = "Use {prompt} or {promptFile}. Also available: {project}, {workspace}, {evidence}, and {run}.",
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(0, 6, 0, 6)
			}, 0, 4);

			var buttons = new FlowLayoutPanel
			{
				AutoSize = true,
				Dock = DockStyle.Fill,
				FlowDirection = FlowDirection.RightToLeft
			};
			buttons.Controls.Add(cancel);
			buttons.Controls.Add(ok);
			buttons.Controls.Add(reset);
			grid.Controls.Add(buttons, 0, 5);

			Controls.Add(grid);
			AcceptButton = ok;
			CancelButton = cancel;
			CommandTheme.Apply(this);
		}

		void Validate(object sender, EventArgs e)
		{
			if (AgentCommand.Length > 0 &&
				AgentArguments.Any(a => a.Contains("{prompt}", StringComparison.Ordinal) ||
					a.Contains("{promptFile}", StringComparison.Ordinal)))
				return;

			MessageBox.Show(this, "Set a command and include {prompt} or {promptFile} in its arguments.",
				"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			DialogResult = DialogResult.None;
		}
	}
}
