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
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Holds the canonical workspace lock. Reentrant leases in one launcher share one OS handle;
	/// another process cannot open the same lock file until the final lease is disposed.
	/// </summary>
	public sealed class TrainingWorkspaceMutation : IDisposable
	{
		sealed class SharedLock
		{
			public FileStream Handle { get; init; }
			public int Count { get; set; }
		}

		static readonly object Gate = new();
		static readonly Dictionary<string, SharedLock> Held =
			new(StringComparer.OrdinalIgnoreCase);

		readonly string key;
		bool disposed;

		public string WorkspaceRoot { get; }
		public string LockPath { get; }

		TrainingWorkspaceMutation(string workspaceRoot, string lockPath, string key)
		{
			WorkspaceRoot = workspaceRoot;
			LockPath = lockPath;
			this.key = key;
		}

		public static TrainingWorkspaceMutation Acquire(string workspaceRoot)
		{
			if (string.IsNullOrWhiteSpace(workspaceRoot))
				throw new InvalidOperationException(
					"The training run has no workspace to lock.");

			var canonical = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(workspaceRoot));
			var key = canonical.ToUpperInvariant();
			var lockPath = LockPathFor(canonical);

			lock (Gate)
			{
				if (Held.TryGetValue(key, out var shared))
				{
					shared.Count++;
					return new TrainingWorkspaceMutation(canonical, lockPath, key);
				}

				Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
				var handle = new FileStream(lockPath, FileMode.OpenOrCreate,
					FileAccess.ReadWrite, FileShare.None, bufferSize: 1,
					FileOptions.WriteThrough);
				Held[key] = new SharedLock { Handle = handle, Count = 1 };
				return new TrainingWorkspaceMutation(canonical, lockPath, key);
			}
		}

		public static string LockPathFor(string workspaceRoot)
		{
			var canonical = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(workspaceRoot)).ToUpperInvariant();
			var hash = Convert.ToHexString(
				SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"AutoCnC", "WorkspaceLocks", hash + ".lock");
		}

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;

			lock (Gate)
			{
				if (!Held.TryGetValue(key, out var shared))
					return;
				if (--shared.Count > 0)
					return;

				Held.Remove(key);
				shared.Handle.Dispose();
			}
		}
	}
}
