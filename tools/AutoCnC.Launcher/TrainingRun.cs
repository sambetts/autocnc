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
using System.Text.Json;

namespace AutoCnC.Launcher
{
	public sealed class TrainingBattleConfiguration
	{
		public string Map { get; set; }
		public string Difficulty { get; set; }
		public int Opponents { get; set; }
		public string Faction { get; set; }
		public string BotFaction { get; set; }
		public string GameSpeed { get; set; }
	}

	public sealed class TrainingPlayerResult
	{
		public string Name { get; set; }
		public bool IsBot { get; set; }
		public string Outcome { get; set; }
		public int Units { get; set; }
		public int ArmyValue { get; set; }
		public int Buildings { get; set; }
		public int BaseValue { get; set; }
		public int Cash { get; set; }
		public int Killed { get; set; }
		public int Lost { get; set; }
	}

	public sealed class TrainingBattleResult
	{
		public int DurationSeconds { get; set; }
		public string LocalPlayer { get; set; }
		public string Outcome { get; set; }
		public string PlayerFeedback { get; set; }
		public List<TrainingPlayerResult> Players { get; set; } = [];
	}

	public sealed class TrainingAgentResult
	{
		public DateTime? StartedUtc { get; set; }
		public DateTime? CompletedUtc { get; set; }
		public DateTime? RestoredUtc { get; set; }
		public int? ExitCode { get; set; }
		public int ChangeCount { get; set; }
		public string Command { get; set; }
		public string SuggestedNextPrompt { get; set; }
		public bool SuggestedNextPromptAccepted { get; set; }
	}

	public sealed class TrainingRunManifest
	{
		public int SchemaVersion { get; set; } = 3;
		public string Id { get; set; }
		public string Status { get; set; }
		public DateTime CreatedUtc { get; set; }
		public DateTime? CompletedUtc { get; set; }
		public string BotPath { get; set; }
		public string BotProject { get; set; }
		public string BotDirectory { get; set; }
		public string SourceRevision { get; set; }
		public string ReplaySource { get; set; }
		public string ReplayFile { get; set; }
		public TrainingBattleConfiguration Battle { get; set; }
		public TrainingBattleResult Result { get; set; }
		public TrainingAgentResult Agent { get; set; }
		public List<string> Warnings { get; set; } = [];
	}

	/// <summary>One durable fight and all evidence needed to understand or improve it.</summary>
	public sealed class TrainingRun
	{
		static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		public const int MaxPlayerFeedbackLength = 4000;

		public TrainingRunManifest Manifest { get; }
		public string RunDirectory { get; }

		public string ManifestPath => Path.Combine(RunDirectory, "manifest.json");
		public string EvidenceDirectory => Path.Combine(RunDirectory, "evidence");
		public string FightManifestPath => Path.Combine(EvidenceDirectory, "fight.json");
		public string TelemetryPath => Path.Combine(EvidenceDirectory, "telemetry.csv");
		public string BattleLogPath => Path.Combine(EvidenceDirectory, "battle.csv");
		public string DecisionTracePath => Path.Combine(EvidenceDirectory, "decisions.jsonl");
		public string ReplayPath => Path.Combine(EvidenceDirectory, "replay.orarep");
		public string PromptPath => Path.Combine(EvidenceDirectory, "agent-prompt.txt");
		public string GameGuidePath => Path.Combine(EvidenceDirectory, "game-guide.md");
		public string GameRulesPath => Path.Combine(EvidenceDirectory, "game-rules.json");
		public string AgentConfigurationPath => Path.Combine(RunDirectory, "agent-command.json");
		public string AgentTranscriptPath => Path.Combine(RunDirectory, "agent-transcript.txt");
		public string SnapshotDirectory => Path.Combine(RunDirectory, "source-before-agent");
		public string SnapshotManifestPath => Path.Combine(RunDirectory, "source-before-agent.json");
		public string ChangesPath => Path.Combine(RunDirectory, "agent-changes.json");

		public bool IsEditable => !string.IsNullOrEmpty(Manifest.BotProject) &&
			File.Exists(Manifest.BotProject) && Directory.Exists(Manifest.BotDirectory);

		TrainingRun(string directory, TrainingRunManifest manifest)
		{
			RunDirectory = Path.GetFullPath(directory);
			Manifest = manifest;
		}

		public static string DefaultRoot => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"AutoCnC", "TrainingRuns");

		public static TrainingRun Create(string botPath, TrainingBattleConfiguration battle,
			string runsRoot = null)
		{
			var fullBotPath = Path.GetFullPath(botPath);
			var project = BotWorkspace.ResolveProject(fullBotPath);
			var sourceRoot = BotWorkspace.ResolveRoot(fullBotPath);
			var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..39];
			var directory = Path.Combine(RunsDirectoryForBot(fullBotPath, runsRoot), id);

			Directory.CreateDirectory(directory);

			var manifest = new TrainingRunManifest
			{
				Id = id,
				Status = "running",
				CreatedUtc = DateTime.UtcNow,
				BotPath = fullBotPath,
				BotProject = project,
				BotDirectory = sourceRoot,
				SourceRevision = project == null && File.Exists(fullBotPath)
					? "assembly:" + BotWorkspace.Sha256(fullBotPath)
					: BotWorkspace.SourceRevision(sourceRoot),
				Battle = battle
			};

