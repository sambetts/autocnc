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
				// New bots have no test project, but one made before they were dropped still can,
				// and picking it as the bot would deploy a test assembly into the game.
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

		public static string Fingerprint(string root)
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

		/// <summary>Whether a commit was made, and the revision or the reason there was none.</summary>
		public readonly record struct CommitOutcome(bool Committed, string Revision, string Reason);

		/// <summary>Records the bot workspace in Git so a measured improvement outlives the tree.</summary>
		/// <remarks>
		/// Both the staging and the commit are confined to the workspace pathspec. The engine
		/// submodule carries an applied source patch at all times and unrelated work may already
		/// be staged elsewhere in the checkout; neither may ride along with a promotion just
		/// because it happened to be dirty when the benchmark finished.
		/// <para>
		/// A checkout without Git, or a workspace that already matches HEAD, is reported rather
		/// than thrown: training has done its job by then, and refusing to continue because the
		/// bookkeeping step found nothing to record would throw away a promotion that succeeded.
		/// </para>
		/// </remarks>
		public static CommitOutcome Commit(string root, string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				throw new ArgumentException("A commit message is required.", nameof(message));
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
				return new CommitOutcome(false, null, "the bot workspace does not exist");

			if (Run(root, "rev-parse", "--is-inside-work-tree").ExitCode != 0)
				return new CommitOutcome(false, null, "the bot workspace is not inside a Git working tree");

			var staged = Run(root, "add", "--all", "--", ".");
			if (staged.ExitCode != 0)
				return new CommitOutcome(false, null, Explain("the workspace could not be staged", staged));

			// --quiet turns this into a question: it exits 0 when nothing is staged under the
			// pathspec and 1 when something is, so an empty promotion is recognised before a
			// commit that git would refuse anyway.
			if (Run(root, "diff", "--cached", "--quiet", "--", ".").ExitCode == 0)
				return new CommitOutcome(false, null, "the bot workspace already matches the last commit");

			var committed = Run(root, "commit", "--message", message, "--", ".");
			if (committed.ExitCode != 0)
				return new CommitOutcome(false, null, Explain("git commit failed", committed));

			return new CommitOutcome(true, Git(root, "rev-parse", "--short", "HEAD"), null);
		}

		static string Explain(string summary, (int ExitCode, string Output, string Error) result)
		{
			var detail = result.Error?.Trim();
			if (string.IsNullOrEmpty(detail))
				detail = result.Output?.Trim();
			if (string.IsNullOrEmpty(detail))
				return summary;

			return summary + ": " + detail.Split('\n')[0].Trim();
		}

		static string Git(string root, params string[] arguments)
		{
			var result = Run(root, arguments);
			return result.ExitCode == 0 ? result.Output.Trim() : null;
		}

		static (int ExitCode, string Output, string Error) Run(string root, params string[] arguments)
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

				// Drained concurrently, because a commit that fails writes enough to standard
				// error to fill its pipe, and a reader waiting on the other stream would deadlock.
				var error = process.StandardError.ReadToEndAsync();
				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				return (process.ExitCode, output, error.GetAwaiter().GetResult());
			}
			catch (System.ComponentModel.Win32Exception)
			{
				return (-1, "", "git is not installed, or not on PATH.");
			}
		}
	}
}
