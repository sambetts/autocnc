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
using AutoCnC.Evidence;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// The launcher window: point it at your battle code, choose who to fight, press play.
	/// </summary>
	/// <remarks>
	/// Every button here runs a repository script and shows you its output, so nothing
	/// you can do in this window is something you could not have typed yourself. That is
	/// deliberate: the window is a convenience over the authoring loop, not a second
	/// implementation of it.
	/// </remarks>
	public sealed partial class MainForm : Form
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

		readonly LauncherSettings settings;
		readonly bool persistSettings;
		readonly string trainingRunsRoot;
		readonly PromptHistory promptHistory;
		readonly ScriptRunner runner = new();
		readonly Queue<ScriptJob> queue = new();
		readonly MatchLog matchLog = new();
		readonly BattleEventLog battleLog = new();
		readonly TaskbarProgress taskbarProgress = new();
		readonly ContinuousTrainingLoop continuousLoop = new();
		readonly ContinuousPromotionRunner continuousPromotion = new();

		/// <summary>Script output produced before there was a window to put it in.</summary>
		readonly List<string> pendingOutput = [];

		RepoLayout repo;
		ScriptJob activeJob;
		TrainingRun activeRun;
		TrainingRun lastRun;
		TrainingRun continuousCandidateRun;
		ContinuousEvaluationPlan continuousEvaluationPlan;
		TrainingWorkspaceMutation activeWorkspaceMutation;
		TrainingHistory loadedHistory = TrainingHistory.Empty;
		string loadedHistoryBot;
		DateTime battleStartedUtc;
		string replayBeforeBattle;
		bool stopRequested;
		bool closing;
		bool battleRunning;
		bool battleFinished;
		ContinuousTrainingAction pendingContinuousAction;

		/// <summary>
		/// A battle finished and the cycle has not yet acted on it.
		/// </summary>
		/// <remarks>
		/// Separate from <see cref="battleRunning"/> because a queued conversation turn runs
		/// between the battle ending and continuous training deciding what to do about it. Reading
		/// the live flag after that turn would see a battle that is no longer running and conclude
		/// none had finished, quietly dropping the improvement it was supposed to trigger.
		/// </remarks>
		bool battleJustCompleted;

		TextBox repositoryBox;
		TextBox botBox;
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

		public MainForm() : this(LauncherSettings.Load(), persistSettings: true) { }

		internal MainForm(LauncherSettings settings, bool persistSettings = false,
			string trainingRunsRoot = null, string promptHistoryRoot = null)
		{
			// Keep the 96-DPI baseline pending until all controls have been constructed.
			SuspendLayout();
			this.settings = settings;
			this.persistSettings = persistSettings;
			this.trainingRunsRoot = trainingRunsRoot;

			// Archiving is persistence, so a launcher told not to keep the player's settings must
			// not quietly start writing to their real profile either. A test that wants to inspect
			// the archive names its own root.
			promptHistory = promptHistoryRoot != null ? new PromptHistory(promptHistoryRoot)
				: persistSettings ? new PromptHistory()
				: null;
			Text = "AutoC&C — Battle Command";
			Font = CommandTheme.Body;
			AutoScaleMode = AutoScaleMode.Dpi;
			AutoScaleDimensions = new SizeF(96, 96);
			StartPosition = FormStartPosition.Manual;

			BuildLayout();
			ResumeLayout(performLayout: true);

			// Both fire on a background thread, and a script can still be writing while the
			// window is going away.
			runner.Output += line =>
			{
				var job = activeJob;
				Post(() => RouteJobOutput(job, line));
			};
			runner.Finished += code => Post(() => JobFinished(code));
		}

		protected override bool ShowWithoutActivation => !persistSettings;

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
		/// Workstations for the authoring loop; live battle evidence stays in its own windows.
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
			BuildCommandLayout();
			var status = new StatusStrip
			{
				BackColor = CommandTheme.Field, ForeColor = CommandTheme.Muted,
				SizingGrip = false, Padding = new Padding(16, 0, 16, 0)
			};
			statusLabel = new ToolStripStatusLabel("Starting up…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
			status.Items.Add(statusLabel);

			// Polled rather than watched: FileSystemWatcher does not raise for every appended line
			// on every filesystem, and half a second is well under the eye's patience anyway.
			matchTimer = new Timer { Interval = 500 };
			matchTimer.Tick += (_, _) => PollBattle(finished: false);

			Controls.Add(status);
			this.status = status;
			CommandTheme.Apply(this);
		}

		/// <summary>
		/// Keeps the command deck on screen; individual workstations scroll on smaller displays.
		/// </summary>
		void FitToContent()
		{
			var area = Screen.FromControl(this).WorkingArea;
			var scale = DeviceDpi / 96f;
			MinimumSize = new Size(Math.Min(area.Width, (int)(940 * scale)),
				Math.Min(area.Height, (int)(700 * scale)));
			Size = new Size(Math.Min(area.Width, (int)(1200 * scale)),
				Math.Min(area.Height, (int)(850 * scale)));
			Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
				area.Top + Math.Max(0, (area.Height - Height) / 2));
		}

		Control BuildBotGroup()
		{
			var grid = Grid(3);

			botBox = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Battle bot project or assembly" };
			botBox.TextChanged += (_, _) =>
			{
				RefreshResultsHistory(reload: true);
				UpdateEnabledState();
			};

			grid.Controls.Add(Caption("Battle bot:"), 0, 0);
			grid.Controls.Add(botBox, 1, 0);
			grid.Controls.Add(SmallButton("Browse…", BrowseBot), 2, 0);

			var hint = new Label
			{
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Text = "Select a C# project to build and improve. A prebuilt .dll can fight, but cannot be AI-trained.",
				MaximumSize = new Size(530, 0),
				Margin = new Padding(3, 12, 3, 12)
			};

			var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			newBotButton = SmallButton("New bot…", NewBot);

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

			options.Controls.Add(newBotButton);
			options.Controls.Add(useReference);

			grid.Controls.Add(options, 1, 1);
			grid.SetColumnSpan(options, 2);

			grid.Controls.Add(hint, 1, 2);
			grid.SetColumnSpan(hint, 2);

			openCodeButton = SmallButton("Open code", (_, _) => OpenCode());
			var deploy = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
			deploy.Controls.Add(openCodeButton);
			deploy.Controls.Add(buildButton);
			grid.Controls.Add(deploy, 1, 3);
			grid.SetColumnSpan(deploy, 2);
			botGroup = Group("BOT WORKSPACE", grid);
			return botGroup;
		}

		Control BuildBattleGroup()
		{
			var grid = Grid(3);

			mapBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Battle map" };
			mapBox.SelectedIndexChanged += (_, _) => MapChanged();

			difficultyBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "AI opponent difficulty" };
			difficultyBox.SelectedIndexChanged += (_, _) => DifficultyChanged();

			difficultySummary = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(510, 0), Margin = new Padding(3, 3, 3, 14) };

			opponentsBox = new NumericUpDown { Minimum = 0, Maximum = 7, Value = 1, Width = 60, AccessibleName = "Number of AI opponents" };

			factionBox = FactionCombo();
			botFactionBox = FactionCombo();
			factionBox.AccessibleName = "Your faction";
			botFactionBox.AccessibleName = "Opponent faction";

			grid.Controls.Add(Caption("Map:"), 0, 0);
			grid.Controls.Add(mapBox, 1, 0);
			grid.Controls.Add(SmallButton("Refresh", (_, _) => LoadMaps()), 2, 0);

			grid.Controls.Add(Caption("AI opponent:"), 0, 1);
			grid.Controls.Add(difficultyBox, 1, 1);

			grid.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
			grid.Controls.Add(difficultySummary, 1, 2);
			grid.SetColumnSpan(difficultySummary, 2);

			var counts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			counts.Controls.Add(opponentsBox);
			counts.Controls.Add(new Label
			{
				Text = "Set 0 for a solo test.",
				AutoSize = true,
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(8, 6, 0, 0)
			});

			grid.Controls.Add(Caption("Opponents:"), 0, 3);
			grid.Controls.Add(counts, 1, 3);

			var sides = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
			sides.Controls.Add(factionBox);
			sides.Controls.Add(new Label { Text = "against", AutoSize = true, Margin = new Padding(8, 6, 8, 0) });
			sides.Controls.Add(botFactionBox);

			grid.Controls.Add(Caption("You play:"), 0, 4);
			grid.Controls.Add(sides, 1, 4);

			speedBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Rendered game speed" };

			grid.Controls.Add(Caption("Speed:"), 0, 5);
			grid.Controls.Add(speedBox, 1, 5);

			executionBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Battle execution mode" };
			executionBox.Items.AddRange(ExecutionModes);
			executionBox.SelectedIndex = 0;
			executionBox.SelectedIndexChanged += (_, _) => ExecutionModeChanged();

			grid.Controls.Add(Caption("Execution:"), 0, 6);
			grid.Controls.Add(executionBox, 1, 6);

			battleGroup = Group("ENGAGEMENT PARAMETERS", grid);
			return battleGroup;
		}

		Control BuildActionRow()
		{
			var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
			row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

			launchButton = new ActionButton { Text = "DEPLOY && FIGHT", Primary = true, AutoSize = false, Height = 56, Dock = DockStyle.Fill, AccessibleName = "Deploy and fight" };
			launchButton.Click += (_, _) => Launch(play: true);

			buildButton = new ActionButton { Text = "Build && deploy", AutoSize = true };
			buildButton.Click += (_, _) => Launch(play: false);

			platformButton = new ActionButton { Text = "Rebuild platform", AutoSize = true };
			platformButton.Click += (_, _) => RebuildPlatform();

			stopButton = new ActionButton { Text = "STOP OPERATION", AutoSize = false, Height = 38, Dock = DockStyle.Fill, Enabled = false, ForeColor = CommandTheme.Danger };
			stopButton.Click += (_, _) => StopEverything();

			replayButton = new ActionButton { Text = "Watch replay", AutoSize = true };
			replayButton.Click += (_, _) => WatchReplay();

			row.Controls.Add(launchButton, 0, 0);
			row.Controls.Add(stopButton, 0, 1);

			AcceptButton = launchButton;
			return row;
		}

		Control BuildTrainingGroup()
		{
			var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };

			historyButton = new ActionButton { Text = "History && trends", AutoSize = true };
			historyButton.Click += (_, _) => ShowResultsWindow(reloadHistory: true).ShowRecordedSessions();

			trainingFeedbackButton = new ActionButton { Text = "Add feedback", AutoSize = true };
			trainingFeedbackButton.Click += (_, _) => ReviewBattleFeedback(trainingRun);
			improveButton = new ActionButton { Text = "Analyze && improve", AutoSize = true };
			improveButton.Click += (_, _) => { ImproveBot(); };

			retryVerificationButton = new ActionButton { Text = "Retry verification", AutoSize = true, Visible = false };
			retryVerificationButton.Click += (_, _) => RetryVerification();

			reviewButton = new ActionButton { Text = "Agent workspace", AutoSize = true };
			reviewButton.Click += (_, _) => ReviewImprovement();

			restoreButton = new ActionButton { Text = "Restore previous iteration", AutoSize = true };
			restoreButton.Click += (_, _) => RestoreIteration();

			runFolderButton = new ActionButton { Text = "Open run", AutoSize = true };
			runFolderButton.Click += (_, _) => OpenTrainingRun();

			var configure = new LinkLabel { Text = "Agent settings", AutoSize = true, Margin = new Padding(14, 8, 0, 0) };
			configure.LinkClicked += (_, _) => ConfigureAgent();

			continuousBox = new CheckBox
			{
				Text = "Repeat: fight > analyze > improve > fight",
				AutoSize = true,
				Margin = new Padding(14, 7, 0, 0)
			};
			continuousBox.CheckedChanged += (_, _) =>
			{
				settings.ContinuousImprovement = continuousBox.Checked;
				PersistSettings();
				UpdateEnabledState();
				RefreshResultsHistory();
			};

			actions.Controls.Add(trainingFeedbackButton);
			actions.Controls.Add(improveButton);
			actions.Controls.Add(retryVerificationButton);
			actions.Controls.Add(reviewButton);
			actions.Controls.Add(restoreButton);
			actions.Controls.Add(runFolderButton);

			var hint = new Label
			{
				AutoSize = true,
				MaximumSize = new Size(530, 0),
				ForeColor = SystemColors.GrayText,
				Margin = new Padding(3, 7, 3, 0),
				Text = "Every fight is evidence. The agent reviews the battle, changes your bot's source, then verifies and deploys it. " +
					"Fight again to find out whether it improved. Previous source iterations remain recoverable."
			};

			var stack = new TableLayoutPanel
			{
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				ColumnCount = 1,
				Dock = DockStyle.Fill,
				Margin = new Padding(0)
			};
			stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			stack.Controls.Add(BuildTrainingBattlePanel());
			stack.Controls.Add(actions);
			stack.Controls.Add(hint);
			stack.Controls.Add(continuousBox);
			stack.Controls.Add(configure);
			trainingHint = new Label
			{
				AutoSize = true, MaximumSize = new Size(530, 0),
				ForeColor = CommandTheme.Amber, Margin = new Padding(3, 18, 3, 6)
			};
			stack.Controls.Add(trainingHint);

			trainingGroup = Group("IMPROVEMENT ORDERS", stack);
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

			battleBanner = new Panel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Padding = new Padding(12, 8, 10, 8),
				Margin = new Padding(0, 4, 0, 4),
				Visible = false,
				BackColor = CommandTheme.Raised,
				ForeColor = CommandTheme.Ink
			};

			battleBanner.Controls.Add(stack);
			return battleBanner;
		}

		Control BuildRepositoryRow()
		{
			var row = Grid(3);
			repositoryBox = new TextBox { Dock = DockStyle.Fill, AccessibleName = "AutoC&C repository" };
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
				resultsWindow.FeedbackRequested += run => ReviewBattleFeedback(run);
				resultsWindow.ReplayRequested += run => WatchReplay(run);
				resultsWindow.DeleteRequested += ConfirmDeleteRecordedSessions;
				resultsWindow.TrainRequested += TrainFromHistory;
			}

			RefreshResultsHistory(reloadHistory);
			resultsWindow.Show(this, 0f, 0.55f);
			resultsWindow.Redraw(battleFinished);
			return resultsWindow;
		}

		void RefreshResultsHistory(bool reload = false)
		{
			if (trainingBattleBox == null)
				return;

			try
			{
				var selectedPath = botBox.Text.Trim();
				var selectedBot = selectedPath.Length > 0
					? BotWorkspace.ResolveProject(selectedPath) ?? Path.GetFullPath(selectedPath)
					: null;
				if (selectedBot == null)
				{
					loadedHistory = TrainingHistory.Empty;
					loadedHistoryBot = null;
				}
				else if (reload || !SamePath(selectedBot, loadedHistoryBot))
				{
					loadedHistory = TrainingHistory.Load(botBox.Text.Trim(), trainingRunsRoot);
					foreach (var unresolved in TrainingHistory.LoadUnresolved(
						botBox.Text.Trim(), trainingRunsRoot))
						loadedHistory = loadedHistory.WithRun(unresolved);
					loadedHistoryBot = selectedBot;
				}

				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = loadedHistory.WithRun(current);
				RefreshTrainingBattleChoices();
				resultsWindow?.SetHistory(loadedHistory, current, OperationInProgress);
			}
			catch (IOException ex)
			{
				Append($"Could not read training history: {ex.Message}");
				loadedHistoryBot = null;
				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = TrainingHistory.Empty.WithRun(current);
				RefreshTrainingBattleChoices();
				resultsWindow?.SetHistory(loadedHistory, current, OperationInProgress);
			}
			catch (UnauthorizedAccessException ex)
			{
				Append($"Could not read training history: {ex.Message}");
				loadedHistoryBot = null;
				var current = activeRun ?? (LastRunMatchesSelectedBot() ? lastRun : null);
				loadedHistory = TrainingHistory.Empty.WithRun(current);
				RefreshTrainingBattleChoices();
				resultsWindow?.SetHistory(loadedHistory, current, OperationInProgress);
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
				improvementWindow.FormClosing += ImprovementWindowClosing;
				improvementWindow.FormClosed += (_, _) => improvementWindow = null;
				improvementWindow.NextPromptAccepted += AcceptNextPrompt;
				improvementWindow.NextPromptRejected += RejectNextPrompt;
				improvementWindow.MessageSent += SendAgentMessage;
				improvementWindow.ShownRunChanged += shown =>
					improvementWindow?.ShowConversation(ConversationFor(shown));
			}

			improvementWindow.CurrentPromptTemplate = settings.AgentPromptTemplate;
			improvementWindow.Show(this, 0.05f, 0.9f);
			var run = activeTrainingRun ?? trainingRun;
			if (created && run != null &&
				(run.Manifest.Agent != null || File.Exists(run.PromptPath)))
				improvementWindow.ShowAgentRun(run);

			if (improvementWindow.ShownRun == null && run != null)
				improvementWindow.ShowConversation(ConversationFor(run));
			return improvementWindow;
		}

		/// <summary>
		/// Closing this window while the agent is working is ambiguous, so it asks.
		/// </summary>
		/// <remarks>
		/// Closing it used to do nothing but hide the agent, which is the worst of the three
		/// answers: the agent carries on editing the bot where you cannot see it, and the reason
		/// people close it is usually that they want it to stop. Stopping silently would be just
		/// as wrong for the times you only wanted the screen space back, and a long agent run is
		/// not something to discard by accident.
		/// </remarks>
		void ImprovementWindowClosing(object sender, FormClosingEventArgs e)
		{
			if (closing || !ImprovementRunning())
				return;

			// Remember which job the question is about. A message box pumps messages, so the agent
			// can finish while it is on screen and continuous training can start the next one.
			var askedAbout = activeJob;

			var answer = MessageBox.Show(improvementWindow,
				"The improvement agent is still working on your bot." +
				Environment.NewLine + Environment.NewLine +
				"Yes — stop it now." + Environment.NewLine +
				"No — leave it running and just close this window." + Environment.NewLine +
				"Cancel — keep watching.",
				"AutoC&C", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

			if (answer == DialogResult.Cancel)
			{
				e.Cancel = true;
				return;
			}

			// Stop only what was actually asked about. Stopping anything else would kill a job the
			// player never saw, and stopping nothing at all would leave the request latched onto
			// whatever runs next, which would then be reported as stopped without being stopped.
			if (answer == DialogResult.Yes && ImprovementRunning() &&
				ReferenceEquals(activeJob, askedAbout))
				StopEverything();
		}

		/// <summary>True while the running job is the improvement agent rather than a battle.</summary>
		bool ImprovementRunning() => runner.IsRunning &&
			activeJob?.Kind == ScriptJobKind.Improvement;

		/// <summary>
		/// True while the agent is answering something the player typed rather than working a
		/// round of its own.
		/// </summary>
		bool AnsweringMessage => activeJob?.Kind == ScriptJobKind.Chat;

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

		static GroupBox Group(string title, Control content)
		{
			content.ForeColor = CommandTheme.Ink;
			return new CommandSection { Text = title, AutoSize = true, Dock = DockStyle.Top, Controls = { content } };
		}

		static Label Caption(string text) =>
			new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

		static Button SmallButton(string text, EventHandler onClick)
		{
			var button = new ActionButton { Text = text, AutoSize = true };
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
			LoadConfiguration();
			FitToContent();
		}

		void LoadConfiguration()
		{
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
			RefreshResultsHistory(reload: true);
			continuousBox.Checked = settings.ContinuousImprovement;
			opponentsBox.Value = Math.Clamp(settings.Opponents, opponentsBox.Minimum, opponentsBox.Maximum);
			Select(factionBox, settings.Faction);
			Select(botFactionBox, settings.BotFaction);
			SelectExecutionMode(settings.ExecutionMode);

			LoadDifficulties();
			LoadSpeeds();
			LoadMaps();
			UpdateEnabledState();
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
			mapBox.SelectedIndex = -1;
			mapBox.Items.Clear();
			UpdateEnabledState();

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

			UpdateEnabledState();
		}

		bool IsHeadlessSelected() =>
			(executionBox.SelectedItem as ExecutionChoice)?.Value == BattleExecutionModes.Headless;

		// -------------------------------------------------------------------
		// Reacting to choices
		// -------------------------------------------------------------------
		void MapChanged()
		{
			// One slot is yours, so a two-player map has room for exactly one opponent.
			if (mapBox.SelectedItem is MapInfo map)
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
			if (launchButton == null || continuousBox == null || statusLabel == null)
				return;

			var busy = OperationInProgress;
			if (IsHandleCreated)
				taskbarProgress.SetBusy(Handle, busy);
			var compatible = repo?.SupportsTraining == true;
			var ready = !busy && compatible && BotExists() && mapBox.SelectedItem is MapInfo;
			var editable = BotExists() && BotWorkspace.ResolveProject(botBox.Text.Trim()) != null;
			var continuousReady = compatible && editable && continuousBox.Checked;
			var run = trainingRun;
			var agent = run?.Manifest.Agent;
			var trainingRunMatches = RunMatchesSelectedBot(run);
			var stoppedImprovement = agent is { Cancelled: true };
			var failedImprovement = !stoppedImprovement &&
				agent?.ExitCode is int agentExitCode && agentExitCode != 0;
			launchButton.Enabled = ready;
			buildButton.Enabled = !busy && compatible && BotExists();
			platformButton.Enabled = !busy && repo != null;
			newBotButton.Enabled = !busy && compatible && File.Exists(repo.NewBotScript);
			openCodeButton.Enabled = !busy && editable;
			historyButton.Enabled = BotExists();
			continuousBox.Enabled = !busy && compatible && editable;
			launchButton.Text = continuousReady ? "START AI TRAINING" : "DEPLOY && FIGHT";
			launchButton.AccessibleName = continuousReady ? "Start continuous AI training" : "Deploy and fight";
			improveButton.Enabled = !busy && compatible && File.Exists(repo.TrainBotScript) &&
				trainingRunMatches && run.CanImprove;
			improveButton.Text = failedImprovement && agent.RestoredUtc == null
				? "Fix failed improvement"
				: "Analyze && improve";
			retryVerificationButton.Visible = failedImprovement &&
				string.Equals(agent.FailurePhase, "verification", StringComparison.OrdinalIgnoreCase);
			retryVerificationButton.Enabled = !busy && compatible &&
				File.Exists(repo.VerifyBotScript) && trainingRunMatches;
			reviewButton.Enabled = compatible && trainingRunMatches &&
				run?.IsEditable == true && run.Manifest.CompletedUtc != null;
			// Any count but a confirmed zero. An unknown count means the workspace could not be
			// inspected, which is the state where undoing matters most, not least.
			restoreButton.Enabled = !busy && trainingRunMatches && run != null &&
				!run.IsBusy &&
				((agent != null && agent.ChangeCount != 0 && agent.RestoredUtc == null) ||
					run.HasUnresolvedContinuousExperiment) &&
				File.Exists(run.SnapshotManifestPath);
			runFolderButton.Enabled = !busy && run != null && Directory.Exists(run.RunDirectory);
			stopButton.Enabled = busy;

			// A replay is only complete once the game that recorded it has exited.
			replayButton.Enabled = !busy && repo != null && NewestReplay() != null;

			// Continuous training must keep the bot and battle configuration stable between its
			// fight and improvement stages. Stop is outside these groups and remains available.
			var cycleLocked = battleRunning || continuousLoop.IsRunning || activeTrainingRun != null;
			botGroup.Enabled = !cycleLocked;
			battleGroup.Enabled = !cycleLocked;
			trainingGroup.Enabled = !cycleLocked;

			UpdateFeedbackActions(busy || continuousLoop.IsRunning);
			resultsWindow?.SetOperationState(busy || continuousLoop.IsRunning);
			UpdateTrainingBattleState();
			UpdateCommandReadout(ready, editable, busy);
			UpdateBattleBanner();

			if (busy)
				return;

			Status(SettledStatus());
		}

		/// <summary>
		/// What the launcher would say if nothing were running: where the last operation left the
		/// bot, and what to do about it.
		/// </summary>
		/// <remarks>
		/// Separate from <see cref="UpdateEnabledState"/> because a finished operation is worth
		/// reporting even when something else has already started. A queued message delivered the
		/// moment an improvement ends used to replace this outright, so a round that took twenty
		/// minutes and succeeded said nothing at all on its way past.
		/// </remarks>
		string SettledStatus()
		{
			if (repo == null)
				return "Point me at your AutoC&C checkout to get started.";

			if (!repo.SupportsTraining)
				return $"This checkout uses authoring API {repo.AuthoringApiVersion}; the launcher needs {RepoLayout.RequiredAuthoringApiVersion}. Select the checkout that built this launcher.";

			if (!repo.EngineFetched)
				return "The engine submodule is missing. Run ./scripts/setup.ps1 in the repository first.";

			if (!BotExists())
				return "Choose the battle bot project or assembly you want to play.";

			if (mapBox.SelectedItem is not MapInfo)
				return "No skirmish maps available. Fetch the engine maps, then refresh the Proving ground.";

			if (LastRunMatchesSelectedBot() && lastRun?.Manifest.Status == "finished" && lastRun.IsEditable)
				return lastRun.HasPlayerFeedback
					? "Fight and feedback saved. Ready to analyze and improve."
					: "Fight saved. Add your feedback in Proving ground, or review recorded sessions in History & trends.";

			if (LastRunMatchesSelectedBot() &&
				lastRun?.HasUnresolvedContinuousExperiment == true)
				return lastRun.CanResumeContinuousEvaluation
					? "An interrupted continuous candidate must be reevaluated or restored before another fight."
					: "An interrupted continuous candidate must be restored before another fight.";

			if (lastRun?.Manifest.Status == "improved")
				return "Improvement verified and deployed. Fight again to measure it.";

			if (lastRun?.Manifest.Status == "candidate")
				return "Candidate verified. Paired benchmark evaluation has not finished.";

			if (lastRun?.Manifest.Status == "promoted")
				return "Candidate beat its paired control and was promoted. Fighting the new champion next.";

			if (LastRunMatchesSelectedBot() && lastRun?.Manifest.Status == "improvement-failed")
				return "Improvement failed. Fix its current changes with the agent, or restore the previous iteration.";

			if (lastRun?.Manifest.Status == "restored")
				return "The previous source iteration has been restored.";

			return repo.EngineBuilt
				? "Ready."
				: "Ready — the engine is not built yet, so the first launch will take a few minutes.";
		}

		void UpdateBattleBanner()
		{
			battleBanner.Visible = battleRunning || battleFinished;
			if (!battleBanner.Visible)
				return;

			Text = battleRunning ? "AutoC&C — Battle Command (battle running)" : "AutoC&C — Battle Command";

			battleBannerText.Text = battleRunning
				? continuousLoop.IsRunning
					? "Continuous training is fighting this iteration. Results and output are following it; Stop ends the loop."
					: activeRun?.Manifest.Battle?.ExecutionMode == BattleExecutionModes.Headless
						? "Headless battle in progress at CPU speed. Results and output are following it; Stop ends it cleanly."
						: "Battle in progress. The results and output windows are following it; this one waits until it is over."
				: continuousLoop.Stage switch
				{
					ContinuousTrainingStage.Improving =>
						"Continuous training is editing a candidate from the finished battle.",
					ContinuousTrainingStage.Evaluating =>
						"Continuous training is evaluating the candidate against its paired control.",
					ContinuousTrainingStage.Restoring =>
						"Continuous training is restoring the pre-agent champion after evaluation.",
					_ => "The last battle is finished. Its results and output are still open - close them when you are done reading."
				};
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

		bool LastRunMatchesSelectedBot() => RunMatchesSelectedBot(lastRun);

		bool RunMatchesSelectedBot(TrainingRun run)
		{
			if (run == null || string.IsNullOrWhiteSpace(botBox.Text))
				return false;

			try
			{
				var selectedPath = Path.GetFullPath(botBox.Text.Trim());
				var selectedProject = BotWorkspace.ResolveProject(selectedPath);
				if (selectedProject != null && run.Manifest.BotProject != null)
				{
					if (string.Equals(Path.GetFullPath(selectedProject),
						Path.GetFullPath(run.Manifest.BotProject), StringComparison.OrdinalIgnoreCase))
						return true;

					return !string.IsNullOrWhiteSpace(run.Manifest.BotDirectory) &&
						string.Equals(Path.GetDirectoryName(Path.GetFullPath(selectedProject)),
							Path.GetFullPath(run.Manifest.BotDirectory),
							StringComparison.OrdinalIgnoreCase);
				}

				return SamePath(selectedPath, run.Manifest.BotPath) ||
					SamePath(selectedPath, run.Manifest.BotProject) ||
					SamePath(Directory.Exists(selectedPath)
						? selectedPath
						: Path.GetDirectoryName(selectedPath), run.Manifest.BotDirectory);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
			{
				return false;
			}
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
			if (trainingRun != null)
				OpenFolder(trainingRun.RunDirectory, "No training run selected");
		}

		void ConfigureAgent()
		{
			using var dialog = new AgentSettingsDialog(settings.AgentCommand, settings.AgentArguments,
				settings.AgentStdin);
			if (dialog.ShowDialog(this) != DialogResult.OK)
				return;

			settings.AgentCommand = dialog.AgentCommand;
			settings.AgentArguments = dialog.AgentArguments;
			settings.AgentStdin = dialog.AgentStdin;
			PersistSettings();
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
			if (LastRunMatchesSelectedBot() && lastRun?.HasRecordedBattle == true &&
				File.Exists(lastRun.ReplayPath))
			{
				WatchReplay(lastRun);
				return;
			}

			var replay = NewestReplay();
			if (repo == null || replay == null || runner.IsRunning || continuousLoop.IsRunning)
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
			{
				var unresolved = UnresolvedContinuousExperiment();
				if (unresolved != null)
				{
					trainingRun = unresolved;
					settings.SelectedTrainingRunDirectory = unresolved.RunDirectory;
					PersistSettings();
					RefreshTrainingBattleChoices();
					UpdateEnabledState();
					if (unresolved.IsBusy)
					{
						ReportAutomationFailure(
							"Another launcher still owns the unresolved continuous candidate. " +
							"Wait for it to finish or stop it there before starting another fight.",
							MessageBoxIcon.Warning, automatic: false);
						return false;
					}
					if (continuousBox.Checked && unresolved.CanResumeContinuousEvaluation)
						return ResumeContinuousEvaluation(unresolved);

					ReportAutomationFailure(
						"An earlier continuous candidate still has no promotion decision. " +
						"Restore its pre-agent snapshot, or enable Continuous improvement to reevaluate it before fighting again.",
						MessageBoxIcon.Warning, automatic: false);
					return false;
				}

				continuousLoop.Begin(continuousBox.Checked &&
					BotWorkspace.ResolveProject(botBox.Text.Trim()) != null);
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
			}

			Save();
			ClearOutput();
			queue.Clear();

			if (play)
			{
				try
				{
					var project = BotWorkspace.ResolveProject(botBox.Text.Trim());
					if (project != null)
						EnsureWorkspaceMutation(Path.GetDirectoryName(project));
					StartWatchingBattle();
				}
				catch (IOException ex)
				{
					continuousLoop.Stop();
					ReleaseWorkspaceMutation();
					ReportAutomationFailure($"Could not create the training run: {ex.Message}",
						MessageBoxIcon.Error, continuousContinuation);
					return false;
				}
				catch (UnauthorizedAccessException ex)
				{
					continuousLoop.Stop();
					ReleaseWorkspaceMutation();
					ReportAutomationFailure($"Could not create the training run: {ex.Message}",
						MessageBoxIcon.Error, continuousContinuation);
					return false;
				}
				catch (InvalidOperationException ex)
				{
					continuousLoop.Stop();
					ReleaseWorkspaceMutation();
					ReportAutomationFailure($"Could not capture the continuous champion: {ex.Message}",
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

		TrainingRun UnresolvedContinuousExperiment()
		{
			var remembered = loadedHistory.Runs
				.Concat(TrainingHistory.LoadUnresolved(
					botBox.Text.Trim(), trainingRunsRoot))
				.Append(lastRun)
				.Append(trainingRun)
				.Where(run => run != null && RunMatchesSelectedBot(run))
				.GroupBy(run => run.RunDirectory, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First())
				.ToList();
			var runs = new List<TrainingRun>();
			foreach (var rememberedRun in remembered)
				try
				{
					var run = TrainingRun.Load(rememberedRun.RunDirectory) ?? rememberedRun;
					if (!continuousLoop.IsRunning &&
						run.IsBusy && ProcessOwnership.IsCurrent(run.Manifest.Owner))
					{
						using var mutation = LockForContinuousMutation(
							run, allowCurrentOwner: true);
						run = mutation.Run;
						run.AbortContinuousExperiment(
							"The in-memory continuous loop ended before a promotion decision.");
					}
					ReplaceRunReference(rememberedRun, run);
					runs.Add(run);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					Append("Could not reconcile an interrupted continuous experiment: " + ex.Message);
				}

			return runs
				.Where(run => run.HasUnresolvedContinuousExperiment)
				.OrderBy(run => run.Manifest.CreatedUtc)
				.LastOrDefault();
		}

		TrainingRunMutation LockForContinuousMutation(TrainingRun run,
			bool allowCurrentOwner)
		{
			var mutation = TrainingRun.AcquireMutation(run);
			try
			{
				var current = mutation.Run;
				if (current.IsBusy &&
					!(allowCurrentOwner && ProcessOwnership.IsCurrent(current.Manifest.Owner)))
					throw new InvalidOperationException(
						"Another launcher still owns this continuous experiment.");
				if (!current.IsBusy && current.HasUnresolvedContinuousExperiment &&
					!string.Equals(current.Manifest.Experiment.State,
						TrainingExperimentStates.Aborted, StringComparison.OrdinalIgnoreCase))
					current.ReconcileInterruptedContinuousExperiment();

				ReplaceRunReference(run, current);
				return mutation;
			}
			catch
			{
				mutation.Dispose();
				throw;
			}
		}

		void EnsureWorkspaceMutation(TrainingRun run)
			=> EnsureWorkspaceMutation(run.Manifest.BotDirectory);

		void EnsureWorkspaceMutation(string workspace)
		{
			workspace = Path.GetFullPath(workspace);
			if (activeWorkspaceMutation != null)
			{
				if (!SamePath(activeWorkspaceMutation.WorkspaceRoot, workspace))
					throw new InvalidOperationException(
						"Another bot workspace is already locked by this launcher.");
				return;
			}

			activeWorkspaceMutation = TrainingWorkspaceMutation.Acquire(workspace);
		}

		void ReleaseWorkspaceMutation()
		{
			activeWorkspaceMutation?.Dispose();
			activeWorkspaceMutation = null;
		}

		void ClearContinuousState()
		{
			continuousLoop.Stop();
			pendingContinuousAction = ContinuousTrainingAction.None;
			continuousCandidateRun = null;
			continuousEvaluationPlan = null;
			ReleaseWorkspaceMutation();
		}

		void ReplaceRunReference(TrainingRun previous, TrainingRun current)
		{
			if (SamePath(lastRun?.RunDirectory, previous?.RunDirectory))
				lastRun = current;
			if (SamePath(trainingRun?.RunDirectory, previous?.RunDirectory))
				trainingRun = current;
			if (SamePath(activeTrainingRun?.RunDirectory, previous?.RunDirectory))
				activeTrainingRun = current;
			if (SamePath(continuousCandidateRun?.RunDirectory, previous?.RunDirectory))
				continuousCandidateRun = current;
			var key = Path.GetFullPath(current.RunDirectory);
			if (conversations.TryGetValue(key, out var thread))
				thread.Rebind(current);
			improvementWindow?.RebindRun(previous, current);
			loadedHistory = loadedHistory.WithRun(current);
		}

		bool ResumeContinuousEvaluation(TrainingRun run)
		{
			try
			{
				using (var mutation = LockForContinuousMutation(
					run, allowCurrentOwner: false))
				{
					run = mutation.Run;
					run.ResumeContinuousExperiment();
				}
				if (continuousLoop.ResumeEvaluation(enabled: true) !=
					ContinuousTrainingAction.Evaluate)
					throw new InvalidOperationException(
						"The continuous state machine could not resume evaluation.");

				ClearOutput();
				queue.Clear();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = run;
				continuousEvaluationPlan = null;
				lastRun = run;
				trainingRun = run;
				settings.LastTrainingRunDirectory = run.RunDirectory;
				settings.SelectedTrainingRunDirectory = run.RunDirectory;
				PersistSettings();
				Status("Resuming the unresolved continuous candidate before any new fight...");
				StartContinuousEvaluation();
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				AbortUnresolvedContinuousExperiment(
					"Recovery failed before the interrupted candidate could be reevaluated.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				ReportAutomationFailure(
					"Could not resume the unresolved continuous candidate: " + ex.Message,
					MessageBoxIcon.Error, automatic: false);
				return false;
			}
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
					"-DecisionTrace", activeRun.DecisionTracePath,

					// Map dimensions, spawn cells, resource cells and the effective random seed
					// are resolved at world load and cannot be recovered from the event stream
					// afterwards, so the engine has to be asked for them while the match exists.
					"-MapFacts", activeRun.MapFactsPath
				]);

				if (IsHeadlessSelected())
					arguments.AddRange(
					[
						"-CancellationFile", activeRun.CancellationPath,
						"-PerformanceReport", activeRun.PerformancePath
					]);
			}

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
			}, trainingRunsRoot);
			if (continuousLoop.IsRunning)
			{
				EnsureWorkspaceMutation(activeRun);
				continuousPromotion.CaptureChampion(activeRun);
			}
			lastRun = activeRun;
			trainingRun = activeRun;
			settings.LastTrainingRunDirectory = activeRun.RunDirectory;
			settings.SelectedTrainingRunDirectory = activeRun.RunDirectory;
			PersistSettings();
			battleStartedUtc = DateTime.UtcNow;
			replayBeforeBattle = NewestReplay()?.FullName;

			battleRunning = true;
			battleFinished = false;

			matchLog.Watch(activeRun.TelemetryPath);
			battleLog.Watch(activeRun.BattleLogPath);

			ShowResultsWindow().ShowBattle();
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
					using (var workspaceMutation =
						TrainingRun.AcquireWorkspaceMutation(finishedRun))
					using (var runMutation = TrainingRun.AcquireMutation(finishedRun))
					{
						var current = runMutation.Run;
						if (current.IsBusy &&
							!ProcessOwnership.IsCurrent(current.Manifest.Owner))
							throw new InvalidOperationException(
								"Another launcher owns this training run.");

						current.Finish(status, matchLog, battleLog);
						var replay = NewestReplay();
						var isNew = replay != null &&
							(!string.Equals(replay.FullName, replayBeforeBattle,
									StringComparison.OrdinalIgnoreCase) ||
								replay.LastWriteTimeUtc >= battleStartedUtc.AddSeconds(-2));
						current.CaptureReplay(isNew ? replay.FullName : null);
						ReplaceRunReference(finishedRun, current);
						finishedRun = current;
					}

					if (finishedRun.IsEditable &&
						File.Exists(finishedRun.BattleLogPath) &&
						File.Exists(finishedRun.TelemetryPath) &&
						File.Exists(finishedRun.DecisionTracePath))
						finishedRun = EnsureAgentContext(finishedRun);
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
				PersistSettings();
				RefreshResultsHistory();
			}

			if (!continuousLoop.IsRunning)
				ReleaseWorkspaceMutation();
		}

		/// <summary>Reads whatever the running game has written since last time.</summary>
		void PollBattle(bool finished)
		{
			if ((matchLog.Refresh() || finished) && resultsWindow != null)
				resultsWindow.Redraw(finished);

			if ((battleLog.Refresh() || finished) && outputWindow != null)
				outputWindow.ShowBattle(finished);
		}

		bool ImproveBot(bool automatic = false, bool feedbackReviewed = false)
		{
			var run = automatic ? lastRun : trainingRun;
			var previousAgent = run?.Manifest.Agent;
			if (run?.HasUnresolvedContinuousExperiment == true)
			{
				ReportAutomationFailure(
					"This continuous candidate still needs reevaluation or an explicit snapshot restore before another improvement.",
					MessageBoxIcon.Warning, automatic);
				return false;
			}
			if (runner.IsRunning || activeJob != null || battleRunning ||
				repo == null || !RunMatchesSelectedBot(run) || run?.CanImprove != true)
			{
				ReportAutomationFailure(
					"The selected battle is not eligible for another agent improvement, or an operation is already running.",
					MessageBoxIcon.Error, automatic);
				return false;
			}

			if (!automatic && !feedbackReviewed && run.HasRecordedBattle &&
				!run.HasPlayerFeedback && !ReviewBattleFeedback(run, beforeImprovement: true))
				return false;

			TrainingRunMutation agentMutation = null;
			var hadWorkspaceMutation = activeWorkspaceMutation != null;
			try
			{
				EnsureWorkspaceMutation(run);
				agentMutation = LockForContinuousMutation(
					run, allowCurrentOwner: false);
				run = agentMutation.Run;
				previousAgent = run.Manifest.Agent;
				if (!RunMatchesSelectedBot(run) || !run.CanImprove)
					throw new InvalidOperationException(
						"The selected battle changed and is no longer eligible for improvement.");
				if (automatic && !File.Exists(run.SnapshotManifestPath))
					throw new InvalidOperationException(
						"The continuous champion snapshot was not captured before the battle.");
				if (!automatic && !File.Exists(run.SnapshotManifestPath))
					WorkspaceSnapshot.Capture(run);

				if (!File.Exists(run.GameRulesPath))
					AgentRulesExporter.Export(repo, run.GameRulesPath);

				var previousCancelled = previousAgent is { Cancelled: true };
				var recovering = !previousCancelled &&
					previousAgent?.ExitCode is int failedExitCode &&
					failedExitCode != 0 && previousAgent.RestoredUtc == null;

				// A stopped attempt is archived too. Its transcript is the only record of how far
				// it got, and AgentStarted deletes the live one.
				var archivedTranscript = recovering || previousCancelled
					? run.ArchiveAgentAttempt()
					: null;
				var recoveryContext = recovering
					? TrainingAgent.BuildRecoveryContext(previousAgent, archivedTranscript)
					: TrainingAgent.BuildCancellationContext(previousAgent, archivedTranscript);
				TrainingAgent.Prepare(run, repo.AgentGameGuide, repo.AgentMechanics, run.GameRulesPath,
					CurrentPromptTemplate(), settings.AgentCommand, settings.AgentArguments,
					settings.AgentStdin, recoveryContext);
				if (automatic)
					run.ContinuousAgentStarted(
						settings.AgentCommand, archivedTranscript, recovering);
				else
					run.AgentStarted(settings.AgentCommand, archivedTranscript, recovering);
			}
			catch (InvalidOperationException ex)
			{
				if (!hadWorkspaceMutation)
					ReleaseWorkspaceMutation();
				ReportAutomationFailure(ex.Message, MessageBoxIcon.Warning, automatic);
				return false;
			}
			catch (IOException ex)
			{
				if (!hadWorkspaceMutation)
					ReleaseWorkspaceMutation();
				ReportAutomationFailure($"Could not prepare the improvement: {ex.Message}",
					MessageBoxIcon.Error, automatic);
				return false;
			}
			catch (UnauthorizedAccessException ex)
			{
				if (!hadWorkspaceMutation)
					ReleaseWorkspaceMutation();
				ReportAutomationFailure($"Could not prepare the improvement: {ex.Message}",
					MessageBoxIcon.Error, automatic);
				return false;
			}
			finally
			{
				agentMutation?.Dispose();
			}

			trainingRun = run;
			activeTrainingRun = run;
			Save();
			ShowImprovementWindow().StartAgentRun(run);
			queue.Clear();
			queue.Enqueue(new ScriptJob
			{
				Title = $"Analyzing battle {run.Manifest.CreatedUtc.ToLocalTime():g} and improving the bot",
				ScriptPath = repo.TrainBotScript,
				Arguments =
				[
					"-BattleBot", run.Manifest.BotProject,
					"-RunDirectory", run.RunDirectory,
					"-AgentConfiguration", run.AgentConfigurationPath
				],
				PreserveColor = true,
				Kind = ScriptJobKind.Improvement,
				Output = AppendImprovementOutput,
				Completed = code => FinishImprovement(run, code)
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

		void FinishImprovement(TrainingRun run, int exitCode)
		{
			var continuousAttempt =
				continuousLoop.Stage == ContinuousTrainingStage.Improving;
			if (SamePath(activeTrainingRun?.RunDirectory, run.RunDirectory))
				activeTrainingRun = null;

			TrainingAgentExecutionStatus status = null;
			try
			{
				status = run.ReadAgentStatus();
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

			// stopRequested is still set: JobFinished clears it only after this runs. Killing a
			// process tree returns an arbitrary exit code, so this flag is the only evidence that
			// the player asked for the stop rather than something breaking.
			//
			// Stopping a repair is not the same as stopping an improvement. The fault it was sent
			// to fix is still there, so that stays recorded as a failure — otherwise the next
			// round is told everything is fine and the original fault is quietly forgotten.
			var repairing = run.Manifest.Agent?.Repairing == true ||
				string.Equals(run.Manifest.Status, "verifying", StringComparison.Ordinal);
			var cancelled = failed && stopRequested && !repairing;
			var failurePhase = failed ? cancelled ? "cancelled" : status?.Phase ?? "process" : null;
			var failureMessage = failed
				? cancelled
					? "The improvement was stopped from the launcher before it finished."
					: status?.Message ?? $"Improvement process exited with code {exitCode}."
				: null;
			var suggestedNextPrompt = !failed
				? TrainingAgent.FindSuggestedNextPrompt(runner.LastOutput, run)
				: null;
			try
			{
				var changes = WorkspaceSnapshot.Compare(run);
				var current = TrainingRun.FinishLatestAgent(run, exitCode,
					changes.Count, suggestedNextPrompt, failurePhase,
					failureMessage, cancelled);
				ReplaceRunReference(run, current);
				run = current;
			}
			catch (InvalidDataException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				var current = TrainingRun.FinishLatestAgent(run, exitCode,
					TrainingAgentResult.UnknownChangeCount, suggestedNextPrompt,
					failurePhase, failureMessage, cancelled);
				ReplaceRunReference(run, current);
				run = current;
			}
			catch (IOException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				var current = TrainingRun.FinishLatestAgent(run, exitCode,
					TrainingAgentResult.UnknownChangeCount, suggestedNextPrompt,
					failurePhase, failureMessage, cancelled);
				ReplaceRunReference(run, current);
				run = current;
			}
			catch (System.Text.Json.JsonException ex)
			{
				AppendImprovementOutput($"Could not record the agent's changes: {ex.Message}");
				var current = TrainingRun.FinishLatestAgent(run, exitCode,
					TrainingAgentResult.UnknownChangeCount, suggestedNextPrompt,
					failurePhase, failureMessage, cancelled);
				ReplaceRunReference(run, current);
				run = current;
			}

			// Deliberately outside the block above: this only draws what was just recorded, and a
			// display fault must not be able to rewrite the durable change count with a zero.
			if (!closing && improvementWindow != null)
			{
				improvementWindow.CurrentPromptTemplate = settings.AgentPromptTemplate;
				improvementWindow.CompleteAgentRun(run, reviewNextPrompt: !continuousLoop.IsRunning);
			}

			if (continuousLoop.Stage == ContinuousTrainingStage.Improving)
			{
				var action = continuousLoop.ImprovementCompleted(exitCode == 0);
				if (action == ContinuousTrainingAction.Evaluate)
				{
					continuousCandidateRun = run;
					pendingContinuousAction = action;
					if (!string.IsNullOrWhiteSpace(suggestedNextPrompt))
						AppendImprovementOutput(
							"The next-prompt draft was saved for manual review; continuous mode kept the current prompt.");
				}
				else if (action == ContinuousTrainingAction.Restore)
				{
					try
					{
						var completion = continuousPromotion.RecordFailedCandidate(run,
							failureMessage ?? "The candidate improvement failed before paired evaluation.");
						var decision = continuousPromotion.ApplyDecision(run, null,
							completion.Evaluation);
						if (decision == ContinuousEvaluationDecision.Reevaluate)
							AppendImprovementOutput(
								"The failed candidate was not restored because the live workspace changed.");
						else
						{
							continuousLoop.RestorationCompleted();
							AppendImprovementOutput(
								"The failed continuous candidate was restored to its pre-agent champion snapshot.");
						}
					}
					catch (Exception ex) when (ex is InvalidDataException or IOException or
						UnauthorizedAccessException or InvalidOperationException)
					{
						continuousEvaluationPlan = null;
						if (SamePath(activeTrainingRun?.RunDirectory, run.RunDirectory))
							activeTrainingRun = null;
						AbortUnresolvedContinuousExperiment(
							"The failed continuous candidate could not be restored.");
						continuousLoop.Stop();
						ReleaseWorkspaceMutation();
						AppendImprovementOutput(
							"Could not restore the failed continuous candidate: " + ex.Message);
					}
				}
			}

			RefreshFeedbackRun(run);
			if (!continuousAttempt || exitCode != 0)
				ReleaseWorkspaceMutation();
		}

		void StartContinuousEvaluation()
		{
			var run = continuousCandidateRun;
			if (run == null || continuousLoop.Stage != ContinuousTrainingStage.Evaluating)
			{
				AbortUnresolvedContinuousExperiment(
					"Continuous evaluation could not resume its in-memory state.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				Status("Continuous improvement stopped before paired evaluation could start.");
				UpdateEnabledState();
				return;
			}

			try
			{
				EnsureWorkspaceMutation(run);
				continuousEvaluationPlan = continuousPromotion.PrepareEvaluation(repo, run,
					settings.ContinuousBenchmark,
					settings.ContinuousBenchmarkDifficulty);
				run = continuousEvaluationPlan.Run;
				continuousCandidateRun = run;
				activeTrainingRun = run;
				queue.Clear();
				EnqueueContinuousStep(
					continuousPromotion.BuildArm(repo, continuousEvaluationPlan,
						ContinuousEvaluationArm.Candidate),
					code => FinishContinuousArmBuild(run,
						ContinuousEvaluationArm.Candidate, code));
				RunNext();
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or
				UnauthorizedAccessException or InvalidOperationException)
			{
				continuousEvaluationPlan = null;
				if (SamePath(activeTrainingRun?.RunDirectory, run.RunDirectory))
					activeTrainingRun = null;
				try
				{
					continuousPromotion.InvalidateEvaluation(run,
						"Paired evaluation preparation failed: " + ex.Message);
				}
				catch (Exception saveError) when (saveError is IOException or
					UnauthorizedAccessException or InvalidOperationException)
				{
					AppendImprovementOutput(
						"Could not persist evaluation invalidation: " + saveError.Message);
				}
				AbortUnresolvedContinuousExperiment(
					"Paired evaluation setup failed before a durable decision.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				AppendImprovementOutput("Could not prepare paired evaluation: " + ex.Message);
				Status("Continuous improvement stopped before paired evaluation could start.");
				UpdateEnabledState();
			}
		}

		void FinishContinuousArmBuild(TrainingRun run, ContinuousEvaluationArm arm,
			int exitCode)
		{
			if (exitCode != 0)
			{
				FinishContinuousFailure(run,
					$"The immutable {arm.ToString().ToLowerInvariant()} build exited with code {exitCode}.");
				return;
			}

			try
			{
				continuousPromotion.CaptureBuiltArm(run, continuousEvaluationPlan, arm);
				if (arm == ContinuousEvaluationArm.Candidate)
					EnqueueContinuousStep(
						continuousPromotion.BuildArm(repo, continuousEvaluationPlan,
							ContinuousEvaluationArm.Control),
						code => FinishContinuousArmBuild(run,
							ContinuousEvaluationArm.Control, code));
				else
					EnqueueContinuousStep(
						continuousPromotion.BenchmarkArm(repo, run,
							continuousEvaluationPlan, ContinuousEvaluationArm.Candidate),
						code => FinishContinuousArmBenchmark(run,
							ContinuousEvaluationArm.Candidate, code));
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or
				UnauthorizedAccessException or InvalidOperationException)
			{
				FinishContinuousFailure(run,
					$"Could not capture the immutable {arm.ToString().ToLowerInvariant()} arm: " +
					ex.Message);
			}
		}

		void FinishContinuousArmBenchmark(TrainingRun run, ContinuousEvaluationArm arm,
			int exitCode)
		{
			if (exitCode != 0)
			{
				FinishContinuousFailure(run,
					$"The immutable {arm.ToString().ToLowerInvariant()} benchmark exited with code {exitCode}.");
				return;
			}

			try
			{
				if (arm == ContinuousEvaluationArm.Candidate)
					EnqueueContinuousStep(
						continuousPromotion.BenchmarkArm(repo, run,
							continuousEvaluationPlan, ContinuousEvaluationArm.Control),
						code => FinishContinuousArmBenchmark(run,
							ContinuousEvaluationArm.Control, code));
				else
				{
					if (SamePath(activeTrainingRun?.RunDirectory, run.RunDirectory))
						activeTrainingRun = null;
					FinishContinuousEvaluation(run,
						continuousPromotion.CompleteEvaluation(run,
							continuousEvaluationPlan, exitCode));
				}
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or
				UnauthorizedAccessException or InvalidOperationException)
			{
				FinishContinuousFailure(run,
					$"Could not continue the immutable {arm.ToString().ToLowerInvariant()} benchmark: " +
					ex.Message);
			}
		}

		void FinishContinuousFailure(TrainingRun run, string reason)
		{
			if (SamePath(activeTrainingRun?.RunDirectory, run.RunDirectory))
				activeTrainingRun = null;

			try
			{
				FinishContinuousEvaluation(run,
					continuousPromotion.FailEvaluation(run, continuousEvaluationPlan, reason));
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or
				UnauthorizedAccessException or InvalidOperationException)
			{
				AbortUnresolvedContinuousExperiment(
					"The failed paired evaluation could not be settled.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousEvaluationPlan = null;
				AppendImprovementOutput("Could not settle the failed paired evaluation: " + ex.Message);
			}
		}

		void FinishContinuousEvaluation(TrainingRun run,
			ContinuousEvaluationCompletion completion)
		{
			if (completion.RequiresReevaluation)
			{
				pendingContinuousAction = continuousLoop.EvaluationCompleted(
					ContinuousEvaluationDecision.Reevaluate);
				continuousCandidateRun = run;
				continuousEvaluationPlan = null;
				AppendImprovementOutput(
					"Paired evaluation was invalidated and will be rerun: " +
					completion.InvalidationReason);
				RefreshFeedbackRun(run);
				return;
			}

			try
			{
				var decision = continuousPromotion.ApplyDecision(run,
					continuousEvaluationPlan, completion.Evaluation);
				if (decision == ContinuousEvaluationDecision.Reevaluate)
				{
					pendingContinuousAction = continuousLoop.EvaluationCompleted(decision);
					continuousCandidateRun = run;
					continuousEvaluationPlan = null;
					AppendImprovementOutput(
						"The live workspace changed before the decision could be applied; reevaluating it.");
					RefreshFeedbackRun(run);
					return;
				}

				var action = continuousLoop.EvaluationCompleted(decision);
				if (action == ContinuousTrainingAction.Restore)
					action = continuousLoop.RestorationCompleted();

				pendingContinuousAction = action;
				continuousCandidateRun = run;
				continuousEvaluationPlan = null;
				AppendImprovementOutput(completion.Evaluation.CanPromote
					? "Paired evaluation promoted the immutable candidate."
					: "Paired evaluation did not promote the candidate; the pre-agent champion was restored. " +
						completion.Evaluation.Reason);
				RefreshFeedbackRun(run);
				if (!continuousLoop.IsRunning)
					ReleaseWorkspaceMutation();
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or
				UnauthorizedAccessException or InvalidOperationException)
			{
				AbortUnresolvedContinuousExperiment(
					"The paired evaluation decision could not be applied.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousEvaluationPlan = null;
				AppendImprovementOutput("Could not apply the paired evaluation decision: " + ex.Message);
			}
		}

		void EnqueueContinuousStep(ContinuousScriptPlan plan, Action<int> completed)
		{
			queue.Enqueue(new ScriptJob
			{
				Title = plan.Title,
				ScriptPath = plan.ScriptPath,
				Arguments = plan.Arguments,
				PreserveColor = true,
				Kind = ScriptJobKind.Improvement,
				Output = AppendImprovementOutput,
				Completed = completed
			});
		}

		void StartNextContinuousFight()
		{
			try
			{
				if (continuousCandidateRun != null &&
					!continuousPromotion.ValidateForNextFight(
						continuousCandidateRun, out var invalidation))
				{
					var action = continuousLoop.WorkspaceChangedBeforeFight();
					if (action == ContinuousTrainingAction.Evaluate)
					{
						AppendImprovementOutput(
							"Champion fight deferred until the changed workspace is reevaluated: " +
							invalidation);
						StartContinuousEvaluation();
						return;
					}

					AbortUnresolvedContinuousExperiment(
						"The changed workspace could not return to continuous evaluation.");
					continuousLoop.Stop();
					ReleaseWorkspaceMutation();
					Status("Continuous improvement stopped because the changed workspace could not be reevaluated.");
					UpdateEnabledState();
					return;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				AbortUnresolvedContinuousExperiment(
					"The live workspace could not be reconciled before the next fight.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				Status("Continuous improvement stopped before the next fight: " + ex.Message);
				UpdateEnabledState();
				return;
			}

			Status("Promotion decision settled. Starting the next champion fight...");
			if (!Launch(play: true, continuousContinuation: true))
			{
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				Status("Continuous improvement stopped before the next fight could start.");
				UpdateEnabledState();
			}
			else
			{
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
			}
		}

		void RetryVerification()
		{
			var run = trainingRun;
			if (OperationInProgress || repo == null || run?.Manifest.Agent == null ||
				!RunMatchesSelectedBot(run) ||
				!string.Equals(run.Manifest.Agent.FailurePhase, "verification",
					StringComparison.OrdinalIgnoreCase))
				return;

			try
			{
				EnsureWorkspaceMutation(run);
				var current = TrainingRun.StartLatestVerification(run);
				ReplaceRunReference(run, current);
				run = current;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				ReleaseWorkspaceMutation();
				MessageBox.Show(this,
					"Could not start verification: " + ex.Message,
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
			activeTrainingRun = run;
			ShowImprovementWindow().StartVerificationRun(run);
			queue.Clear();
			queue.Enqueue(new ScriptJob
			{
				Title = "Retrying independent verification",
				ScriptPath = repo.VerifyBotScript,
				Arguments =
				[
					"-BattleBot", run.Manifest.BotProject,
					"-RunDirectory", run.RunDirectory
				],
				PreserveColor = true,
				Kind = ScriptJobKind.Improvement,
				Output = AppendImprovementOutput,
				Completed = code => FinishImprovement(run, code)
			});

			RunNext();
		}

		void ReviewImprovement()
		{
			var run = activeTrainingRun ?? trainingRun;
			if (repo == null || run?.IsEditable != true || !RunMatchesSelectedBot(run))
				return;

			try
			{
				if (!File.Exists(run.PromptPath) ||
					!File.Exists(run.GameGuidePath) ||
					!File.Exists(run.GameRulesPath) ||
					!File.Exists(run.FightManifestPath))
					run = EnsureAgentContext(run);

				ShowImprovementWindow().ShowAgentRun(run,
					promptFirst: run.Manifest.Agent == null);
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

		TrainingRun EnsureAgentContext(TrainingRun run)
		{
			using var workspaceMutation = TrainingRun.AcquireWorkspaceMutation(run);
			using var runMutation = TrainingRun.AcquireMutation(run);
			var current = runMutation.Run;
			if (current.IsBusy &&
				!ProcessOwnership.IsCurrent(current.Manifest.Owner))
				throw new InvalidOperationException(
					"Another launcher still owns this training run.");

			if (!File.Exists(current.GameRulesPath))
				AgentRulesExporter.Export(repo, current.GameRulesPath);

			TrainingAgent.PrepareContext(current, repo.AgentGameGuide, repo.AgentMechanics,
				current.GameRulesPath, CurrentPromptTemplate());
			ReplaceRunReference(run, current);
			return current;
		}

		string CurrentPromptTemplate()
		{
			if (!string.IsNullOrWhiteSpace(settings.AgentPromptTemplate))
				return MigrateTemplate(settings.AgentPromptTemplate);

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

			RecordPromptRevision(template, PromptOrigin.Baseline);
			settings.AgentPromptTemplate = template;
			settings.AgentPromptGuidance = null;
			PersistSettings();
			return template;
		}

		/// <summary>
		/// Adds <c>{gameMechanics}</c> to a template saved before the prompt was split into a
		/// gospel half and a learned half.
		/// </summary>
		/// <remarks>
		/// Without this every saved template is suddenly invalid, and the player's evolved prompt —
		/// often the work of many rounds — is refused at the moment they press Improve.
		/// </remarks>
		string MigrateTemplate(string template)
		{
			var migrated = TrainingAgent.EnsureMechanicsPlaceholder(template);
			if (ReferenceEquals(migrated, template))
				return template;

			RecordPromptRevision(migrated, PromptOrigin.Baseline);
			settings.AgentPromptTemplate = migrated;
			PersistSettings();
			return migrated;
		}

		void AcceptNextPrompt(TrainingRun run, string promptTemplate)
		{
			if (run == null)
				return;

			promptTemplate = TrainingAgent.EnsureMechanicsPlaceholder(promptTemplate);
			if (!TrainingAgent.ValidatePromptTemplate(promptTemplate, out var error))
			{
				MessageBox.Show(improvementWindow,
					"The proposed next prompt cannot be saved yet. " + error,
					"AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			var approved = promptTemplate.Trim();
			TrainingRun current;
			try
			{
				current = TrainingRun.AcceptLatestSuggestedNextPrompt(run, approved);
				ReplaceRunReference(run, current);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException or System.Text.Json.JsonException)
			{
				MessageBox.Show(improvementWindow,
					"The prompt decision could not be saved against the latest run state. " +
					ex.Message, "AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			RecordPromptRevision(approved, PromptOrigin.Manual, current);
			settings.AgentPromptTemplate = approved;
			settings.AgentPromptGuidance = null;
			PersistSettings();
			if (improvementWindow != null)
			{
				improvementWindow.ReloadPromptTransition(current);
				improvementWindow.CurrentPromptTemplate = approved;
				improvementWindow.MarkNextPromptSaved();
			}
		}

		/// <summary>
		/// Turns a proposed prompt down, leaving the one in force exactly as it was.
		/// </summary>
		/// <remarks>
		/// Nothing is written to the prompt archive, because nothing was adopted: a rejected
		/// proposal is a fact about the round that made it, and the round's own manifest is where
		/// that belongs. What the player gets back is a prompt lineage in which every revision is
		/// a prompt that was actually used.
		/// </remarks>
		void RejectNextPrompt(TrainingRun run)
		{
			if (run == null)
				return;

			TrainingRun current;
			try
			{
				current = TrainingRun.RejectLatestSuggestedNextPrompt(run);
				ReplaceRunReference(run, current);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException or System.Text.Json.JsonException)
			{
				MessageBox.Show(improvementWindow,
					"The prompt decision could not be saved against the latest run state. " +
					ex.Message, "AutoC&C", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			if (improvementWindow != null)
			{
				improvementWindow.ReloadPromptTransition(current);
				improvementWindow.MarkNextPromptRejected();
			}
		}

		/// <summary>
		/// Keeps the adopted template where the player can still read it a hundred rounds later.
		/// </summary>
		/// <remarks>
		/// This has to run before the caller assigns the new template, because the archive backfills
		/// the outgoing one when it finds itself empty and would otherwise record the replacement as
		/// its own predecessor. Failing to write it is reported and then dropped: the improvement it
		/// belongs to has already succeeded, and refusing to adopt a good prompt over a full disk
		/// would be a worse outcome than an incomplete history.
		/// </remarks>
		void RecordPromptRevision(string template, PromptOrigin origin, TrainingRun run = null)
		{
			if (promptHistory == null)
				return;

			try
			{
				// Players who were already evolving prompts before this archive existed have a
				// current template that came from nowhere on record. Without it there is nothing
				// for the first archived revision to be a change from.
				if (origin != PromptOrigin.Baseline && promptHistory.Read().Revisions.Count == 0)
					promptHistory.Record(settings.AgentPromptTemplate, PromptOrigin.Baseline);

				promptHistory.Record(template, origin, run);
			}
			catch (IOException ex)
			{
				Append("Could not archive the prompt revision: " + ex.Message);
			}
			catch (UnauthorizedAccessException ex)
			{
				Append("Could not archive the prompt revision: " + ex.Message);
			}
		}

		void RestoreIteration()
		{
			var run = trainingRun;
			if (OperationInProgress || run == null || !RunMatchesSelectedBot(run) ||
				(!run.HasUnresolvedContinuousExperiment &&
					(run.Manifest.Agent == null || run.Manifest.Agent.ChangeCount == 0)))
				return;

			var answer = MessageBox.Show(this,
				$"Restore every source file to the snapshot for the battle from {run.Manifest.CreatedUtc.ToLocalTime():g}?\n\n" +
				$"Session: {run.Manifest.Id}\n\nThis replaces the bot's current source with its pre-improvement snapshot.",
				"Restore previous iteration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
			if (answer != DialogResult.Yes)
				return;

			try
			{
				using var mutation = LockForContinuousMutation(
					run, allowCurrentOwner: false);
				run = mutation.Run;
				WorkspaceSnapshot.Restore(run);
				if (File.Exists(run.Manifest.BotProject))
					botBox.Text = run.Manifest.BotProject;
				ShowImprovementWindow().ShowAgentRun(run);
				Status("The previous source iteration has been restored.");
				RefreshFeedbackRun(run);
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
				if (battleRunning)
				{
					battleJustCompleted = true;
					StopWatchingBattle("finished");
				}

				var automatedBattle = continuousLoop.IsRunning;

				// The conversation goes ahead of the next stage. An answer asked for during a
				// round is worth most before the next one starts, not after it has already
				// overwritten the evidence the question was about. Whether a battle had just
				// finished is remembered across the turn, so delivering a message cannot cost
				// continuous training its next step.
				//
				// What the finished work left behind is handed over with it, because the turn
				// takes the status line for as long as it runs. Without this, an improvement that
				// took twenty minutes and succeeded was announced only as another agent being
				// asked something, which is indistinguishable from it never having finished.
				if (StartQueuedConversation(SettledStatus()))
					return;

				var completedBattle = battleJustCompleted;
				battleJustCompleted = false;

				var continuousAction = completedBattle
					? continuousLoop.BattleCompleted()
					: pendingContinuousAction;
				if (!completedBattle)
					pendingContinuousAction = ContinuousTrainingAction.None;

				if (continuousAction == ContinuousTrainingAction.Improve)
				{
					Status("Battle saved. Starting continuous improvement...");
					if (!ImproveBot(automatic: true))
					{
						continuousLoop.Stop();
						ReleaseWorkspaceMutation();
						Status("Continuous improvement stopped before the agent could start.");
						UpdateEnabledState();
					}

					return;
				}

				if (continuousAction == ContinuousTrainingAction.Evaluate)
				{
					Status("Candidate verified. Starting paired benchmark evaluation...");
					StartContinuousEvaluation();
					return;
				}

				if (continuousAction == ContinuousTrainingAction.Fight)
				{
					StartNextContinuousFight();
					return;
				}

				Status("Done.");
				UpdateEnabledState();
				if (completedBattle && BattleFeedback.ShouldPrompt(lastRun, automatedBattle))
				{
					SelectStation(1);
					ReviewBattleFeedback(lastRun);
				}
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
				AbortUnresolvedContinuousExperiment(
					"A continuous operation could not start.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
			}
			catch (InvalidOperationException ex)
			{
				RouteJobOutput(job, $"Could not start PowerShell: {ex.Message}");
				Status("Failed to start.");
				queue.Clear();
				activeJob = null;
				InvokeJobCompleted(job, -1);
				StopWatchingBattle("failed");
				AbortUnresolvedContinuousExperiment(
					"A continuous operation could not start.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
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
				AbortUnresolvedContinuousExperiment(
					"The continuous operation was stopped before a promotion decision.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
				battleJustCompleted = false;
				Status("Stopped.");
				stopRequested = false;
				UpdateEnabledState();
				return;
			}

			// A question that could not be answered is not a reason to tear down a training cycle.
			// It failed on its own and says so in the conversation; the work it interrupted was
			// going to carry on regardless, and cancelling that would punish the player for asking.
			if (exitCode != 0 && completed?.Kind == ScriptJobKind.Chat)
			{
				RouteJobOutput(completed, $"=== Finished with exit code {exitCode} ===");
				InvokeJobCompleted(completed, exitCode);
				Status("The agent could not answer. See the Chat tab in the improvement window.");
				RunNext();
				return;
			}

			if (exitCode != 0)
			{
				RouteJobOutput(completed, $"=== Finished with exit code {exitCode} ===");
				InvokeJobCompleted(completed, exitCode);
				StopWatchingBattle("failed");
				AbortUnresolvedContinuousExperiment(
					"The continuous operation failed before a promotion decision.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				pendingContinuousAction = ContinuousTrainingAction.None;
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
				battleJustCompleted = false;
				Status($"Stopped with exit code {exitCode}. See the {(completed?.Kind == ScriptJobKind.Generic ? "output" : "improvement")} window.");
				queue.Clear();
				if (completed?.Kind == ScriptJobKind.Generic)
					ShowOutputWindow();
				else if (improvementWindow == null && trainingRun != null)
					ShowImprovementWindow().ShowAgentRun(trainingRun);
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
			// Only claim a stop when there is something running to stop. The flag is read by the
			// next JobFinished, so setting it with nothing in flight hands it to whatever starts
			// afterwards: that job would be reported as stopped, and a failed improvement would be
			// recorded as one the player cancelled.
			stopRequested = runner.IsRunning;
			if (!stopRequested)
			{
				AbortUnresolvedContinuousExperiment(
					"The continuous operation was stopped before a promotion decision.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
			}
			pendingContinuousAction = ContinuousTrainingAction.None;
			queue.Clear();

			// Queued questions go with it. They were written for a situation the player has just
			// called off, and delivering them afterwards would answer a conversation that no
			// longer applies.
			ClearQueuedConversation();
			runner.Stop();
			Status("Stopping…");
		}

		void AbortUnresolvedContinuousExperiment(string reason)
		{
			var run = continuousCandidateRun ?? activeTrainingRun ?? trainingRun ?? lastRun;
			if (run == null)
				return;

			try
			{
				TrainingRun current;
				using (var mutation = TrainingRun.AcquireMutation(run))
				{
					current = mutation.Run;
					if (!current.HasUnresolvedContinuousExperiment)
						return;
					if (current.IsBusy &&
						!ProcessOwnership.IsCurrent(current.Manifest.Owner))
					{
						AppendImprovementOutput(
							"Did not abort the continuous experiment because another launcher still owns it.");
						return;
					}

					if (!string.Equals(current.Manifest.Experiment?.State,
						TrainingExperimentStates.Aborted, StringComparison.OrdinalIgnoreCase))
						current.AbortContinuousExperiment(reason);
				}

				ReplaceRunReference(run, current);
				RefreshFeedbackRun(current);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				AppendImprovementOutput(
					"Could not persist the interrupted continuous experiment: " + ex.Message);
			}
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
			if (commandStatus != null)
				commandStatus.Text = message;
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
			settings.ContinuousImprovement = continuousBox.Checked;
			settings.LastTrainingRunDirectory = lastRun?.RunDirectory;
			settings.SelectedTrainingRunDirectory = trainingRun?.RunDirectory;
			PersistSettings();
		}

		void PersistSettings()
		{
			if (persistSettings)
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
				pendingContinuousAction = ContinuousTrainingAction.None;
				runner.Stop();
				InvokeJobCompleted(stoppedJob, -1);
				StopWatchingBattle("stopped");
				AbortUnresolvedContinuousExperiment(
					"The launcher closed before a continuous promotion decision.");
				continuousLoop.Stop();
				ReleaseWorkspaceMutation();
				continuousCandidateRun = null;
				continuousEvaluationPlan = null;
			}

			if (IsHandleCreated)
				taskbarProgress.SetBusy(Handle, busy: false);

			base.OnFormClosing(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				taskbarProgress.Dispose();
				matchTimer?.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
