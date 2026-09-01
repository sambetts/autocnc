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

		static readonly FactionChoice[] Factions =
		[
			new() { Value = "Random", Label = "Random" },
			new() { Value = "gdi", Label = "GDI" },
			new() { Value = "nod", Label = "Nod" }
		];

		readonly LauncherSettings settings = LauncherSettings.Load();
		readonly ScriptRunner runner = new();
		readonly Queue<ScriptJob> queue = new();
		readonly MatchLog matchLog = new();
		readonly BattleEventLog battleLog = new();

		/// <summary>Script output produced before there was a window to put it in.</summary>
		readonly List<string> pendingOutput = [];

		RepoLayout repo;
		bool stopRequested;
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
		Button launchButton;
		Button buildButton;
		Button platformButton;
		Button stopButton;
		Button replayButton;
		GroupBox botGroup;
		GroupBox battleGroup;
		Panel battleBanner;
		Label battleBannerText;
		TableLayoutPanel root;
		StatusStrip status;
		ResultsWindow resultsWindow;
		OutputWindow outputWindow;
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
			runner.Output += line => Post(() => Append(line));
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
				RowCount = 6,
				Padding = new Padding(10)
			};

			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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
			root.Controls.Add(BuildBattleBanner(), 0, 3);
			root.Controls.Add(BuildRepositoryRow(), 0, 5);

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
			botBox.TextChanged += (_, _) => UpdateEnabledState();

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

			battleGroup = Group("The battle", grid);
			return battleGroup;
		}

		Control BuildActionRow()
		{
			var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };

			launchButton = new Button { Text = "Launch battle", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
			launchButton.Click += (_, _) => Launch(play: true);

			buildButton = new Button { Text = "Build only", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
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

			var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
			links.Controls.Add(results);
			links.Controls.Add(output);

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

		/// <summary>Where the launcher asks the game to record the match it is about to start.</summary>
		static string TelemetryPath => LogPath("autocnc-launcher.csv");

		/// <summary>Where the launcher asks the game to record what your side saw and did.</summary>
		static string BattleLogPath => LogPath("autocnc-launcher-battle.csv");

		static string LogPath(string file) => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA", "Logs", file);

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
		ResultsWindow ShowResultsWindow()
		{
			if (resultsWindow == null || resultsWindow.IsDisposed)
			{
				resultsWindow = new ResultsWindow(matchLog);
				resultsWindow.FormClosed += (_, _) => resultsWindow = null;
			}

			resultsWindow.Show(this, 0f, 0.55f);
			resultsWindow.Redraw(battleFinished);
			return resultsWindow;
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

			botBox.Text = settings.BattleBotPath ?? repo?.ReferenceBot ?? string.Empty;
			runTestsBox.Checked = settings.RunTests;
			opponentsBox.Value = Math.Clamp(settings.Opponents, opponentsBox.Minimum, opponentsBox.Maximum);
			Select(factionBox, settings.Faction);
			Select(botFactionBox, settings.BotFaction);

			LoadDifficulties();
			LoadSpeeds();
			LoadMaps();
			UpdateEnabledState();
			FitToContent();
		}

		void LoadSpeeds()
		{
			speedBox.Items.Clear();

			if (repo == null)
				return;

			var speeds = GameSpeedCatalog.Read(repo);
			foreach (var speed in speeds)
				speedBox.Items.Add(speed);

			if (speeds.Count == 0)
				return;

			var index = speeds.ToList().FindIndex(s => string.Equals(s.Id, settings.GameSpeed, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
				index = speeds.ToList().FindIndex(s => s.IsDefault);

			speedBox.SelectedIndex = Math.Max(0, index);
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
			var ready = !busy && repo != null && BotExists() && mapBox.SelectedItem is MapInfo;

			launchButton.Enabled = ready;
			buildButton.Enabled = !busy && repo != null && BotExists();
			platformButton.Enabled = !busy && repo != null;
			stopButton.Enabled = busy;

			// A replay is only complete once the game that recorded it has exited.
			replayButton.Enabled = !busy && repo != null && NewestReplay() != null;

			// Tests come from a project's Tests folder, so a prebuilt assembly has none to run.
			runTestsBox.Enabled = !busy && !IsPrebuilt();

			// Setting up the next battle while one is running would be editing a form whose Launch
			// button is already unavailable — the greying out is what says so.
			botGroup.Enabled = !battleRunning;
			battleGroup.Enabled = !battleRunning;

			UpdateBattleBanner();

			if (busy)
				return;

			if (repo == null)
				Status("Point me at your AutoC&C checkout to get started.");
			else if (!repo.EngineFetched)
				Status("The engine submodule is missing. Run ./scripts/setup.ps1 in the repository first.");
			else if (!BotExists())
				Status("Choose the battle bot project or assembly you want to play.");
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
				? "Battle in progress. The results and output windows are following it; this one waits until it is over."
				: "The last battle is finished. Its results and output are still open — close them when you are done reading.";
		}

		bool BotExists()
		{
			var path = botBox.Text.Trim();
			return path.Length > 0 && (File.Exists(path) || Directory.Exists(path));
		}

		bool IsPrebuilt() =>
			botBox.Text.Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

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
		void Launch(bool play)
		{
			if (repo == null || !BotExists())
				return;

			if (!repo.EngineFetched)
			{
				MessageBox.Show(this,
					"The OpenRA engine submodule has not been fetched. Run ./scripts/setup.ps1 in the repository, then try again.",
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			Save();
			ClearOutput();
			queue.Clear();

			if (play)
				StartWatchingBattle();

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
				Title = play ? "Building your bot and starting the battle" : "Building your bot",
				ScriptPath = repo.RunBotScript,
				Arguments = RunBotArguments(play)
			});

			RunNext();
		}

		IReadOnlyList<string> RunBotArguments(bool play)
		{
			var map = (MapInfo)mapBox.SelectedItem;
			var difficulty = difficultyBox.SelectedItem as DifficultyLevel;

			var arguments = new List<string>
			{
				"-BattleBot", botBox.Text.Trim(),
				"-Opponents", ((int)opponentsBox.Value).ToString(),
				"-Faction", ((FactionChoice)factionBox.SelectedItem).Value,
				"-BotFaction", ((FactionChoice)botFactionBox.SelectedItem).Value
			};

			if (map != null)
				arguments.AddRange(["-Map", map.Id]);

			if (difficulty != null)
				arguments.AddRange(["-Difficulty", difficulty.Name]);

			if (speedBox.SelectedItem is GameSpeedInfo speed)
				arguments.AddRange(["-GameSpeed", speed.Id]);

			if (play)
				arguments.AddRange(["-Telemetry", TelemetryPath, "-BattleLog", BattleLogPath]);

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
			battleRunning = true;
			battleFinished = false;

			foreach (var path in new[] { TelemetryPath, BattleLogPath })
			{
				try
				{
					if (File.Exists(path))
						File.Delete(path);
				}
				catch (IOException)
				{
					// Still held by a match that is on its way out. The game truncates it anyway.
				}
			}

			matchLog.Watch(TelemetryPath);
			battleLog.Watch(BattleLogPath);

			ShowResultsWindow();
			ShowOutputWindow().ClearBattle();

			matchTimer.Start();
		}

		/// <summary>Stops polling, after one last read so the end of the battle is on both windows.</summary>
		void StopWatchingBattle()
		{
			if (!battleRunning)
				return;

			battleRunning = false;
			battleFinished = true;
			matchTimer.Stop();
			PollBattle(finished: true);
		}

		/// <summary>Reads whatever the running game has written since last time.</summary>
		void PollBattle(bool finished)
		{
			if ((matchLog.Refresh() || finished) && resultsWindow != null)
				resultsWindow.Redraw(finished);

			if ((battleLog.Refresh() || finished) && outputWindow != null)
				outputWindow.ShowBattle(finished);
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
				// The last job has finished, so the battle — if there was one — is over.
				StopWatchingBattle();
				Status("Done.");
				UpdateEnabledState();
				return;
			}

			var job = queue.Dequeue();
			Append($"=== {job.Title} ===");
			Status(job.Title + "…");

			try
			{
				runner.Start(job, repo.Root);
			}
			catch (Exception ex)
			{
				Append($"Could not start PowerShell: {ex.Message}");
				Status("Failed to start.");
				queue.Clear();
			}

			UpdateEnabledState();
		}

		void JobFinished(int exitCode)
		{
			if (stopRequested)
			{
				// Killing the process tree gives us whatever exit code Windows felt like, so the
				// only honest thing to report is that you asked for it to stop.
				StopWatchingBattle();
				Append("=== Stopped ===");
				Status("Stopped.");
				stopRequested = false;
				UpdateEnabledState();
				return;
			}

			if (exitCode != 0)
			{
				StopWatchingBattle();
				Append($"=== Finished with exit code {exitCode} ===");
				Status($"Stopped with exit code {exitCode}. See the output window.");
				queue.Clear();
				ShowOutputWindow();
				UpdateEnabledState();
				return;
			}

			Append("=== Finished ===");
			RunNext();
		}

		void StopEverything()
		{
			stopRequested = true;
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
			settings.Opponents = (int)opponentsBox.Value;
			settings.Faction = ((FactionChoice)factionBox.SelectedItem).Value;
			settings.BotFaction = ((FactionChoice)botFactionBox.SelectedItem).Value;
			settings.RunTests = runTestsBox.Checked;
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
				runner.Stop();
			}

			base.OnFormClosing(e);
		}
	}
}
