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
	/// <summary>A button that stays readable on the dark battle windows.</summary>
	/// <remarks>
	/// <para>
	/// A stock button inherits its parent's colour, which on these windows means its face is the
	/// same near-black as the panel behind it and its border is darker still. There is nothing
	/// there to press: the label floats on the panel like a caption.
	/// </para>
	/// <para>
	/// Disabled is worse, and is why this class exists. WinForms derives the disabled label
	/// colour by *darkening the face*, which assumes the light grey a button normally has. On a
	/// 38,38,38 panel it produced 12,12,12 text — a contrast ratio of 1.29:1, measured off a
	/// rendered frame, which is text you cannot read. Face and label colours alone cannot fix
	/// that, because the engine that picks the label colour is the thing that is wrong, so this
	/// paints its own disabled state: about 4.2:1, plainly switched off but still legible.
	/// Enabled is left to the flat renderer, which honours these colours and reaches 7.6:1.
	/// </para>
	/// </remarks>
	public sealed class ActionButton : Button
	{
		static readonly Color Face = Color.FromArgb(64, 64, 64);
		static readonly Color Edge = Color.FromArgb(122, 122, 122);
		static readonly Color Hover = Color.FromArgb(82, 82, 82);
		static readonly Color Pressed = Color.FromArgb(96, 96, 96);

		static readonly Color OffFace = Color.FromArgb(48, 48, 48);
		static readonly Color OffEdge = Color.FromArgb(78, 78, 78);
		static readonly Color OffInk = Color.FromArgb(145, 145, 145);

		public ActionButton()
		{
			AutoSize = true;
			Padding = new Padding(8, 3, 8, 3);
			FlatStyle = FlatStyle.Flat;
			UseVisualStyleBackColor = false;
			BackColor = Face;
			ForeColor = BattleWindow.Ink;
			FlatAppearance.BorderSize = 1;
			FlatAppearance.BorderColor = Edge;
			FlatAppearance.MouseOverBackColor = Hover;
			FlatAppearance.MouseDownBackColor = Pressed;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (Enabled)
			{
				base.OnPaint(e);
				return;
			}

			var area = ClientRectangle;
			using (var face = new SolidBrush(OffFace))
				e.Graphics.FillRectangle(face, area);

			using (var edge = new Pen(OffEdge))
				e.Graphics.DrawRectangle(edge, 0, 0, area.Width - 1, area.Height - 1);

			TextRenderer.DrawText(e.Graphics, Text, Font, area, OffInk,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
				TextFormatFlags.EndEllipsis);
		}
	}
}
