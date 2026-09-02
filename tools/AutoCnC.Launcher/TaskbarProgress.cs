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

namespace AutoCnC.Launcher
{
	/// <summary>Shows indeterminate work on the launcher's Windows taskbar button.</summary>
	public sealed class TaskbarProgress : IDisposable
	{
		enum State : uint
		{
			None = 0,
			Indeterminate = 0x1
		}

		[ComImport]
		[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		interface ITaskbarList3
		{
			[PreserveSig] int HrInit();
			[PreserveSig] int AddTab(IntPtr window);
			[PreserveSig] int DeleteTab(IntPtr window);
			[PreserveSig] int ActivateTab(IntPtr window);
			[PreserveSig] int SetActiveAlt(IntPtr window);
			[PreserveSig] int MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
			[PreserveSig] int SetProgressValue(IntPtr window, ulong completed, ulong total);
			[PreserveSig] int SetProgressState(IntPtr window, State state);
		}

		[ComImport]
		[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
		[ClassInterface(ClassInterfaceType.None)]
		sealed class TaskbarList { }

		ITaskbarList3 taskbar;
		IntPtr lastWindow;
		State lastState;
		bool unavailable;

		public void SetBusy(IntPtr window, bool busy)
		{
			if (window == IntPtr.Zero || unavailable)
				return;

			var state = busy ? State.Indeterminate : State.None;
			if (window == lastWindow && state == lastState)
				return;

			try
			{
				taskbar ??= (ITaskbarList3)(object)new TaskbarList();
				if (taskbar.HrInit() < 0 || taskbar.SetProgressState(window, state) < 0)
				{
					unavailable = true;
					return;
				}

				lastWindow = window;
				lastState = state;
			}
			catch (COMException)
			{
				unavailable = true;
			}
			catch (InvalidCastException)
			{
				unavailable = true;
			}
		}

		public void Dispose()
		{
			if (taskbar != null && Marshal.IsComObject(taskbar))
			{
				try
				{
					Marshal.FinalReleaseComObject(taskbar);
				}
				catch (COMException)
				{
				}
				catch (InvalidComObjectException)
				{
				}
			}

			taskbar = null;
		}
	}
}
