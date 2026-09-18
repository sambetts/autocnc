// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.ComponentModel;
using System.Diagnostics;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Names the launcher process that claimed a piece of work, and says whether it is still there.
	/// </summary>
	/// <remarks>
	/// Work under way is recorded on disk, so a launcher that is killed or crashes mid-fight leaves
	/// that record behind with nobody left to finish it. Read on its own the leftover says "busy",
	/// and it says so for good: the next launcher has no way to tell a battle that is still being
	/// fought from one whose launcher died months ago, so it treats both as untouchable. Naming the
	/// owner makes the difference legible. A claim held by a process that is no longer running is a
	/// leftover, whoever reads it and however long ago that process went away.
	/// </remarks>
	public sealed class ProcessOwnership
	{
		/// <summary>
		/// How far a recorded start time may drift from the one Windows reports before the process
		/// is taken to be a different one wearing a recycled id.
		/// </summary>
		const double StartToleranceSeconds = 2;

		public int ProcessId { get; set; }

		/// <summary>
		/// When the owner started, which is what actually identifies it: process ids are recycled,
		/// and an unrelated program inheriting one must not inherit the claim with it.
		/// </summary>
		public DateTime? StartedUtc { get; set; }

		public string ProcessName { get; set; }
		public DateTime ClaimedUtc { get; set; }

		/// <summary>Claims work for the running launcher.</summary>
		public static ProcessOwnership Claim()
		{
			using var process = Process.GetCurrentProcess();
			return new ProcessOwnership
			{
				ProcessId = process.Id,
				StartedUtc = StartTime(process),
				ProcessName = process.ProcessName,
				ClaimedUtc = DateTime.UtcNow
			};
		}

		/// <summary>True only while the process that made this claim is still running.</summary>
		/// <remarks>
		/// An absent claim counts as gone rather than present. Sessions recorded before claims were
		/// kept have no owner at all, and treating those as forever busy is the fault this exists to
		/// cure; an unowned leftover has nobody coming back to finish it either.
		/// </remarks>
		public static bool IsLive(ProcessOwnership claim)
		{
			if (claim == null || claim.ProcessId <= 0)
				return false;

			try
			{
				// No process carries the id any more if this throws, which is the answer we want.
				using var process = Process.GetProcessById(claim.ProcessId);
				if (!string.IsNullOrEmpty(claim.ProcessName) &&
					!string.Equals(process.ProcessName, claim.ProcessName, StringComparison.OrdinalIgnoreCase))
					return false;

				var started = StartTime(process);

				// An unreadable start time is not evidence the owner has gone, so the claim stands.
				return started == null || claim.StartedUtc == null ||
					Math.Abs((started.Value - claim.StartedUtc.Value).TotalSeconds) <= StartToleranceSeconds;
			}
			catch (ArgumentException)
			{
				return false;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}

		static DateTime? StartTime(Process process)
		{
			try
			{
				return process.StartTime.ToUniversalTime();
			}
			catch (Win32Exception)
			{
				// Another account's process, or one that ended while it was being read.
				return null;
			}
			catch (InvalidOperationException)
			{
				return null;
			}
			catch (NotSupportedException)
			{
				return null;
			}
		}
	}
}
