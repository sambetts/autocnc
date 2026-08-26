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

		RepoLayout repo;
		bool stopRequested;
		bool matchTabShown;

		TextBox repositoryBox;
		TextBox doctrineBox;
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
		TextBox outputBox;
		TabControl resultsTabs;
		TabPage matchTab;
		MatchChart unitsChart;
		MatchChart armyChart;
		MatchChart buildingsChart;
		MatchChart baseChart;
		MatchChart killsChart;
		MatchChart[] charts = [];
		Timer matchTimer;
		ToolStripStatusLabel statusLabel;

		public MainForm()
		{
			Text = "AutoC&C — Battle Launcher";
			Font = SystemFonts.MessageBoxFont;
			MinimumSize = new Size(760, 680);
			Size = new Size(900, 900);
			StartPosition = FormStartPosition.CenterScreen;

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
		void BuildLayout()
		{
			var root = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 5,
				Padding = new Padding(10)
			};

			root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

			root.Controls.Add(BuildDoctrineGroup(), 0, 0);
			root.Controls.Add(BuildBattleGroup(), 0, 1);
			root.Controls.Add(BuildActionRow(), 0, 2);
			root.Controls.Add(BuildOutputGroup(), 0, 3);

			var status = new StatusStrip();
			statusLabel = new ToolStripStatusLabel("Starting up…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
			status.Items.Add(statusLabel);

			Controls.Add(root);
			Controls.Add(status);
		}

		Control BuildDoctrineGroup()
		{
			var grid = Grid(3);

			doctrineBox = new TextBox { Dock = DockStyle.Fill };
			doctrineBox.TextChanged += (_, _) => UpdateEnabledState();

			grid.Controls.Add(Caption("Doctrine:"), 0, 0);
			grid.Controls.Add(doctrineBox, 1, 0);
			grid.Controls.Add(SmallButton("Browse…", BrowseDoctrine), 2, 0);

			var hint = new Label
			{
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Text = "A doctrine project (.csproj) is built before it plays. A prebuilt .dll is played as it is."
			};

			var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			runTestsBox = new CheckBox { Text = "Run its tests first", AutoSize = true };

			var useReference = new LinkLabel
			{
				Text = "Use the reference doctrine",
				AutoSize = true,
				Margin = new Padding(16, 4, 0, 0)
			};

			useReference.LinkClicked += (_, _) =>
			{
				if (repo != null)
					doctrineBox.Text = repo.ReferenceDoctrine;
			};

			options.Controls.Add(runTestsBox);
			options.Controls.Add(useReference);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 1);
			grid.Controls.Add(options, 1, 1);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
			grid.Controls.Add(hint, 1, 2);
			grid.SetColumnSpan(hint, 2);

			return Group("Your battle code", grid);
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

			return Group("The battle", grid);
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

		Control BuildOutputGroup()
		{
			outputBox = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ReadOnly = true,
				ScrollBars = ScrollBars.Vertical,
				WordWrap = false,
				BackColor = Color.FromArgb(30, 30, 30),
				ForeColor = Color.Gainsboro,
				Font = new Font(FontFamily.GenericMonospace, 8.5f)
			};

			resultsTabs = new TabControl { Dock = DockStyle.Fill };

			var outputTab = new TabPage("Output") { Padding = new Padding(2) };
			outputTab.Controls.Add(outputBox);

			matchTab = new TabPage("Match") { Padding = new Padding(2), BackColor = Color.FromArgb(30, 30, 30) };
			matchTab.Controls.Add(BuildMatchCharts());

			resultsTabs.TabPages.Add(outputTab);
			resultsTabs.TabPages.Add(matchTab);

			var group = Group("Results", resultsTabs);
			group.Dock = DockStyle.Fill;

			var repositoryRow = Grid(3);
			repositoryBox = new TextBox { Dock = DockStyle.Fill };
			repositoryRow.Controls.Add(Caption("Repository:"), 0, 0);
			repositoryRow.Controls.Add(repositoryBox, 1, 0);
			repositoryRow.Controls.Add(SmallButton("Browse…", BrowseRepository), 2, 0);

			var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
			stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			stack.Controls.Add(group, 0, 0);
			stack.Controls.Add(repositoryRow, 0, 1);

			return stack;
		}

		/// <summary>
		/// The questions a doctrine has to answer, laid out so you can see them at once: is it
		/// building an army, is that army worth anything, is the base growing behind it, and is any
		/// of it killing the other side. The crossings between the graphs are the interesting part,
		/// so they share a time axis rather than hiding behind tabs.
		/// </summary>
		Control BuildMatchCharts()
		{
			unitsChart = new MatchChart("Units", s => s.Units) { Dock = DockStyle.Fill, Log = matchLog };
			armyChart = new MatchChart("Army value", s => s.Army) { Dock = DockStyle.Fill, Log = matchLog };
			buildingsChart = new MatchChart("Buildings", s => s.Buildings) { Dock = DockStyle.Fill, Log = matchLog };
			baseChart = new MatchChart("Base value", s => s.BaseValue) { Dock = DockStyle.Fill, Log = matchLog };
			killsChart = new MatchChart("Kills", s => s.Killed) { Dock = DockStyle.Fill, Log = matchLog };

			charts = [unitsChart, armyChart, buildingsChart, baseChart, killsChart];

			var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
			stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			for (var i = 0; i < 3; i++)
				stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));

			stack.Controls.Add(unitsChart, 0, 0);
			stack.Controls.Add(armyChart, 1, 0);
			stack.Controls.Add(buildingsChart, 0, 1);
			stack.Controls.Add(baseChart, 1, 1);

			// Kills is the one that reads as a score line, so it gets the full width underneath.
			stack.Controls.Add(killsChart, 0, 2);
			stack.SetColumnSpan(killsChart, 2);

			// Polled rather than watched: FileSystemWatcher does not raise for every appended line
			// on every filesystem, and half a second is well under the eye's patience anyway.
			matchTimer = new Timer { Interval = 500 };
			matchTimer.Tick += (_, _) => RefreshMatch();

			return stack;
		}

		/// <summary>Where the launcher asks the game to record the match it is about to start.</summary>
		static string TelemetryPath => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"OpenRA", "Logs", "autocnc-launcher.csv");

		void RedrawCharts()
		{
			foreach (var chart in charts)
				chart.Invalidate();
		}

		void RefreshMatch()
		{
			if (!matchLog.Refresh())
				return;

			RedrawCharts();

			// Bring the graph forward once, when the first rows say the battle is actually running.
			if (!matchTabShown && !matchLog.IsEmpty)
			{
				matchTabShown = true;
				resultsTabs.SelectedTab = matchTab;
			}
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

			doctrineBox.Text = settings.DoctrinePath ?? repo?.ReferenceDoctrine ?? string.Empty;
			runTestsBox.Checked = settings.RunTests;
			opponentsBox.Value = Math.Clamp(settings.Opponents, opponentsBox.Minimum, opponentsBox.Maximum);
			Select(factionBox, settings.Faction);
			Select(botFactionBox, settings.BotFaction);

			LoadDifficulties();
			LoadSpeeds();
			LoadMaps();
			UpdateEnabledState();
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
			var ready = !busy && repo != null && DoctrineExists() && mapBox.SelectedItem is MapInfo;

			launchButton.Enabled = ready;
			buildButton.Enabled = !busy && repo != null && DoctrineExists();
			platformButton.Enabled = !busy && repo != null;
			stopButton.Enabled = busy;

			// A replay is only complete once the game that recorded it has exited.
			replayButton.Enabled = !busy && repo != null && NewestReplay() != null;

			// Tests come from a project's Tests folder, so a prebuilt assembly has none to run.
			runTestsBox.Enabled = !busy && !IsPrebuilt();

			if (busy)
				return;

			if (repo == null)
				Status("Point me at your AutoC&C checkout to get started.");
			else if (!repo.EngineFetched)
				Status("The engine submodule is missing. Run ./scripts/setup.ps1 in the repository first.");
			else if (!DoctrineExists())
				Status("Choose the doctrine project or assembly you want to play.");
			else
				Status(repo.EngineBuilt ? "Ready." : "Ready — the engine is not built yet, so the first launch will take a few minutes.");
		}

		bool DoctrineExists()
		{
			var path = doctrineBox.Text.Trim();
			return path.Length > 0 && (File.Exists(path) || Directory.Exists(path));
		}

		bool IsPrebuilt() =>
			doctrineBox.Text.Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

		// -------------------------------------------------------------------
		// Browsing
		// -------------------------------------------------------------------
		void BrowseDoctrine(object sender, EventArgs e)
		{
			using var dialog = new OpenFileDialog
			{
				Title = "Select your doctrine",
				Filter = "Doctrine project or assembly (*.csproj;*.dll)|*.csproj;*.dll|Doctrine project (*.csproj)|*.csproj|Built doctrine (*.dll)|*.dll|All files (*.*)|*.*",
				CheckFileExists = true
			};

			var current = doctrineBox.Text.Trim();
			if (current.Length > 0)
			{
				var directory = File.Exists(current) ? Path.GetDirectoryName(current) : current;
				if (Directory.Exists(directory))
					dialog.InitialDirectory = directory;
			}

			if (dialog.ShowDialog(this) == DialogResult.OK)
				doctrineBox.Text = dialog.FileName;
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
					"That folder does not look like an AutoC&C checkout — it has no AutoCnC.sln and no scripts/run-doctrine.ps1.",
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
			outputBox.Clear();
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
			if (repo == null || !DoctrineExists())
				return;

			if (!repo.EngineFetched)
			{
				MessageBox.Show(this,
					"The OpenRA engine submodule has not been fetched. Run ./scripts/setup.ps1 in the repository, then try again.",
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			Save();
			outputBox.Clear();
			queue.Clear();

			if (play)
				StartWatchingMatch();

			// The engine is a several-minute build, so only do it when it genuinely is not there.
			if (!repo.EngineBuilt)
				queue.Enqueue(new ScriptJob
				{
					Title = "Building the engine — this one takes a few minutes",
					ScriptPath = repo.BuildScript,
					Arguments = ["-SkipDoctrines"]
				});

			queue.Enqueue(new ScriptJob
			{
				Title = play ? "Building your doctrine and starting the battle" : "Building your doctrine",
				ScriptPath = repo.RunDoctrineScript,
				Arguments = RunDoctrineArguments(play)
			});

			RunNext();
		}

		IReadOnlyList<string> RunDoctrineArguments(bool play)
		{
			var map = (MapInfo)mapBox.SelectedItem;
			var difficulty = difficultyBox.SelectedItem as DifficultyLevel;

			var arguments = new List<string>
			{
				"-Doctrine", doctrineBox.Text.Trim(),
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
				arguments.AddRange(["-Telemetry", TelemetryPath]);

			if (runTestsBox.Checked && !IsPrebuilt())
				arguments.Add("-Test");

			if (!play)
				arguments.Add("-NoLaunch");

			return arguments;
		}

		/// <summary>
		/// Clears the previous match out of the way and starts polling for the new one.
		/// </summary>
		/// <remarks>
		/// The old file is deleted rather than left to be overwritten so that a battle that never
		/// gets as far as starting — a doctrine that fails to build, say — shows an empty graph
		/// instead of the last match's, which would be a quietly misleading thing to look at.
		/// </remarks>
		void StartWatchingMatch()
		{
			matchTabShown = false;

			try
			{
				if (File.Exists(TelemetryPath))
					File.Delete(TelemetryPath);
			}
			catch (IOException)
			{
				// Still held by a match that is on its way out. The game truncates it anyway.
			}

			matchLog.Watch(TelemetryPath);
			RedrawCharts();
			matchTimer.Start();
		}

		/// <summary>Stops polling, after one last read so the end of the match is on the graph.</summary>
		void StopWatchingMatch()
		{
			if (!matchTimer.Enabled)
				return;

			matchTimer.Stop();
			RefreshMatch();
		}

		void RebuildPlatform()
		{
			if (repo == null)
				return;

			Save();
			outputBox.Clear();
			queue.Clear();

			queue.Enqueue(new ScriptJob
			{
				Title = "Rebuilding the platform and doctrines",
				ScriptPath = repo.BuildScript,
				Arguments = ["-SkipEngine"]
			});

			RunNext();
		}

		void RunNext()
		{
			if (queue.Count == 0)
			{
				// The last job has finished, so the match — if there was one — is over.
				StopWatchingMatch();
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
				StopWatchingMatch();
				Append("=== Stopped ===");
				Status("Stopped.");
				stopRequested = false;
				UpdateEnabledState();
				return;
			}

			if (exitCode != 0)
			{
				StopWatchingMatch();
				Append($"=== Finished with exit code {exitCode} ===");
				Status($"Stopped with exit code {exitCode}. See the output above.");
				queue.Clear();
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
		void Append(string line)
		{
			outputBox.AppendText(line + Environment.NewLine);
		}

		void Status(string message)
		{
			statusLabel.Text = message;
		}

		void Save()
		{
			settings.RepositoryRoot = repo?.Root;
			settings.DoctrinePath = doctrineBox.Text.Trim();
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
