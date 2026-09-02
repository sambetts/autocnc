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
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Plots one final KPI across successive training battles.</summary>
	public sealed class IterationChart : Control
	{
		static readonly Color Ink = Color.FromArgb(210, 210, 210);
		static readonly Color Faded = Color.FromArgb(140, 140, 140);
		static readonly Color Grid = Color.FromArgb(58, 58, 58);
		static readonly Color Paper = Color.FromArgb(30, 30, 30);
		static readonly Color Local = Color.FromArgb(0, 150, 230);
		static readonly Color Opponent = Color.FromArgb(160, 160, 160);
		static readonly Color Won = Color.FromArgb(22, 198, 12);
		static readonly Color Lost = Color.FromArgb(231, 72, 86);
		static readonly Color Undecided = Color.FromArgb(220, 170, 45);

		const int Gridlines = 4;

		readonly string title;
		readonly Func<TrainingIteration, int> localMetric;
		readonly Func<TrainingIteration, int> opponentMetric;
		readonly bool outcomePoints;

		public IterationChart(string title, Func<TrainingIteration, int> localMetric,
			Func<TrainingIteration, int> opponentMetric = null, bool outcomePoints = false)
		{
			this.title = title;
			this.localMetric = localMetric;
			this.opponentMetric = opponentMetric;
			this.outcomePoints = outcomePoints;
			DoubleBuffered = true;
			BackColor = Paper;
			ForeColor = Ink;
		}

		public IReadOnlyList<TrainingIteration> Iterations { get; set; } = [];

		protected override void OnPaint(PaintEventArgs e)
		{
			var g = e.Graphics;
			g.Clear(Paper);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

			using var titleFont = new Font(Font, FontStyle.Bold);
			using var ink = new SolidBrush(Ink);
			using var faded = new SolidBrush(Faded);
			g.DrawString(title, titleFont, ink, 8, 2);

			if (Iterations.Count == 0)
			{
				g.DrawString("No completed battles yet.", Font, faded, 8, Font.Height + 6);
				return;
			}

			var values = Iterations.Select(localMetric).ToList();
			if (opponentMetric != null)
				values.AddRange(Iterations.Where(iteration => iteration.OpponentCount > 0).Select(opponentMetric));

			var top = NiceCeiling(Math.Max(1, values.DefaultIfEmpty().Max()));
			var line = Font.Height;
			var legendWidth = Math.Clamp(Width / 3, 118, 180);
			var left = (int)Math.Ceiling(Enumerable.Range(0, Gridlines + 1)
				.Max(index => g.MeasureString(Compact(top * index / Gridlines), Font).Width)) + 12;
			var plot = Rectangle.FromLTRB(
				left,
				line + line / 2 + 4,
				Math.Max(left + 20, Width - legendWidth),
				Math.Max(line * 3, Height - line - 8));

			DrawGrid(g, plot, top, faded, line);
			DrawLine(g, plot, top, localMetric, Local, iteration => true);
			if (opponentMetric != null)
				DrawLine(g, plot, top, opponentMetric, Opponent, iteration => iteration.OpponentCount > 0);
			DrawLegend(g, plot, ink, faded, legendWidth, line);
		}

		void DrawGrid(Graphics g, Rectangle plot, int top, Brush faded, int line)
		{
			using var grid = new Pen(Grid);
			using var axis = new Pen(Color.FromArgb(90, 90, 90));
			using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
			using var centred = new StringFormat { Alignment = StringAlignment.Center };

			for (var index = 0; index <= Gridlines; index++)
			{
				var y = plot.Bottom - index * plot.Height / Gridlines;
				g.DrawLine(grid, plot.Left, y, plot.Right, y);
				g.DrawString(Compact(top * index / Gridlines), Font, faded,
					new RectangleF(0, y - line / 2f, plot.Left - 6, line), right);
			}

			var step = Math.Max(1, (int)Math.Ceiling(Iterations.Count / 6f));
			for (var index = 0; index < Iterations.Count; index += step)
				DrawIterationLabel(g, plot, faded, centred, line, index);
			if ((Iterations.Count - 1) % step != 0)
				DrawIterationLabel(g, plot, faded, centred, line, Iterations.Count - 1);

			g.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);
			g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
		}

		void DrawIterationLabel(Graphics g, Rectangle plot, Brush faded, StringFormat centred, int line, int index)
		{
			var x = X(plot, index);
			if (index > 0)
				using (var grid = new Pen(Grid))
					g.DrawLine(grid, x, plot.Top, x, plot.Bottom);

			g.DrawString(Iterations[index].Number.ToString(), Font, faded,
				new RectangleF(x - 24, plot.Bottom + 3, 48, line), centred);
		}

		void DrawLine(Graphics g, Rectangle plot, int top, Func<TrainingIteration, int> metric,
			Color colour, Func<TrainingIteration, bool> include)
		{
			var points = Iterations.Select((iteration, index) => new
			{
				Iteration = iteration,
				Point = new PointF(X(plot, index),
					plot.Bottom - (float)metric(iteration) / top * plot.Height)
			}).Where(value => include(value.Iteration)).ToList();

			using var pen = new Pen(colour, 2f) { LineJoin = LineJoin.Round };
			if (points.Count > 1)
				g.DrawLines(pen, points.Select(value => value.Point).ToArray());

			foreach (var value in points)
			{
				var pointColour = outcomePoints ? OutcomeColour(value.Iteration.Outcome) : colour;
				using var brush = new SolidBrush(pointColour);
				g.FillEllipse(brush, value.Point.X - 3, value.Point.Y - 3, 6, 6);
			}
		}

		void DrawLegend(Graphics g, Rectangle plot, Brush ink, Brush faded, int legendWidth, int line)
		{
			var x = plot.Right + 10;
			var y = plot.Top;
			var textWidth = Math.Max(30, legendWidth - 24);
			var latest = Iterations[^1];

			using var bold = new Font(Font, FontStyle.Bold);
			using var local = new SolidBrush(Local);
			g.FillRectangle(local, x, y + line / 3f, 10, 10);
			g.DrawString(outcomePoints ? "Duration" : "Your bot", Font, ink, x + 16, y);
			g.DrawString(outcomePoints ? Csv.Clock(localMetric(latest)) : Compact(localMetric(latest)),
				bold, ink, new RectangleF(x + 16, y + line, textWidth, line));
			y += 2 * line + 8;

			if (opponentMetric != null)
			{
				using var opponent = new SolidBrush(Opponent);
				g.FillRectangle(opponent, x, y + line / 3f, 10, 10);
				g.DrawString("Opponents total", Font, ink, x + 16, y);
				g.DrawString(Compact(opponentMetric(latest)), bold, ink,
					new RectangleF(x + 16, y + line, textWidth, line));
				return;
			}

			DrawOutcomeKey(g, "Win", Won, x, y, ink, line);
			DrawOutcomeKey(g, "Loss", Lost, x, y + line + 4, ink, line);
			g.DrawString("Iteration", Font, faded, x, plot.Bottom - line);
		}

		static void DrawOutcomeKey(Graphics g, string label, Color colour, float x, float y, Brush ink, int line)
		{
			using var brush = new SolidBrush(colour);
			g.FillEllipse(brush, x + 2, y + line / 3f, 7, 7);
			g.DrawString(label, SystemFonts.MessageBoxFont, ink, x + 16, y);
		}

		float X(Rectangle plot, int index) =>
			Iterations.Count == 1
				? plot.Left + plot.Width / 2f
				: plot.Left + (float)index / (Iterations.Count - 1) * plot.Width;

		static Color OutcomeColour(string outcome) =>
			string.Equals(outcome, "Won", StringComparison.OrdinalIgnoreCase) ? Won :
			string.Equals(outcome, "Lost", StringComparison.OrdinalIgnoreCase) ? Lost :
			Undecided;

		static int NiceCeiling(int value)
		{
			var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
			foreach (var step in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
				if (value <= step * magnitude)
					return Quarters((int)Math.Round(step * magnitude));

			return Quarters((int)(10 * magnitude));
		}

		static int Quarters(int value) => (value + 3) / 4 * 4;

		static string Compact(int value) =>
			value >= 1000 ? $"{value / 1000f:0.#}k" : value.ToString();
	}
}
