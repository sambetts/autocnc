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
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Recorded sessions, live battle graphs, trends, and access to per-battle feedback.</summary>
	public sealed class ResultsWindow : BattleWindow
	{
		static readonly (string Heading, int Width)[] SessionColumns =
		[
			("Battle", 55), ("Recorded", 140), ("Result", 80), ("User feedback", 135), ("Duration", 75),
			("Units", 65), ("Army", 80), ("Buildings", 75), ("Base", 80), ("Kills", 65),
			("Losses", 65), ("Cash", 75), ("Execution", 85)
		];

		readonly MatchLog liveLog;
		readonly MatchChart[] battleCharts;
		readonly IterationChart[] iterationCharts;
		readonly ComboBox battlePicker;
		readonly Label result;
		readonly Label historySummary;
		readonly ScoreStrip scores;
		readonly CommandTabs views;
		readonly TabPage historyPage;
		readonly TabPage battlePage;
		readonly ListView sessions;
		readonly Button deleteSession;
		readonly ContextMenuStrip sessionMenu;
		readonly ToolStripMenuItem trainFromBattle;
		readonly ToolStripMenuItem deleteFromMenu;
		TextBox feedbackPreview;
		Button reviewFeedback;
		Button replay;
		Button statistics;
		Label feedbackStatus;

		MatchLog shownLog;
		TrainingHistory history = TrainingHistory.Empty;
		TrainingRun currentRun;
		TrainingRun selectedRun;
		bool operationBusy;
		bool loadingBattles;
		bool followCurrentBattle = true;

		public event Action<TrainingRun> FeedbackRequested;
		public event Action<TrainingRun> ReplayRequested;
		public event Action<TrainingRun> DeleteRequested;
		public event Action<TrainingRun> TrainRequested;

		internal bool CanReviewFeedback => reviewFeedback.Enabled;
		internal bool CanWatchReplay => replay.Enabled;
		internal bool CanDeleteSession => deleteSession.Enabled;
		internal bool CanTrainFromBattle => trainFromBattle.Enabled;
		internal string FeedbackText => feedbackPreview.Text;
		internal string FeedbackActionText => reviewFeedback.Text;
		internal string HistorySummaryText => historySummary.Text;
		internal ListView RecordedSessions => sessions;
		internal TrainingRun SelectedRun => selectedRun;
		internal int SelectedBattleIndex
		{
			get => battlePicker.SelectedIndex;
			set => battlePicker.SelectedIndex = value;
		}
		public ResultsWindow(MatchLog log)
			: base("AutoC&C - Battle results")
		{
			SuspendLayout();
			AutoScaleMode = AutoScaleMode.Dpi;
			AutoScaleDimensions = new SizeF(96, 96);
			liveLog = log;
			shownLog = log;

			var unitsChart = new MatchChart("Units", sample => sample.Units) { Dock = DockStyle.Fill, Log = log };
			var armyChart = new MatchChart("Army value", sample => sample.Army) { Dock = DockStyle.Fill, Log = log };
			var buildingsChart = new MatchChart("Buildings", sample => sample.Buildings) { Dock = DockStyle.Fill, Log = log };
			var baseChart = new MatchChart("Base value", sample => sample.BaseValue) { Dock = DockStyle.Fill, Log = log };
			var killsChart = new MatchChart("Kills", sample => sample.Killed) { Dock = DockStyle.Fill, Log = log };
			battleCharts = [unitsChart, armyChart, buildingsChart, baseChart, killsChart];

			battlePicker = new ComboBox
			{
				DropDownStyle = ComboBoxStyle.DropDownList,
				Dock = DockStyle.Fill,
				AccessibleName = "Selected recorded battle",
				Margin = new Padding(8, 4, 8, 4)
			};
			battlePicker.SelectedIndexChanged += (_, _) => SelectBattle();

			var pickerRow = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				ColumnCount = 2,
				BackColor = Paper,
				Margin = new Padding(0)
			};
			pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			pickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			pickerRow.Controls.Add(new Label
			{
				Text = "Battle:",
				AutoSize = true,
				ForeColor = Faded,
				Margin = new Padding(8, 8, 0, 0)
			}, 0, 0);
			pickerRow.Controls.Add(battlePicker, 1, 0);

			result = new Label
			{
				Dock = DockStyle.Fill,
				AutoSize = false,
				Padding = new Padding(10, 5, 10, 0),
				ForeColor = Faded,
				Text = "No battle selected."
			};

			var battleGrid = ChartGrid();
			battleGrid.Controls.Add(unitsChart, 0, 0);
			battleGrid.Controls.Add(armyChart, 1, 0);
			battleGrid.Controls.Add(buildingsChart, 0, 1);
			battleGrid.Controls.Add(baseChart, 1, 1);
			battleGrid.Controls.Add(killsChart, 0, 2);
			battleGrid.SetColumnSpan(killsChart, 2);

			scores = new ScoreStrip { Dock = DockStyle.Fill, Log = log };

			var battleLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 4,
				BackColor = Paper
			};
			battleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 12));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 3 * Font.Height + 16));
			battleLayout.Controls.Add(pickerRow, 0, 0);
			battleLayout.Controls.Add(result, 0, 1);
			battleLayout.Controls.Add(battleGrid, 0, 2);
			battleLayout.Controls.Add(scores, 0, 3);

			iterationCharts =
			[
				new IterationChart("Final units", iteration => iteration.LocalPlayer.Units,
					iteration => iteration.Opponents.Units) { Dock = DockStyle.Fill },
				new IterationChart("Final army value", iteration => iteration.LocalPlayer.ArmyValue,
					iteration => iteration.Opponents.ArmyValue) { Dock = DockStyle.Fill },
				new IterationChart("Final buildings", iteration => iteration.LocalPlayer.Buildings,
					iteration => iteration.Opponents.Buildings) { Dock = DockStyle.Fill },
				new IterationChart("Final base value", iteration => iteration.LocalPlayer.BaseValue,
					iteration => iteration.Opponents.BaseValue) { Dock = DockStyle.Fill },
				new IterationChart("Final kills", iteration => iteration.LocalPlayer.Killed,
					iteration => iteration.Opponents.Killed) { Dock = DockStyle.Fill },
				new IterationChart("Battle duration", iteration => iteration.DurationSeconds,
					outcomePoints: true) { Dock = DockStyle.Fill }
			];

			var trendGrid = ChartGrid();
			for (var index = 0; index < iterationCharts.Length; index++)
				trendGrid.Controls.Add(iterationCharts[index], index % 2, index / 2);

			historySummary = new Label
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				Padding = new Padding(10, 5, 10, 5),
				ForeColor = Faded,
				Text = "No recorded sessions yet."
			};
			var trendsPage = new TabPage("Trends") { BackColor = Paper, Padding = new Padding(2) };
			trendsPage.Controls.Add(trendGrid);

			sessions = new ListView
			{
				Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
				MultiSelect = false, HideSelection = false, ShowItemToolTips = true,
				BackColor = CommandTheme.Field, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
				AccessibleName = "Recorded battles"
			};
			foreach (var (heading, width) in SessionColumns)
				sessions.Columns.Add(heading, width);
			sessions.SelectedIndexChanged += (_, _) =>
			{
				if (!loadingBattles && sessions.SelectedItems.Count > 0)
					battlePicker.SelectedIndex = ((RecordedBattleChoice)sessions.SelectedItems[0].Tag).Number - 1;
			};
			sessions.ItemActivate += (_, _) => ShowBattle();
			sessionMenu = new ContextMenuStrip();
			trainFromBattle = new ToolStripMenuItem("Train from this battle", null, (_, _) => RequestTraining());
			deleteFromMenu = new ToolStripMenuItem("Delete session", null, (_, _) => RequestSessionDeletion());
			sessionMenu.Items.Add(trainFromBattle);
			sessionMenu.Items.Add(new ToolStripSeparator());
			sessionMenu.Items.Add(deleteFromMenu);
			sessionMenu.Opening += (_, e) =>
			{
				e.Cancel = sessions.SelectedItems.Count == 0;
				UpdateFeedbackState();
			};
			sessions.ContextMenuStrip = sessionMenu;
			sessions.MouseDown += (_, e) =>
			{
				if (e.Button == MouseButtons.Right)
					SelectContextBattle(e.Location);
			};
			historyPage = new TabPage("Recorded sessions") { BackColor = Paper, Padding = new Padding(2) };
			var historyLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0)
			};
			historyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			historyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			var historyActions = new TableLayoutPanel
			{
				AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4)
			};
			historyActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			historyActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			historyActions.Controls.Add(new Label
			{
				Text = "Newest first. Stats are for your bot.",
				AutoSize = true, Dock = DockStyle.Fill, ForeColor = Faded,
				Padding = new Padding(6), TextAlign = ContentAlignment.MiddleLeft
			}, 0, 0);
			deleteSession = new ActionButton
			{
				Text = "&Delete session", ForeColor = CommandTheme.Danger,
				AccessibleName = "Delete selected recorded session",
				AccessibleDescription = "Permanently delete this session and its saved evidence after confirmation.",
				Margin = new Padding(4, 0, 3, 0)
			};
			deleteSession.Click += (_, _) => RequestSessionDeletion();
			historyActions.Controls.Add(deleteSession, 1, 0);
			historyLayout.Controls.Add(historyActions, 0, 0);
			historyLayout.Controls.Add(sessions, 0, 1);
			historyPage.Controls.Add(historyLayout);

			battlePage = new TabPage("Battle charts") { BackColor = Paper, Padding = new Padding(2) };
			battlePage.Controls.Add(battleLayout);
			views = new CommandTabs { Dock = DockStyle.Fill };
			views.TabPages.Add(historyPage);
			views.TabPages.Add(battlePage);
			views.TabPages.Add(trendsPage);
			views.SelectedIndexChanged += (_, _) => UpdateTitle();

			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0)
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(historySummary, 0, 0);
			layout.Controls.Add(views, 0, 1);
			layout.Controls.Add(BuildFeedbackPanel(), 0, 2);
			Controls.Add(layout);
			UpdateFeedbackState();
			ResumeLayout(true);
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);
			ScaleSessionColumns();
		}

		protected override void OnDpiChanged(DpiChangedEventArgs e)
		{
			base.OnDpiChanged(e);
			ScaleSessionColumns();
		}

		void ScaleSessionColumns()
		{
			for (var index = 0; index < SessionColumns.Length; index++)
				sessions.Columns[index].Width = SessionColumns[index].Width * DeviceDpi / 96;
		}

		public void ShowRecordedSessions()
		{
			views.SelectedTab = historyPage;
			sessions.Focus();
			UpdateTitle();
		}

		public void ShowBattle()
		{
			views.SelectedTab = battlePage;
			UpdateTitle();
		}

		public void SetOperationState(bool busy)
		{
			operationBusy = busy;
			UpdateFeedbackState();
		}

		public void SetHistory(TrainingHistory value, TrainingRun current, bool busy)
		{
			var previouslySelected = selectedRun?.RunDirectory;
			var wasFollowingCurrent = followCurrentBattle;
			history = (value ?? TrainingHistory.Empty).WithRun(current);
			currentRun = current;
			operationBusy = busy;
			var runs = history.Runs.ToList();

			loadingBattles = true;
			battlePicker.Items.Clear();
			for (var index = 0; index < runs.Count; index++)
				battlePicker.Items.Add(new RecordedBattleChoice { Number = index + 1, Run = runs[index] });

			var iterations = history.Iterations.ToDictionary(iteration => iteration.Run.RunDirectory,
				StringComparer.OrdinalIgnoreCase);
			sessions.BeginUpdate();
			sessions.Items.Clear();
			for (var index = runs.Count - 1; index >= 0; index--)
			{
				var choice = (RecordedBattleChoice)battlePicker.Items[index];
				var run = choice.Run;
				iterations.TryGetValue(run.RunDirectory, out var iteration);
				var player = iteration?.LocalPlayer;
				var item = new ListViewItem(new[]
				{
					$"#{choice.Number}", run.Manifest.CreatedUtc.ToLocalTime().ToString("g"),
					run.Manifest.Result?.Outcome ?? run.Manifest.Status, BattleFeedback.Status(run),
					run.HasRecordedBattle ? Csv.Clock(run.Manifest.Result.DurationSeconds) : "-",
					player?.Units.ToString() ?? "-", player?.ArmyValue.ToString() ?? "-",
					player?.Buildings.ToString() ?? "-", player?.BaseValue.ToString() ?? "-",
					player?.Killed.ToString() ?? "-", player?.Lost.ToString() ?? "-",
					player?.Cash.ToString() ?? "-", run.IsHeadless ? "Headless" : "Rendered"
				})
				{
					Tag = choice,
					ToolTipText = $"{run.Manifest.Id}\nMap: {run.Manifest.Battle?.Map}\n" +
						$"Difficulty: {run.Manifest.Battle?.Difficulty}; opponents: {run.Manifest.Battle?.Opponents}\n" +
						BattleFeedback.Description(run)
				};
				sessions.Items.Add(item);
			}
			sessions.EndUpdate();

			var wanted = wasFollowingCurrent ? current?.RunDirectory : previouslySelected;
			var selectedIndex = runs.FindIndex(run => SameDirectory(run.RunDirectory, wanted));
			if (selectedIndex < 0)
				selectedIndex = runs.Count - 1;
			battlePicker.SelectedIndex = selectedIndex;
			loadingBattles = false;

			foreach (var chart in iterationCharts)
			{
				chart.Iterations = history.Iterations;
				chart.Invalidate();
			}

			historySummary.Text = HistorySummary(history);
			SelectBattle();
		}

		/// <summary>Called on every poll of the live telemetry file.</summary>
		public void Redraw(bool finished)
		{
			if (!SameRun(selectedRun, currentRun) || shownLog != liveLog)
				return;

			foreach (var chart in battleCharts)
				chart.Invalidate();
			scores.Invalidate();
			result.Text = Summary(shownLog, finished);
			result.ForeColor = shownLog.IsDecided ? Ink : Faded;
			UpdateTitle();
		}

		void UpdateTitle()
		{
			Text = views.SelectedTab == battlePage && selectedRun != null
				? $"AutoC&C - Battle #{battlePicker.SelectedIndex + 1} results"
				: "AutoC&C - History & trends";
			historySummary.Text = HistorySummary(history, includeDurationTrends: views.SelectedTab?.Text == "Trends");
		}

		internal void RequestFeedback()
		{
			if (selectedRun != null && reviewFeedback.Enabled)
				FeedbackRequested?.Invoke(selectedRun);
		}

		internal void RequestSessionDeletion()
		{
			if (selectedRun != null && deleteSession.Enabled)
				DeleteRequested?.Invoke(selectedRun);
		}

		internal void RequestTraining()
		{
			if (selectedRun != null && trainFromBattle.Enabled)
				TrainRequested?.Invoke(selectedRun);
		}

		internal void SelectContextBattle(Point location)
		{
			var item = sessions.GetItemAt(location.X, location.Y);
			sessions.SelectedItems.Clear();
			if (item == null)
				return;
			item.Selected = true;
			item.Focused = true;
			battlePicker.SelectedIndex = ((RecordedBattleChoice)item.Tag).Number - 1;
		}

		Control BuildFeedbackPanel()
		{
			feedbackPreview = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ReadOnly = true,
				ScrollBars = ScrollBars.Vertical,
				BackColor = CommandTheme.Field,
				ForeColor = Ink,
				AccessibleName = "Saved battle feedback",
				Margin = new Padding(0, 4, 0, 8)
			};

			reviewFeedback = new ActionButton
			{
				Text = "Add feedback",
				Primary = true,
				AccessibleName = "Review selected battle feedback"
			};
			reviewFeedback.Click += (_, _) => RequestFeedback();
			replay = new ActionButton { Text = "Watch replay", AccessibleName = "Watch selected battle replay" };
			replay.Click += (_, _) =>
			{
				if (selectedRun != null && replay.Enabled)
					ReplayRequested?.Invoke(selectedRun);
			};
			statistics = new ActionButton { Text = "Battle statistics" };
			statistics.Click += (_, _) => ShowBattle();

			feedbackStatus = new Label
			{
				AutoSize = true,
				Dock = DockStyle.Fill,
				ForeColor = CommandTheme.Green,
				UseMnemonic = false,
				Margin = new Padding(0)
			};

			var actions = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				WrapContents = false,
				Margin = new Padding(0)
			};
			actions.Controls.Add(reviewFeedback);
			actions.Controls.Add(replay);
			actions.Controls.Add(statistics);

			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				ColumnCount = 1,
				RowCount = 3,
				Padding = new Padding(10, 8, 10, 8),
				BackColor = CommandTheme.Surface
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.Controls.Add(feedbackStatus, 0, 0);
			layout.Controls.Add(feedbackPreview, 0, 1);
			layout.Controls.Add(actions, 0, 2);
			return layout;
		}

		void SelectBattle()
		{
			if (loadingBattles)
				return;

			selectedRun = (battlePicker.SelectedItem as RecordedBattleChoice)?.Run;
			if (selectedRun == null)
			{
				ShowLog(new MatchLog());
				result.Text = "No battle selected.";
				UpdateFeedbackState();
				UpdateTitle();
				return;
			}

			followCurrentBattle = currentRun == null
				? battlePicker.SelectedIndex == battlePicker.Items.Count - 1
				: SameRun(selectedRun, currentRun);

			if (SameRun(selectedRun, currentRun) &&
				SameDirectory(liveLog.Path, selectedRun.TelemetryPath))
				ShowLog(liveLog);
			else
			{
				var historical = new MatchLog();
				historical.Watch(selectedRun.TelemetryPath);
				historical.Refresh();
				ShowLog(historical);
			}

			var finished = selectedRun.Manifest.CompletedUtc != null;
			result.Text = shownLog.IsEmpty && selectedRun.HasRecordedBattle
				? $"{selectedRun.Manifest.Result.Outcome ?? "Undecided"} after " +
					$"{Csv.Clock(selectedRun.Manifest.Result.DurationSeconds)}. Final stats are in Recorded sessions; the timeline is unavailable."
				: Summary(shownLog, finished);
			result.ForeColor = shownLog.IsDecided ? Ink : Faded;
			loadingBattles = true;
			foreach (ListViewItem item in sessions.Items)
			{
				item.Selected = SameRun(((RecordedBattleChoice)item.Tag).Run, selectedRun);
				if (item.Selected && sessions.IsHandleCreated)
					item.EnsureVisible();
			}
			loadingBattles = false;
			UpdateFeedbackState();
			UpdateTitle();
		}

		void ShowLog(MatchLog log)
		{
			shownLog = log;
			foreach (var chart in battleCharts)
			{
				chart.Log = log;
				chart.Invalidate();
			}

			scores.Log = log;
			scores.Invalidate();
		}

		void UpdateFeedbackState()
		{
			reviewFeedback.Text = BattleFeedback.ActionText(selectedRun);
			reviewFeedback.Enabled = !operationBusy && BattleFeedback.CanReview(selectedRun);
			replay.Enabled = !operationBusy && selectedRun?.HasRecordedBattle == true &&
				File.Exists(selectedRun.ReplayPath);
			statistics.Enabled = selectedRun != null;
			deleteSession.Enabled = !operationBusy && selectedRun?.CanDelete == true;
			deleteFromMenu.Enabled = deleteSession.Enabled;
			trainFromBattle.Enabled = !operationBusy && selectedRun?.HasImprovementEvidence == true;
			feedbackStatus.Text = (selectedRun == null ? "" : $"Battle #{battlePicker.SelectedIndex + 1} / ") +
				BattleFeedback.Status(selectedRun) + (operationBusy ? " / Operation in progress" : "");
			feedbackStatus.ForeColor = selectedRun?.HasPlayerFeedback == true ? CommandTheme.Green : CommandTheme.Amber;
			feedbackPreview.Text = selectedRun?.HasPlayerFeedback == true
				? selectedRun.Manifest.Result.PlayerFeedback : BattleFeedback.Description(selectedRun, operationBusy);
		}

		static string Summary(MatchLog log, bool finished)
		{
			if (log.IsEmpty)
				return finished
					? "No match was recorded. If the doctrine failed to build, the output window says why."
					: "Waiting for the battle to start...";

			var clock = Csv.Clock(log.Duration);
			var won = log.Players.Where(player =>
				string.Equals(player.Outcome, "Won", StringComparison.OrdinalIgnoreCase)).ToList();
			if (won.Count > 0)
				return $"{string.Join(", ", won.Select(player => player.Label))} won at {clock}.";
			if (log.IsDecided)
				return $"Everybody lost by {clock} - a draw, or the last side standing quit.";
			return finished
				? $"Stopped at {clock} with the match undecided."
				: $"Battle in progress - {clock}.";
		}

		static string HistorySummary(TrainingHistory history, bool includeDurationTrends = false)
		{
			var wins = history.Iterations.Where(iteration =>
				string.Equals(iteration.Outcome, "Won", StringComparison.OrdinalIgnoreCase)).ToList();
			var losses = history.Iterations.Where(iteration =>
				string.Equals(iteration.Outcome, "Lost", StringComparison.OrdinalIgnoreCase)).ToList();
			var parts = new List<string>
			{
				$"{history.Runs.Count} recorded session(s): {wins.Count} won, {losses.Count} lost."
			};
			var completed = history.Runs.Where(run => run.HasRecordedBattle).ToList();
			var provided = completed.Count(run => run.HasPlayerFeedback);
			parts.Add($"Your feedback: {provided} provided, {completed.Count - provided} missing.");
			if (includeDurationTrends)
			{
				AddDurationTrend(parts, "Wins", wins);
				AddDurationTrend(parts, "Losses", losses);
			}
			if (history.Warnings.Count > 0)
				parts.Add($"{history.Warnings.Count} unreadable run(s) skipped.");
			return string.Join("  ", parts);
		}

		static void AddDurationTrend(List<string> parts, string label, IReadOnlyList<TrainingIteration> iterations)
		{
			if (iterations.Count < 2)
				return;

			var first = iterations[0].DurationSeconds;
			var last = iterations[^1].DurationSeconds;
			var direction = last < first ? "faster" : last > first ? "slower" : "unchanged";
			parts.Add($"{label}: {Csv.Clock(first)} -> {Csv.Clock(last)} ({direction}).");
		}

		static TableLayoutPanel ChartGrid()
		{
			var grid = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				RowCount = 3,
				BackColor = Paper
			};
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			for (var index = 0; index < 3; index++)
				grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
			return grid;
		}

		static bool SameRun(TrainingRun left, TrainingRun right) =>
			left != null && right != null && SameDirectory(left.RunDirectory, right.RunDirectory);

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				sessionMenu?.Dispose();
			base.Dispose(disposing);
		}

		static bool SameDirectory(string left, string right)
		{
			if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
				return false;

			try
			{
				return string.Equals(
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
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

		sealed class ScoreStrip : Control
		{
			static readonly string[] Headings = ["Player", "Units", "Army", "Buildings", "Killed", "Lost", "Cash", "Result"];

			public ScoreStrip()
			{
				DoubleBuffered = true;
				BackColor = Paper;
				ForeColor = Ink;
			}

			public MatchLog Log { get; set; }

			protected override void OnPaint(PaintEventArgs e)
			{
				var g = e.Graphics;
				g.Clear(Paper);
				var players = Log?.Players.Where(player => player.Samples.Count > 0).ToList() ?? [];
				if (players.Count == 0)
					return;

				using var bold = new Font(Font, FontStyle.Bold);
				using var faded = new SolidBrush(Faded);
				using var rule = new Pen(Rule);
				var line = Font.Height;
				var nameWidth = Math.Clamp(Width / 4, 90, 220);
				var columnWidth = Math.Max(52, (Width - nameWidth - 16) / (Headings.Length - 1));
				float Column(int index) => 8 + (index == 0 ? 0 : nameWidth + (index - 1) * columnWidth);

				for (var index = 0; index < Headings.Length; index++)
					g.DrawString(Headings[index], Font, faded, Column(index), 2);
				g.DrawLine(rule, 0, line + 4, Width, line + 4);
				var rows = Math.Max(1, (Height - line - 8) / line);

				for (var row = 0; row < Math.Min(rows, players.Count); row++)
				{
					var player = players[row];
					var last = player.Samples[^1];
					var y = line + 8 + row * line;
					using var brush = new SolidBrush(player.Colour);
					using var clipped = new StringFormat(StringFormatFlags.NoWrap)
					{
						Trimming = StringTrimming.EllipsisCharacter
					};

					g.DrawString(player.Label, Font, brush,
						new RectangleF(Column(0), y, nameWidth - 8, line), clipped);
					var cells = new[]
					{
						last.Units.ToString(),
						last.Army.ToString(),
						last.Buildings.ToString(),
						last.Killed.ToString(),
						last.Lost.ToString(),
						last.Cash.ToString(),
						player.HasResult ? player.Outcome : "-"
					};
					for (var index = 0; index < cells.Length; index++)
						g.DrawString(cells[index],
							player.HasResult && index == cells.Length - 1 ? bold : Font,
							brush, Column(index + 1), y);
				}
			}
		}
	}
}
