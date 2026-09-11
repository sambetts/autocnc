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

using System.Drawing;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>Command-console controls with native button semantics and explicit readable states.</summary>
	public sealed class ActionButton : Button
	{
		bool hovered;
		bool pressed;
		bool selected;
		bool primary;

		public string Detail { get; set; }
		public bool Primary
		{
			get => primary;
			set { primary = value; Invalidate(); }
		}

		public bool Selected
		{
			get => selected;
			set { selected = value; Invalidate(); }
		}

		public ActionButton()
		{
			DoubleBuffered = true;
			AutoSize = true;
			Padding = new Padding(12, 6, 12, 6);
			FlatStyle = FlatStyle.Flat;
			UseVisualStyleBackColor = false;
			BackColor = CommandTheme.Raised;
			ForeColor = CommandTheme.Ink;
			FlatAppearance.BorderSize = 1;
			FlatAppearance.BorderColor = CommandTheme.Rule;
			MouseEnter += (_, _) => { hovered = true; Invalidate(); };
			MouseLeave += (_, _) => { hovered = false; pressed = false; Invalidate(); };
			MouseDown += (_, _) => { pressed = true; Invalidate(); };
			MouseUp += (_, _) => { pressed = false; Invalidate(); };
			KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } };
			KeyUp += (_, _) => { pressed = false; Invalidate(); };
			LostFocus += (_, _) => { pressed = false; Invalidate(); };
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			var area = ClientRectangle;
			var faceColor = !Enabled ? CommandTheme.Surface
				: Primary ? CommandTheme.Amber
				: pressed ? CommandTheme.Field
				: hovered || Selected ? Color.FromArgb(55, 72, 53) : BackColor;
			var ink = !Enabled ? CommandTheme.Muted : Primary ? CommandTheme.Field
				: Selected ? CommandTheme.Green : ForeColor;
			using (var face = new SolidBrush(faceColor))
				e.Graphics.FillRectangle(face, area);

			using (var edge = new Pen(Enabled && (Selected || hovered) ? CommandTheme.Green : CommandTheme.Rule))
				e.Graphics.DrawRectangle(edge, 0, 0, area.Width - 1, area.Height - 1);
			using (var bevel = new Pen(Primary && Enabled ? Color.FromArgb(255, 218, 159) : CommandTheme.Rule))
				e.Graphics.DrawLine(bevel, 1, 1, area.Width - 2, 1);

			var content = Rectangle.Inflate(area, -Padding.Left, -4);
			if (pressed && Enabled)
				content.Offset(1, 1);
			if (string.IsNullOrEmpty(Detail))
				TextRenderer.DrawText(e.Graphics, Text, Font, content, ink,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
					TextFormatFlags.EndEllipsis | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
			else
			{
				content.Y += 6;
				TextRenderer.DrawText(e.Graphics, Text, Font, content, ink,
					TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
				content.Y += Font.Height + 4;
				TextRenderer.DrawText(e.Graphics, Detail, CommandTheme.Small, content, CommandTheme.Muted,
					TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
			}

			if (Focused && ShowFocusCues)
				ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(area, -4, -4), ink, faceColor);
		}
	}
}
