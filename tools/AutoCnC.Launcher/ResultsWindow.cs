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
	/// <summary>Live battle graphs, historical KPI trends, and per-battle player feedback.</summary>
	public sealed class ResultsWindow : BattleWindow
	{
		sealed class BattleChoice
		{
			public int Number { get; init; }
			public TrainingRun Run { get; init; }

			public override string ToString()
			{
				var result = Run.Manifest.Result;
				var outcome = result?.Outcome ?? (Run.Manifest.CompletedUtc == null ? "running" : Run.Manifest.Status);
				var duration = result == null ? "" : $" in {Csv.Clock(result.DurationSeconds)}";
				var feedback = string.IsNullOrWhiteSpace(result?.PlayerFeedback) ? "" : " [feedback]";
				return $"#{Number}  {Run.Manifest.CreatedUtc.ToLocalTime():g}  {outcome}{duration}{feedback}";
			}
		}

		readonly MatchLog liveLog;
		readonly MatchChart[] battleCharts;
		readonly IterationChart[] iterationCharts;
		readonly ComboBox battlePicker;
		readonly Label result;
		readonly Label historySummary;
		readonly ScoreStrip scores;
		TextBox feedback;
		Button saveFeedback;
		Label feedbackStatus;

		MatchLog shownLog;
		TrainingHistory history = TrainingHistory.Empty;
		TrainingRun currentRun;
		TrainingRun selectedRun;
		bool continuousMode;
		bool loadingBattles;
		bool followCurrentBattle = true;

		public event Action<TrainingRun, string> PlayerFeedbackSaved;

		internal bool CanSaveFeedback => saveFeedback.Enabled;
		internal string FeedbackText => feedback.Text;
		internal string HistorySummaryText => historySummary.Text;
		internal int SelectedBattleIndex
		{
			get => battlePicker.SelectedIndex;
			set => battlePicker.SelectedIndex = value;
		}
		internal void SetFeedbackText(string value) => feedback.Text = value;

		public ResultsWindow(MatchLog log)
			: base("AutoC&C - Battle results")
		{
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
				Width = 480,
				Margin = new Padding(8, 4, 8, 4)
			};
			battlePicker.SelectedIndexChanged += (_, _) => SelectBattle();

			var pickerRow = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				BackColor = Paper,
				Margin = new Padding(0)
			};
			pickerRow.Controls.Add(new Label
			{
				Text = "Battle:",
				AutoSize = true,
				ForeColor = Faded,
				Margin = new Padding(8, 8, 0, 0)
			});
			pickerRow.Controls.Add(battlePicker);

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
			var feedbackPanel = BuildFeedbackPanel();

			var battleLayout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 5,
				BackColor = Paper
			};
			battleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 12));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 3 * Font.Height + 16));
			battleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 4 * Font.Height + 28));
			battleLayout.Controls.Add(pickerRow, 0, 0);
			battleLayout.Controls.Add(result, 0, 1);
			battleLayout.Controls.Add(battleGrid, 0, 2);
			battleLayout.Controls.Add(scores, 0, 3);
			battleLayout.Controls.Add(feedbackPanel, 0, 4);

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
				Dock = DockStyle.Top,
				AutoSize = false,
				Height = 2 * Font.Height + 12,
				Padding = new Padding(10, 5, 10, 0),
				ForeColor = Faded,
				Text = "No completed battles yet."
			};
			var trendsPage = new TabPage("Trends") { BackColor = Paper, Padding = new Padding(2) };
			trendsPage.Controls.Add(trendGrid);
			trendsPage.Controls.Add(historySummary);

			var battlePage = new TabPage("Battle") { BackColor = Paper, Padding = new Padding(2) };
			battlePage.Controls.Add(battleLayout);
			var views = new CommandTabs { Dock = DockStyle.Fill };
			views.TabPages.Add(battlePage);
			views.TabPages.Add(trendsPage);
			Controls.Add(views);
		}

		public void SetHistory(TrainingHistory value, TrainingRun current, bool continuous)
		{
			var previouslySelected = selectedRun?.RunDirectory;
			var wasFollowingCurrent = followCurrentBattle;
			var draftFeedback = feedback.Text;
			var hadDraftFeedback = saveFeedback.Enabled;
			history = value ?? TrainingHistory.Empty;
			currentRun = current;
			continuousMode = continuous;

			var runs = history.Runs
				.Select(run => SameRun(run, current) ? current : run)
				.Where(run => run != null)
				.ToList();
			if (current != null && !runs.Any(run => SameRun(run, current)))
			{
				runs.Add(current);
				runs.Sort((left, right) => left.Manifest.CreatedUtc.CompareTo(right.Manifest.CreatedUtc));
			}

			loadingBattles = true;
			battlePicker.Items.Clear();
			for (var index = 0; index < runs.Count; index++)
				battlePicker.Items.Add(new BattleChoice { Number = index + 1, Run = runs[index] });

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
			if (hadDraftFeedback && SameDirectory(selectedRun?.RunDirectory, previouslySelected))
				feedback.Text = draftFeedback;
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
			Text = finished ? "AutoC&C - Battle results (finished)" : "AutoC&C - Battle results (running)";
		}

		public void MarkFeedbackSaved(TrainingRun run)
		{
			if (!SameRun(run, selectedRun))
				return;

			feedbackStatus.Text = "Saved. This assessment will be included in the next improvement prompt.";
			battlePicker.Refresh();
			UpdateFeedbackState();
		}

		internal void SubmitFeedback()
		{
			if (selectedRun != null && saveFeedback.Enabled)
				PlayerFeedbackSaved?.Invoke(selectedRun, feedback.Text);
		}

		Control BuildFeedbackPanel()
		{
			feedback = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ScrollBars = ScrollBars.Vertical,
				MaxLength = TrainingRun.MaxPlayerFeedbackLength,
				BackColor = Paper,
				ForeColor = Ink
			};
			feedback.TextChanged += (_, _) => UpdateFeedbackState();

			saveFeedback = new ActionButton
			{
				Text = "Save assessment",
				Enabled = false
			};
			saveFeedback.Click += (_, _) => SubmitFeedback();

			feedbackStatus = new Label
			{
				AutoSize = true,
				ForeColor = Faded,
				MaximumSize = new Size(330, 0),
				Margin = new Padding(8, 3, 3, 3)
			};

			var actions = new FlowLayoutPanel
			{
				Dock = DockStyle.Fill,
				AutoSize = true,
				FlowDirection = FlowDirection.TopDown,
				WrapContents = false,
				Margin = new Padding(0)
			};
			actions.Controls.Add(saveFeedback);
			actions.Controls.Add(feedbackStatus);

			var layout = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				RowCount = 2,
				Padding = new Padding(8, 4, 8, 4),
				BackColor = CommandTheme.Surface
			};
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
			layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
			var heading = new Label
			{
				Text = "Your assessment: why did this battle win or lose?",
				AutoSize = true,
				ForeColor = Faded
			};
			layout.Controls.Add(heading, 0, 0);
			layout.SetColumnSpan(heading, 2);
			layout.Controls.Add(feedback, 0, 1);
			layout.Controls.Add(actions, 1, 1);
			return layout;
		}

		void SelectBattle()
		{
			if (loadingBattles)
				return;

			selectedRun = (battlePicker.SelectedItem as BattleChoice)?.Run;
			if (selectedRun == null)
			{
				ShowLog(new MatchLog());
				result.Text = "No battle selected.";
				feedback.Clear();
				UpdateFeedbackState();
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
			result.Text = Summary(shownLog, finished);
			result.ForeColor = shownLog.IsDecided ? Ink : Faded;
			feedback.Text = selectedRun.Manifest.Result?.PlayerFeedback ?? "";
			Text = $"AutoC&C - Battle #{battlePicker.SelectedIndex + 1} results";
			UpdateFeedbackState();
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
			var existing = selectedRun?.Manifest.Result?.PlayerFeedback ?? "";
			var available = selectedRun?.Manifest.CompletedUtc != null && selectedRun.IsEditable && !continuousMode;
			feedback.Enabled = available;
			saveFeedback.Enabled = available &&
				!string.Equals(feedback.Text.Trim(), existing, StringComparison.Ordinal);

			if (continuousMode)
				feedbackStatus.Text = "Unavailable while continuous improvement is enabled.";
			else if (selectedRun?.Manifest.CompletedUtc == null)
				feedbackStatus.Text = "Available when the battle finishes.";
			else if (selectedRun?.IsEditable != true)
				feedbackStatus.Text = "Feedback requires an editable bot project.";
			else if (!saveFeedback.Enabled)
				feedbackStatus.Text = "Saved feedback is added to this battle's next improvement prompt.";
			else
				feedbackStatus.Text = "Save before choosing Analyze & improve.";
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

		static string HistorySummary(TrainingHistory history)
		{
			if (history.Iterations.Count == 0)
				return history.Warnings.Count == 0
					? "No completed battles yet."
					: $"No readable completed battles. {history.Warnings.Count} run(s) could not be read.";

			var wins = history.Iterations.Where(iteration =>
				string.Equals(iteration.Outcome, "Won", StringComparison.OrdinalIgnoreCase)).ToList();
			var losses = history.Iterations.Where(iteration =>
				string.Equals(iteration.Outcome, "Lost", StringComparison.OrdinalIgnoreCase)).ToList();
			var parts = new List<string>
			{
				$"{history.Iterations.Count} iteration(s): {wins.Count} won, {losses.Count} lost."
			};
			AddDurationTrend(parts, "Wins", wins);
			AddDurationTrend(parts, "Losses", losses);
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
