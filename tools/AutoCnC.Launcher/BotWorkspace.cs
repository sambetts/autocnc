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

		/// <summary>Whether the branch reached its remote, and where, or the reason it did not.</summary>
		public readonly record struct PushOutcome(bool Pushed, string Destination, string Reason);

		/// <summary>
		/// Commits one file the checkout already tracks, when it differs from HEAD. A file that is
		/// unchanged, untracked or outside any working tree is left alone with no reason given,
		/// because there is nothing wrong with it; only a commit that was attempted and failed
		/// carries a <see cref="CommitOutcome.Reason"/>.
		/// </summary>
		/// <remarks>
		/// Tracked-only on purpose: a template a player keeps beside the checkout, or one they
		/// have not added yet, is theirs rather than history's. The commit is confined to the one
		/// path, so anything else staged or dirty in the checkout stays out of it.
		/// </remarks>
		public static CommitOutcome CommitFile(string path, string message)
		{
			if (string.IsNullOrWhiteSpace(message))
				throw new ArgumentException("A commit message is required.", nameof(message));
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return new CommitOutcome(false, null, null);

			var full = Path.GetFullPath(path);
			var directory = Path.GetDirectoryName(full);
			var name = Path.GetFileName(full);
			if (Run(directory, "rev-parse", "--is-inside-work-tree").ExitCode != 0 ||
				Run(directory, "ls-files", "--error-unmatch", "--", name).ExitCode != 0 ||
				Run(directory, "diff", "--quiet", "HEAD", "--", name).ExitCode == 0)
				return new CommitOutcome(false, null, null);

			var committed = Run(directory, "commit", "--message", message, "--", name);
			if (committed.ExitCode != 0)
				return new CommitOutcome(false, null, Explain("git commit failed", committed));

			return new CommitOutcome(true, Git(directory, "rev-parse", "--short", "HEAD"), null);
		}

		/// <summary>
		/// Publishes the checked-out branch to the remote it tracks, so a measured improvement is
		/// visible beyond this machine as soon as it is committed.
		/// </summary>
		/// <remarks>
		/// The whole branch goes, not only the latest commit, because a branch cannot be published
		/// in part: a hand-made commit or a promotion whose push failed earlier travels with the
		/// next one, which is what keeps the remote from drifting behind.
		/// <para>
		/// Only a fast-forward is attempted. A remote that has moved on is reported rather than
		/// merged, rebased or forced, because rewriting someone else's history is not bookkeeping.
		/// It never prompts either: credentials are either already cached or the push fails and
		/// says so, and a hung credential dialog would stall an unattended loop, which is why it
		/// has a deadline as well.
		/// </para>
		/// </remarks>
		public static PushOutcome Push(string root, TimeSpan timeout)
		{
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
				return new PushOutcome(false, null, "the bot workspace does not exist");

			var branch = Git(root, "symbolic-ref", "--quiet", "--short", "HEAD");
			if (string.IsNullOrEmpty(branch))
				return new PushOutcome(false, null, "the checkout is not on a branch");

			var remote = Git(root, "config", "--get", $"branch.{branch}.remote");
			if (string.IsNullOrEmpty(remote))
				return new PushOutcome(false, null, $"{branch} does not track a remote branch");

			var upstream = Git(root, "config", "--get", $"branch.{branch}.merge");
			if (string.IsNullOrEmpty(upstream))
				upstream = "refs/heads/" + branch;

			var target = upstream.StartsWith("refs/heads/", StringComparison.Ordinal)
				? upstream["refs/heads/".Length..]
				: upstream;
			var destination = $"{remote}/{target}";
			var pushed = Run(root, timeout, NonInteractive, "push", remote, "HEAD:" + upstream);
			return pushed.ExitCode == 0
				? new PushOutcome(true, destination, null)
				: new PushOutcome(false, destination, ExplainPush(pushed));
		}

		/// <summary>Stops git and its credential helper asking a question nobody is there to answer.</summary>
		static readonly (string Name, string Value)[] NonInteractive =
		[
			("GIT_TERMINAL_PROMPT", "0"),
			("GCM_INTERACTIVE", "never")
		];

		/// <summary>
		/// The line of a failed push that says why. Git leads with "To &lt;url&gt;", which is
		/// where it tried, not what went wrong.
		/// </summary>
		static string ExplainPush((int ExitCode, string Output, string Error) result)
		{
			var lines = $"{result.Error}\n{result.Output}".Split('\n')
				.Select(line => line.Trim())
				.Where(line => line.Length > 0)
				.ToList();
			var reason = lines.FirstOrDefault(line => line.StartsWith("! ", StringComparison.Ordinal)) ??
				lines.FirstOrDefault(line => line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)) ??
				lines.FirstOrDefault(line => line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) ??
				lines.FirstOrDefault(line => !line.StartsWith("To ", StringComparison.Ordinal));
			return reason ?? $"git push exited with code {result.ExitCode}";
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

		static (int ExitCode, string Output, string Error) Run(string root, params string[] arguments) =>
			Run(root, null, [], arguments);

		static (int ExitCode, string Output, string Error) Run(string root, TimeSpan? timeout,
			IReadOnlyList<(string Name, string Value)> environment, params string[] arguments)
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
				foreach (var (name, value) in environment)
					start.Environment[name] = value;

				using var process = Process.Start(start);

				// Drained concurrently, because a commit that fails writes enough to standard
				// error to fill its pipe, and a reader waiting on the other stream would deadlock.
				var error = process.StandardError.ReadToEndAsync();
				var output = process.StandardOutput.ReadToEndAsync();
				if (timeout is { } limit && !process.WaitForExit(limit))
				{
					try
					{
						process.Kill(entireProcessTree: true);
					}
					catch (InvalidOperationException)
					{
					}

					process.WaitForExit();
					return (-1, "", $"git {arguments.FirstOrDefault()} did not finish within {limit.TotalSeconds:0} seconds");
				}

				process.WaitForExit();
				return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
			}
			catch (System.ComponentModel.Win32Exception)
			{
				return (-1, "", "git is not installed, or not on PATH.");
			}
		}
	}
}
