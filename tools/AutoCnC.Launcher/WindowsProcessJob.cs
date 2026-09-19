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
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoCnC.Launcher
{
	/// <summary>Kills a worker process tree if the launcher exits without cleaning it up.</summary>
	internal sealed class WindowsProcessJob : IDisposable
	{
		const uint KillOnJobClose = 0x00002000;
		const int ExtendedLimitInformation = 9;

		[StructLayout(LayoutKind.Sequential)]
		struct BasicLimitInformation
		{
			public long PerProcessUserTimeLimit;
			public long PerJobUserTimeLimit;
			public uint LimitFlags;
			public UIntPtr MinimumWorkingSetSize;
			public UIntPtr MaximumWorkingSetSize;
			public uint ActiveProcessLimit;
			public IntPtr Affinity;
			public uint PriorityClass;
			public uint SchedulingClass;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct IoCounters
		{
			public ulong ReadOperationCount;
			public ulong WriteOperationCount;
			public ulong OtherOperationCount;
			public ulong ReadTransferCount;
			public ulong WriteTransferCount;
			public ulong OtherTransferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct ExtendedLimit
		{
			public BasicLimitInformation BasicLimitInformation;
			public IoCounters IoInfo;
			public UIntPtr ProcessMemoryLimit;
			public UIntPtr JobMemoryLimit;
			public UIntPtr PeakProcessMemoryUsed;
			public UIntPtr PeakJobMemoryUsed;
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateJobObject(IntPtr attributes, string name);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetInformationJobObject(IntPtr job, int informationClass,
			ref ExtendedLimit information, uint length);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);

		IntPtr handle;

		WindowsProcessJob(IntPtr handle)
		{
			this.handle = handle;
		}

		public static WindowsProcessJob Create()
		{
			var handle = CreateJobObject(IntPtr.Zero, null);
			if (handle == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error(),
					"Could not create a worker job object.");

			var information = new ExtendedLimit
			{
				BasicLimitInformation = new BasicLimitInformation
				{
					LimitFlags = KillOnJobClose
				}
			};
			if (!SetInformationJobObject(handle, ExtendedLimitInformation,
				ref information, (uint)Marshal.SizeOf<ExtendedLimit>()))
			{
				var error = Marshal.GetLastWin32Error();
				CloseHandle(handle);
				throw new Win32Exception(error,
					"Could not configure the worker job object.");
			}

			return new WindowsProcessJob(handle);
		}

		public void Assign(Process process)
		{
			if (handle == IntPtr.Zero ||
				!AssignProcessToJobObject(handle, process.Handle))
				throw new Win32Exception(Marshal.GetLastWin32Error(),
					"Could not attach the worker process to its job object.");
		}

		public void Dispose()
		{
			if (handle == IntPtr.Zero)
				return;
			CloseHandle(handle);
			handle = IntPtr.Zero;
		}
	}
}
