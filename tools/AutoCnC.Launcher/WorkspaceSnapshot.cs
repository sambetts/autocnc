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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	public sealed class WorkspaceSnapshotEntry
	{
		public string RelativePath { get; set; }
		public string Sha256 { get; set; }
	}

	public sealed class WorkspaceSnapshotManifest
	{
		public string WorkspaceRoot { get; set; }
		public DateTime CreatedUtc { get; set; }
		public List<WorkspaceSnapshotEntry> Files { get; set; } = [];
	}

	public sealed class WorkspaceChange
	{
		public string Kind { get; set; }
		public string RelativePath { get; set; }
	}

	/// <summary>Captures, compares and safely restores the editable files in a bot workspace.</summary>
	public static class WorkspaceSnapshot
	{
		static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		public static void Capture(TrainingRun run)
		{
			if (!run.IsEditable)
				throw new InvalidOperationException("Only a battle bot project can be snapshotted.");

			if (File.Exists(run.SnapshotManifestPath))
				throw new InvalidOperationException("This training run already has a source snapshot.");

			// A failed copy never writes the manifest. Clear only that exact incomplete snapshot so
			// the user can retry after fixing the underlying I/O problem.
			if (Directory.Exists(run.SnapshotDirectory))
			{
				if ((File.GetAttributes(run.SnapshotDirectory) & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException("The incomplete snapshot path is a link or junction.");

				Directory.Delete(run.SnapshotDirectory, true);
			}

			Directory.CreateDirectory(run.SnapshotDirectory);
			var manifest = new WorkspaceSnapshotManifest
			{
				WorkspaceRoot = Path.GetFullPath(run.Manifest.BotDirectory),
				CreatedUtc = DateTime.UtcNow
			};

			foreach (var file in BotWorkspace.SourceFiles(manifest.WorkspaceRoot))
			{
				var relative = Path.GetRelativePath(manifest.WorkspaceRoot, file);
				var destination = Under(run.SnapshotDirectory, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				File.Copy(file, destination);
				manifest.Files.Add(new WorkspaceSnapshotEntry
				{
					RelativePath = relative,
					Sha256 = BotWorkspace.Sha256(destination)
				});
			}

			WriteAtomic(run.SnapshotManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
		}

		public static IReadOnlyList<WorkspaceChange> Compare(TrainingRun run)
		{
			var snapshot = Read(run);
			EnsureSameWorkspace(run, snapshot);

			var before = snapshot.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
			var current = BotWorkspace.SourceFiles(snapshot.WorkspaceRoot)
				.ToDictionary(f => Path.GetRelativePath(snapshot.WorkspaceRoot, f), StringComparer.OrdinalIgnoreCase);
			var changes = new List<WorkspaceChange>();

			foreach (var pair in current)
			{
				if (!before.TryGetValue(pair.Key, out var original))
					changes.Add(new WorkspaceChange { Kind = "added", RelativePath = pair.Key });
				else if (!string.Equals(original.Sha256, BotWorkspace.Sha256(pair.Value), StringComparison.Ordinal))
					changes.Add(new WorkspaceChange { Kind = "modified", RelativePath = pair.Key });
			}

			foreach (var original in before.Keys)
				if (!current.ContainsKey(original))
					changes.Add(new WorkspaceChange { Kind = "deleted", RelativePath = original });

			changes = changes
				.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
				.ToList();
			WriteAtomic(run.ChangesPath, JsonSerializer.Serialize(changes, JsonOptions));
			return changes;
		}

		public static IReadOnlyList<WorkspaceChange> ReadChanges(TrainingRun run)
		{
			if (!File.Exists(run.ChangesPath))
				return [];

			return JsonSerializer.Deserialize<List<WorkspaceChange>>(
				File.ReadAllText(run.ChangesPath), JsonOptions) ?? [];
		}

		public static string Fingerprint(TrainingRun run)
		{
			var snapshot = Read(run);
			using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			foreach (var entry in snapshot.Files
				.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
			{
				var relative = entry.RelativePath.Replace(Path.DirectorySeparatorChar, '/');
				aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
				aggregate.AppendData([0]);
				aggregate.AppendData(Convert.FromHexString(entry.Sha256));
			}

			return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
		}

		public static string CaptureImmutableCurrent(TrainingRun run, string destination,
			string manifestPath)
		{
			if (!run.IsEditable)
				throw new InvalidOperationException("Only a battle bot project can be snapshotted.");

			EnsureExperimentDestination(run, destination);
			var before = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			PrepareDestination(destination);
			var manifest = CopyWorkspace(run.Manifest.BotDirectory, destination);
			var after = BotWorkspace.Fingerprint(run.Manifest.BotDirectory);
			var captured = BotWorkspace.Fingerprint(destination);

			if (!string.Equals(before, after, StringComparison.Ordinal) ||
				!string.Equals(before, captured, StringComparison.Ordinal))
			{
				DeleteTree(destination);
				throw new InvalidOperationException(
					"The bot workspace changed while its immutable candidate snapshot was being captured.");
			}

			WriteAtomic(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
			MakeReadOnly(destination);
			return captured;
		}

		public static string MaterializeImmutableChampion(TrainingRun run, string destination,
			string manifestPath)
		{
			EnsureExperimentDestination(run, destination);
			var snapshot = Read(run);
			EnsureSameWorkspace(run, snapshot);
			PrepareDestination(destination);

			var materialized = new WorkspaceSnapshotManifest
			{
				WorkspaceRoot = Path.GetFullPath(destination),
				CreatedUtc = DateTime.UtcNow
			};

			foreach (var entry in snapshot.Files)
			{
				var source = Under(run.SnapshotDirectory, entry.RelativePath);
				if (!File.Exists(source) ||
					!string.Equals(BotWorkspace.Sha256(source), entry.Sha256,
						StringComparison.OrdinalIgnoreCase))
					throw new InvalidDataException(
						$"The champion snapshot file '{entry.RelativePath}' is missing or changed.");

				var copy = Under(destination, entry.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(copy));
				File.Copy(source, copy);
				materialized.Files.Add(new WorkspaceSnapshotEntry
				{
					RelativePath = entry.RelativePath,
					Sha256 = entry.Sha256
				});
			}

			WriteAtomic(manifestPath, JsonSerializer.Serialize(materialized, JsonOptions));
			var fingerprint = BotWorkspace.Fingerprint(destination);
			MakeReadOnly(destination);
			return fingerprint;
		}

		public static void Restore(TrainingRun run)
		{
			if (run.IsBusy && !ProcessOwnership.IsCurrent(run.Manifest.Owner))
				throw new InvalidOperationException(
					"Another launcher still owns this source snapshot.");

			var snapshot = Read(run);
			EnsureSameWorkspace(run, snapshot);
			var changes = Compare(run);

			foreach (var added in changes.Where(c => c.Kind == "added"))
			{
				var path = Under(snapshot.WorkspaceRoot, added.RelativePath);
				if (File.Exists(path))
					File.Delete(path);
			}

			foreach (var entry in snapshot.Files)
			{
				var source = Under(run.SnapshotDirectory, entry.RelativePath);
				var destination = Under(snapshot.WorkspaceRoot, entry.RelativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				File.Copy(source, destination, true);
			}

			WriteAtomic(run.ChangesPath, "[]");
			run.MarkRestored();
		}

		static WorkspaceSnapshotManifest Read(TrainingRun run)
		{
			if (!File.Exists(run.SnapshotManifestPath))
				throw new InvalidOperationException("This training run has no source snapshot.");

			return JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
				File.ReadAllText(run.SnapshotManifestPath), JsonOptions)
				?? throw new InvalidDataException("The source snapshot manifest is invalid.");
		}

		static void EnsureSameWorkspace(TrainingRun run, WorkspaceSnapshotManifest snapshot)
		{
			if (!string.Equals(Path.GetFullPath(run.Manifest.BotDirectory),
				Path.GetFullPath(snapshot.WorkspaceRoot), StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The snapshot belongs to a different bot workspace.");
		}

		static WorkspaceSnapshotManifest CopyWorkspace(string sourceRoot, string destination)
		{
			var manifest = new WorkspaceSnapshotManifest
			{
				WorkspaceRoot = Path.GetFullPath(destination),
				CreatedUtc = DateTime.UtcNow
			};

			foreach (var file in BotWorkspace.SourceFiles(sourceRoot))
			{
				var relative = Path.GetRelativePath(sourceRoot, file);
				var copy = Under(destination, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(copy));
				File.Copy(file, copy);
				manifest.Files.Add(new WorkspaceSnapshotEntry
				{
					RelativePath = relative,
					Sha256 = BotWorkspace.Sha256(copy)
				});
			}

			return manifest;
		}

		static void EnsureExperimentDestination(TrainingRun run, string destination)
		{
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.ExperimentDirectory));
			var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
			if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase) ||
				!full.StartsWith(root + Path.DirectorySeparatorChar,
					StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(
					"Immutable evaluation snapshots must stay inside the experiment directory.");
		}

		static void PrepareDestination(string destination)
		{
			if (Directory.Exists(destination))
				DeleteTree(destination);
			Directory.CreateDirectory(destination);
		}

		static void MakeReadOnly(string directory)
		{
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
		}

		static void DeleteTree(string directory)
		{
			if (!Directory.Exists(directory))
				return;

			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
			Directory.Delete(directory, recursive: true);
		}

		static string Under(string root, string relative)
		{
			var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
			var prefix = fullRoot + Path.DirectorySeparatorChar;

			if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException($"'{relative}' points outside the expected directory.");

			return full;
		}

		static void WriteAtomic(string path, string content)
		{
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, content);
			File.Move(temporary, path, true);
		}
	}
}
