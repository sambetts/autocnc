// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>An original doctrine-core schematic, not a map or a claim about the bot's internals.</summary>
	internal sealed class BotDossier : Panel
	{
		readonly Label name;
		readonly Label kind;
		readonly Label state;
		readonly Label lastBattle;

		internal string BotName => name.Text;
		internal string Readiness => state.Text;

		internal BotDossier()
		{
			DoubleBuffered = true;
			SetStyle(ControlStyles.ResizeRedraw, true);
			BackColor = CommandTheme.Field;
			Padding = new Padding(24);
			name = new Label
			{
				Font = CommandTheme.Display, ForeColor = CommandTheme.Ink,
				AutoEllipsis = true, UseMnemonic = false, Text = "No bot selected"
			};
			kind = new Label
			{
				Font = CommandTheme.Small, ForeColor = CommandTheme.Muted,
				Text = "YOUR CODE. YOUR DOCTRINE. YOUR ARMY.", AutoEllipsis = true
			};
			state = new Label
			{
				Font = CommandTheme.Body, ForeColor = CommandTheme.Green,
				AutoEllipsis = true, UseMnemonic = false
			};
			lastBattle = new Label
			{
				Font = CommandTheme.Small, ForeColor = CommandTheme.Muted,
				AutoEllipsis = true, UseMnemonic = false
			};
			Controls.AddRange([name, kind, state, lastBattle]);
			AccessibleName = "Selected battle bot";
		}

		internal void SetBot(string botName, string description, string readiness, bool ready, string lastSortie)
		{
			name.Text = botName;
			kind.Text = description;
			state.Text = readiness;
			state.ForeColor = ready ? CommandTheme.Green : CommandTheme.Amber;
			lastBattle.Text = lastSortie;
		}

		protected override void OnLayout(LayoutEventArgs e)
		{
			base.OnLayout(e);
			if (name == null)
				return;
			var scale = DeviceDpi / 96f;
			var left = (int)(24 * scale);
			var width = Math.Max(80, ClientSize.Width - left * 2 - (int)(220 * scale));
			kind.SetBounds(left, (int)(28 * scale), width, (int)(24 * scale));
			name.SetBounds(left - 2, (int)(57 * scale), width, (int)(56 * scale));
			state.SetBounds(left, (int)(120 * scale), width, (int)(24 * scale));
			lastBattle.SetBounds(left, (int)(150 * scale), width, (int)(22 * scale));
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			var g = e.Graphics;
			using (var seam = new Pen(Color.FromArgb(19, 32, 23)))
				for (var x = 0; x < Width; x += 48)
					g.DrawLine(seam, x, Height, x + 120, 0);
			var saved = g.Save();
			var scale = DeviceDpi / 96f;
			g.TranslateTransform(Width - 216 * scale, 16 * scale);
			g.ScaleTransform(scale, scale);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using var grid = new Pen(Color.FromArgb(28, 45, 32));
			for (var x = 0; x < 200; x += 16)
				g.DrawLine(grid, x, 0, x, 152);
			for (var y = 0; y < 152; y += 16)
				g.DrawLine(grid, 0, y, 192, y);
			using var line = new Pen(CommandTheme.Rule);
			using var signal = new Pen(CommandTheme.Green, 1.5f);
			using var fill = new SolidBrush(CommandTheme.Surface);
			PointF[] core = [new(96, 30), new(143, 58), new(143, 106), new(96, 134), new(49, 106), new(49, 58)];
			g.FillPolygon(fill, core);
			g.DrawPolygon(signal, core);
			g.DrawPolygon(line, new Point[] { new(96, 40), new(133, 63), new(133, 101), new(96, 124), new(59, 101), new(59, 63) });
			g.DrawLine(line, 96, 40, 96, 62);
			g.DrawLine(line, 59, 101, 78, 89);
			g.DrawLine(line, 133, 101, 114, 89);
			g.DrawLines(signal, new Point[] { new(49, 66), new(27, 66), new(27, 24), new(6, 24) });
			g.DrawLines(signal, new Point[] { new(143, 66), new(168, 66), new(168, 24), new(187, 24) });
			g.DrawLines(signal, new Point[] { new(96, 134), new(96, 147), new(163, 147) });
			using var dot = new SolidBrush(CommandTheme.Amber);
			g.FillRectangle(dot, 2, 20, 7, 7);
			g.FillRectangle(dot, 184, 20, 7, 7);
			g.FillRectangle(dot, 160, 143, 7, 7);
			using var coreFont = new Font("Bahnschrift", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
			using var labelFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
			using var ink = new SolidBrush(CommandTheme.Green);
			using var muted = new SolidBrush(CommandTheme.Muted);
			using var center = new StringFormat { Alignment = StringAlignment.Center };
			g.DrawString("BOT", coreFont, ink, new PointF(96, 67), center);
			g.DrawString("DOCTRINE CORE", labelFont, muted, new PointF(96, 91), center);
			g.DrawString("SENSE", labelFont, muted, new PointF(26, 4), center);
			g.DrawString("DECIDE", labelFont, muted, new PointF(164, 4), center);
			g.DrawString("ACT", labelFont, muted, new PointF(182, 135), center);
			g.Restore(saved);
			using var edge = new Pen(CommandTheme.Rule);
			g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
			using var corner = new Pen(CommandTheme.Green, 2);
			g.DrawLines(corner, new Point[] { new(0, 18), new(0, 0), new(18, 0) });
			g.DrawLines(corner, new Point[] { new(Width - 19, Height - 1), new(Width - 1, Height - 1), new(Width - 1, Height - 19) });
		}
	}
}
