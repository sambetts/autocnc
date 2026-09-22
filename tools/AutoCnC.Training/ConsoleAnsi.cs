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
using System.Runtime.InteropServices;

namespace AutoCnC.Training
{
	/// <summary>Decides whether this console can show colour, and switches it on if it can.</summary>
	/// <remarks>
	/// Windows Terminal and PowerShell 7 usually arrive with virtual terminal processing already
	/// enabled, but a plain conhost window does not, and printing escape codes into one prints the
	/// escape codes. Asking the console host directly is the only reliable answer, since the same
	/// executable can be run from either.
	/// </remarks>
	static class ConsoleAnsi
	{
		const int StdOutputHandle = -11;
		const uint EnableVirtualTerminalProcessing = 0x0004;

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr GetStdHandle(int nStdHandle);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

		public static bool TryEnable()
		{
			// Redirected output is a file or another program's pipe, and colour codes there are
			// corruption rather than presentation. NO_COLOR is the agreed way to ask for none.
			if (Console.IsOutputRedirected)
				return false;
			if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
				return false;

			try
			{
				var handle = GetStdHandle(StdOutputHandle);
				if (handle == IntPtr.Zero || handle == new IntPtr(-1))
					return false;
				if (!GetConsoleMode(handle, out var mode))
					return false;
				if ((mode & EnableVirtualTerminalProcessing) != 0)
					return true;

				return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
		}
	}
}
