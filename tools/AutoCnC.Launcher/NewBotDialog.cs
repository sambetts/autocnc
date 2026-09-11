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
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Collects the two choices needed to create a bot workspace.</summary>
	public sealed class NewBotDialog : Form
	{
		readonly TextBox nameBox;
		readonly TextBox locationBox;

		public string BotName => nameBox.Text.Trim();
		public string OutputDirectory => locationBox.Text.Trim();

		public NewBotDialog(string defaultDirectory)
		{
			Text = "Create a battle bot";
			Font = CommandTheme.Body;
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MinimizeBox = false;
			MaximizeBox = false;
			ShowInTaskbar = false;
			AutoSize = true;
			AutoSizeMode = AutoSizeMode.GrowAndShrink;
			Padding = new Padding(12);

			nameBox = new TextBox { Width = 380 };
			locationBox = new TextBox { Width = 380, Text = defaultDirectory };

			var browse = new ActionButton { Text = "Browse…", AutoSize = true };
			browse.Click += (_, _) => Browse();

			var ok = new ActionButton { Text = "Create", Primary = true, AutoSize = true, DialogResult = DialogResult.OK };
			ok.Click += Validate;
			var cancel = new ActionButton { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

			var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			grid.Controls.Add(Label("Bot name:"), 0, 0);
			grid.Controls.Add(nameBox, 1, 0);
			grid.SetColumnSpan(nameBox, 2);
			grid.Controls.Add(Label("Create under:"), 0, 1);
			grid.Controls.Add(locationBox, 1, 1);
			grid.Controls.Add(browse, 2, 1);

			var hint = new Label
			{
				AutoSize = true,
				MaximumSize = new Size(500, 0),
				ForeColor = SystemColors.GrayText,
				Text = "A new folder containing a C# solution, bot, starter doctrine, mode, and tests will be created."
			};
			grid.Controls.Add(hint, 1, 2);
			grid.SetColumnSpan(hint, 2);

			var buttons = new FlowLayoutPanel
			{
				AutoSize = true,
				FlowDirection = FlowDirection.RightToLeft,
				Dock = DockStyle.Fill,
				Margin = new Padding(0, 12, 0, 0)
			};
			buttons.Controls.Add(cancel);
			buttons.Controls.Add(ok);
			grid.Controls.Add(buttons, 0, 3);
			grid.SetColumnSpan(buttons, 3);

			Controls.Add(grid);
			AcceptButton = ok;
			CancelButton = cancel;
			CommandTheme.Apply(this);
		}

		static Label Label(string text) =>
			new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

		void Browse()
		{
			using var dialog = new FolderBrowserDialog { Description = "Choose where the bot folder will be created" };
			if (Directory.Exists(OutputDirectory))
				dialog.SelectedPath = OutputDirectory;

			if (dialog.ShowDialog(this) == DialogResult.OK)
				locationBox.Text = dialog.SelectedPath;
		}

		void Validate(object sender, EventArgs e)
		{
			if (BotName.Length > 0 && OutputDirectory.Length > 0)
				return;

			MessageBox.Show(this, "Enter a bot name and a parent folder.", "AutoC&C",
				MessageBoxButtons.OK, MessageBoxIcon.Warning);
			DialogResult = DialogResult.None;
		}
	}
}
