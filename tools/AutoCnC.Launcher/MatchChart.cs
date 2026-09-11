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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Plots one number per player against game time, in each player's own colour.
	/// </summary>
	/// <remarks>
	/// Drawn by hand because .NET has shipped no charting control since Framework, and a chart
	/// package for two line graphs would be a dependency the rest of this window does not need.
	/// It is a line per player, a shared axis, and a legend showing the latest value — which is
	/// the whole question being asked of it: whose army is growing, and from when.
	/// </remarks>
	public sealed class MatchChart : Control
	{
		static readonly Color Ink = CommandTheme.Ink;
		static readonly Color Grid = CommandTheme.Rule;
		static readonly Color Paper = CommandTheme.Surface;

		/// <summary>Horizontal divisions of the value axis. Four keeps the labels round.</summary>
		const int Gridlines = 4;

		readonly Func<MatchSample, int> metric;
		readonly string title;

		public MatchChart(string title, Func<MatchSample, int> metric)
		{
			this.title = title;
			this.metric = metric;

			DoubleBuffered = true;
			BackColor = Paper;
			ForeColor = Ink;
		}

		public MatchLog Log { get; set; }

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(Paper);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

			using var titleFont = new Font(Font, FontStyle.Bold);
			using var ink = new SolidBrush(Ink);
			using var faded = new SolidBrush(CommandTheme.Muted);

			// Every measurement below is in text lines rather than pixels. The window follows the
			// system font, which on a scaled display is half again as tall as it is at 100%, and
			// fixed pixel boxes clip the labels — names included — exactly there and nowhere else.
			var line = Font.Height;

			g.DrawString(title, titleFont, ink, 8, 2);

			var series = Log?.Players?.Where(p => p.Samples.Count > 0).ToList() ?? [];
			if (series.Count == 0)
			{
				g.DrawString("No match recorded yet. Launch a battle and the graph fills in as it plays.",
					Font, faded, 8, line + 6);
				return;
			}

			var maxValue = series.Max(p => p.Samples.Max(metric));
			var maxSeconds = Math.Max(1, series.Max(p => p.Samples[^1].Seconds));
			var top = NiceCeiling(Math.Max(1, maxValue));

			// The legend gives up width when the chart is narrow — five graphs share this tab, so
			// half of them are half-width — but never so much that a name cannot be read.
			var legendWidth = Math.Clamp(Width / 3, 110, 200);

			// Measured across every label rather than just the topmost: on a money axis the top
			// reads "30k" while the one below it reads "7.5k", which is the wider of the two.
			var left = (int)Math.Ceiling(Enumerable.Range(0, Gridlines + 1)
				.Max(i => g.MeasureString(Compact(top * i / Gridlines), Font).Width)) + 12;

			// The title sits on the first line; the plot starts half a line below it so the topmost
			// axis label, which is centred on the top gridline, still has room above it.
			var plot = Rectangle.FromLTRB(
				left,
				line + line / 2 + 4,
				Math.Max(left + 20, Width - legendWidth),
				Math.Max(line * 3, Height - line - 8));

			DrawGrid(g, plot, top, maxSeconds, faded, line);

			foreach (var player in series)
				DrawSeries(g, plot, player, top, maxSeconds);

			DrawLegend(g, plot, series, ink, legendWidth, line);
		}

		void DrawGrid(Graphics g, Rectangle plot, int top, int maxSeconds, Brush faded, int line)
		{
			using var grid = new Pen(Grid);
			using var axis = new Pen(Color.FromArgb(90, 90, 90));
			using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

			const int Lines = Gridlines;
			for (var i = 0; i <= Lines; i++)
			{
				var y = plot.Bottom - i * plot.Height / Lines;
				g.DrawLine(grid, plot.Left, y, plot.Right, y);
				g.DrawString(Compact(top * i / Lines), Font, faded,
					new RectangleF(0, y - line / 2f, plot.Left - 6, line), format);
			}

			// Time is labelled on round minutes rather than on quarters of however long the match
			// happens to have run, so two matches of different lengths still read the same way.
			using var centred = new StringFormat { Alignment = StringAlignment.Center };
			var step = NiceTimeStep(maxSeconds);
			for (var seconds = 0; seconds <= maxSeconds; seconds += step)
			{
				var x = plot.Left + (float)seconds / maxSeconds * plot.Width;
				if (seconds > 0)
					g.DrawLine(grid, x, plot.Top, x, plot.Bottom);

				g.DrawString(Clock(seconds), Font, faded,
					new RectangleF(x - 30, plot.Bottom + 3, 60, line), centred);
			}

			g.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);
			g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
		}

		void DrawSeries(Graphics g, Rectangle plot, MatchPlayer player, int top, int maxSeconds)
		{
			var points = new List<PointF>(player.Samples.Count);
			foreach (var sample in player.Samples)
			{
				var x = plot.Left + (float)sample.Seconds / maxSeconds * plot.Width;
				var y = plot.Bottom - (float)metric(sample) / top * plot.Height;
				points.Add(new PointF(x, y));
			}

			// A flat line at zero is still information — it says the doctrine built nothing.
			using var pen = new Pen(player.Colour, 2f) { LineJoin = LineJoin.Round };
			if (points.Count == 1)
				g.FillEllipse(new SolidBrush(player.Colour), points[0].X - 2, points[0].Y - 2, 4, 4);
			else
				g.DrawLines(pen, [.. points]);
		}

		void DrawLegend(Graphics g, Rectangle plot, IReadOnlyList<MatchPlayer> series, Brush ink, int legendWidth, int line)
		{
			var x = plot.Right + 10;
			var y = plot.Top;
			var textWidth = Math.Max(20, legendWidth - 28);

			// Names come from a lobby, so they are as long as somebody felt like making them.
			using var clipped = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
			using var bold = new Font(Font, FontStyle.Bold);

			foreach (var player in series)
			{
				using var swatch = new SolidBrush(player.Colour);
				g.FillRectangle(swatch, x, y + line / 3f, 10, 10);

				g.DrawString(player.Label, Font, ink, new RectangleF(x + 16, y, textWidth, line), clipped);
				g.DrawString(Compact(metric(player.Samples[^1])), bold, ink,
					new RectangleF(x + 16, y + line, textWidth, line), clipped);

				y += 2 * line + 8;
			}
		}

		/// <summary>
		/// Rounds an axis up to a number a person would have chosen, and no further: 62 units
		/// becomes 80 rather than 100, so the line still uses most of the height it is given.
		/// </summary>
		static int NiceCeiling(int value)
		{
			var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
			foreach (var step in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
				if (value <= step * magnitude)
					return Quarters((int)Math.Round(step * magnitude));

			return Quarters((int)(10 * magnitude));
		}

		/// <summary>Rounds up to a multiple of four, so the four gridlines land on whole numbers.</summary>
		static int Quarters(int value) => (value + 3) / 4 * 4;

		/// <summary>Round minutes, chosen so a match gets four to eight labels whatever its length.</summary>
		static int NiceTimeStep(int maxSeconds)
		{
			foreach (var step in new[] { 15, 30, 60, 120, 300, 600, 900, 1800, 3600 })
				if (maxSeconds / step <= 8)
					return step;

			return 7200;
		}

		/// <summary>Thousands from a thousand up, so a money axis reads 7.5k / 15k, not 7500 / 15k.</summary>
		static string Compact(int value) =>
			value >= 1000 ? $"{value / 1000f:0.#}k" : value.ToString();

		static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
	}
}
