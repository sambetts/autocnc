// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	/// <summary>What put a prompt revision into the archive.</summary>
	public enum PromptOrigin
	{
		/// <summary>The repository's starting template, kept so later revisions have something to diff against.</summary>
		Baseline,

		/// <summary>The player read the agent's proposal and approved it.</summary>
		Manual,

		/// <summary>
		/// Unattended training adopted the agent's proposal without review: every revision the
		/// PowerShell training loop takes, and those the launcher's continuous mode took before it
		/// began keeping proposals as drafts.
		/// </summary>
		Continuous
	}

	/// <summary>One template the improvement loop adopted, and the fight that argued for it.</summary>
	public sealed class PromptRevision
	{
		public int Revision { get; set; }
		public DateTime RecordedUtc { get; set; }
		public string Origin { get; set; }
		public string File { get; set; }
		public string Sha256 { get; set; }
		public int Length { get; set; }
		public string Bot { get; set; }
		public string RunId { get; set; }
		public string BattleOutcome { get; set; }
	}

	public sealed class PromptHistoryIndex
	{
		public int SchemaVersion { get; set; } = 1;
		public List<PromptRevision> Revisions { get; set; } = [];
	}

	/// <summary>
	/// An append-only archive of every agent prompt template the improvement loop has adopted.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Each round ends with the agent proposing a whole replacement template, and accepting one
	/// overwrites <see cref="LauncherSettings.AgentPromptTemplate"/> — the only copy there is. The
	/// round that proposed it keeps its own copy in the training run's manifest, but that goes as
	/// soon as the player deletes the session, and a continuous loop left running overnight
	/// replaces the prompt many times without anyone reading a single one. So by the time the
	/// prompts are worth studying, every version but the last has been destroyed, which is exactly
	/// the record needed to tell a loop that is sharpening its instructions from one that is
	/// slowly wandering away from them.
	/// </para>
	/// <para>
	/// Revisions are therefore written as separate numbered plain-text files rather than as blobs
	/// inside the index: the point of keeping them is to read and diff them with ordinary tools.
	/// The index carries only the provenance that the text cannot show on its own.
	/// </para>
	/// <para>
	/// This lives beside the settings and training runs in the user profile for the same reason
	/// they do — it is one player's working state, and a shared checkout should not have two
	/// people overwriting each other's prompt lineage.
	/// </para>
	/// </remarks>
	public sealed class PromptHistory
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		public string Root { get; }

		public string IndexPath => Path.Combine(Root, "index.json");

		public PromptHistory(string root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);

		public static string DefaultRoot => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"AutoCnC", "PromptHistory");

		/// <summary>
		/// Every revision in adoption order. An unreadable index reads as empty rather than
		/// throwing, because losing the archive must not also stop new prompts being recorded.
		/// </summary>
		public PromptHistoryIndex Read()
		{
			try
			{
				if (File.Exists(IndexPath))
					return JsonSerializer.Deserialize<PromptHistoryIndex>(
						File.ReadAllText(IndexPath), JsonOptions) ?? new PromptHistoryIndex();
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (JsonException)
			{
			}

			return new PromptHistoryIndex();
		}

		/// <summary>Reads back the text of a recorded revision.</summary>
		public string TextOf(PromptRevision revision)
		{
			var path = PathOf(revision);
			return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
		}

		public string PathOf(PromptRevision revision) =>
			string.IsNullOrWhiteSpace(revision?.File) ||
				revision.File != Path.GetFileName(revision.File)
				? null
				: Path.Combine(Root, revision.File);

		/// <summary>
		/// Adds a template to the archive, returning the revision it became — or null when there
		/// was nothing to add.
		/// </summary>
		/// <remarks>
		/// A template identical to the newest one is not a new revision. Agents regularly propose
		/// a template byte-for-byte identical to the one they were given, and the player can press
		/// the accept button twice; recording those would fill the archive with revisions that
		/// differ only in their timestamp and bury the rounds that actually changed something.
		/// </remarks>
		public PromptRevision Record(string template, PromptOrigin origin, TrainingRun run = null)
		{
			var text = template?.Trim();
			if (string.IsNullOrEmpty(text))
				return null;

			var index = Read();
			var digest = Sha256(text);
			var previous = index.Revisions.LastOrDefault();
			if (string.Equals(previous?.Sha256, digest, StringComparison.OrdinalIgnoreCase))
				return null;

			var number = Math.Max(0, previous?.Revision ?? 0) + 1;
			var revision = new PromptRevision
			{
				Revision = number,
				RecordedUtc = DateTime.UtcNow,
				Origin = origin.ToString().ToLowerInvariant(),
				File = $"{number:D4}-{origin.ToString().ToLowerInvariant()}.txt",
				Sha256 = digest,
				Length = text.Length,
				Bot = BotName(run),
				RunId = run?.Manifest.Id,
				BattleOutcome = run?.Manifest.Result?.Outcome
			};

			Directory.CreateDirectory(Root);

			// An earlier attempt that wrote its text but failed before the index was updated left a
			// file claiming this number. Clearing them keeps one file per revision, so a folder
			// listing stays a truthful account of the lineage.
			foreach (var orphan in Directory.EnumerateFiles(Root, $"{number:D4}-*.txt")
				.Where(file => !string.Equals(Path.GetFileName(file), revision.File,
					StringComparison.OrdinalIgnoreCase))
				.ToList())
				File.Delete(orphan);

			File.WriteAllText(Path.Combine(Root, revision.File), text);
			index.Revisions.Add(revision);
			WriteIndex(index);
			WriteReadMe();
			return revision;
		}

		void WriteIndex(PromptHistoryIndex index)
		{
			var temporary = IndexPath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(index, JsonOptions));
			File.Move(temporary, IndexPath, true);
		}

		/// <summary>
		/// Explains the folder to whoever opens it, which — since nothing in the launcher displays
		/// this archive — is the only explanation they are going to get.
		/// </summary>
		/// <remarks>
		/// Refreshed whenever its text differs rather than written once, because it says what each
		/// origin means and that has changed: <c>continuous</c> was legacy-only until the unattended
		/// loop began adopting prompts again.
		/// </remarks>
		void WriteReadMe()
		{
			var path = Path.Combine(Root, "README.txt");
			if (File.Exists(path) && string.Equals(File.ReadAllText(path), ReadMeText, StringComparison.Ordinal))
				return;

			File.WriteAllText(path, ReadMeText);
		}

		const string ReadMeText =
				"""
				AutoC&C prompt history
				======================

				Every agent prompt template the improvement loop has adopted, oldest first. Each
				improvement round ends with the agent proposing a complete replacement template;
				accepting one overwrites the current prompt, so this folder is the only record of
				how the prompt reached its present form.

				  NNNN-<origin>.txt   The template as adopted. Diff consecutive files to see what
				                      a round changed.
				  index.json          When each revision was adopted, whether a player approved it
				                      (manual) or continuous improvement did (continuous), and the
				                      bot, recorded session and battle outcome behind it.

				Origins:
				  baseline     The template in force before a revision replaced it, usually
				               docs/agent-prompt-template.md, recorded so the next revision has
				               something to diff against.
				  manual       The player reviewed and approved the proposal.
				  continuous   Unattended training adopted it unreviewed: scripts/train-loop.ps1
				               takes every valid proposal for the round after.

				A template identical to the previous one is not recorded, so consecutive files
				always differ.

				These are templates, with placeholders such as {workspace} and {telemetry} still
				in place. The exact instruction sent for one fight, with those filled in, is kept
				with that fight as evidence/agent-prompt.txt under %LOCALAPPDATA%\AutoCnC\TrainingRuns.

				Nothing here is read back by the launcher: deleting or editing these files changes
				no behaviour, it only loses the history.
				""";

		static string BotName(TrainingRun run)
		{
			var project = run?.Manifest.BotProject;
			if (!string.IsNullOrWhiteSpace(project))
				return Path.GetFileNameWithoutExtension(project);

			var path = run?.Manifest.BotPath;
			return string.IsNullOrWhiteSpace(path)
				? null
				: Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar));
		}

		static string Sha256(string value) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
	}
}
