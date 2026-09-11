// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	internal static class CommandTheme
	{
		internal static readonly Color Background = Color.FromArgb(16, 23, 19);
		internal static readonly Color Surface = Color.FromArgb(25, 35, 29);
		internal static readonly Color Raised = Color.FromArgb(39, 51, 42);
		internal static readonly Color Field = Color.FromArgb(11, 18, 14);
		internal static readonly Color Ink = Color.FromArgb(231, 235, 219);
		internal static readonly Color Muted = Color.FromArgb(168, 184, 164);
		internal static readonly Color Green = Color.FromArgb(181, 218, 143);
		internal static readonly Color Amber = Color.FromArgb(237, 186, 102);
		internal static readonly Color Danger = Color.FromArgb(238, 155, 136);
		internal static readonly Color Rule = Color.FromArgb(75, 94, 76);

		internal static readonly Font Body = new("Segoe UI", 10f);
		internal static readonly Font Small = new("Segoe UI", 9f);
		internal static readonly Font Heading = new("Bahnschrift", 20f, FontStyle.Bold);
		internal static readonly Font Display = new("Bahnschrift", 30f, FontStyle.Bold);

		internal static void Apply(Control control)
		{
			if (control is Form)
			{
				control.BackColor = Background;
				control.ForeColor = Ink;
			}
			else if (control is TextBoxBase or ListView or TreeView or NumericUpDown)
			{
				control.BackColor = Field;
				control.ForeColor = Ink;
				if (control is TextBoxBase text)
					text.BorderStyle = BorderStyle.FixedSingle;
			}
			else if (control is ComboBox combo)
			{
				combo.BackColor = Field;
				combo.ForeColor = Ink;
				combo.FlatStyle = FlatStyle.Flat;
				combo.DrawMode = DrawMode.OwnerDrawFixed;
				combo.ItemHeight = Math.Max(combo.ItemHeight, combo.Font.Height + 6);
				combo.DrawItem += DrawChoice;
			}
			else if (control is LinkLabel link)
			{
				link.LinkColor = Green;
				link.ActiveLinkColor = Amber;
				link.VisitedLinkColor = Green;
				link.DisabledLinkColor = Muted;
			}
			else if (control is Label && control.ForeColor == SystemColors.GrayText)
				control.ForeColor = Muted;

			foreach (Control child in control.Controls)
				Apply(child);
		}

		static void DrawChoice(object sender, DrawItemEventArgs e)
		{
			var combo = (ComboBox)sender;
			var selected = (e.State & DrawItemState.Selected) != 0;
			using var brush = new SolidBrush(selected ? Raised : Field);
			e.Graphics.FillRectangle(brush, e.Bounds);
			var text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
			TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -4, 0),
				combo.Enabled ? Ink : Muted,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
				TextFormatFlags.NoPrefix);
			e.DrawFocusRectangle();
		}
	}

	internal sealed class CommandSection : GroupBox
	{
		public CommandSection()
		{
			DoubleBuffered = true;
			ForeColor = CommandTheme.Green;
			BackColor = CommandTheme.Surface;
			Padding = new Padding(16, 28, 16, 16);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			e.Graphics.Clear(BackColor);
			using var edge = new Pen(CommandTheme.Rule);
			e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
			TextRenderer.DrawText(e.Graphics, Text, Font,
				new Rectangle(Padding.Left, 5, Math.Max(0, Width - Padding.Horizontal), Font.Height + 4),
				Enabled ? ForeColor : CommandTheme.Muted,
				TextFormatFlags.Left | TextFormatFlags.NoPrefix);
		}
	}

	internal sealed class CommandTabs : TabControl
	{
		public CommandTabs()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
				ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
			Padding = new Point(14, 7);
			SelectedIndexChanged += (_, _) => Invalidate();
			GotFocus += (_, _) => Invalidate();
			LostFocus += (_, _) => Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			e.Graphics.Clear(CommandTheme.Background);
			for (var index = 0; index < TabCount; index++)
			{
				var bounds = GetTabRect(index);
				var selected = index == SelectedIndex;
				using var face = new SolidBrush(selected ? CommandTheme.Raised : CommandTheme.Surface);
				e.Graphics.FillRectangle(face, bounds);
				TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds,
					selected ? CommandTheme.Green : CommandTheme.Muted,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
				if (selected)
				{
					using var rule = new Pen(CommandTheme.Green, 2);
					e.Graphics.DrawLine(rule, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
					if (Focused && ShowFocusCues)
						ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
				}
			}
		}
	}
}
