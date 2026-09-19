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
		public string ExecutionMode { get; set; }
	}

	public sealed class TrainingSimulationPerformance
	{
		public int SchemaVersion { get; set; }
		public string Mode { get; set; }
		public string Status { get; set; }
		public DateTime StartedUtc { get; set; }
		public DateTime CompletedUtc { get; set; }
		public long ElapsedMilliseconds { get; set; }
		public int WorldTicks { get; set; }
		public long LogicAttempts { get; set; }
		public double TicksPerSecond { get; set; }
		public double SimulationSpeed { get; set; }
		public double GameSeconds { get; set; }
		public int MaxGameSeconds { get; set; }
		public string Result { get; set; }
		public string Error { get; set; }
	}

	/// <summary>
	/// One side's numbers from a finished battle.
	/// </summary>
	/// <remarks>
	/// The headline figures are taken from the moment the match was decided rather than from its
	/// final instant. A beaten player has every actor they own destroyed by the engine the moment
	/// they lose, and the match carries on being recorded until the survivors are done, so the
	/// last instant of a defeat is uniformly zero and says nothing about how the battle went. The
	/// peaks sit alongside them because a bot that massed forty units and then threw them away is
	/// a different problem from one that never built a fifth.
	/// </remarks>
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
		public int PeakUnits { get; set; }
		public int PeakArmyValue { get; set; }
		public int PeakBuildings { get; set; }
		public int PeakBaseValue { get; set; }
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
		/// <summary>
		/// <see cref="ChangeCount"/> when the workspace could not be compared at all.
		/// </summary>
		/// <remarks>
		/// Distinct from zero on purpose. Zero is a comparison that ran and found the workspace
		/// untouched, which is a reason to say nothing; this is a comparison that never happened,
		/// which is a reason to be careful — the edits may be there and simply could not be seen.
		/// </remarks>
		public const int UnknownChangeCount = -1;

		public int Attempt { get; set; }
		public DateTime? StartedUtc { get; set; }
		public DateTime? CompletedUtc { get; set; }
		public DateTime? RestoredUtc { get; set; }
		public int? ExitCode { get; set; }

		/// <summary>
		/// True when the player stopped this attempt rather than it going wrong.
		/// </summary>
		/// <remarks>
		/// Stopping kills the process tree, so the exit code that comes back is whatever Windows
		/// chose and is indistinguishable from a crash. Without this flag the next attempt opens
		/// as an investigation into a failure that never happened.
		/// </remarks>
		public bool Cancelled { get; set; }

		/// <summary>
		/// True when this attempt was sent to repair a failure rather than to make a fresh
		/// improvement.
		/// </summary>
		/// <remarks>
		/// Stopping one of these does not undo the reason it was started, so the failure it was
		/// sent to fix is still there and the next attempt has to be told. Without this, stopping
		/// a repair would read as "nothing is wrong" and quietly lose the original fault.
		/// </remarks>
		public bool Repairing { get; set; }

		public int ChangeCount { get; set; }
		public string Command { get; set; }
		public string SuggestedNextPrompt { get; set; }
		public bool SuggestedNextPromptAccepted { get; set; }

		/// <summary>
		/// True when the player read this round's proposed prompt and declined to adopt it.
		/// </summary>
		/// <remarks>
		/// The proposal itself is kept rather than cleared: a round that argued for a bad prompt
		/// is evidence about that round, and losing it would leave a session whose agent finished
		/// successfully looking as though it had proposed nothing at all. What the flag buys is
		/// that reopening the session stops presenting a settled question as outstanding.
		/// </remarks>
		public bool SuggestedNextPromptRejected { get; set; }
		public string FailurePhase { get; set; }
		public string FailureMessage { get; set; }
		public string RecoveryTranscript { get; set; }
	}

	public sealed class TrainingAgentExecutionStatus
	{
		public string State { get; set; }
		public string Phase { get; set; }
		public int? AgentExitCode { get; set; }
		public int? VerificationExitCode { get; set; }
		public string Message { get; set; }
	}

	public static class TrainingExperimentStates
	{
		public const string Prepared = "prepared";
		public const string Improving = "improving";
		public const string Candidate = "candidate";
		public const string Failed = "failed";
		public const string Evaluating = "evaluating";
		public const string Promoted = "promoted";
		public const string Restoring = "restoring";
		public const string Restored = "restored";
		public const string Aborted = "aborted";
	}

	/// <summary>Durable promotion state for one continuous candidate.</summary>
	public sealed class TrainingExperiment
	{
		public int SchemaVersion { get; set; } = 1;
		public string Id { get; set; }
		public bool? Continuous { get; set; }
		public string State { get; set; }
		public DateTime? StartedUtc { get; set; }
		public DateTime? EvaluationStartedUtc { get; set; }
		public DateTime? EvaluationCompletedUtc { get; set; }
		public DateTime? PromotedUtc { get; set; }
		public DateTime? RestoredUtc { get; set; }
		public DateTime? InvalidatedUtc { get; set; }
		public DateTime? AbortedUtc { get; set; }
		public int EvaluationAttempt { get; set; }
		public string ChampionSourceRevision { get; set; }
		public string CandidateSourceRevision { get; set; }
		public string ControlRevision { get; set; }
		public string ChampionFingerprint { get; set; }
		public string CandidateFingerprint { get; set; }
		public string ExpectedLiveFingerprint { get; set; }
		public string ChampionSnapshotFile { get; set; }
		public string ChampionSourceManifestFile { get; set; }
		public string CandidateSourceManifestFile { get; set; }
		public string CandidateAssemblyFile { get; set; }
		public string CandidateAssemblySha256 { get; set; }
		public string ControlAssemblyFile { get; set; }
		public string ControlAssemblySha256 { get; set; }
		public string CandidateBenchmarkResultFile { get; set; }
		public string ControlBenchmarkResultFile { get; set; }
		public string BenchmarkResultFile { get; set; }
		public string EvaluationFile { get; set; }
		public string Benchmark { get; set; }
		public string RequestedBenchmark { get; set; }
		public string RequestedDifficulty { get; set; }
		public string Batch { get; set; }
		public int? ExpectedMatchesPerArm { get; set; }
		public string CandidateBatch { get; set; }
		public string ControlBatch { get; set; }
		public string Decision { get; set; }
		public string Reason { get; set; }
		public string InvalidationReason { get; set; }
		public string AbortReason { get; set; }
		public bool? RequiresReevaluation { get; set; }
		public bool? CanResumeEvaluation { get; set; }

		/// <summary>
		/// True records that continuous mode kept the current prompt until a player reviewed the
		/// proposal. Nullable so manifests written before this policy remain valid.
		/// </summary>
		public bool? PromptRewriteFrozen { get; set; }
	}

	public sealed class TrainingRunManifest
	{
		public int SchemaVersion { get; set; } = 11;
		public string Id { get; set; }
		public string Status { get; set; }

		/// <summary>
		/// The coding agent's conversation, shared by every turn this fight ever produces.
		/// </summary>
		/// <remarks>
		/// One id per fight rather than one per attempt, so the improvement round, the repair that
		/// follows a failure, and anything the player types before or after all land in the same
		/// conversation. An agent that already remembers what it changed and why does not have to
		/// be re-taught it, and the player is talking to the same correspondent throughout instead
		/// of to a stranger who has read the same files.
		/// </remarks>
		public string AgentSessionId { get; set; }
		public DateTime CreatedUtc { get; set; }
		public DateTime? CompletedUtc { get; set; }
		public string BotPath { get; set; }
		public string BotProject { get; set; }
		public string BotDirectory { get; set; }
		public string SourceRevision { get; set; }
		public string ReplaySource { get; set; }
		public string ReplayFile { get; set; }
		public DateTime? ReplayWatchedUtc { get; set; }
		public TrainingBattleConfiguration Battle { get; set; }
		public TrainingBattleResult Result { get; set; }
		public TrainingSimulationPerformance Performance { get; set; }
		public TrainingAgentResult Agent { get; set; }
		public TrainingExperiment Experiment { get; set; }

		/// <summary>
		/// The launcher that is fighting this battle or running its improvement, while one is.
		/// </summary>
		/// <remarks>
		/// Cleared the moment the work ends, so anything left here alongside unfinished work names
		/// the process to ask about it. Absent in sessions written before claims were kept, and
		/// absent is read as "nobody", which is the truth for every one of them: the launcher that
		/// wrote them exited long ago.
		/// </remarks>
		public ProcessOwnership Owner { get; set; }

		public List<string> Warnings { get; set; } = [];
	}

	/// <summary>One durable fight and all evidence needed to understand or improve it.</summary>
	public sealed class TrainingRun
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		public const int MaxPlayerFeedbackLength = 4000;

		public TrainingRunManifest Manifest { get; }
		public string RunDirectory { get; }

		public string ManifestPath => Path.Combine(RunDirectory, "manifest.json");
		public string EvidenceDirectory => Path.Combine(RunDirectory, "evidence");
		public string FightManifestPath => Path.Combine(EvidenceDirectory, "fight.json");
		public string TelemetryPath => Path.Combine(EvidenceDirectory, "telemetry.csv");
		public string BattleLogPath => Path.Combine(EvidenceDirectory, "battle.csv");
		public string DecisionTracePath => Path.Combine(EvidenceDirectory, "decisions.jsonl");

		/// <summary>Public map facts, written by the engine once the world has loaded.</summary>
		public string MapFactsPath => Path.Combine(EvidenceDirectory, "map.json");

		/// <summary>Precomputed per-fight aggregates, derived after the match by AutoCnC.Evidence.</summary>
		public string SummaryPath => Path.Combine(EvidenceDirectory, "summary.json");

		/// <summary>One row per unit, whole lifecycle.</summary>
		public string UnitsPath => Path.Combine(EvidenceDirectory, "units.csv");

		/// <summary>Falsifiable checks a round writes for the next one to be measured against.</summary>
		public string ChecksPath => Path.Combine(EvidenceDirectory, "checks.json");

		public string CheckResultsPath => Path.Combine(EvidenceDirectory, "check-results.json");
		public string TrendPath => Path.Combine(EvidenceDirectory, "trend.json");
		public string PerformancePath => Path.Combine(EvidenceDirectory, "performance.json");
		public string ReplayPath => Path.Combine(EvidenceDirectory, "replay.orarep");
		public string CancellationPath => Path.Combine(RunDirectory, "cancel.request");
		public string PromptPath => Path.Combine(EvidenceDirectory, "agent-prompt.txt");
		public string GameGuidePath => Path.Combine(EvidenceDirectory, "game-guide.md");
		public string MechanicsPath => Path.Combine(EvidenceDirectory, "mechanics.md");
		public string GameRulesPath => Path.Combine(EvidenceDirectory, "game-rules.json");
		public string AgentConfigurationPath => Path.Combine(RunDirectory, "agent-command.json");
		public string AgentTranscriptPath => Path.Combine(RunDirectory, "agent-transcript.txt");
		public string AgentStatusPath => Path.Combine(RunDirectory, "agent-status.json");
		public string ChatPath => Path.Combine(RunDirectory, "agent-chat.jsonl");
		public string ChatMessagePath => Path.Combine(RunDirectory, "agent-chat-message.txt");
		public string ChatTranscriptPath => Path.Combine(RunDirectory, "agent-chat-turn.txt");
		public string AttemptsDirectory => Path.Combine(EvidenceDirectory, "attempts");
		public string SnapshotDirectory => Path.Combine(RunDirectory, "source-before-agent");
		public string SnapshotManifestPath => Path.Combine(RunDirectory, "source-before-agent.json");
		public string ChangesPath => Path.Combine(RunDirectory, "agent-changes.json");
		public string MutationLockPath => Path.Combine(RunDirectory, "experiment.lock");
		public string ExperimentDirectory => Path.Combine(RunDirectory, "experiment");
		public string CandidateSourceDirectory => Path.Combine(ExperimentDirectory, "candidate-source");
		public string ControlSourceDirectory => Path.Combine(ExperimentDirectory, "control-source");
		public string CandidateSourceManifestPath => Path.Combine(ExperimentDirectory, "candidate-source.json");
		public string ControlSourceManifestPath => Path.Combine(ExperimentDirectory, "control-source.json");
		public string CandidateArtifactDirectory => Path.Combine(ExperimentDirectory, "candidate-artifact");
		public string ControlArtifactDirectory => Path.Combine(ExperimentDirectory, "control-artifact");
		public string CandidateBenchmarkRunsDirectory => Path.Combine(ExperimentDirectory, "candidate-benchmark");
		public string ControlBenchmarkRunsDirectory => Path.Combine(ExperimentDirectory, "control-benchmark");
		public string CandidateBenchmarkResultPath => Path.Combine(ExperimentDirectory, "candidate-result.json");
		public string ControlBenchmarkResultPath => Path.Combine(ExperimentDirectory, "control-result.json");
		public string BenchmarkResultPath => Path.Combine(ExperimentDirectory, "benchmark-result.json");
		public string PromotionEvaluationPath => Path.Combine(ExperimentDirectory, "promotion-evaluation.json");

		public bool IsEditable => !string.IsNullOrEmpty(Manifest.BotProject) &&
			File.Exists(Manifest.BotProject) && Directory.Exists(Manifest.BotDirectory);

		public bool HasRecordedBattle => Manifest.CompletedUtc != null &&
			Manifest.Result?.Players is { Count: > 0 };
		public bool HasPlayerFeedback => !string.IsNullOrWhiteSpace(Manifest.Result?.PlayerFeedback);
		public bool IsHeadless => string.Equals(Manifest.Battle?.ExecutionMode,
			BattleExecutionModes.Headless, StringComparison.OrdinalIgnoreCase);
		public bool NeedsReplayForFeedback => IsHeadless && Manifest.ReplayWatchedUtc == null;
		public bool CanProvideFeedback => HasRecordedBattle && !NeedsReplayForFeedback;
		public bool HasUnresolvedContinuousExperiment =>
			Manifest.Experiment?.Continuous == true &&
			!string.Equals(Manifest.Experiment.State, TrainingExperimentStates.Promoted,
				StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(Manifest.Experiment.State, TrainingExperimentStates.Restored,
				StringComparison.OrdinalIgnoreCase);
		public bool CanResumeContinuousEvaluation =>
			HasUnresolvedContinuousExperiment &&
			Manifest.Experiment?.CanResumeEvaluation == true;

		/// <summary>True while the manifest still describes a battle or improvement as under way.</summary>
		public bool HasUnfinishedWork => Manifest.CompletedUtc == null ||
			Manifest.Agent is { StartedUtc: not null, CompletedUtc: null } ||
			HasUnresolvedContinuousExperiment ||
			string.Equals(Manifest.Status, "improving", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Manifest.Status, "verifying", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Manifest.Status, "candidate", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Manifest.Status, "evaluating", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Manifest.Status, "restoring", StringComparison.OrdinalIgnoreCase);

		/// <summary>True while that work is genuinely still going on somewhere.</summary>
		public bool IsBusy => HasUnfinishedWork && ProcessOwnership.IsLive(Manifest.Owner);

		/// <summary>
		/// True when the session was left mid-battle or mid-improvement by a launcher that is gone.
		/// </summary>
		public bool WasInterrupted => HasUnfinishedWork && !ProcessOwnership.IsLive(Manifest.Owner);

		/// <summary>
		/// A session can be deleted unless something is still writing to it.
		/// </summary>
		/// <remarks>
		/// Deliberately says nothing about whether the battle finished, was any good, or produced a
		/// result at all. A fight abandoned when the launcher was closed is exactly the junk a
		/// player wants swept up, and refusing to remove it because its manifest still reads
		/// "improving" strands it in the history for ever with no way to clear it.
		/// </remarks>
		public bool CanDelete => !IsBusy && !HasUnresolvedContinuousExperiment;
		public bool HasImprovementEvidence => IsEditable && Manifest.CompletedUtc != null &&
			File.Exists(BattleLogPath) && File.Exists(TelemetryPath) && File.Exists(DecisionTracePath);
		public bool CanImprove => !HasUnresolvedContinuousExperiment && HasImprovementEvidence &&
			(Manifest.Agent == null || Manifest.Agent.ChangeCount == 0 ||
				Manifest.Agent.RestoredUtc != null || Manifest.Agent.Cancelled ||
				Manifest.Agent.ExitCode is int exitCode && exitCode != 0);

		/// <summary>
		/// True when the player can hold a conversation with this fight's coding agent.
		/// </summary>
		/// <remarks>
		/// Deliberately weaker than <see cref="CanImprove"/>. Talking is how you steer a round
		/// before it starts and interrogate it after it ends, so it has to be available in states
		/// where starting another round is not: with the evidence still unread, and once the
		/// agent's changes are already in the workspace and there is nothing left to do but ask it
		/// what it did.
		/// </remarks>
		public bool CanChat => IsEditable;

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
				Owner = ProcessOwnership.Claim(),
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
			var run = LoadUnreconciled(directory);
			if (run == null || !run.HasUnresolvedContinuousExperiment ||
				ProcessOwnership.IsLive(run.Manifest.Owner))
				return run;

			try
			{
				using var mutation = AcquireMutation(run);
				mutation.Run.ReconcileInterruptedContinuousExperiment();
				return mutation.Run;
			}
			catch (IOException ex)
			{
				run.Manifest.Warnings ??= [];
				run.Manifest.Warnings.Add(
					"The interrupted continuous experiment is locked by another launcher: " +
					ex.Message);
				return run;
			}
			catch (UnauthorizedAccessException ex)
			{
				run.Manifest.Warnings ??= [];
				run.Manifest.Warnings.Add(
					"The interrupted continuous experiment could not be locked: " +
					ex.Message);
				return run;
			}
		}

		internal static TrainingRun LoadUnreconciled(string directory)
		{
			if (string.IsNullOrWhiteSpace(directory))
				return null;

			var full = Path.GetFullPath(directory);
			var manifestPath = Path.Combine(full, "manifest.json");
			if (!File.Exists(manifestPath))
				return null;

			var manifest = JsonSerializer.Deserialize<TrainingRunManifest>(
				File.ReadAllText(manifestPath), JsonOptions);
			if (manifest == null)
				return null;

			var run = new TrainingRun(full, manifest);
			run.InferLegacyFailure();
			run.RescoreLegacyResult();
			return run;
		}

		public static TrainingRunMutation AcquireMutation(TrainingRun run)
		{
			if (run == null)
				throw new ArgumentNullException(nameof(run));

			Directory.CreateDirectory(run.RunDirectory);
			var handle = new FileStream(run.MutationLockPath, FileMode.OpenOrCreate,
				FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
			try
			{
				var current = LoadUnreconciled(run.RunDirectory) ??
					throw new InvalidOperationException(
						"The training run no longer exists on disk.");
				return new TrainingRunMutation(current, handle);
			}
			catch
			{
				handle.Dispose();
				throw;
			}
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
				var scored = new TrainingPlayerResult
				{
					Name = player.Name,
					IsBot = player.IsBot,
					Outcome = player.Outcome
				};

				Score(scored, player);
				result.Players.Add(scored);
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
			Manifest.Owner = null;
			LoadPerformance();
			if (File.Exists(CancellationPath))
				File.Delete(CancellationPath);
			Save();
		}

		/// <summary>Fills in one side's figures from its recorded samples.</summary>
		static void Score(TrainingPlayerResult result, MatchPlayer player)
		{
			var decided = player.LastContested;
			result.Units = decided.Units;
			result.ArmyValue = decided.Army;
			result.Buildings = decided.Buildings;
			result.BaseValue = decided.BaseValue;
			result.Cash = decided.Cash;
			result.Killed = decided.Killed;
			result.Lost = decided.Lost;
			result.PeakUnits = player.Peak.Units;
			result.PeakArmyValue = player.Peak.Army;
			result.PeakBuildings = player.Peak.Buildings;
			result.PeakBaseValue = player.Peak.BaseValue;
		}

		/// <summary>
		/// Rescores a run recorded before the figures were taken at the moment of defeat.
		/// </summary>
		/// <remarks>
		/// Older manifests stored the very last sample, which for any loss is the engine's clean
		/// sweep of a beaten player: zero units, zero army, zero buildings, every time. The
		/// samples that would say otherwise are still sitting in the run's own telemetry, so
		/// rather than leave a history of zeros behind, it is read back and rescored in memory.
		/// Nothing is written: the run on disk is evidence of a battle that has already been
		/// fought, and re-reading it costs a few milliseconds per run.
		/// </remarks>
		void RescoreLegacyResult()
		{
			if (Manifest.SchemaVersion >= 6 || Manifest.Result?.Players is not { Count: > 0 } players)
				return;

			var log = new MatchLog();
			log.Watch(TelemetryPath);
			try
			{
				if (!log.Refresh() || log.IsEmpty)
					return;
			}
			catch (UnauthorizedAccessException)
			{
				// A history of zeros is a poor result but a readable one. Refusing to open the run
				// at all because its evidence is locked away would be worse.
				return;
			}

			foreach (var player in players)
			{
				var recorded = log.Players.FirstOrDefault(p =>
					string.Equals(p.Name, player.Name, StringComparison.Ordinal));
				if (recorded != null && recorded.Samples.Count > 0)
					Score(player, recorded);
			}
		}

		void LoadPerformance()
		{
			if (!File.Exists(PerformancePath))
			{
				if (string.Equals(Manifest.Battle?.ExecutionMode, BattleExecutionModes.Headless,
					StringComparison.OrdinalIgnoreCase))
					Manifest.Warnings.Add("The headless performance report is missing.");
				return;
			}

			try
			{
				Manifest.Performance = JsonSerializer.Deserialize<TrainingSimulationPerformance>(
					File.ReadAllText(PerformancePath), JsonOptions);
				if (Manifest.Performance == null)
					Manifest.Warnings.Add("The headless performance report was empty.");
			}
			catch (JsonException ex)
			{
				Manifest.Warnings.Add("The headless performance report could not be read: " + ex.Message);
			}
		}

		public void SetPlayerFeedback(string feedback)
		{
			if (!HasRecordedBattle)
				throw new InvalidOperationException("Player feedback requires a completed, recorded battle.");
			if (NeedsReplayForFeedback)
				throw new InvalidOperationException("Watch this headless battle's replay before adding feedback.");

			var value = feedback?.Trim();
			if (value?.Length > MaxPlayerFeedbackLength)
				throw new ArgumentException(
					$"Player feedback cannot exceed {MaxPlayerFeedbackLength:N0} characters.", nameof(feedback));

			var previous = Manifest.Result.PlayerFeedback;
			Manifest.Result.PlayerFeedback = string.IsNullOrEmpty(value) ? null : value;
			try
			{
				Save();
			}
			catch (IOException)
			{
				Manifest.Result.PlayerFeedback = previous;
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Manifest.Result.PlayerFeedback = previous;
				throw;
			}

			ExportFightManifest();
		}

		public bool RecordReplayPlayback(int exitCode, bool cancelled)
		{
			if (exitCode != 0 || cancelled)
				return false;
			if (!HasRecordedBattle || !File.Exists(ReplayPath))
				throw new InvalidOperationException("A completed battle and its recorded replay are required.");
			if (Manifest.ReplayWatchedUtc != null)
				return true;

			Manifest.ReplayWatchedUtc = DateTime.UtcNow;
			try
			{
				Save();
			}
			catch (IOException)
			{
				Manifest.ReplayWatchedUtc = null;
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Manifest.ReplayWatchedUtc = null;
				throw;
			}

			ExportFightManifest();
			return true;
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

		public string ArchiveAgentAttempt()
		{
			if (Manifest.Agent == null)
				return null;

			var directory = Path.Combine(AttemptsDirectory,
				$"attempt-{Math.Max(1, Manifest.Agent.Attempt):D2}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
			Directory.CreateDirectory(directory);

			CopyIfExists(AgentTranscriptPath, Path.Combine(directory, "transcript.txt"));
			CopyIfExists(PromptPath, Path.Combine(directory, "prompt.txt"));
			CopyIfExists(AgentConfigurationPath, Path.Combine(directory, "agent-command.json"));
			CopyIfExists(AgentStatusPath, Path.Combine(directory, "status.json"));
			CopyIfExists(ChangesPath, Path.Combine(directory, "changes.json"));

			var transcript = Path.Combine(directory, "transcript.txt");
			return File.Exists(transcript) ? transcript : directory;
		}

		public TrainingAgentExecutionStatus ReadAgentStatus()
		{
			if (!File.Exists(AgentStatusPath))
				return null;

			return JsonSerializer.Deserialize<TrainingAgentExecutionStatus>(
				File.ReadAllText(AgentStatusPath), JsonOptions);
		}

		/// <summary>
		/// The id of the coding agent's conversation for this fight, created on first use.
		/// </summary>
		/// <remarks>
		/// Created here rather than when the fight is recorded because most fights are never
		/// improved, and a session id handed to nothing is a promise of a conversation that does
		/// not exist. Asking for it is what brings it into being, so the first person to speak —
		/// the player typing before the round, or the round itself — is the one who opens it.
		/// </remarks>
		public string EnsureAgentSessionId()
		{
			if (!string.IsNullOrWhiteSpace(Manifest.AgentSessionId))
				return Manifest.AgentSessionId;

			Manifest.AgentSessionId = Guid.NewGuid().ToString();
			try
			{
				Save();
			}
			catch (IOException)
			{
				Manifest.AgentSessionId = null;
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Manifest.AgentSessionId = null;
				throw;
			}

			ExportFightManifest();
			return Manifest.AgentSessionId;
		}

		TrainingExperiment NewContinuousExperiment()
		{
			if (!File.Exists(SnapshotManifestPath))
				throw new InvalidOperationException(
					"Continuous improvement requires the pre-agent source snapshot.");
			if (Manifest.Experiment != null)
				throw new InvalidOperationException("This training run already has an experiment.");

			return new TrainingExperiment
			{
				Id = Guid.NewGuid().ToString("N"),
				Continuous = true,
				State = TrainingExperimentStates.Improving,
				StartedUtc = DateTime.UtcNow,
				ChampionSourceRevision = BotWorkspace.SourceRevision(Manifest.BotDirectory),
				ChampionFingerprint = WorkspaceSnapshot.Fingerprint(this),
				ChampionSnapshotFile = Path.GetRelativePath(RunDirectory, SnapshotManifestPath),
				ChampionSourceManifestFile =
					Path.GetRelativePath(RunDirectory, ControlSourceManifestPath),
				CandidateSourceManifestFile =
					Path.GetRelativePath(RunDirectory, CandidateSourceManifestPath),
				CandidateBenchmarkResultFile =
					Path.GetRelativePath(RunDirectory, CandidateBenchmarkResultPath),
				ControlBenchmarkResultFile =
					Path.GetRelativePath(RunDirectory, ControlBenchmarkResultPath),
				BenchmarkResultFile = Path.GetRelativePath(RunDirectory, BenchmarkResultPath),
				EvaluationFile = Path.GetRelativePath(RunDirectory, PromotionEvaluationPath),
				PromptRewriteFrozen = true
			};
		}

		public void BeginContinuousEvaluation(string candidateFingerprint,
			string championFingerprint, string benchmark, string difficulty)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true ||
				!string.Equals(experiment.State, TrainingExperimentStates.Candidate,
					StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Only a finished continuous candidate can be evaluated.");

			Directory.CreateDirectory(ExperimentDirectory);
			experiment.State = TrainingExperimentStates.Evaluating;
			experiment.EvaluationAttempt++;
			experiment.EvaluationStartedUtc = DateTime.UtcNow;
			experiment.EvaluationCompletedUtc = null;
			experiment.PromotedUtc = null;
			experiment.RestoredUtc = null;
			experiment.CandidateSourceRevision = BotWorkspace.SourceRevision(Manifest.BotDirectory);
			experiment.ControlRevision = null;
			experiment.CandidateFingerprint = candidateFingerprint;
			experiment.ChampionFingerprint = championFingerprint;
			experiment.RequestedBenchmark = benchmark;
			experiment.RequestedDifficulty = difficulty;
			experiment.ExpectedLiveFingerprint = candidateFingerprint;
			experiment.CandidateAssemblyFile = null;
			experiment.CandidateAssemblySha256 = null;
			experiment.ControlAssemblyFile = null;
			experiment.ControlAssemblySha256 = null;
			experiment.CandidateBatch = null;
			experiment.ControlBatch = null;
			experiment.Batch = null;
			experiment.Benchmark = null;
			experiment.ExpectedMatchesPerArm = null;
			experiment.Decision = null;
			experiment.Reason = null;
			experiment.RequiresReevaluation = false;
			experiment.CanResumeEvaluation = false;
			experiment.InvalidationReason = null;
			Manifest.Status = "evaluating";
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public void RecordContinuousArmArtifacts(string candidateAssembly,
			string candidateSha256, string controlAssembly, string controlSha256)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true ||
				!string.Equals(experiment.State, TrainingExperimentStates.Evaluating,
					StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("This run has no continuous evaluation in progress.");

			experiment.CandidateAssemblyFile = Path.GetRelativePath(RunDirectory, candidateAssembly);
			experiment.CandidateAssemblySha256 = candidateSha256;
			experiment.ControlAssemblyFile = Path.GetRelativePath(RunDirectory, controlAssembly);
			experiment.ControlAssemblySha256 = controlSha256;
			Save();
		}

		public void RecordContinuousCandidateFingerprint(string fingerprint)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true)
				throw new InvalidOperationException("This run has no continuous experiment.");

			experiment.CandidateFingerprint = fingerprint;
			experiment.ExpectedLiveFingerprint = fingerprint;
			Save();
		}

		public void RecordContinuousEvaluation(string decision, string reason,
			string benchmark = null, string batch = null,
			int? expectedMatchesPerArm = null)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true ||
				(!string.Equals(experiment.State, TrainingExperimentStates.Evaluating,
						StringComparison.OrdinalIgnoreCase) &&
					!string.Equals(experiment.State, TrainingExperimentStates.Failed,
						StringComparison.OrdinalIgnoreCase)))
				throw new InvalidOperationException("This run has no continuous evaluation in progress.");

			experiment.EvaluationStartedUtc ??= DateTime.UtcNow;
			experiment.EvaluationCompletedUtc = DateTime.UtcNow;
			experiment.Decision = decision;
			experiment.Reason = reason;
			experiment.Benchmark = benchmark;
			experiment.Batch = batch;
			experiment.ExpectedMatchesPerArm = expectedMatchesPerArm;
			experiment.ExpectedLiveFingerprint = experiment.CandidateFingerprint;
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public void RecordContinuousBenchmarkBatches(string candidateBatch, string controlBatch)
		{
			var experiment = Manifest.Experiment ??
				throw new InvalidOperationException("This run has no continuous experiment.");
			experiment.CandidateBatch = candidateBatch;
			experiment.ControlBatch = controlBatch;
			Save();
		}

		public void InvalidateContinuousEvaluation(string reason)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true)
				throw new InvalidOperationException("This run has no continuous experiment.");

			experiment.State = TrainingExperimentStates.Candidate;
			experiment.InvalidatedUtc = DateTime.UtcNow;
			experiment.InvalidationReason = reason;
			experiment.RequiresReevaluation = true;
			experiment.Decision = "Undefined";
			experiment.Reason = reason;
			experiment.ExpectedLiveFingerprint = null;
			Manifest.Status = "candidate";
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public bool ReconcileInterruptedContinuousExperiment()
		{
			var experiment = Manifest.Experiment;
			if (!HasUnresolvedContinuousExperiment ||
				string.Equals(experiment.State, TrainingExperimentStates.Aborted,
					StringComparison.OrdinalIgnoreCase) ||
				ProcessOwnership.IsLive(Manifest.Owner))
				return false;

			var resumable = Manifest.Agent is
			{
				CompletedUtc: not null,
				ExitCode: 0
			} && File.Exists(SnapshotManifestPath) && IsEditable;
			AbortContinuousExperiment(
				"The launcher stopped before this continuous candidate reached a durable promotion decision.",
				resumable);
			return true;
		}

		public void AbortContinuousExperiment(string reason, bool canResumeEvaluation)
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true)
				return;
			if (IsBusy && !ProcessOwnership.IsCurrent(Manifest.Owner))
				throw new InvalidOperationException(
					"Another launcher still owns this continuous experiment.");

			experiment.State = TrainingExperimentStates.Aborted;
			experiment.AbortedUtc = DateTime.UtcNow;
			experiment.AbortReason = reason;
			experiment.CanResumeEvaluation = canResumeEvaluation;
			experiment.RequiresReevaluation = canResumeEvaluation;
			experiment.ExpectedLiveFingerprint = null;
			if (string.IsNullOrWhiteSpace(experiment.CandidateFingerprint) && IsEditable)
				experiment.CandidateFingerprint = BotWorkspace.Fingerprint(Manifest.BotDirectory);
			Manifest.Status = "experiment-aborted";
			Manifest.Owner = null;
			Save();
		}

		public void AbortContinuousExperiment(string reason)
		{
			var resumable = Manifest.Agent is
			{
				CompletedUtc: not null,
				ExitCode: 0
			} && File.Exists(SnapshotManifestPath) && IsEditable;
			AbortContinuousExperiment(reason, resumable);
		}

		public void ResumeContinuousExperiment()
		{
			var experiment = Manifest.Experiment;
			if (IsBusy)
				throw new InvalidOperationException(
					"Another launcher still owns this continuous experiment.");
			if (!CanResumeContinuousEvaluation ||
				!string.Equals(experiment.State, TrainingExperimentStates.Aborted,
					StringComparison.OrdinalIgnoreCase) ||
				!File.Exists(SnapshotManifestPath) || !IsEditable)
				throw new InvalidOperationException(
					"This interrupted continuous candidate cannot be resumed.");

			experiment.State = TrainingExperimentStates.Candidate;
			experiment.RequiresReevaluation = true;
			experiment.CanResumeEvaluation = false;
			experiment.ExpectedLiveFingerprint = null;
			Manifest.Status = "candidate";
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public void MarkContinuousPromoted()
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true ||
				!string.Equals(experiment.State, TrainingExperimentStates.Evaluating,
					StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(experiment.Decision, "Promote", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Only an evaluated improving candidate can be promoted.");

			experiment.State = TrainingExperimentStates.Promoted;
			experiment.PromotedUtc = DateTime.UtcNow;
			experiment.ExpectedLiveFingerprint = experiment.CandidateFingerprint;
			experiment.RequiresReevaluation = false;
			experiment.CanResumeEvaluation = false;
			Manifest.Status = "promoted";
			Manifest.Owner = null;
			Save();
		}

		public void MarkContinuousRestoring()
		{
			var experiment = Manifest.Experiment;
			if (experiment?.Continuous != true ||
				experiment.State is TrainingExperimentStates.Promoted or TrainingExperimentStates.Restored)
				throw new InvalidOperationException("This run has no continuous candidate to restore.");

			experiment.State = TrainingExperimentStates.Restoring;
			Manifest.Status = "restoring";
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public void AgentStarted(string command, string recoveryTranscript = null,
			bool repairing = false) =>
			StartAgent(command, recoveryTranscript, repairing, continuous: false);

		public void ContinuousAgentStarted(string command, string recoveryTranscript = null,
			bool repairing = false) =>
			StartAgent(command, recoveryTranscript, repairing, continuous: true);

		void StartAgent(string command, string recoveryTranscript, bool repairing,
			bool continuous)
		{
			var previousAgent = Manifest.Agent;
			var previousStatus = Manifest.Status;
			var previousOwner = Manifest.Owner;
			var attempt = Manifest.Agent == null
				? 1
				: Math.Max(1, Manifest.Agent.Attempt) + 1;
			if (File.Exists(AgentTranscriptPath))
				File.Delete(AgentTranscriptPath);
			if (File.Exists(ChangesPath))
				File.Delete(ChangesPath);
			if (File.Exists(AgentStatusPath))
				File.Delete(AgentStatusPath);

			var previousExperiment = Manifest.Experiment;
			if (continuous)
				Manifest.Experiment = NewContinuousExperiment();

			Manifest.Agent = new TrainingAgentResult
			{
				Attempt = attempt,
				StartedUtc = DateTime.UtcNow,
				Command = command,
				RecoveryTranscript = recoveryTranscript,
				Repairing = repairing
			};
			Manifest.Status = "improving";
			Manifest.Owner = ProcessOwnership.Claim();
			try
			{
				Save();
			}
			catch (IOException)
			{
				Manifest.Agent = previousAgent;
				Manifest.Status = previousStatus;
				Manifest.Owner = previousOwner;
				Manifest.Experiment = previousExperiment;
				throw;
			}
			catch (UnauthorizedAccessException)
			{
				Manifest.Agent = previousAgent;
				Manifest.Status = previousStatus;
				Manifest.Owner = previousOwner;
				Manifest.Experiment = previousExperiment;
				throw;
			}
		}

		public void VerificationStarted()
		{
			if (File.Exists(AgentStatusPath))
				File.Delete(AgentStatusPath);

			Manifest.Agent ??= new TrainingAgentResult { Attempt = 1 };
			Manifest.Agent.CompletedUtc = null;
			Manifest.Agent.ExitCode = null;
			Manifest.Agent.FailurePhase = null;
			Manifest.Agent.FailureMessage = null;
			Manifest.Status = "verifying";
			Manifest.Owner = ProcessOwnership.Claim();
			Save();
		}

		public void ExportFightManifest()
		{
			Directory.CreateDirectory(EvidenceDirectory);
			File.WriteAllText(FightManifestPath, JsonSerializer.Serialize(Manifest, JsonOptions));
		}

		public void AgentFinished(int exitCode, int changeCount, string suggestedNextPrompt = null,
			string failurePhase = null, string failureMessage = null, bool cancelled = false)
		{
			Manifest.Agent ??= new TrainingAgentResult();
			Manifest.Agent.CompletedUtc = DateTime.UtcNow;
			Manifest.Agent.ExitCode = exitCode;
			Manifest.Agent.Cancelled = cancelled && exitCode != 0;
			Manifest.Agent.ChangeCount = changeCount;
			Manifest.Agent.SuggestedNextPrompt = suggestedNextPrompt;
			Manifest.Agent.FailurePhase = failurePhase;
			Manifest.Agent.FailureMessage = failureMessage;
			var continuousCandidate = Manifest.Experiment?.Continuous == true &&
				string.Equals(Manifest.Experiment.State, TrainingExperimentStates.Improving,
					StringComparison.OrdinalIgnoreCase);
			if (continuousCandidate)
				Manifest.Experiment.State = exitCode == 0
					? TrainingExperimentStates.Candidate
					: TrainingExperimentStates.Failed;

			Manifest.Status = exitCode == 0
				? continuousCandidate ? "candidate" : "improved"
				: cancelled ? "improvement-cancelled" : "improvement-failed";
			Manifest.Owner = continuousCandidate
				? ProcessOwnership.Claim()
				: null;
			Save();
		}

		public void AcceptSuggestedNextPrompt(string approvedPrompt)
		{
			if (Manifest.Agent == null)
				throw new InvalidOperationException("This run has no next prompt to accept.");

			Manifest.Agent.SuggestedNextPrompt = approvedPrompt;
			Manifest.Agent.SuggestedNextPromptAccepted = true;
			Manifest.Agent.SuggestedNextPromptRejected = false;
			Save();
		}

		/// <summary>Records that the player turned this round's proposed prompt down.</summary>
		public void RejectSuggestedNextPrompt()
		{
			if (Manifest.Agent == null)
				throw new InvalidOperationException("This run has no next prompt to reject.");

			Manifest.Agent.SuggestedNextPromptRejected = true;
			Manifest.Agent.SuggestedNextPromptAccepted = false;
			Save();
		}

		public void MarkRestored()
		{
			Manifest.Agent ??= new TrainingAgentResult();
			Manifest.Agent.RestoredUtc = DateTime.UtcNow;
			if (Manifest.Experiment?.Continuous == true)
			{
				Manifest.Experiment.State = TrainingExperimentStates.Restored;
				Manifest.Experiment.RestoredUtc = Manifest.Agent.RestoredUtc;
				Manifest.Experiment.ExpectedLiveFingerprint =
					Manifest.Experiment.ChampionFingerprint;
				Manifest.Experiment.RequiresReevaluation = false;
				Manifest.Experiment.CanResumeEvaluation = false;
			}
			Manifest.Status = "restored";
			Manifest.Owner = null;
			Save();
		}

		public void Delete(string runsRoot = null)
		{
			if (!CanDelete)
				throw new InvalidOperationException("Finish or stop the session's battle and improvement before deleting it.");

			var root = Path.GetFullPath(runsRoot ?? DefaultRoot);
			var id = Manifest.Id;
			if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id != Path.GetFileName(id))
				throw new InvalidDataException("The recorded session has an invalid directory identifier.");
			var expected = Path.GetFullPath(Path.Combine(
				RunsDirectoryForBot(Manifest.BotProject ?? Manifest.BotPath, root), id));
			if (!string.Equals(Path.TrimEndingDirectorySeparator(RunDirectory), expected, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("Only this session's directory inside the training archive can be deleted.");

			for (var directory = new DirectoryInfo(RunDirectory); directory != null; directory = directory.Parent)
			{
				if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
					throw new InvalidDataException("The recorded session's directory is a link or junction.");
				if (string.Equals(directory.FullName, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
					break;
			}

			var pending = new Stack<string>();
			pending.Push(RunDirectory);
			while (pending.Count > 0)
			{
				foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
				{
					if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
						throw new InvalidDataException("The recorded session contains a link or junction: " + entry.Name);
					if (entry is DirectoryInfo)
						pending.Push(entry.FullName);
				}
			}

			var current = Load(RunDirectory) ??
				throw new InvalidDataException("The recorded session's manifest is missing.");
			if (!current.CanDelete || current.Manifest.Id != id ||
				current.Manifest.BotPath != Manifest.BotPath || current.Manifest.BotProject != Manifest.BotProject)
				throw new InvalidOperationException("The recorded session changed. Refresh history before deleting it.");
			foreach (var source in new[] { current.Manifest.BotPath, current.Manifest.BotProject, current.Manifest.BotDirectory })
			{
				if (string.IsNullOrWhiteSpace(source))
					continue;
				var relative = Path.GetRelativePath(RunDirectory, source);
				if (!Path.IsPathRooted(relative) && relative != ".." &&
					!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
					throw new InvalidDataException("A recorded session containing the bot's source cannot be deleted.");
			}

			// Keep the manifest until the evidence is removed so a failed deletion remains visible and retryable.
			foreach (var directory in Directory.EnumerateDirectories(RunDirectory))
			{
				ClearReadOnlyFiles(directory);
				Directory.Delete(directory, recursive: true);
			}
			foreach (var file in Directory.EnumerateFiles(RunDirectory))
				if (!string.Equals(file, ManifestPath, StringComparison.OrdinalIgnoreCase))
				{
					File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
					File.Delete(file);
				}
			File.Delete(ManifestPath);
			Directory.Delete(RunDirectory);
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

		static void CopyIfExists(string source, string destination)
		{
			if (File.Exists(source))
				File.Copy(source, destination, true);
		}

		static void ClearReadOnlyFiles(string directory)
		{
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
		}

		void InferLegacyFailure()
		{
			var agent = Manifest.Agent;
			if (agent?.ExitCode is not int exitCode || exitCode == 0 || agent.Cancelled ||
				!string.IsNullOrEmpty(agent.FailurePhase))
				return;

			try
			{
				var status = ReadAgentStatus();
				if (string.Equals(status?.State, "failed", StringComparison.OrdinalIgnoreCase))
				{
					agent.FailurePhase = status.Phase;
					agent.FailureMessage = status.Message;
					return;
				}

				if (File.Exists(AgentTranscriptPath) && File.ReadLines(AgentTranscriptPath)
					.Any(line => line.StartsWith("=== Verification", StringComparison.Ordinal)))
				{
					agent.FailurePhase = "verification";
					agent.FailureMessage = "Independent bot verification failed.";
				}
				else
					agent.FailurePhase = "agent";
			}
			catch (IOException)
			{
				agent.FailurePhase = "process";
			}
			catch (JsonException)
			{
				agent.FailurePhase = "process";
			}
		}
	}
}
