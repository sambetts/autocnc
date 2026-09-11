// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	public sealed partial class MainForm
	{
		// A field command console: bot identity above the active workstation, orders at right.
		// Original olive/phosphor/amber artwork, not a recreation of Westwood's assets.
		// Build, fight and improve remain the existing script-backed operations.
		readonly Panel[] stations = new Panel[4];
		readonly ActionButton[] navigation = new ActionButton[4];
		BotDossier dossier;
		Label stationTitle;
		Label stationDescription;
		Label commandStatus;
		Label trainingHint;
		Label operationSummary;
		Panel stationHost;

		internal int SelectedStation { get; private set; }

		void BuildCommandLayout()
		{
			SuspendLayout();
			root = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
				Padding = new Padding(20, 14, 20, 14), BackColor = CommandTheme.Background
			};
			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
			root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
			root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

			var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
			header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			header.Controls.Add(new Label
			{
				Text = "AUTO C&&C", Font = CommandTheme.Display, ForeColor = CommandTheme.Amber,
				AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0)
			}, 0, 0);
			header.Controls.Add(new Label
			{
				Text = "BATTLE COMMAND\nBuild a better opponent.", ForeColor = CommandTheme.Muted,
				AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right
			}, 1, 0);
			root.Controls.Add(header, 0, 0);
			root.SetColumnSpan(header, 2);

			var main = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 18, 0)
			};
			main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			main.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
			main.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
			main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			dossier = new BotDossier { Dock = DockStyle.Fill, Margin = new Padding(0) };
			main.Controls.Add(dossier, 0, 0);

			var heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
			stationTitle = new Label
			{
				Font = CommandTheme.Heading, AutoSize = false, Dock = DockStyle.Top,
				Height = 51, Padding = new Padding(0, 16, 0, 0)
			};
			stationDescription = new Label
			{
				Dock = DockStyle.Bottom, Height = 30, ForeColor = CommandTheme.Muted, AutoEllipsis = true
			};
			heading.Controls.Add(stationTitle);
			heading.Controls.Add(stationDescription);
			main.Controls.Add(heading, 0, 1);
			stationHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
			main.Controls.Add(stationHost, 0, 2);

			// Construct shared actions first, then place each in its workstation.
			var launch = BuildActionRow();
			AddStation(0, BuildBotGroup());
			AddStation(1, BuildBattleGroup());
			AddStation(2, BuildTrainingGroup());
			AddStation(3, BuildSystemsStation());
			main.Controls.Add(BuildBattleBanner(), 0, 3);
			root.Controls.Add(main, 0, 1);

			var rail = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
				BackColor = CommandTheme.Surface, Padding = new Padding(12), Margin = new Padding(0)
			};
			rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			var navigationHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
			var navigationStack = new TableLayoutPanel
			{
				Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 6, Margin = new Padding(0)
			};
			navigationStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			navigationStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			for (var i = 0; i < 4; i++)
				navigationStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
			navigationStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			navigationHost.Controls.Add(navigationStack);
			rail.Controls.Add(navigationHost, 0, 0);
			navigationStack.Controls.Add(new Label
			{
				Text = "COMMAND DECK", ForeColor = CommandTheme.Green,
				AutoSize = true, Margin = new Padding(3, 3, 3, 10)
			}, 0, 0);
			string[] names = ["&1   BOT BAY", "&2   PROVING GROUND", "&3   AI TRAINING", "&4   SYSTEMS"];
			string[] details = ["Create / code / deploy", "Configure a local battle", "Analyze / improve / repeat", "Platform / logs / replays"];
			for (var i = 0; i < navigation.Length; i++)
			{
				var index = i;
				navigation[i] = new ActionButton
				{
					Text = names[i], Detail = details[i], Dock = DockStyle.Fill,
					AutoSize = false, Margin = new Padding(0, 0, 0, 6),
					AccessibleName = names[i][5..], AccessibleDescription = details[i]
				};
				navigation[i].Click += (_, _) => SelectStation(index);
				navigationStack.Controls.Add(navigation[i], 0, i + 1);
			}
			historyButton.Dock = DockStyle.Fill;
			historyButton.Margin = new Padding(0, 4, 0, 4);
			navigationStack.Controls.Add(historyButton, 0, 5);

			commandStatus = new Label
			{
				Dock = DockStyle.Fill, AutoSize = true, ForeColor = CommandTheme.Muted, UseMnemonic = false,
				Padding = new Padding(3, 8, 3, 4), AutoEllipsis = true,
				MaximumSize = new Size(238, 100),
				AccessibleName = "Operation status"
			};
			rail.Controls.Add(commandStatus, 0, 1);
			operationSummary = new Label
			{
				Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(238, 0),
				ForeColor = CommandTheme.Green, Margin = new Padding(3, 10, 3, 10),
				Text = "LOCAL SKIRMISH\nYour bot vs. AI opponents"
			};
			rail.Controls.Add(operationSummary, 0, 2);
			rail.Controls.Add(launch, 0, 3);
			rail.SizeChanged += (_, _) =>
			{
				var scale = DeviceDpi / 96f;
				var compact = rail.Height < 620 * scale;
				for (var i = 1; i <= 4; i++)
					navigationStack.RowStyles[i].Height = (compact ? 56 : 72) * scale;
				operationSummary.MaximumSize = new Size(rail.ClientSize.Width - rail.Padding.Horizontal - 6,
					compact ? Font.Height : 0);
				commandStatus.MaximumSize = new Size(operationSummary.MaximumSize.Width,
					Font.Height * (compact ? 2 : 4) + commandStatus.Padding.Vertical);
			};
			root.Controls.Add(rail, 1, 1);
			Controls.Add(root);
			SelectStation(1);
			ResumeLayout(true);
		}

		void AddStation(int index, Control content)
		{
			stations[index] = new Panel
			{
				Dock = DockStyle.Fill, AutoScroll = true, Visible = false,
				BackColor = CommandTheme.Background
			};
			content.Dock = DockStyle.Top;
			stations[index].Controls.Add(content);
			stationHost.Controls.Add(stations[index]);
		}

		internal void SelectStation(int index)
		{
			if (index < 0 || index >= stations.Length)
				throw new ArgumentOutOfRangeException(nameof(index));
			SelectedStation = index;
			for (var i = 0; i < stations.Length; i++)
			{
				stations[i].Visible = i == index;
				navigation[i].Selected = i == index;
			}
			stations[index].BringToFront();
			string[] titles = ["Make it yours.", "Prepare for contact.", "Evolve your doctrine.", "Behind the front line."];
			string[] descriptions =
			[
				"Your army is a codebase. Give it a strategy worth fighting for.",
				"Choose the battlefield and opposition. Let your code do the fighting.",
				"Turn combat evidence into the next version of your battle bot.",
				"Maintain the platform and inspect the evidence behind each operation."
			];
			stationTitle.Text = titles[index];
			stationDescription.Text = descriptions[index];
		}

		Control BuildSystemsStation()
		{
			var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1 };
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.Controls.Add(BuildRepositoryRow(), 0, 0);
			var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 16, 0, 16) };
			actions.Controls.Add(platformButton);
			actions.Controls.Add(replayButton);
			actions.Controls.Add(SmallButton("Game logs", (_, _) => OpenLogsFolder()));
			actions.Controls.Add(SmallButton("Replays folder", (_, _) => OpenFolder(ReplaysDir, "No replays yet")));
			actions.Controls.Add(SmallButton("Build output", (_, _) => ShowOutputWindow()));
			layout.Controls.Add(actions, 0, 1);
			layout.Controls.Add(new Label
			{
				Text = "The launcher runs the same scripts you can run in a terminal. " +
					"Game installation and multiplayer lobbies remain in OpenRA; this command deck launches local tests against AI.",
				AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = CommandTheme.Muted
			}, 0, 2);
			return Group("PLATFORM & ARCHIVE", layout);
		}

		void UpdateCommandReadout(bool ready, bool editable, bool busy)
		{
			var path = botBox.Text.Trim();
			var name = path.Length == 0 ? "No bot selected" : Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
			var readiness = busy ? battleRunning ? "IN COMBAT / evidence recording" : "OPERATION IN PROGRESS"
				: !BotExists() ? "AWAITING BOT / select a project in Bot bay"
				: ready ? "LOADOUT SET / ready for a test fight" : "SETUP REQUIRED / check operation status";
			var result = LastRunMatchesSelectedBot() ? lastRun?.Manifest.Result : null;
			var lastSortie = result == null ? "No battle result yet. Put your doctrine to the test."
				: $"Last battle: {result.Outcome ?? lastRun.Manifest.Status} / {Csv.Clock(result.DurationSeconds)}";
			dossier.SetBot(name, editable ? "C# BATTLE BOT / EDITABLE SOURCE"
				: IsPrebuilt() ? "COMPILED BATTLE BOT / PLAY ONLY" : "YOUR CODE. YOUR DOCTRINE. YOUR ARMY.",
				readiness, ready || busy, lastSortie);
			trainingHint.Text = !editable ? "AI training needs an editable C# bot project. Select one in Bot bay."
				: !LastRunMatchesSelectedBot() || lastRun?.Manifest.CompletedUtc == null
					? "First, fight a battle. Its logs and decision trace unlock Analyze & improve."
					: improveButton.Enabled ? "Battle evidence available. Ready for the next improvement."
						: "Review the agent workspace or fight again to gather fresh evidence.";
			operationSummary.Text = continuousBox.Checked && editable
				? "CONTINUOUS TRAINING\nRepeats until you stop the operation"
				: IsHeadlessSelected() ? "HEADLESS SKIRMISH\nCPU speed / no game window"
					: "RENDERED SKIRMISH\nWatch your doctrine in action";
		}
	}
}
