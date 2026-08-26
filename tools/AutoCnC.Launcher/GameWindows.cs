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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Finds the running game's window so it can be asked to close rather than killed.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The game is a grandchild of this process — launcher, PowerShell, game — and SDL windows
	/// are invisible to the usual managed process plumbing, so the window is found the only way
	/// Windows offers: enumerate the top-level windows and match the title the mod sets.
	/// </para>
	/// <para>
	/// The title alone is not enough, because it would also match a game the user started
	/// themselves and has nothing to do with this launcher. Anything that was already running
	/// before the current script started is therefore left alone.
	/// </para>
	/// </remarks>
	static class GameWindows
	{
		/// <summary>Matches the mod's <c>mod-windowtitle</c>, loosely enough to survive a reword.</summary>
		const string TitlePrefix = "AutoC&C";

		const uint WmClose = 0x0010;

		delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

		[DllImport("user32.dll")]
		static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);

		[DllImport("user32.dll")]
		static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

		[DllImport("user32.dll")]
		static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

		/// <summary>Asks every game window younger than the given time to close. True if any did.</summary>
		public static bool CloseAllStartedAfter(DateTime started)
		{
			var closed = false;

			EnumWindows((window, _) =>
			{
				if (!IsWindowVisible(window) || !Title(window).StartsWith(TitlePrefix, StringComparison.Ordinal))
					return true;

				GetWindowThreadProcessId(window, out var processId);
				if (processId == 0 || !StartedAfter((int)processId, started))
					return true;

				closed |= PostMessageW(window, WmClose, IntPtr.Zero, IntPtr.Zero);
				return true;
			}, IntPtr.Zero);

			return closed;
		}

		static bool StartedAfter(int processId, DateTime started)
		{
			try
			{
				using var process = Process.GetProcessById(processId);

				// A second of slack: the script is started first, but only just.
				return process.StartTime >= started.AddSeconds(-1);
			}
			catch (Exception)
			{
				// Gone already, or not ours to ask about.
				return false;
			}
		}

		static string Title(IntPtr window)
		{
			var text = new StringBuilder(256);
			return GetWindowTextW(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
		}
	}
}