			var run = new TrainingRun(directory, manifest);
			Directory.CreateDirectory(run.EvidenceDirectory);
			run.Save();
			return run;
		}

		public static TrainingRun Load(string directory)
		{
			if (string.IsNullOrWhiteSpace(directory))
				return null;

			var full = Path.GetFullPath(directory);
			var manifestPath = Path.Combine(full, "manifest.json");
			if (!File.Exists(manifestPath))
				return null;

			var manifest = JsonSerializer.Deserialize<TrainingRunManifest>(
				File.ReadAllText(manifestPath), JsonOptions);
			return manifest == null ? null : new TrainingRun(full, manifest);
		}

		public void Finish(string status, MatchLog matchLog, BattleEventLog battleLog)
		{
			var localName = battleLog.Sides.FirstOrDefault(s => s.IsYou)?.Name;
			var result = new TrainingBattleResult
			{
				DurationSeconds = matchLog.Duration,
				LocalPlayer = localName
			};

			foreach (var player in matchLog.Players)
			{
				var last = player.Samples.LastOrDefault();
				result.Players.Add(new TrainingPlayerResult
				{
					Name = player.Name,
					IsBot = player.IsBot,
					Outcome = player.Outcome,
					Units = last.Units,
					ArmyValue = last.Army,
					Buildings = last.Buildings,
					BaseValue = last.BaseValue,
					Cash = last.Cash,
					Killed = last.Killed,
					Lost = last.Lost
				});
			}

			result.Outcome = result.Players
				.FirstOrDefault(p => string.Equals(p.Name, localName, StringComparison.Ordinal))?.Outcome;

			if (string.IsNullOrEmpty(result.Outcome))
			{
				var over = battleLog.Events.LastOrDefault(e => e.Kind == "over").Detail;
				if (over?.StartsWith("result=", StringComparison.OrdinalIgnoreCase) == true)
					result.Outcome = over["result=".Length..];
			}

			Manifest.Status = status;
			Manifest.CompletedUtc = DateTime.UtcNow;
			Manifest.Result = result;
			Save();
		}

		public void SetPlayerFeedback(string feedback)
		{
			if (Manifest.Result == null || Manifest.CompletedUtc == null)
				throw new InvalidOperationException("Player feedback can only be saved for a completed battle.");

			var value = feedback?.Trim();
			if (value?.Length > MaxPlayerFeedbackLength)
				throw new ArgumentException(
					$"Player feedback cannot exceed {MaxPlayerFeedbackLength:N0} characters.", nameof(feedback));

			Manifest.Result.PlayerFeedback = string.IsNullOrEmpty(value) ? null : value;
			Save();
			ExportFightManifest();
		}

		public void CaptureReplay(string source)
		{
			if (string.IsNullOrEmpty(source) || !File.Exists(source))
			{
				Manifest.Warnings.Add("No replay was found for this fight.");
				Save();
				return;
			}

			try
			{
				File.Copy(source, ReplayPath, true);
				Manifest.ReplaySource = source;
				Manifest.ReplayFile = Path.GetRelativePath(RunDirectory, ReplayPath);
			}
			catch (IOException ex)
			{
				Manifest.Warnings.Add("The replay could not be copied: " + ex.Message);
			}

			Save();
		}

		public void AgentStarted(string command)
		{
			if (File.Exists(AgentTranscriptPath))
				File.Delete(AgentTranscriptPath);
			if (File.Exists(ChangesPath))
				File.Delete(ChangesPath);

			Manifest.Agent = new TrainingAgentResult
			{
				StartedUtc = DateTime.UtcNow,
				Command = command
			};
			Manifest.Status = "improving";
			Save();
		}

		public void ExportFightManifest()
		{
			Directory.CreateDirectory(EvidenceDirectory);
			File.WriteAllText(FightManifestPath, JsonSerializer.Serialize(Manifest, JsonOptions));
		}

		public void AgentFinished(int exitCode, int changeCount, string suggestedNextPrompt = null)
		{
			Manifest.Agent ??= new TrainingAgentResult();
			Manifest.Agent.CompletedUtc = DateTime.UtcNow;
			Manifest.Agent.ExitCode = exitCode;
			Manifest.Agent.ChangeCount = changeCount;
			Manifest.Agent.SuggestedNextPrompt = suggestedNextPrompt;
			Manifest.Status = exitCode == 0 ? "improved" : "improvement-failed";
			Save();
		}

		public void AcceptSuggestedNextPrompt(string approvedPrompt)
		{
			if (Manifest.Agent == null)
				throw new InvalidOperationException("This run has no next prompt to accept.");

			Manifest.Agent.SuggestedNextPrompt = approvedPrompt;
			Manifest.Agent.SuggestedNextPromptAccepted = true;
			Save();
		}

		public void MarkRestored()
		{
			Manifest.Agent ??= new TrainingAgentResult();
			Manifest.Agent.RestoredUtc = DateTime.UtcNow;
			Manifest.Status = "restored";
			Save();
		}

		public void Save()
		{
			Directory.CreateDirectory(RunDirectory);
			var temporary = ManifestPath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(Manifest, JsonOptions));
			File.Move(temporary, ManifestPath, true);
		}

		internal static string RunsDirectoryForBot(string botPath, string runsRoot = null)
		{
			var fullBotPath = Path.GetFullPath(botPath);
			var project = BotWorkspace.ResolveProject(fullBotPath);
			var botName = project != null
				? Path.GetFileNameWithoutExtension(project)
				: Path.GetFileNameWithoutExtension(fullBotPath.TrimEnd(Path.DirectorySeparatorChar));
			return Path.Combine(runsRoot ?? DefaultRoot, SafeSegment(botName));
		}

		static string SafeSegment(string value)
		{
			var invalid = Path.GetInvalidFileNameChars();
			var cleaned = new string((value ?? "bot").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
			return cleaned.Length == 0 ? "bot" : cleaned;
		}
	}
}
