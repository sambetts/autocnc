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
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Holds the per-run cross-process lock while a freshly reloaded manifest is checked and
	/// mutated.
	/// </summary>
	public sealed class TrainingRunMutation : IDisposable
	{
		readonly FileStream handle;

		public TrainingRun Run { get; }

		internal TrainingRunMutation(TrainingRun run, FileStream handle)
		{
			Run = run;
			this.handle = handle;
		}

		public static string LockPathFor(string runDirectory)
		{
			var canonical = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(runDirectory)).ToUpperInvariant();
			var hash = Convert.ToHexString(
				SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"AutoCnC", "RunLocks", hash + ".experiment.lock");
		}

		public void Dispose() => handle.Dispose();
	}
}
