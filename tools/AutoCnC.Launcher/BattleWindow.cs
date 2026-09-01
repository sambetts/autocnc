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
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// A window that belongs to a battle rather than to the launcher.
	/// </summary>
	/// <remarks>
	/// <para>
	/// These appear when a battle starts and stay up when it ends, which is the whole point of
	/// them: the question you want to ask a match — where did it turn, what did my code know at
	/// the time — is one you ask afterwards. Closing them the moment the game exits would take
	/// the answer away at exactly the wrong moment.
	/// </para>
	/// <para>
	/// Owned by the launcher, so they cannot get lost behind it and they go away with it, but
	/// otherwise ordinary top-level windows: movable, resizable, and yours to put on a second
	/// monitor.
	/// </para>
	/// </remarks>
	public abstract class BattleWindow : Form
	{
		protected static readonly Color Paper = Color.FromArgb(30, 30, 30);
		protected static readonly Color Ink = Color.Gainsboro;
		protected static readonly Color Faded = Color.FromArgb(150, 150, 150);
		protected static readonly Color Rule = Color.FromArgb(58, 58, 58);

		bool placed;

		protected BattleWindow(string title)
		{
			Text = title;
			Font = SystemFonts.MessageBoxFont;
			BackColor = Paper;
			ForeColor = Ink;
			ShowInTaskbar = true;
			MinimumSize = new Size(520, 320);
			StartPosition = FormStartPosition.Manual;
		}

		/// <summary>
		/// Shows the window, putting it beside the launcher the first time only.
		/// </summary>
		/// <remarks>
		/// Only the first time, because the second battle of an afternoon is the one where you
		/// have already dragged this onto the monitor you want it on. Re-launching should not
		/// undo that.
		/// </remarks>
		public void Show(Form launcher, float topFraction, float heightFraction)
		{
			if (!placed)
			{
				placed = true;
				Place(launcher, topFraction, heightFraction);
			}

			if (!Visible)
				Show(launcher);

			if (WindowState == FormWindowState.Minimized)
				WindowState = FormWindowState.Normal;

			BringToFront();
		}

		/// <summary>
		/// Claims the space to the right of the launcher, or the right half of the screen when
		/// the launcher is already using most of it.
		/// </summary>
		void Place(Form launcher, float topFraction, float heightFraction)
		{
			var area = Screen.FromControl(launcher).WorkingArea;

			const int Gap = 8;
			const int Narrowest = 560;

			var left = launcher.Bounds.Right + Gap;
			if (area.Right - left < Narrowest)
				left = Math.Max(area.Left, area.Right - Narrowest);

			var width = Math.Max(Narrowest, area.Right - left - Gap);
			var top = area.Top + (int)(area.Height * topFraction);
			var height = (int)(area.Height * heightFraction) - Gap;

			Bounds = new Rectangle(left, top, width, Math.Max(MinimumSize.Height, height));
		}

		/// <summary>A heading strip in the window's own palette.</summary>
		protected static Label Heading(string text) =>
			new()
			{
				Text = text,
				Dock = DockStyle.Top,
				AutoSize = false,
				Height = SystemFonts.MessageBoxFont.Height + 10,
				ForeColor = Faded,
				Padding = new Padding(8, 5, 8, 0)
			};
	}
}
