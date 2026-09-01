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
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// The graphs, in a window of their own for as long as you want to look at them.
	/// </summary>
	/// <remarks>
	/// The questions a doctrine has to answer, laid out so you can see them at once: is it
	/// building an army, is that army worth anything, is the base growing behind it, and is any
	/// of it killing the other side. The crossings between the graphs are the interesting part,
	/// so they share a time axis rather than hiding behind tabs.
	/// </remarks>
	public sealed class ResultsWindow : BattleWindow
	{
		readonly MatchLog log;
		readonly MatchChart[] charts;
		readonly Label result;
		readonly ScoreStrip scores;

		public ResultsWindow(MatchLog log)
			: base("AutoC&C — Match results")
		{
			this.log = log;

			var unitsChart = new MatchChart("Units", s => s.Units) { Dock = DockStyle.Fill, Log = log };
			var armyChart = new MatchChart("Army value", s => s.Army) { Dock = DockStyle.Fill, Log = log };
			var buildingsChart = new MatchChart("Buildings", s => s.Buildings) { Dock = DockStyle.Fill, Log = log };
			var baseChart = new MatchChart("Base value", s => s.BaseValue) { Dock = DockStyle.Fill, Log = log };
			var killsChart = new MatchChart("Kills", s => s.Killed) { Dock = DockStyle.Fill, Log = log };

			charts = [unitsChart, armyChart, buildingsChart, baseChart, killsChart];

			var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Paper };
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
			for (var i = 0; i < 3; i++)
				grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));

			grid.Controls.Add(unitsChart, 0, 0);
			grid.Controls.Add(armyChart, 1, 0);
			grid.Controls.Add(buildingsChart, 0, 1);
			grid.Controls.Add(baseChart, 1, 1);

			// Kills is the one that reads as a score line, so it gets the full width underneath.
			grid.Controls.Add(killsChart, 0, 2);
			grid.SetColumnSpan(killsChart, 2);

			result = new Label
			{
				Dock = DockStyle.Top,
				AutoSize = false,
				Height = Font.Height + 12,
				Padding = new Padding(10, 6, 10, 0),
				ForeColor = Faded,
				Text = "Waiting for the battle to start…"
			};

			scores = new ScoreStrip(log) { Dock = DockStyle.Bottom, Height = 3 * Font.Height + 16 };

			Controls.Add(grid);
			Controls.Add(scores);
			Controls.Add(result);
		}

		/// <summary>Called on every poll of the telemetry file, and once more when it stops.</summary>
		public void Redraw(bool finished)
		{
			foreach (var chart in charts)
				chart.Invalidate();

			scores.Invalidate();
			result.Text = Summary(finished);
			result.ForeColor = log.IsDecided ? Ink : Faded;

			Text = finished ? "AutoC&C — Match results (finished)" : "AutoC&C — Match results (running)";
		}

		/// <summary>
		/// One line saying how it went, because that is the first thing anybody looks for and the
		/// graphs answer a different question.
		/// </summary>
		string Summary(bool finished)
		{
			if (log.IsEmpty)
				return finished
					? "No match was recorded. If the doctrine failed to build, the output window says why."
					: "Waiting for the battle to start…";

			var clock = Csv.Clock(log.Duration);

			var won = log.Players.Where(p => string.Equals(p.Outcome, "Won", StringComparison.OrdinalIgnoreCase)).ToList();
			if (won.Count > 0)
				return $"{string.Join(", ", won.Select(p => p.Label))} won at {clock}.";

			if (log.IsDecided)
				return $"Everybody lost by {clock} — a draw, or the last side standing quit.";

			return finished
				? $"Stopped at {clock} with the match undecided."
				: $"Battle in progress — {clock}.";
		}

		/// <summary>
		/// The final numbers, in each player's own colour.
		/// </summary>
		/// <remarks>
		/// Drawn rather than a grid control for the same reason the charts are: it is a handful of
		/// rows in colours the game chose, and a DataGridView styled to match would be more code
		/// than this and still look like a spreadsheet.
		/// </remarks>
		sealed class ScoreStrip : Control
		{
			static readonly string[] Headings = ["Player", "Units", "Army", "Buildings", "Killed", "Lost", "Cash", "Result"];

			readonly MatchLog log;

			public ScoreStrip(MatchLog log)
			{
				this.log = log;
				DoubleBuffered = true;
				BackColor = Paper;
				ForeColor = Ink;
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				var g = e.Graphics;
				g.Clear(Paper);

				var players = log.Players.Where(p => p.Samples.Count > 0).ToList();
				if (players.Count == 0)
					return;

				using var bold = new Font(Font, FontStyle.Bold);
				using var faded = new SolidBrush(Faded);
				using var rule = new Pen(Rule);

				var line = Font.Height;
				var nameWidth = Math.Clamp(Width / 4, 90, 220);
				var columnWidth = Math.Max(52, (Width - nameWidth - 16) / (Headings.Length - 1));

				float Column(int index) => 8 + (index == 0 ? 0 : nameWidth + (index - 1) * columnWidth);

				for (var i = 0; i < Headings.Length; i++)
					g.DrawString(Headings[i], Font, faded, Column(i), 2);

				g.DrawLine(rule, 0, line + 4, Width, line + 4);

				// Room for as many rows as there is height. Anything past that is a free-for-all
				// map, where a table was never going to be the readable view anyway.
				var rows = Math.Max(1, (Height - line - 8) / line);

				for (var row = 0; row < Math.Min(rows, players.Count); row++)
				{
					var player = players[row];
					var last = player.Samples[^1];
					var y = line + 8 + row * line;

					using var brush = new SolidBrush(player.Colour);
					using var clipped = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

					g.DrawString(player.Label, Font, brush, new RectangleF(Column(0), y, nameWidth - 8, line), clipped);

					var cells = new[]
					{
						last.Units.ToString(),
						last.Army.ToString(),
						last.Buildings.ToString(),
						last.Killed.ToString(),
						last.Lost.ToString(),
						last.Cash.ToString(),
						player.HasResult ? player.Outcome : "—"
					};

					for (var i = 0; i < cells.Length; i++)
						g.DrawString(cells[i], player.HasResult && i == cells.Length - 1 ? bold : Font,
							brush, Column(i + 1), y);
				}
			}
		}
	}
}
