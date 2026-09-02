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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AutoCnC.Launcher
{
	/// <summary>Resolves the editable part of a bot and the source files an iteration may change.</summary>
	public static class BotWorkspace
	{
		static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
		{
			".git", ".idea", ".vs", ".autocnc", "bin", "obj", "TestResults", "packages"
		};

		public static string ResolveProject(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return null;

			var full = Path.GetFullPath(path);
			if (File.Exists(full))
				return string.Equals(Path.GetExtension(full), ".csproj", StringComparison.OrdinalIgnoreCase)
					? full
					: null;

			if (!Directory.Exists(full))
				return null;

			return Directory.EnumerateFiles(full, "*.csproj", SearchOption.TopDirectoryOnly)
				.Where(p => !p.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		}

		public static string ResolveRoot(string path)
		{
			var project = ResolveProject(path);
			if (project != null)
				return Path.GetDirectoryName(project);

			var full = Path.GetFullPath(path);
			return Directory.Exists(full) ? full : Path.GetDirectoryName(full);
		}

		public static IReadOnlyList<string> SourceFiles(string root)
		{
			var fullRoot = Path.GetFullPath(root);
			if (string.Equals(
				Path.TrimEndingDirectorySeparator(fullRoot),
				Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullRoot)),
				StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("A bot workspace cannot be a filesystem root.");

			var files = new List<string>();
			var pending = new Stack<string>();
			pending.Push(fullRoot);

			while (pending.Count > 0)
			{
				var directory = pending.Pop();

				foreach (var child in Directory.EnumerateDirectories(directory))
				{
					var info = new DirectoryInfo(child);
					if (!ExcludedDirectories.Contains(info.Name) &&
						(info.Attributes & FileAttributes.ReparsePoint) == 0)
						pending.Push(child);
				}

				foreach (var file in Directory.EnumerateFiles(directory))
					if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
						files.Add(file);
			}

			files.Sort(StringComparer.OrdinalIgnoreCase);
			return files;
		}

		public static string SourceRevision(string root)
		{
			var revision = Git(root, "rev-parse", "HEAD");
			if (revision != null)
			{
				var status = Git(root, "status", "--porcelain", "--untracked-files=normal", "--", ".");
				return status?.Length > 0 ? revision + "-dirty:" + Fingerprint(root) : revision;
			}

			return "tree:" + Fingerprint(root);
		}

		static string Fingerprint(string root)
		{
			using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			foreach (var file in SourceFiles(root))
			{
				var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
				aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
				aggregate.AppendData([0]);

				using var stream = File.OpenRead(file);
				aggregate.AppendData(SHA256.HashData(stream));
			}

			return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
		}

		public static string Sha256(string file)
		{
			using var stream = File.OpenRead(file);
			return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}

		static string Git(string root, params string[] arguments)
		{
			try
			{
				var start = new ProcessStartInfo
				{
					FileName = "git",
					WorkingDirectory = root,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};

				start.ArgumentList.Add("-C");
				start.ArgumentList.Add(root);
				foreach (var argument in arguments)
					start.ArgumentList.Add(argument);

				using var process = Process.Start(start);
				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				return process.ExitCode == 0 ? output.Trim() : null;
			}
			catch (System.ComponentModel.Win32Exception)
			{
				return null;
			}
		}
	}
}
