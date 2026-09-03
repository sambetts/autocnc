#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// The launcher window: point it at your battle code, choose who to fight, press play.
	/// </summary>
	/// <remarks>
	/// Every button here runs a script from <c>scripts/</c> and shows you its output, so nothing
	/// you can do in this window is something you could not have typed yourself. That is
	/// deliberate: the window is a convenience over the authoring loop, not a second
	/// implementation of it.
	/// </remarks>
	public sealed class MainForm : Form
	{
		sealed class FactionChoice
		{
			public string Value { get; init; }
			public string Label { get; init; }
			public override string ToString() => Label;
		}

		sealed class ExecutionChoice
		{
			public string Value { get; init; }
			public string Label { get; init; }
			public override string ToString() => Label;
		}

		static readonly FactionChoice[] Factions =
		[
			new() { Value = "Random", Label = "Random" },
			new() { Value = "gdi", Label = "GDI" },
			new() { Value = "nod", Label = "Nod" }
		];

		static readonly ExecutionChoice[] ExecutionModes =
		[
			new()
			{
				Value = BattleExecutionModes.Headless,
				Label = "Headless - CPU maximum, no game window"
			},
			new()
			{
				Value = BattleExecutionModes.Rendered,
				Label = "Rendered - watch the battle, up to 40x"
			}
		];

		readonly LauncherSettings settings = LauncherSettings.Load();
		readonly ScriptRunner runner = new();
		readonly Queue<ScriptJob> queue = new();
		readonly MatchLog matchLog = new();
		readonly BattleEventLog battleLog = new();
		readonly TaskbarProgress taskbarProgress = new();
		readonly ContinuousTrainingLoop continuousLoop = new();

		/// <summary>Script output produced before there was a window to put it in.</summary>
		readonly List<string> pendingOutput = [];

		RepoLayout repo;
		ScriptJob activeJob;
		TrainingRun activeRun;
		TrainingRun lastRun;
		TrainingHistory loadedHistory = TrainingHistory.Empty;
		string loadedHistoryBot;
		DateTime battleStartedUtc;
		string replayBeforeBattle;
		bool stopRequested;
		bool closing;
		bool battleRunning;
		bool battleFinished;

		TextBox repositoryBox;
		TextBox botBox;
		CheckBox runTestsBox;
		ComboBox mapBox;
		ComboBox difficultyBox;
		Label difficultySummary;
		NumericUpDown opponentsBox;
		ComboBox factionBox;
		ComboBox botFactionBox;
		ComboBox speedBox;
		ComboBox executionBox;
		Button launchButton;
		Button buildButton;
		Button platformButton;
		Button newBotButton;
		Button openCodeButton;
		Button historyButton;
		Button improveButton;
		Button retryVerificationButton;
		Button reviewButton;
		Button restoreButton;
		Button runFolderButton;
		Button stopButton;
		Button replayButton;
		CheckBox continuousBox;
		GroupBox botGroup;
		GroupBox battleGroup;
		GroupBox trainingGroup;
		Panel battleBanner;
		Label battleBannerText;
		TableLayoutPanel root;
		StatusStrip status;
		ResultsWindow resultsWindow;
		OutputWindow outputWindow;
		ImprovementWindow improvementWindow;
		Timer matchTimer;
		ToolStripStatusLabel statusLabel;

		public MainForm()
		{
			Text = "AutoC&C — Battle Launcher";
			Font = SystemFonts.MessageBoxFont;
			StartPosition = FormStartPosition.Manual;

			BuildLayout();

			// Both fire on a background thread, and a script can still be writing while the
			// window is going away.
			runner.Output += line =>
			{
				var job = activeJob;
				Post(() => RouteJobOutput(job, line));
			};
			runner.Finished += code => Post(() => JobFinished(code));
		}

		void Post(Action action)
		{
			if (IsDisposed || Disposing || !IsHandleCreated)
				return;

			try
			{
				BeginInvoke(action);
			}
			catch (ObjectDisposedException)
			{
			}
			catch (InvalidOperationException)
			{
			}
		}

		// -------------------------------------------------------------------
		// Layout
		// -------------------------------------------------------------------

		/// <summary>
		/// This window is where a battle is set up, and nothing else.
		/// </summary>
		/// <remarks>
		/// What a battle produced — the graphs and the log — lives in windows of its own that
		/// appear when one starts, the way a debugger's windows appear when you run rather than
		/// sitting empty in the editor all day. It keeps this window to the size of the question
		/// it actually asks, and it means the results are still there, at whatever size you gave
		/// them, while you set the next fight up.
		/// </remarks>
		void BuildLayout()
		{
			root = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 7,
				Padding = new Padding(10)
			};

			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			// The slack lives in a row of its own so that everything else keeps the height it asked
			// for. A banner that appears and disappears must not be able to squeeze the row below
			// it out of the window.
			root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			root.Controls.Add(BuildBotGroup(), 0, 0);
			root.Controls.Add(BuildBattleGroup(), 0, 1);
			root.Controls.Add(BuildActionRow(), 0, 2);
			root.Controls.Add(BuildTrainingGroup(), 0, 3);
			root.Controls.Add(BuildBattleBanner(), 0, 4);
			root.Controls.Add(BuildRepositoryRow(), 0, 6);

			var status = new StatusStrip();
			statusLabel = new ToolStripStatusLabel("Starting up…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
			status.Items.Add(statusLabel);

			// Polled rather than watched: FileSystemWatcher does not raise for every appended line
			// on every filesystem, and half a second is well under the eye's patience anyway.
			matchTimer = new Timer { Interval = 500 };
			matchTimer.Tick += (_, _) => PollBattle(finished: false);

			Controls.Add(root);
			Controls.Add(status);
			this.status = status;
		}

		/// <summary>
		/// Sizes the window to what it is actually holding, and puts it out of the way of the
		/// windows a battle will open to the right of it.
		/// </summary>
		/// <remarks>
		/// Measured rather than declared. The tallest things in here are paragraphs of hint text
		/// and a list of game speeds, both of which are as tall as the system font makes them — so
		/// a size that fits at 100% clips at 150%, and what it clips is the bottom row.
		/// </remarks>
		void FitToContent()
		{
			var chrome = Size - ClientSize;
			var content = root.PreferredSize;

			// Room for the battle banner, which only appears once something has been launched. It
			// is not part of the measurement because a hidden control has no preferred size, and
			// it must not push the row below it out of the window when it arrives.
			var banner = 3 * Font.Height + 24;

			MinimumSize = new Size(
				content.Width + chrome.Width,
				content.Height + status.Height + chrome.Height + banner);

			Size = MinimumSize;

			// Left of centre, because a battle claims the space to the right of this window.
			var area = Screen.FromControl(this).WorkingArea;
			Location = new Point(area.Left + 24, area.Top + Math.Max(0, (area.Height - Height) / 2));
		}

		Control BuildBotGroup()
		{
			var grid = Grid(3);

			botBox = new TextBox { Dock = DockStyle.Fill };
			botBox.TextChanged += (_, _) =>
			{
				UpdateEnabledState();
				if (resultsWindow != null)
					RefreshResultsHistory(reload: true);
			};

			grid.Controls.Add(Caption("Battle bot:"), 0, 0);
			grid.Controls.Add(botBox, 1, 0);
			grid.Controls.Add(SmallButton("Browse…", BrowseBot), 2, 0);

			var hint = new Label
			{
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Text = "A bot project (.csproj) is built before it plays. A prebuilt .dll is played as it is."
			};

			var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			runTestsBox = new CheckBox { Text = "Run its tests first", AutoSize = true };
			newBotButton = SmallButton("New bot…", NewBot);
			newBotButton.Margin = new Padding(16, 0, 0, 0);

			var useReference = new LinkLabel
			{
				Text = "Use the reference bot",
				AutoSize = true,
				Margin = new Padding(16, 4, 0, 0)
			};

			useReference.LinkClicked += (_, _) =>
			{
				if (repo != null)
					botBox.Text = repo.ReferenceBot;
			};

			options.Controls.Add(runTestsBox);
			options.Controls.Add(newBotButton);
			options.Controls.Add(useReference);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 1);
			grid.Controls.Add(options, 1, 1);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
			grid.Controls.Add(hint, 1, 2);
			grid.SetColumnSpan(hint, 2);

			botGroup = Group("Your battle code", grid);
			return botGroup;
		}

		Control BuildBattleGroup()
		{
			var grid = Grid(3);

			mapBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
			mapBox.SelectedIndexChanged += (_, _) => MapChanged();

			difficultyBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
			difficultyBox.SelectedIndexChanged += (_, _) => DifficultyChanged();

			difficultySummary = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0) };

			opponentsBox = new NumericUpDown { Minimum = 0, Maximum = 7, Value = 1, Width = 60 };

			factionBox = FactionCombo();
			botFactionBox = FactionCombo();

			grid.Controls.Add(Caption("Map:"), 0, 0);
			grid.Controls.Add(mapBox, 1, 0);
			grid.Controls.Add(SmallButton("Refresh", (_, _) => LoadMaps()), 2, 0);

			grid.Controls.Add(Caption("Opponent:"), 0, 1);
			grid.Controls.Add(difficultyBox, 1, 1);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
			grid.Controls.Add(difficultySummary, 1, 2);
			grid.SetColumnSpan(difficultySummary, 2);

			var counts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			counts.Controls.Add(opponentsBox);
			counts.Controls.Add(new Label
			{
				Text = "0 launches the map with nobody to fight.",
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(8, 6, 0, 0)
			});

			grid.Controls.Add(Caption("How many:"), 0, 3);
			grid.Controls.Add(counts, 1, 3);

			var sides = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			sides.Controls.Add(factionBox);
			sides.Controls.Add(new Label { Text = "against", AutoSize = true, Margin = new Padding(8, 6, 8, 0) });
			sides.Controls.Add(botFactionBox);

			grid.Controls.Add(Caption("You play:"), 0, 4);
			grid.Controls.Add(sides, 1, 4);

			speedBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

			grid.Controls.Add(Caption("Speed:"), 0, 5);
			grid.Controls.Add(speedBox, 1, 5);

			executionBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
			executionBox.Items.AddRange(ExecutionModes);
			executionBox.SelectedIndex = 0;
			executionBox.SelectedIndexChanged += (_, _) => ExecutionModeChanged();

			grid.Controls.Add(Caption("Execution:"), 0, 6);
			grid.Controls.Add(executionBox, 1, 6);

			battleGroup = Group("The battle", grid);
			return battleGroup;
		}

		Control BuildActionRow()
		{
			var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };

			launchButton = new Button { Text = "Fight", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
			launchButton.Click += (_, _) => Launch(play: true);

			buildButton = new Button { Text = "Deploy bot", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
			buildButton.Click += (_, _) => Launch(play: false);

			platformButton = new Button { Text = "Rebuild platform", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
			platformButton.Click += (_, _) => RebuildPlatform();

			stopButton = new Button { Text = "Stop", AutoSize = true, Enabled = false, Padding = new Padding(8, 4, 8, 4) };
			stopButton.Click += (_, _) => StopEverything();

			replayButton = new Button { Text = "Watch replay", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
			replayButton.Click += (_, _) => WatchReplay();

			var logs = new LinkLabel { Text = "Game logs", AutoSize = true, Margin = new Padding(16, 10, 0, 0) };
			logs.LinkClicked += (_, _) => OpenLogsFolder();

			var replays = new LinkLabel { Text = "Replays", AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
			replays.LinkClicked += (_, _) => OpenFolder(ReplaysDir, "No replays yet");

			row.Controls.Add(launchButton);
			row.Controls.Add(buildButton);
			row.Controls.Add(platformButton);
			row.Controls.Add(stopButton);
			row.Controls.Add(replayButton);
			row.Controls.Add(logs);
			row.Controls.Add(replays);

			AcceptButton = launchButton;
			return row;
		}

		Control BuildTrainingGroup()
		{
			var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };

			openCodeButton = new Button { Text = "Open code", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			openCodeButton.Click += (_, _) => OpenCode();

			historyButton = new Button { Text = "History && trends", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			historyButton.Click += (_, _) => ShowResultsWindow(reloadHistory: true);

			improveButton = new Button { Text = "Analyze && improve", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			improveButton.Click += (_, _) => { ImproveBot(); };

			retryVerificationButton = new Button { Text = "Retry verification", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Visible = false };
			retryVerificationButton.Click += (_, _) => RetryVerification();

			reviewButton = new Button { Text = "Agent workspace", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			reviewButton.Click += (_, _) => ReviewImprovement();

			restoreButton = new Button { Text = "Restore previous iteration", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			restoreButton.Click += (_, _) => RestoreIteration();

			runFolderButton = new Button { Text = "Open run", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
			runFolderButton.Click += (_, _) => OpenTrainingRun();

			var configure = new LinkLabel { Text = "Agent settings", AutoSize = true, Margin = new Padding(14, 8, 0, 0) };
			configure.LinkClicked += (_, _) => ConfigureAgent();

			continuousBox = new CheckBox
			{
				Text = "Continuous improvement",
				AutoSize = true,
				Margin = new Padding(14, 7, 0, 0)
			};
			continuousBox.CheckedChanged += (_, _) =>
			{
				settings.ContinuousImprovement = continuousBox.Checked;
				settings.Save();
				UpdateEnabledState();
				if (resultsWindow != null)
					RefreshResultsHistory();
			};

			actions.Controls.Add(openCodeButton);
			actions.Controls.Add(historyButton);
			actions.Controls.Add(improveButton);
			actions.Controls.Add(retryVerificationButton);
			actions.Controls.Add(reviewButton);
			actions.Controls.Add(restoreButton);
			actions.Controls.Add(runFolderButton);
			actions.Controls.Add(continuousBox);
			actions.Controls.Add(configure);

			var hint = new Label
			{
				AutoSize = true,
				MaximumSize = new Size(680, 0),
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(3, 7, 3, 0),
				Text = "Continuous improvement repeats Fight -> analyze and improve -> Fight until Stop. " +
					"History & trends compares every iteration; player assessments can be added to finished battles in manual mode."
			};

			var stack = new FlowLayoutPanel
			{
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				FlowDirection = FlowDirection.TopDown,
				Dock = DockStyle.Fill,
				Margin = new Padding(0),
				WrapContents = false
			};
			stack.Controls.Add(actions);
			stack.Controls.Add(hint);

			trainingGroup = Group("Train the bot", stack);
			return trainingGroup;
		}

		/// <summary>
		/// What this window has to say while a battle is somewhere else.
		/// </summary>
		/// <remarks>
		/// It is not decoration. Two windows appearing on a second monitor is easy to miss, and a
		/// launcher that looks idle while a game is running is a launcher somebody launches twice.
		/// </remarks>
		Control BuildBattleBanner()
		{
			battleBannerText = new Label
			{
				AutoSize = true,
				MaximumSize = new Size(560, 0),
				Margin = new Padding(3, 4, 3, 6)
			};

			var results = new LinkLabel { Text = "Results", AutoSize = true, Margin = new Padding(3, 0, 14, 3) };
			results.LinkClicked += (_, _) => ShowResultsWindow();

			var output = new LinkLabel { Text = "Output", AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
			output.LinkClicked += (_, _) => ShowOutputWindow();

			var improvement = new LinkLabel { Text = "Improvement", AutoSize = true, Margin = new Padding(14, 0, 3, 3) };
			improvement.LinkClicked += (_, _) => ShowImprovementWindow();

			var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
			links.Controls.Add(results);
			links.Controls.Add(output);
			links.Controls.Add(improvement);

			var stack = new FlowLayoutPanel
			{
				FlowDirection = FlowDirection.TopDown,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Dock = DockStyle.Fill,
				Margin = new Padding(0)
			};

			stack.Controls.Add(battleBannerText);
			stack.Controls.Add(links);

			// A coloured edge rather than a tinted panel: on the default Windows theme a panel a
			// few shades off the form background is indistinguishable from it, which for the one
			// control that says "a game is running" is the wrong way to be subtle.
			var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Color.FromArgb(0, 120, 212) };

			battleBanner = new Panel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Padding = new Padding(12, 8, 10, 8),
				Margin = new Padding(0, 4, 0, 4),
				Visible = false,
				BackColor = Color.FromArgb(232, 240, 250)
			};

			battleBanner.Controls.Add(stack);
			battleBanner.Controls.Add(accent);
			return battleBanner;
		}

		Control BuildRepositoryRow()
		{
			var row = Grid(3);
			repositoryBox = new TextBox { Dock = DockStyle.Fill };
			row.Controls.Add(Caption("Repository:"), 0, 0);
			row.Controls.Add(repositoryBox, 1, 0);
			row.Controls.Add(SmallButton("Browse…", BrowseRepository), 2, 0);
			return row;
		}

		// -------------------------------------------------------------------
		// The battle windows
		// -------------------------------------------------------------------

		/// <summary>
		/// Brings up the graphs, creating them if this is the first battle or you closed them.
		/// </summary>
		/// <remarks>
		/// Recreated rather than hidden, because a window you closed is one you decided you were
		/// finished with — reopening it full of the last match would be answering a question
		/// nobody asked.
		/// </remarks>
		ResultsWindow ShowResultsWindow(bool reloadHistory = false)
		{
			if (resultsWindow == null || resultsWindow.IsDisposed)
			{
				resultsWindow = new ResultsWindow(matchLog);
				resultsWindow.FormClosed += (_, _) => resultsWindow = null;
				resultsWindow.PlayerFeedbackSaved += SavePlayerFeedback;
			}

			RefreshResultsHistory(reloadHistory);
			resultsWindow.Show(this, 0f, 0.55f);
			resultsWindow.Redraw(battleFinished);
			return resultsWindow;
		}

		void RefreshResultsHistory(bool reload = false)
		{
			if (resultsWindow == null || resultsWindow.IsDisposed)
				return;

			try
			{
				var selectedBot = BotExists()
					? BotWorkspace.ResolveProject(botBox.Text.Trim()) ?? Path.GetFullPath(botBox.Text.Trim())
					: null;
				if (selectedBot == null)
				{
					loadedHistory = TrainingHistory.Empty;
					loadedHistoryBot = null;
				}
				else if (reload || !SamePath(selectedBot, loadedHistoryBot))
				{
					loadedHistory = TrainingHistory.Load(botBox.Text.Trim());
					loadedHistoryBot = selectedBot;
				}

				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = loadedHistory.WithRun(current);
				resultsWindow.SetHistory(loadedHistory, current,
					continuousBox.Checked || continuousLoop.IsRunning);
			}
			catch (IOException ex)
			{
				Append($"Could not read training history: {ex.Message}");
				loadedHistoryBot = null;
				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = TrainingHistory.Empty.WithRun(current);
				resultsWindow.SetHistory(loadedHistory, current,
					continuousBox.Checked || continuousLoop.IsRunning);
			}
			catch (UnauthorizedAccessException ex)
			{
				Append($"Could not read training history: {ex.Message}");
				loadedHistoryBot = null;
				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = TrainingHistory.Empty.WithRun(current);
				resultsWindow.SetHistory(loadedHistory, current,
					continuousBox.Checked || continuousLoop.IsRunning);
			}
		}

		OutputWindow ShowOutputWindow()
		{
			if (outputWindow == null || outputWindow.IsDisposed)
			{
				outputWindow = new OutputWindow(battleLog);
				outputWindow.FormClosed += (_, _) => outputWindow = null;

				foreach (var line in pendingOutput)
					outputWindow.AppendBuildOutput(line);

				pendingOutput.Clear();
			}

			outputWindow.Show(this, 0.55f, 0.45f);
			outputWindow.ShowBattle(battleFinished);
			return outputWindow;
		}

		ImprovementWindow ShowImprovementWindow()
		{
			var created = false;
			if (improvementWindow == null || improvementWindow.IsDisposed)
			{
				created = true;
				improvementWindow = new ImprovementWindow();
				improvementWindow.FormClosed += (_, _) => improvementWindow = null;
				improvementWindow.NextPromptAccepted += AcceptNextPrompt;
			}

			improvementWindow.Show(this, 0.05f, 0.9f);
			if (created && lastRun != null &&
				(lastRun.Manifest.Agent != null || File.Exists(lastRun.PromptPath)))
				improvementWindow.ShowAgentRun(lastRun);
			return improvementWindow;
		}

		static TableLayoutPanel Grid(int columns)
		{
			var grid = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				ColumnCount = columns
			};

			grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

			for (var i = 2; i < columns; i++)
				grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

			return grid;
		}

		static GroupBox Group(string title, Control content) =>
			new() { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Controls = { content } };

		static Label Caption(string text) =>
			new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

		static Button SmallButton(string text, EventHandler onClick)
		{
			var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 1, 6, 1) };
			button.Click += onClick;
			return button;
		}

		static ComboBox FactionCombo()
		{
			var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
			combo.Items.AddRange(Factions);
			combo.SelectedIndex = 0;
			return combo;
		}

		// -------------------------------------------------------------------
		// Start-up
		// -------------------------------------------------------------------
		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);

			repo = RepoLayout.Discover(settings.RepositoryRoot);
			repositoryBox.Text = repo?.Root ?? string.Empty;
			repositoryBox.TextChanged += (_, _) => RepositoryChanged();

			var rememberedBot = settings.BattleBotPath;
			if (repo != null &&
				!SamePath(repo.Root, settings.RepositoryRoot) &&
				IsUnder(rememberedBot, settings.RepositoryRoot))
				rememberedBot = null;

			botBox.Text = rememberedBot ?? repo?.ReferenceBot ?? string.Empty;
			LoadLastTrainingRun();
			runTestsBox.Checked = settings.RunTests;
			continuousBox.Checked = settings.ContinuousImprovement;
			opponentsBox.Value = Math.Clamp(settings.Opponents, opponentsBox.Minimum, opponentsBox.Maximum);
			Select(factionBox, settings.Faction);
			Select(botFactionBox, settings.BotFaction);
			SelectExecutionMode(settings.ExecutionMode);

			LoadDifficulties();
			LoadSpeeds();
			LoadMaps();
			UpdateEnabledState();
			FitToContent();
		}

		void LoadLastTrainingRun()
		{
			try
			{
				lastRun = TrainingRun.Load(settings.LastTrainingRunDirectory);
			}
			catch (IOException ex)
			{
				Append($"Could not read the last training run: {ex.Message}");
			}
			catch (System.Text.Json.JsonException ex)
			{
				Append($"Could not read the last training run: {ex.Message}");
			}
		}

		void LoadSpeeds()
		{
			speedBox.Items.Clear();

			if (repo == null)
			{
				ExecutionModeChanged();
				return;
			}

			var speeds = GameSpeedCatalog.Read(repo);
			foreach (var speed in speeds)
				speedBox.Items.Add(speed);

			if (speeds.Count == 0)
			{
				ExecutionModeChanged();
				return;
			}

			var index = speeds.ToList().FindIndex(s => string.Equals(s.Id, settings.GameSpeed, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
				index = speeds.ToList().FindIndex(s => s.IsDefault);

			speedBox.SelectedIndex = Math.Max(0, index);
			ExecutionModeChanged();
		}

		void RepositoryChanged()
		{
			var candidate = RepoLayout.For(repositoryBox.Text.Trim());
			if (candidate == null)
			{
				UpdateEnabledState();
				return;
			}

			repo = candidate;
			LoadDifficulties();
			LoadSpeeds();
			LoadMaps();
			UpdateEnabledState();
		}

		void LoadDifficulties()
		{
			difficultyBox.Items.Clear();

			if (repo == null || !File.Exists(repo.DifficultiesFile))
				return;

			try
			{
				var table = DifficultyTable.Load(repo.DifficultiesFile);
				foreach (var level in table.Levels)
					difficultyBox.Items.Add(level);

				var wanted = settings.Difficulty ?? table.Default;
				var index = table.Levels.FindIndex(l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase));
				difficultyBox.SelectedIndex = index >= 0 ? index : 0;
			}
			catch (Exception ex)
			{
				Append($"Could not read {repo.DifficultiesFile}: {ex.Message}");
			}
		}

		void LoadMaps()
		{
			mapBox.Items.Clear();

			if (repo == null)
				return;

			var maps = MapCatalog.Scan(repo);
			foreach (var map in maps)
				mapBox.Items.Add(map);

			if (maps.Count == 0)
			{
				Status("No skirmish maps found. Has the engine submodule been fetched?");
				return;
			}

			mapBox.SelectedIndex = Math.Max(0, DefaultMapIndex(maps));
		}

		/// <summary>
		/// The map to start on: whatever you played last, or failing that a straightforward
		/// head-to-head rather than whichever map happens to sort first.
		/// </summary>
		int DefaultMapIndex(IReadOnlyList<MapInfo> maps)
		{
			var remembered = maps.ToList().FindIndex(m => string.Equals(m.Id, settings.Map, StringComparison.OrdinalIgnoreCase));
			if (remembered >= 0)
				return remembered;

			var duel = maps.ToList().FindIndex(m => m.PlayerCount == 2);
			return duel >= 0 ? duel : 0;
		}

		static void Select(ComboBox combo, string value)
		{
			var match = combo.Items.Cast<FactionChoice>()
				.FirstOrDefault(f => string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase));

			if (match != null)
				combo.SelectedItem = match;
		}

		void SelectExecutionMode(string value)
		{
			var normalized = BattleExecutionModes.Normalize(value);
			var match = executionBox.Items.Cast<ExecutionChoice>()
				.First(choice => choice.Value == normalized);
			executionBox.SelectedItem = match;
		}

		void ExecutionModeChanged()
		{
			var headless = IsHeadlessSelected();
			speedBox.Enabled = !headless;

			if (headless)
			{
				var maximum = speedBox.Items.Cast<GameSpeedInfo>()
					.FirstOrDefault(speed => speed.Id == BattleExecutionModes.MaximumGameSpeed);
				if (maximum != null)
					speedBox.SelectedItem = maximum;
			}

			if (launchButton != null)
				launchButton.Text = headless ? "Run fight" : "Fight";
		}

		bool IsHeadlessSelected() =>
			(executionBox.SelectedItem as ExecutionChoice)?.Value == BattleExecutionModes.Headless;

		// -------------------------------------------------------------------
		// Reacting to choices
		// -------------------------------------------------------------------
		void MapChanged()
		{
			if (mapBox.SelectedItem is not MapInfo map)
				return;

			// One slot is yours, so a two-player map has room for exactly one opponent.
			opponentsBox.Maximum = Math.Max(0, map.PlayerCount - 1);
			UpdateEnabledState();
		}

		void DifficultyChanged()
		{
			difficultySummary.Text = difficultyBox.SelectedItem is DifficultyLevel level ? level.Summary : string.Empty;
		}

		void UpdateEnabledState()
		{
			// Text changes can arrive while the window is still being assembled.
			if (launchButton == null)
				return;

			var busy = runner.IsRunning;
			if (IsHandleCreated)
				taskbarProgress.SetBusy(Handle, busy);
			var compatible = repo?.SupportsTraining == true;
			var ready = !busy && compatible && BotExists() && mapBox.SelectedItem is MapInfo;
			var editable = BotExists() && BotWorkspace.ResolveProject(botBox.Text.Trim()) != null;
			var continuousReady = compatible && editable && continuousBox.Checked;
			var agent = lastRun?.Manifest.Agent;
			var lastRunMatches = LastRunMatchesSelectedBot();
			var failedImprovement = agent?.ExitCode is int agentExitCode && agentExitCode != 0;
			var canRetryImprovement = agent == null || agent.ChangeCount == 0 ||
				agent.RestoredUtc != null || failedImprovement;

			launchButton.Enabled = ready;
			buildButton.Enabled = !busy && compatible && BotExists();
			platformButton.Enabled = !busy && repo != null;
			newBotButton.Enabled = !busy && compatible && File.Exists(repo.NewBotScript);
			openCodeButton.Enabled = !busy && editable;
			historyButton.Enabled = BotExists();
			continuousBox.Enabled = !busy && compatible && editable;
			launchButton.Text = continuousReady ? "Start continuous training" : "Fight";
			improveButton.Enabled = !busy && compatible && File.Exists(repo.TrainBotScript) &&
				lastRunMatches && lastRun?.IsEditable == true &&
				lastRun.Manifest.CompletedUtc != null && canRetryImprovement &&
				File.Exists(lastRun.BattleLogPath) &&
				File.Exists(lastRun.TelemetryPath) &&
				File.Exists(lastRun.DecisionTracePath);
			improveButton.Text = failedImprovement && agent.RestoredUtc == null
				? "Fix failed improvement"
				: "Analyze && improve";
			retryVerificationButton.Visible = failedImprovement &&
				string.Equals(agent.FailurePhase, "verification", StringComparison.OrdinalIgnoreCase);
			retryVerificationButton.Enabled = !busy && compatible &&
				File.Exists(repo.VerifyBotScript) && lastRunMatches;
			reviewButton.Enabled = compatible && lastRunMatches &&
				lastRun?.IsEditable == true && lastRun.Manifest.CompletedUtc != null;
			restoreButton.Enabled = !busy && lastRunMatches && lastRun != null &&
				agent?.ChangeCount > 0 && agent.RestoredUtc == null &&
				File.Exists(lastRun.SnapshotManifestPath);
			runFolderButton.Enabled = !busy && lastRun != null && Directory.Exists(lastRun.RunDirectory);
			stopButton.Enabled = busy;

			// A replay is only complete once the game that recorded it has exited.
			replayButton.Enabled = !busy && repo != null && NewestReplay() != null;

			// Tests come from a project's Tests folder, so a prebuilt assembly has none to run.
			runTestsBox.Enabled = !busy && !IsPrebuilt();

			// Continuous training must keep the bot and battle configuration stable between its
			// fight and improvement stages. Stop is outside these groups and remains available.
			var cycleLocked = battleRunning || continuousLoop.IsRunning;
			botGroup.Enabled = !cycleLocked;
			battleGroup.Enabled = !cycleLocked;
			trainingGroup.Enabled = !cycleLocked;

			UpdateBattleBanner();

			if (busy)
				return;

			if (repo == null)
				Status("Point me at your AutoC&C checkout to get started.");
			else if (!repo.SupportsTraining)
				Status($"This checkout uses authoring API {repo.AuthoringApiVersion}; the launcher needs {RepoLayout.RequiredAuthoringApiVersion}. Select the checkout that built this launcher.");
			else if (!repo.EngineFetched)
				Status("The engine submodule is missing. Run ./scripts/setup.ps1 in the repository first.");
			else if (!BotExists())
				Status("Choose the battle bot project or assembly you want to play.");
			else if (lastRunMatches && lastRun?.Manifest.Status == "finished" && lastRun.IsEditable)
				Status("Fight saved. Edit manually, or analyze and improve it with the configured agent.");
			else if (lastRun?.Manifest.Status == "improved")
				Status("Improvement verified and deployed. Fight again to measure it.");
			else if (lastRunMatches && lastRun?.Manifest.Status == "improvement-failed")
				Status("Improvement failed. Fix its current changes with the agent, or restore the previous iteration.");
			else if (lastRun?.Manifest.Status == "restored")
				Status("The previous source iteration has been restored.");
			else
				Status(repo.EngineBuilt ? "Ready." : "Ready — the engine is not built yet, so the first launch will take a few minutes.");
		}

		void UpdateBattleBanner()
		{
			battleBanner.Visible = battleRunning || battleFinished;
			if (!battleBanner.Visible)
				return;

			Text = battleRunning ? "AutoC&C — Battle Launcher (battle running)" : "AutoC&C — Battle Launcher";

			battleBannerText.Text = battleRunning
				? continuousLoop.IsRunning
					? "Continuous training is fighting this iteration. Results and output are following it; Stop ends the loop."
					: activeRun?.Manifest.Battle?.ExecutionMode == BattleExecutionModes.Headless
						? "Headless battle in progress at CPU speed. Results and output are following it; Stop ends it cleanly."
						: "Battle in progress. The results and output windows are following it; this one waits until it is over."
				: continuousLoop.Stage == ContinuousTrainingStage.Improving
					? "Continuous training is improving the bot from the finished battle. The next fight starts automatically."
					: "The last battle is finished. Its results and output are still open - close them when you are done reading.";
		}

		bool BotExists()
		{
			var path = botBox.Text.Trim();
			return path.Length > 0 && (File.Exists(path) || Directory.Exists(path));
		}

		bool IsPrebuilt() =>
			botBox.Text.Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

		static bool SamePath(string first, string second)
		{
			if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
				return false;

			try
			{
				return string.Equals(
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
					StringComparison.OrdinalIgnoreCase);
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (NotSupportedException)
			{
				return false;
			}
		}

		static bool IsUnder(string path, string root)
		{
			if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
				return false;

			try
			{
				var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
				var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
				return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
					fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (NotSupportedException)
			{
				return false;
			}
		}

		bool LastRunMatchesSelectedBot()
		{
			if (lastRun == null || !BotExists())
				return false;

			var selectedProject = BotWorkspace.ResolveProject(botBox.Text.Trim());
			if (selectedProject != null && lastRun.Manifest.BotProject != null)
				return string.Equals(Path.GetFullPath(selectedProject),
					Path.GetFullPath(lastRun.Manifest.BotProject), StringComparison.OrdinalIgnoreCase);

			return string.Equals(Path.GetFullPath(botBox.Text.Trim()),
				Path.GetFullPath(lastRun.Manifest.BotPath), StringComparison.OrdinalIgnoreCase);
		}

		// -------------------------------------------------------------------
		// Browsing
		// -------------------------------------------------------------------
		void BrowseBot(object sender, EventArgs e)
		{
			using var dialog = new OpenFileDialog
			{
				Title = "Select your battle bot",
				Filter = "Battle bot project or assembly (*.csproj;*.dll)|*.csproj;*.dll|Project (*.csproj)|*.csproj|Built bot (*.dll)|*.dll|All files (*.*)|*.*",
				CheckFileExists = true
			};

			var current = botBox.Text.Trim();
			if (current.Length > 0)
			{
				var directory = File.Exists(current) ? Path.GetDirectoryName(current) : current;
				if (Directory.Exists(directory))
					dialog.InitialDirectory = directory;
			}

			if (dialog.ShowDialog(this) == DialogResult.OK)
				botBox.Text = dialog.FileName;
		}

		void NewBot(object sender, EventArgs e)
		{
			if (repo == null || !File.Exists(repo.NewBotScript))
				return;

			using var dialog = new NewBotDialog(Path.Combine(repo.Root, "bots"));
			if (dialog.ShowDialog(this) != DialogResult.OK)
				return;

			Save();
			ClearOutput();
			queue.Clear();

			var arguments = new List<string>
			{
				"-Name", dialog.BotName,
				"-OutputDirectory", dialog.OutputDirectory
			};
			if (!repo.PackagesBuilt)
				arguments.Add("-NoBuild");

			queue.Enqueue(new ScriptJob
			{
				Title = $"Creating battle bot {dialog.BotName}",
				ScriptPath = repo.NewBotScript,
				Arguments = arguments,
				Completed = FinishBotCreation
			});

			RunNext();
		}

		void FinishBotCreation(int exitCode)
		{
			if (exitCode != 0)
				return;

			const string Prefix = "AUTOCNC_BOT_PROJECT=";
			var line = runner.LastOutput.LastOrDefault(output =>
				output.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
			var project = line?[Prefix.Length..].Trim();

			if (string.IsNullOrEmpty(project) || !File.Exists(project))
			{
				Append("The bot was created, but the script did not report its project path.");
				return;
			}

			botBox.Text = project;
			Save();
			Status("Bot created and selected. Open its code or fight the starter bot.");
		}

		void OpenCode()
		{
			var project = BotWorkspace.ResolveProject(botBox.Text.Trim());
			if (project == null)
				return;

			var directory = Path.GetDirectoryName(project);
			var solution = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
			try
			{
				Process.Start(new ProcessStartInfo(solution ?? project) { UseShellExecute = true });
			}
			catch (System.ComponentModel.Win32Exception ex)
			{
				MessageBox.Show(this, $"Windows could not open the bot solution: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		void OpenTrainingRun()
		{
			if (lastRun != null)
				OpenFolder(lastRun.RunDirectory, "No training run yet");
		}

		void SavePlayerFeedback(TrainingRun run, string feedback)
		{
			if (run == null || continuousBox.Checked || continuousLoop.IsRunning)
				return;

			try
			{
				run.SetPlayerFeedback(feedback);
				if (lastRun != null && SamePath(run.RunDirectory, lastRun.RunDirectory))
					lastRun = run;

				if (run.IsEditable &&
					File.Exists(run.BattleLogPath) &&
					File.Exists(run.TelemetryPath) &&
					File.Exists(run.DecisionTracePath))
					EnsureAgentContext(run);

				resultsWindow?.MarkFeedbackSaved(run);
			}
			catch (ArgumentException ex)
			{
				MessageBox.Show(resultsWindow, ex.Message, "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			catch (InvalidOperationException ex)
			{
				MessageBox.Show(resultsWindow, ex.Message, "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			catch (IOException ex)
			{
				MessageBox.Show(resultsWindow, $"Could not save the battle assessment: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (UnauthorizedAccessException ex)
			{
				MessageBox.Show(resultsWindow, $"Could not save the battle assessment: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		void ConfigureAgent()
		{
			using var dialog = new AgentSettingsDialog(settings.AgentCommand, settings.AgentArguments);
			if (dialog.ShowDialog(this) != DialogResult.OK)
				return;

			settings.AgentCommand = dialog.AgentCommand;
			settings.AgentArguments = dialog.AgentArguments;
			settings.Save();
		}

		void BrowseRepository(object sender, EventArgs e)
		{
			using var dialog = new FolderBrowserDialog { Description = "Select your AutoC&C checkout" };

			if (repo != null)
				dialog.SelectedPath = repo.Root;

			if (dialog.ShowDialog(this) != DialogResult.OK)
				return;

			if (!RepoLayout.LooksLikeRoot(dialog.SelectedPath))
			{
				MessageBox.Show(this,
					"That folder does not look like an AutoC&C checkout — it has no AutoCnC.sln and no scripts/run-bot.ps1.",
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			repositoryBox.Text = dialog.SelectedPath;
		}

		void OpenLogsFolder()
		{
			var logs = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA", "Logs");

			OpenFolder(logs, "No logs yet");
		}

		/// <summary>Where the engine records every match it plays, without being asked to.</summary>
		static string ReplaysDir => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA", "Replays", "autocnc");

		/// <summary>
		/// The match you most likely want back: the one you just watched.
		/// </summary>
		/// <remarks>
		/// Searched rather than composed from a path, because the engine files replays under the
		/// mod version and this window has no business knowing what that is today.
		/// </remarks>
		static FileInfo NewestReplay()
		{
			try
			{
				var dir = new DirectoryInfo(ReplaysDir);
				return dir.Exists
					? dir.EnumerateFiles("*.orarep", SearchOption.AllDirectories).MaxBy(f => f.LastWriteTimeUtc)
					: null;
			}
			catch (IOException)
			{
				return null;
			}
		}

		/// <summary>
		/// Plays the last match back.
		/// </summary>
		/// <remarks>
		/// Worth having next to a 40x launch button: a replay is not bound to one rendered frame
		/// per tick the way live play is, so it scrubs and fast-forwards on its own terms. Run the
		/// battle as fast as the machine will go, then watch the part that mattered at a speed you
		/// can actually see.
		/// </remarks>
		void WatchReplay()
		{
			var replay = NewestReplay();
			if (repo == null || replay == null)
				return;

			Save();
			ClearOutput();
			queue.Clear();

			queue.Enqueue(new ScriptJob
			{
				Title = $"Watching {replay.Name}",
				ScriptPath = repo.LaunchScript,
				Arguments = ["-Replay", replay.FullName]
			});

			RunNext();
		}

		static void OpenFolder(string path, string missing)
		{
			if (!Directory.Exists(path))
			{
				MessageBox.Show(Form.ActiveForm, $"{missing} — {path} does not exist.", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}

		// -------------------------------------------------------------------
		// Running things
		// -------------------------------------------------------------------
		bool Launch(bool play, bool continuousContinuation = false)
		{
			if (repo == null || !BotExists())
			{
				if (continuousContinuation)
					ReportAutomationFailure("The selected battle bot is no longer available.",
						MessageBoxIcon.Error, automatic: true);
				return false;
			}

			if (!repo.SupportsTraining)
			{
				ReportAutomationFailure(
					$"The selected checkout uses authoring API {repo.AuthoringApiVersion}, but this launcher needs {RepoLayout.RequiredAuthoringApiVersion}. " +
					"Select the repository that this launcher was built from.",
					MessageBoxIcon.Warning, continuousContinuation);
				return false;
			}

			if (!repo.EngineFetched)
			{
				ReportAutomationFailure(
					"The OpenRA engine submodule has not been fetched. Run ./scripts/setup.ps1 in the repository, then try again.",
					MessageBoxIcon.Warning, continuousContinuation);
				return false;
			}

			if (play && !continuousContinuation)
				continuousLoop.Begin(continuousBox.Checked &&
					BotWorkspace.ResolveProject(botBox.Text.Trim()) != null);

			Save();
			ClearOutput();
			queue.Clear();

			if (play)
			{
				try
				{
					StartWatchingBattle();
				}
				catch (IOException ex)
				{
					continuousLoop.Stop();
					ReportAutomationFailure($"Could not create the training run: {ex.Message}",
						MessageBoxIcon.Error, continuousContinuation);
					return false;
				}
				catch (UnauthorizedAccessException ex)
				{
					continuousLoop.Stop();
					ReportAutomationFailure($"Could not create the training run: {ex.Message}",
						MessageBoxIcon.Error, continuousContinuation);
					return false;
				}
			}

			// The engine is a several-minute build, so only do it when it genuinely is not there.
			if (!repo.EngineBuilt)
				queue.Enqueue(new ScriptJob
				{
					Title = "Building the engine — this one takes a few minutes",
					ScriptPath = repo.BuildScript,
					Arguments = ["-SkipBots"]
				});

			queue.Enqueue(new ScriptJob
			{
				Title = play
					? IsHeadlessSelected()
						? "Building your bot and running the headless battle"
						: "Building your bot and starting the battle"
					: "Building your bot",
				ScriptPath = repo.RunBotScript,
				Arguments = RunBotArguments(play),
				CancellationFile = play && IsHeadlessSelected() ? activeRun.CancellationPath : null
			});

			RunNext();
			return true;
		}

		IReadOnlyList<string> RunBotArguments(bool play)
		{
			var map = (MapInfo)mapBox.SelectedItem;
			var difficulty = difficultyBox.SelectedItem as DifficultyLevel;

			var arguments = new List<string>
			{
				"-BattleBot", botBox.Text.Trim(),
				"-ExecutionMode", IsHeadlessSelected()
					? BattleExecutionModes.Headless
					: BattleExecutionModes.Rendered,
				"-Opponents", ((int)opponentsBox.Value).ToString(),
				"-Faction", ((FactionChoice)factionBox.SelectedItem).Value,
				"-BotFaction", ((FactionChoice)botFactionBox.SelectedItem).Value
			};

			if (map != null)
				arguments.AddRange(["-Map", map.Id]);

			if (difficulty != null)
				arguments.AddRange(["-Difficulty", difficulty.Name]);

			var gameSpeed = BattleExecutionModes.EffectiveGameSpeed(
				IsHeadlessSelected() ? BattleExecutionModes.Headless : BattleExecutionModes.Rendered,
				(speedBox.SelectedItem as GameSpeedInfo)?.Id);
			if (gameSpeed != null)
				arguments.AddRange(["-GameSpeed", gameSpeed]);

			if (play)
			{
				if (activeRun == null)
					throw new InvalidOperationException("A fight must have a training run before it can launch.");

				arguments.AddRange(
				[
					"-Telemetry", activeRun.TelemetryPath,
					"-BattleLog", activeRun.BattleLogPath,
					"-DecisionTrace", activeRun.DecisionTracePath
				]);

				if (IsHeadlessSelected())
					arguments.AddRange(
					[
						"-CancellationFile", activeRun.CancellationPath,
						"-PerformanceReport", activeRun.PerformancePath
					]);
			}

			if (runTestsBox.Checked && !IsPrebuilt())
				arguments.Add("-Test");

			if (!play)
				arguments.Add("-NoLaunch");

			return arguments;
		}

		/// <summary>
		/// Clears the previous battle out of the way, opens the windows for the new one and starts
		/// polling.
		/// </summary>
		/// <remarks>
		/// The old files are deleted rather than left to be overwritten so that a battle which
		/// never gets as far as starting — a bot that fails to build, say — shows an empty
		/// graph and an empty log instead of the last one's, which would be a quietly misleading
		/// thing to look at.
		/// </remarks>
		void StartWatchingBattle()
		{
			var map = (MapInfo)mapBox.SelectedItem;
			var difficulty = difficultyBox.SelectedItem as DifficultyLevel;
			var speed = speedBox.SelectedItem as GameSpeedInfo;

			activeRun = TrainingRun.Create(botBox.Text.Trim(), new TrainingBattleConfiguration
			{
				Map = map?.Id,
				Difficulty = difficulty?.Name,
				Opponents = (int)opponentsBox.Value,
				Faction = ((FactionChoice)factionBox.SelectedItem).Value,
				BotFaction = ((FactionChoice)botFactionBox.SelectedItem).Value,
				GameSpeed = BattleExecutionModes.EffectiveGameSpeed(
					IsHeadlessSelected() ? BattleExecutionModes.Headless : BattleExecutionModes.Rendered,
					speed?.Id),
				ExecutionMode = IsHeadlessSelected()
					? BattleExecutionModes.Headless
					: BattleExecutionModes.Rendered
			});
			lastRun = activeRun;
			settings.LastTrainingRunDirectory = activeRun.RunDirectory;
			settings.Save();
			battleStartedUtc = DateTime.UtcNow;
			replayBeforeBattle = NewestReplay()?.FullName;

			battleRunning = true;
			battleFinished = false;

			matchLog.Watch(activeRun.TelemetryPath);
			battleLog.Watch(activeRun.BattleLogPath);

			ShowResultsWindow();
			ShowOutputWindow().ClearBattle();

			matchTimer.Start();
		}

		/// <summary>Stops polling, after one last read so the end of the battle is on both windows.</summary>
		void StopWatchingBattle(string status)
		{
			if (!battleRunning)
				return;

			battleRunning = false;
			battleFinished = true;
			matchTimer.Stop();
			PollBattle(finished: true);

			var finishedRun = activeRun;
			activeRun = null;
			if (finishedRun != null)
			{
				try
				{
					finishedRun.Finish(status, matchLog, battleLog);

					var replay = NewestReplay();
					var isNew = replay != null &&
						(!string.Equals(replay.FullName, replayBeforeBattle, StringComparison.OrdinalIgnoreCase) ||
							replay.LastWriteTimeUtc >= battleStartedUtc.AddSeconds(-2));
					finishedRun.CaptureReplay(isNew ? replay.FullName : null);

					if (finishedRun.IsEditable &&
						File.Exists(finishedRun.BattleLogPath) &&
						File.Exists(finishedRun.TelemetryPath) &&
						File.Exists(finishedRun.DecisionTracePath))
						EnsureAgentContext(finishedRun);
				}
				catch (IOException ex)
				{
					Append($"Could not finish the training run: {ex.Message}");
				}
				catch (UnauthorizedAccessException ex)
				{
					Append($"Could not finish the training run: {ex.Message}");
				}
				catch (InvalidOperationException ex)
				{
					Append($"Could not prepare the agent inputs: {ex.Message}");
				}

				lastRun = finishedRun;
				settings.LastTrainingRunDirectory = finishedRun.RunDirectory;
				settings.Save();
				if (resultsWindow != null)
					RefreshResultsHistory();
			}
		}

		/// <summary>Reads whatever the running game has written since last time.</summary>
		void PollBattle(bool finished)
		{
			if ((matchLog.Refresh() || finished) && resultsWindow != null)
				resultsWindow.Redraw(finished);

			if ((battleLog.Refresh() || finished) && outputWindow != null)
				outputWindow.ShowBattle(finished);
		}

		bool ImproveBot(bool automatic = false)
		{
			var previousAgent = lastRun?.Manifest.Agent;
			var canRetry = previousAgent == null || previousAgent.ChangeCount == 0 ||
				previousAgent.RestoredUtc != null ||
				previousAgent.ExitCode is int previousExitCode && previousExitCode != 0;
			if (repo == null || !LastRunMatchesSelectedBot() ||
				lastRun?.IsEditable != true || !canRetry)
			{
				if (automatic)
					ReportAutomationFailure(
						"The finished battle is not eligible for another agent improvement.",
						MessageBoxIcon.Error, automatic: true);
				return false;
			}

			try
			{
				if (!File.Exists(lastRun.SnapshotManifestPath))
					WorkspaceSnapshot.Capture(lastRun);

				if (!File.Exists(lastRun.GameRulesPath))
					AgentRulesExporter.Export(repo, lastRun.GameRulesPath);

				var recovering = previousAgent?.ExitCode is int failedExitCode &&
					failedExitCode != 0 && previousAgent.RestoredUtc == null;
				var archivedTranscript = recovering
					? lastRun.ArchiveAgentAttempt()
					: null;
				var recoveryContext = recovering
					? TrainingAgent.BuildRecoveryContext(previousAgent, archivedTranscript)
					: null;
				TrainingAgent.Prepare(lastRun, repo.AgentGameGuide, lastRun.GameRulesPath,
					CurrentPromptTemplate(), settings.AgentCommand, settings.AgentArguments,
					recoveryContext);
				lastRun.AgentStarted(settings.AgentCommand, archivedTranscript);
			}
			catch (InvalidOperationException ex)
			{
				ReportAutomationFailure(ex.Message, MessageBoxIcon.Warning, automatic);
				return false;
			}
			catch (IOException ex)
			{
				ReportAutomationFailure($"Could not prepare the improvement: {ex.Message}",
					MessageBoxIcon.Error, automatic);
				return false;
			}
			catch (UnauthorizedAccessException ex)
			{
				ReportAutomationFailure($"Could not prepare the improvement: {ex.Message}",
					MessageBoxIcon.Error, automatic);
				return false;
			}

			Save();
			ShowImprovementWindow().StartAgentRun(lastRun);
			queue.Clear();
			queue.Enqueue(new ScriptJob
			{
				Title = "Analyzing the fight and improving the bot",
				ScriptPath = repo.TrainBotScript,
				Arguments =
				[
					"-BattleBot", lastRun.Manifest.BotProject,
					"-RunDirectory", lastRun.RunDirectory,
					"-AgentConfiguration", lastRun.AgentConfigurationPath
				],
				PreserveColor = true,
				Output = AppendImprovementOutput,
				Completed = FinishImprovement
			});

			RunNext();
			return true;
		}

		void ReportAutomationFailure(string message, MessageBoxIcon icon, bool automatic)
		{
			if (automatic)
			{
				Append("Continuous improvement stopped: " + message);
				ShowOutputWindow();
				return;
			}

			MessageBox.Show(this, message, "AutoC&C", MessageBoxButtons.OK, icon);
		}

		void FinishImprovement(int exitCode)
		{
			if (lastRun == null)
				return;

			TrainingAgentExecutionStatus status = null;
			try
			{
				status = lastRun.ReadAgentStatus();
			}
			catch (IOException ex)
			{
				AppendImprovementOutput($"Could not read the improvement status: {ex.Message}");
			}
			catch (System.Text.Json.JsonException ex)
			{
				AppendImprovementOutput($"Could not read the improvement status: {ex.Message}");
			}

			var failed = exitCode != 0;
			var failurePhase = failed ? status?.Phase ?? "process" : null;
			var failureMessage = failed
				? status?.Message ?? $"Improvement process exited with code {exitCode}."
				: null;
			var suggestedNextPrompt = !failed
				? TrainingAgent.FindSuggestedNextPrompt(runner.LastOutput)
				: null;
			try
			{
				var changes = WorkspaceSnapshot.Compare(lastRun);
				lastRun.AgentFinished(exitCode, changes.Count, suggestedNextPrompt,
					failurePhase, failureMessage);
				if (!closing && improvementWindow != null)
					improvementWindow.CompleteAgentRun(lastRun);
			}
			catch (InvalidDataException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				lastRun.AgentFinished(exitCode, 0, suggestedNextPrompt,
					failurePhase, failureMessage);
			}
			catch (IOException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				lastRun.AgentFinished(exitCode, 0, suggestedNextPrompt,
					failurePhase, failureMessage);
			}
			catch (System.Text.Json.JsonException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				lastRun.AgentFinished(exitCode, 0, suggestedNextPrompt,
					failurePhase, failureMessage);
			}

			if (exitCode == 0 && continuousLoop.Stage == ContinuousTrainingStage.Improving)
				AcceptContinuousPrompt(lastRun, suggestedNextPrompt);

			if (resultsWindow != null)
				RefreshResultsHistory();
		}

		void AcceptContinuousPrompt(TrainingRun run, string promptTemplate)
		{
			if (string.IsNullOrWhiteSpace(promptTemplate))
			{
				AppendImprovementOutput("The agent returned no next-round prompt; continuing with the current prompt.");
				return;
			}

			if (!TrainingAgent.ValidatePromptTemplate(promptTemplate, out var error))
			{
				AppendImprovementOutput("The agent's next-round prompt was invalid; continuing with the current prompt. " + error);
				return;
			}

			var approved = promptTemplate.Trim();
			settings.AgentPromptTemplate = approved;
			settings.AgentPromptGuidance = null;
			run.AcceptSuggestedNextPrompt(approved);
			settings.Save();
			improvementWindow?.MarkNextPromptSaved();
			AppendImprovementOutput("The next-round prompt was accepted automatically for continuous improvement.");
		}

		void RetryVerification()
		{
			if (repo == null || lastRun?.Manifest.Agent == null ||
				!LastRunMatchesSelectedBot() ||
				!string.Equals(lastRun.Manifest.Agent.FailurePhase, "verification",
					StringComparison.OrdinalIgnoreCase))
				return;

			lastRun.VerificationStarted();
			ShowImprovementWindow().StartVerificationRun(lastRun);
			queue.Clear();
			queue.Enqueue(new ScriptJob
			{
				Title = "Retrying independent verification",
				ScriptPath = repo.VerifyBotScript,
				Arguments =
				[
					"-BattleBot", lastRun.Manifest.BotProject,
					"-RunDirectory", lastRun.RunDirectory
				],
				PreserveColor = true,
				Output = AppendImprovementOutput,
				Completed = FinishImprovement
			});

			RunNext();
		}

		void ReviewImprovement()
		{
			if (repo == null || lastRun?.IsEditable != true)
				return;

			try
			{
				if (!File.Exists(lastRun.PromptPath) ||
					!File.Exists(lastRun.GameGuidePath) ||
					!File.Exists(lastRun.GameRulesPath) ||
					!File.Exists(lastRun.FightManifestPath))
					EnsureAgentContext(lastRun);

				ShowImprovementWindow().ShowAgentRun(lastRun,
					promptFirst: lastRun.Manifest.Agent == null);
			}
			catch (IOException ex)
			{
				MessageBox.Show(this, $"Could not prepare the agent workspace: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (UnauthorizedAccessException ex)
			{
				MessageBox.Show(this, $"Could not prepare the agent workspace: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (InvalidOperationException ex)
			{
				MessageBox.Show(this, ex.Message, "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		void EnsureAgentContext(TrainingRun run)
		{
			if (!File.Exists(run.GameRulesPath))
				AgentRulesExporter.Export(repo, run.GameRulesPath);

			TrainingAgent.PrepareContext(run, repo.AgentGameGuide, run.GameRulesPath,
				CurrentPromptTemplate());
		}

		string CurrentPromptTemplate()
		{
			if (!string.IsNullOrWhiteSpace(settings.AgentPromptTemplate))
				return settings.AgentPromptTemplate;

			if (!File.Exists(repo.AgentPromptTemplate))
				throw new FileNotFoundException("The default agent prompt template is missing.",
					repo.AgentPromptTemplate);

			var template = File.ReadAllText(repo.AgentPromptTemplate);
			if (settings.AgentPromptGuidance is { Length: > 0 })
			{
				var legacy = "## Player guidance migrated from earlier runs" +
					Environment.NewLine + Environment.NewLine +
					string.Join(Environment.NewLine, settings.AgentPromptGuidance
						.Where(g => !string.IsNullOrWhiteSpace(g))
						.Select(g => "- " + g.Trim())) +
					Environment.NewLine + Environment.NewLine +
					"{nextPromptContract}";
				template = template.Replace("{nextPromptContract}", legacy,
					StringComparison.Ordinal);
			}

			settings.AgentPromptTemplate = template;
			settings.AgentPromptGuidance = null;
			settings.Save();
			return template;
		}

		void AcceptNextPrompt(TrainingRun run, string promptTemplate)
		{
			if (run == null)
				return;

			if (!TrainingAgent.ValidatePromptTemplate(promptTemplate, out var error))
			{
				MessageBox.Show(improvementWindow,
					"The proposed next prompt cannot be saved yet. " + error,
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			var approved = promptTemplate.Trim();
			settings.AgentPromptTemplate = approved;
			settings.AgentPromptGuidance = null;
			if (run.Manifest.Agent != null)
				run.AcceptSuggestedNextPrompt(approved);
			settings.Save();
			improvementWindow?.MarkNextPromptSaved();
		}

		void RestoreIteration()
		{
			if (lastRun == null || !LastRunMatchesSelectedBot() ||
				lastRun.Manifest.Agent?.ChangeCount <= 0)
				return;

			var answer = MessageBox.Show(this,
				"Restore every source file to the snapshot taken immediately before the improvement agent ran?",
				"Restore previous iteration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
			if (answer != DialogResult.Yes)
				return;

			try
			{
				WorkspaceSnapshot.Restore(lastRun);
				ShowImprovementWindow().ShowAgentRun(lastRun);
				Status("The previous source iteration has been restored.");
				UpdateEnabledState();
			}
			catch (InvalidDataException ex)
			{
				MessageBox.Show(this, ex.Message, "AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (IOException ex)
			{
				MessageBox.Show(this, $"Could not restore the source snapshot: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (UnauthorizedAccessException ex)
			{
				MessageBox.Show(this, $"Could not restore the source snapshot: {ex.Message}", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
			catch (InvalidOperationException ex)
			{
				MessageBox.Show(this, ex.Message, "AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		void RebuildPlatform()
		{
			if (repo == null)
				return;

			Save();
			ClearOutput();
			queue.Clear();

			queue.Enqueue(new ScriptJob
			{
				Title = "Rebuilding the platform and bots",
				ScriptPath = repo.BuildScript,
				Arguments = ["-SkipEngine"]
			});

			RunNext();
		}

		void RunNext()
		{
			if (queue.Count == 0)
			{
				var completedBattle = battleRunning;
				if (completedBattle)
					StopWatchingBattle("finished");

				if (completedBattle &&
					continuousLoop.BattleCompleted() == ContinuousTrainingAction.Improve)
				{
					Status("Battle saved. Starting continuous improvement...");
					if (!ImproveBot(automatic: true))
					{
						continuousLoop.Stop();
						Status("Continuous improvement stopped before the agent could start.");
						UpdateEnabledState();
					}

					return;
				}

				if (!completedBattle &&
					continuousLoop.ImprovementCompleted() == ContinuousTrainingAction.Fight)
				{
					Status("Improvement deployed. Starting the next fight...");
					if (!Launch(play: true, continuousContinuation: true))
					{
						continuousLoop.Stop();
						Status("Continuous improvement stopped before the next fight could start.");
						UpdateEnabledState();
					}

					return;
				}

				Status("Done.");
				UpdateEnabledState();
				return;
			}

			var job = queue.Dequeue();
			activeJob = job;
			RouteJobOutput(job, $"=== {job.Title} ===");
			Status(job.Title + "…");

			try
			{
				runner.Start(job, repo.Root);
			}
			catch (System.ComponentModel.Win32Exception ex)
			{
				RouteJobOutput(job, $"Could not start PowerShell: {ex.Message}");
				Status("Failed to start.");
				queue.Clear();
				activeJob = null;
				InvokeJobCompleted(job, -1);
				StopWatchingBattle("failed");
				continuousLoop.Stop();
			}
			catch (InvalidOperationException ex)
			{
				RouteJobOutput(job, $"Could not start PowerShell: {ex.Message}");
				Status("Failed to start.");
				queue.Clear();
				activeJob = null;
				InvokeJobCompleted(job, -1);
				StopWatchingBattle("failed");
				continuousLoop.Stop();
			}

			UpdateEnabledState();
		}

		void JobFinished(int exitCode)
		{
			var completed = activeJob;
			activeJob = null;

			if (stopRequested)
			{
				// Killing the process tree gives us whatever exit code Windows felt like, so the
				// only honest thing to report is that you asked for it to stop.
				RouteJobOutput(completed, "=== Stopped ===");
				InvokeJobCompleted(completed, exitCode);
				StopWatchingBattle("stopped");
				continuousLoop.Stop();
				Status("Stopped.");
				stopRequested = false;
				UpdateEnabledState();
				return;
			}

			if (exitCode != 0)
			{
				RouteJobOutput(completed, $"=== Finished with exit code {exitCode} ===");
				InvokeJobCompleted(completed, exitCode);
				StopWatchingBattle("failed");
				continuousLoop.Stop();
				Status($"Stopped with exit code {exitCode}. See the {(completed?.Output == null ? "output" : "improvement")} window.");
				queue.Clear();
				if (completed?.Output == null)
					ShowOutputWindow();
				else if (improvementWindow == null && lastRun != null)
					ShowImprovementWindow().ShowAgentRun(lastRun);
				UpdateEnabledState();
				return;
			}

			RouteJobOutput(completed, "=== Finished ===");
			InvokeJobCompleted(completed, exitCode);
			RunNext();
		}

		void InvokeJobCompleted(ScriptJob job, int exitCode)
		{
			try
			{
				job?.Completed?.Invoke(exitCode);
			}
			catch (InvalidDataException ex)
			{
				RouteJobOutput(job, $"Could not finish processing '{job?.Title}': {ex.Message}");
			}
			catch (IOException ex)
			{
				RouteJobOutput(job, $"Could not finish processing '{job?.Title}': {ex.Message}");
			}
			catch (UnauthorizedAccessException ex)
			{
				RouteJobOutput(job, $"Could not finish processing '{job?.Title}': {ex.Message}");
			}
			catch (System.Text.Json.JsonException ex)
			{
				RouteJobOutput(job, $"Could not finish processing '{job?.Title}': {ex.Message}");
			}
			catch (InvalidOperationException ex)
			{
				RouteJobOutput(job, $"Could not finish processing '{job?.Title}': {ex.Message}");
			}
		}

		void StopEverything()
		{
			stopRequested = true;
			continuousLoop.Stop();
			queue.Clear();
			runner.Stop();
			Status("Stopping…");
		}

		// -------------------------------------------------------------------
		// Output
		// -------------------------------------------------------------------

		/// <summary>
		/// Starts a fresh transcript, in the output window if there is one.
		/// </summary>
		/// <remarks>
		/// The window is opened for a build as well as for a battle, because a build that failed
		/// has to say so somewhere — and putting its output anywhere else would mean two places to
		/// look for the same kind of answer.
		/// </remarks>
		void ClearOutput()
		{
			pendingOutput.Clear();
			ShowOutputWindow().ClearBuildOutput();
		}

		void RouteJobOutput(ScriptJob job, string line) =>
			RouteJobOutput(job, TerminalLine.Plain(line));

		void RouteJobOutput(ScriptJob job, TerminalLine line)
		{
			if (job?.Output != null)
				job.Output(line);
			else
				Append(line.PlainText);
		}

		void AppendImprovementOutput(string line) =>
			AppendImprovementOutput(TerminalLine.Plain(line));

		void AppendImprovementOutput(TerminalLine line)
		{
			if (improvementWindow != null && !improvementWindow.IsDisposed)
				improvementWindow.AppendAgentOutput(line);
		}

		void Append(string line)
		{
			// Scripts can write before the window exists, and before it exists again after being
			// closed; nothing said should be lost because of when it was said.
			if (outputWindow != null)
				outputWindow.AppendBuildOutput(line);
			else
				pendingOutput.Add(line);
		}

		void Status(string message)
		{
			statusLabel.Text = message;
		}

		void Save()
		{
			settings.RepositoryRoot = repo?.Root;
			settings.BattleBotPath = botBox.Text.Trim();
			settings.Map = (mapBox.SelectedItem as MapInfo)?.Id;
			settings.Difficulty = (difficultyBox.SelectedItem as DifficultyLevel)?.Name;
			settings.GameSpeed = (speedBox.SelectedItem as GameSpeedInfo)?.Id;
			settings.ExecutionMode = IsHeadlessSelected()
				? BattleExecutionModes.Headless
				: BattleExecutionModes.Rendered;
			settings.Opponents = (int)opponentsBox.Value;
			settings.Faction = ((FactionChoice)factionBox.SelectedItem).Value;
			settings.BotFaction = ((FactionChoice)botFactionBox.SelectedItem).Value;
			settings.RunTests = runTestsBox.Checked;
			settings.ContinuousImprovement = continuousBox.Checked;
			settings.LastTrainingRunDirectory = lastRun?.RunDirectory;
			settings.Save();
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			Save();

			// Leaving the game running with no window to stop it from would be worse than asking.
			if (runner.IsRunning)
			{
				var answer = MessageBox.Show(this, "Stop what is running and close?", "AutoC&C",
					MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (answer == DialogResult.No)
				{
					e.Cancel = true;
					return;
				}

				queue.Clear();
				var stoppedJob = activeJob;
				activeJob = null;
				closing = true;
				stopRequested = true;
				continuousLoop.Stop();
				runner.Stop();
				InvokeJobCompleted(stoppedJob, -1);
				StopWatchingBattle("stopped");
			}

			if (IsHandleCreated)
				taskbarProgress.SetBusy(Handle, busy: false);

			base.OnFormClosing(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				taskbarProgress.Dispose();

			base.Dispose(disposing);
		}
	}
}
