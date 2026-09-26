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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoCnC.Evidence;

namespace AutoCnC.Launcher
{
	internal sealed record TrainingScriptResult(int ExitCode, IReadOnlyList<string> Output);

	/// <summary>The unattended training workflow, without a window or a UI message queue.</summary>
	public sealed class TrainingLoopRunner
	{
		static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
		readonly TrainingLoopOptions options;
		readonly Action<string> output;
		readonly Func<ScriptJob, CancellationToken, TrainingScriptResult> execute;
		readonly ContinuousPromotionRunner promotion = new();
		readonly PromptHistory promptHistory;
		RepoLayout repo;
		TrainingRun activeRun;

		public TrainingLoopRunner(TrainingLoopOptions options, Action<string> output)
			: this(options, output, null) { }

		internal TrainingLoopRunner(TrainingLoopOptions options, Action<string> output,
			Func<ScriptJob, CancellationToken, TrainingScriptResult> execute,
			PromptHistory promptHistory = null)
		{
			this.options = options ?? throw new ArgumentNullException(nameof(options));
			this.output = output ?? throw new ArgumentNullException(nameof(output));
			this.execute = execute ?? ExecuteScript;
			this.promptHistory = promptHistory ?? new PromptHistory();
		}

		public void Run(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!string.IsNullOrWhiteSpace(options.RestoreRun))
			{
				Restore(options.RestoreRun);
				return;
			}

			repo = RepoLayout.For(options.RepoRoot);
			var project = options.Validate(repo);
			var promptPath = options.PromptTemplate ?? repo.AgentPromptTemplate;
			var prompt = File.ReadAllText(promptPath);
			if (!TrainingAgent.ValidatePromptTemplate(prompt, out var error))
				throw new ArgumentException(error);
			var agent = ReadAgentConfiguration();
			if (!repo.EngineFetched)
				throw new InvalidOperationException("The engine is not fetched. Run ./scripts/setup.ps1 first.");

			using var workspace = TrainingWorkspaceMutation.Acquire(Path.GetDirectoryName(project));
			try
			{
				var unresolved = TrainingHistory.LoadUnresolved(project, options.RunsRoot);
				if (unresolved.Count > 1)
					throw new InvalidOperationException("Multiple unresolved experiments need explicit recovery: " +
						string.Join(", ", unresolved.Select(run => run.RunDirectory)));
				if (unresolved.Count == 1)
				{
					TrainingRun restored = null;
					using (var mutation = TrainingRun.AcquireMutation(unresolved[0]))
					{
						var run = mutation.Run;
						if (run.IsBusy)
							throw new InvalidOperationException(
								$"An unfinished experiment blocks training: {run.RunDirectory}. " +
								"Its worker is still running; wait for it or stop it first.");
						if (run.CanResumeContinuousEvaluation)
						{
							run.ResumeContinuousExperiment();
							activeRun = run;
						}
						else if (options.DeleteBlockingRun)
						{
							RestoreSnapshot(run);
							restored = run;
						}
						else
							throw new InvalidOperationException(
								$"An unfinished experiment blocks training: {run.RunDirectory}. " +
								"Rerun with -DeleteBlockingRun to discard its edits and delete it, " +
								"or use -RestoreRun to only discard its edits.");
					}

					// Deletion takes the run lock itself, so it can only start once ours is released.
					if (restored != null)
						DeleteRestoredRun(restored);
					else
						output($"Resuming paired evaluation: {activeRun.RunDirectory}");
				}

				if (!repo.EngineBuilt)
					RequireSuccess(Execute(repo.BuildScript, ["-SkipBots"], "Building engine", cancellationToken));
				if (activeRun != null)
					Evaluate(cancellationToken);

				for (long round = 1; options.Rounds == 0 || round <= options.Rounds; round++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (activeRun != null && !promotion.ValidateForNextFight(activeRun, out var invalidation))
					{
						output(invalidation);
						Evaluate(cancellationToken);
					}

					activeRun = TrainingRun.Create(project, options.Battle(), options.RunsRoot);
					output($"=== Training round {round}{(options.Rounds == 0 ? "" : $"/{options.Rounds}")} ===");
					output($"AUTOCNC_TRAINING_RUN={activeRun.RunDirectory}");
					promotion.CaptureChampion(activeRun);
					Fight(project, cancellationToken);
					RequireSuccess(Execute(repo.ExportAgentRulesScript,
						["-Output", activeRun.GameRulesPath], "Exporting game rules", cancellationToken));
					TrainingAgent.Prepare(activeRun, repo.AgentGameGuide, repo.AgentMechanics,
						activeRun.GameRulesPath, prompt, agent.Command, agent.Arguments, agent.Stdin,
						adoptsNextPrompt: true);
					activeRun.ContinuousAgentStarted(agent.Command);
					Improve(project, cancellationToken);
					prompt = AdoptNextPrompt(promptPath, prompt);
					Evaluate(cancellationToken);
					output($"Round {round}: {activeRun.Manifest.Status}. {activeRun.Manifest.Experiment.Reason}");
				}

				output("Training loop completed.");
			}
			catch
			{
				if (activeRun != null)
				{
					try
					{
						using var mutation = TrainingRun.AcquireMutation(activeRun);
						activeRun = mutation.Run;
						if (activeRun.HasUnresolvedContinuousExperiment)
							activeRun.AbortContinuousExperiment(
								"The PowerShell training loop stopped before a durable promotion decision.");
						if (activeRun.Manifest.CompletedUtc == null)
							FinishBattle(cancellationToken.IsCancellationRequested ? "stopped" : "failed");
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
						InvalidOperationException or JsonException)
					{
						output("Could not persist the interrupted run: " + ex.Message);
					}
					output($"Run saved: {activeRun.RunDirectory}");
					if (activeRun.HasUnresolvedContinuousExperiment)
						output(activeRun.CanResumeContinuousEvaluation
							? "Rerun this command to resume the verified candidate's evaluation."
							: "Rerun with -DeleteBlockingRun to discard its edits and delete this run, " +
								"or use -RestoreRun with this directory to only discard its edits.");
				}
				throw;
			}
		}

		TrainingAgentConfiguration ReadAgentConfiguration()
		{
			var agent = options.AgentConfiguration == null
				? new TrainingAgentConfiguration
				{
					Command = "copilot", Arguments = [.. TrainingAgent.DefaultArguments], Stdin = TrainingAgent.DefaultStdin
				}
				: JsonSerializer.Deserialize<TrainingAgentConfiguration>(
					File.ReadAllText(options.AgentConfiguration), JsonOptions);
			if (string.IsNullOrWhiteSpace(agent?.Command))
				throw new ArgumentException("AgentConfiguration must specify a command.");
			return agent;
		}

		void Fight(string project, CancellationToken token)
		{
			var run = activeRun;
			var arguments = new List<string>
			{
				"-BattleBot", project, "-Map", options.Map, "-Difficulty", options.Difficulty,
				"-Opponents", Number(options.Opponents), "-Faction", options.Faction,
				"-BotFaction", options.BotFaction, "-ExecutionMode", options.ExecutionMode,
				"-Seed", Number(options.Seed), "-MaxGameSeconds", Number(options.MaxGameSeconds),
				"-Telemetry", run.TelemetryPath, "-BattleLog", run.BattleLogPath,
				"-DecisionTrace", run.DecisionTracePath, "-MapFacts", run.MapFactsPath
			};
			if (run.Manifest.Battle.GameSpeed != null)
				arguments.AddRange(["-GameSpeed", run.Manifest.Battle.GameSpeed]);
			if (run.IsHeadless)
				arguments.AddRange(["-CancellationFile", run.CancellationPath, "-PerformanceReport", run.PerformancePath]);

			TrainingScriptResult result;
			try
			{
				result = Execute(repo.RunBotScript, arguments, "Fighting", token,
					run.IsHeadless ? run.CancellationPath : null);
			}
			catch
			{
				FinishBattle(token.IsCancellationRequested ? "stopped" : "failed");
				throw;
			}
			FinishBattle(result.ExitCode == 0 ? "finished" : "failed");
			RequireSuccess(result);
			if (!run.HasImprovementEvidence || !run.HasRecordedBattle ||
				!new[] { "Won", "Lost", "Draw" }.Contains(run.Manifest.Result.Outcome, StringComparer.OrdinalIgnoreCase))
				throw new InvalidDataException("The battle did not produce complete, decided training evidence.");
			CaptureReplay(run);
		}

		void FinishBattle(string status)
		{
			var match = new MatchLog();
			match.Watch(activeRun.TelemetryPath);
			match.Refresh();
			var battle = new BattleEventLog();
			battle.Watch(activeRun.BattleLogPath);
			battle.Refresh();
			activeRun.Finish(status, match, battle);
			activeRun.ExportFightManifest();
		}

		void CaptureReplay(TrainingRun run)
		{
			try
			{
				var portable = Path.Combine(repo.EngineDir, "Support");
				var support = Directory.Exists(portable) ? portable :
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA");
				var directory = Path.Combine(support, "Replays", "autocnc");
				var replay = Directory.Exists(directory)
					? new DirectoryInfo(directory).EnumerateFiles("*.orarep", SearchOption.AllDirectories)
						.Where(file => file.LastWriteTimeUtc >= run.Manifest.CreatedUtc)
						.MaxBy(file => file.LastWriteTimeUtc)?.FullName
					: null;
				run.CaptureReplay(replay);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				output("Could not capture the replay: " + ex.Message);
			}
		}

		void Improve(string project, CancellationToken token)
		{
			TrainingScriptResult result;
			try
			{
				result = Execute(repo.TrainBotScript,
					["-BattleBot", project, "-RunDirectory", activeRun.RunDirectory], "Improving and building", token);
			}
			catch (OperationCanceledException)
			{
				activeRun = TrainingRun.FinishLatestAgent(activeRun, 1,
					TrainingAgentResult.UnknownChangeCount, failurePhase: "cancelled",
					failureMessage: "Stopped from PowerShell.", cancelled: true);
				throw;
			}

			var status = activeRun.ReadAgentStatus();
			activeRun = TrainingRun.FinishLatestAgent(activeRun, result.ExitCode,
				WorkspaceSnapshot.Compare(activeRun).Count,
				TrainingAgent.FindSuggestedNextPrompt(result.Output, activeRun),
				result.ExitCode == 0 ? null : status?.Phase ?? "process",
				result.ExitCode == 0 ? null : status?.Message ?? "The improvement worker failed.");
			if (result.ExitCode != 0)
			{
				var failure = promotion.RecordFailedCandidate(activeRun,
					activeRun.Manifest.Agent.FailureMessage);
				promotion.ApplyDecision(activeRun, null, failure.Evaluation);
				throw new InvalidOperationException("Improvement failed; training stopped. See agent-transcript.txt in the saved run.");
			}
		}

		/// <summary>
		/// Makes the round's proposed template the prompt every later round is given, or keeps the
		/// one in force when there is nothing valid to adopt. Returns the prompt the next round uses.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Adopted as soon as the build is verified and before the paired benchmark runs, whichever
		/// way that goes. The prompt steers the analysis rather than the play, so the benchmark says
		/// nothing about it; its measure is the trend's report of what each revision did to the
		/// rounds it steered. Adopting before the evaluation also means a loop stopped during the
		/// benchmark, and then resumed from it, has already taken the proposal.
		/// </para>
		/// <para>
		/// The checks and repairs are the ones a player's approval gets. A proposal missing
		/// <c>{gameMechanics}</c> or a required placeholder was already mended by
		/// <see cref="TrainingAgent.FindSuggestedNextPrompt"/>, and one that still fails
		/// validation is reported and discarded rather than stopping training.
		/// </para>
		/// <para>
		/// Written back to the template file the loop read, so a restart carries on from the latest
		/// adoption and editing that file is still how a player steers it. Archived as a
		/// <see cref="PromptOrigin.Continuous"/> revision after the template it replaces, so the
		/// lineage stays diffable.
		/// </para>
		/// </remarks>
		string AdoptNextPrompt(string path, string current)
		{
			var proposal = activeRun.Manifest.Agent?.SuggestedNextPrompt;
			if (string.IsNullOrWhiteSpace(proposal))
			{
				output("Next prompt: the round proposed none, so the current prompt carries on.");
				return current;
			}

			var adopted = TrainingAgent.EnsureMechanicsPlaceholder(proposal).Trim();
			if (!TrainingAgent.ValidatePromptTemplate(adopted, out var error))
			{
				output("Next prompt: the proposal is invalid, so the current prompt carries on. " + error);
				return current;
			}

			// The file keeps its own line endings, so a tracked template's diff shows what the round
			// changed rather than every line.
			var newline = current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
			var next = adopted.ReplaceLineEndings(newline) + newline;
			var unchanged = string.Equals(next.Trim(), current.ReplaceLineEndings(newline).Trim(),
				StringComparison.Ordinal);
			if (!unchanged)
			{
				try
				{
					var temporary = path + ".tmp";
					File.WriteAllText(temporary, next);
					File.Move(temporary, path, true);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					output($"Next prompt: could not write {path}, so the current prompt carries on. {ex.Message}");
					return current;
				}
			}

			try
			{
				activeRun = TrainingRun.AdoptLatestSuggestedNextPrompt(activeRun, adopted);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException or JsonException)
			{
				output("Next prompt: adopted, but the run could not record it. " + ex.Message);
			}

			if (unchanged)
			{
				output("Next prompt: the round proposed the prompt it was given, unchanged.");
				return current;
			}

			PromptRevision revision = null;
			try
			{
				promptHistory.Record(current, PromptOrigin.Baseline);
				revision = promptHistory.Record(next, PromptOrigin.Continuous, activeRun);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				output("Next prompt: adopted, but it could not be archived. " + ex.Message);
			}

			var archived = revision == null
				? ""
				: "; archived as revision " + revision.Revision.ToString(CultureInfo.InvariantCulture);
			output("Next prompt: adopted the round's proposal (" +
				next.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters, was " +
				current.Length.ToString("N0", CultureInfo.InvariantCulture) + ") for the next round" +
				archived + ".");
			return next;
		}

		void Evaluate(CancellationToken token)
		{
			while (true)
			{
				token.ThrowIfCancellationRequested();
				var plan = promotion.PrepareEvaluation(repo, activeRun, options.Benchmark, options.BenchmarkDifficulty);
				activeRun = plan.Run;
				if (plan.NoChanges)
					return;
				ContinuousEvaluationCompletion completion;
				try
				{
					foreach (var arm in new[] { ContinuousEvaluationArm.Candidate, ContinuousEvaluationArm.Control })
					{
						var step = promotion.BuildArm(repo, plan, arm);
						RequireSuccess(Execute(step.ScriptPath, step.Arguments, step.Title, token));
						promotion.CaptureBuiltArm(activeRun, plan, arm);
					}
					foreach (var arm in new[] { ContinuousEvaluationArm.Candidate, ContinuousEvaluationArm.Control })
					{
						var step = promotion.BenchmarkArm(repo, activeRun, plan, arm);
						RequireSuccess(Execute(step.ScriptPath, step.Arguments, step.Title, token));
					}
					completion = promotion.CompleteEvaluation(activeRun, plan, 0);
				}
				catch (Exception ex) when (ex is InvalidDataException or IOException or
					UnauthorizedAccessException or InvalidOperationException or JsonException)
				{
					completion = promotion.FailEvaluation(activeRun, plan, ex.Message);
				}

				token.ThrowIfCancellationRequested();
				var decision = completion.RequiresReevaluation
					? ContinuousEvaluationDecision.Reevaluate
					: promotion.ApplyDecision(activeRun, plan, completion.Evaluation);
				output($"Evaluation: {decision}. {completion.InvalidationReason ?? completion.Evaluation.Reason}");
				if (decision == ContinuousEvaluationDecision.Undefined)
					throw new InvalidOperationException("Evaluation evidence was invalid. The champion was restored and training stopped.");
				if (decision == ContinuousEvaluationDecision.Promote)
					CommitPromotion(completion.Evaluation);
				if (decision != ContinuousEvaluationDecision.Reevaluate)
					return;
			}
		}

		/// <summary>Puts a promoted bot in version control, where the next round cannot lose it.</summary>
		/// <remarks>
		/// Only the paired gate reaches this, so the evidence for the commit is the same evidence
		/// that allowed the promotion. Committing is bookkeeping rather than training: a failure
		/// here is reported and the loop carries on, because the promotion itself was already
		/// measured, applied and recorded in the run.
		/// </remarks>
		void CommitPromotion(PairedBenchmarkEvaluation evaluation)
		{
			if (!options.Commit)
				return;

			var workspace = activeRun?.Manifest.BotDirectory;
			BotWorkspace.CommitOutcome result;
			try
			{
				result = BotWorkspace.Commit(workspace, PromotionCommitMessage(evaluation));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				output("Promoted, but the commit failed: " + ex.Message);
				return;
			}

			output(result.Committed
				? $"Committed the promoted bot as {result.Revision}."
				: $"Promoted, but not committed, because {result.Reason}.");
		}

		internal static string PromotionCommitMessage(PairedBenchmarkEvaluation evaluation)
		{
			ArgumentNullException.ThrowIfNull(evaluation);
			var benchmark = string.IsNullOrWhiteSpace(evaluation.Benchmark) ? "the paired benchmark" : evaluation.Benchmark;
			var message = new StringBuilder();
			message.Append(CultureInfo.InvariantCulture, $"Promote the bot {benchmark} preferred\n\n");
			message.Append(CultureInfo.InvariantCulture,
				$"{evaluation.Reason}\n\n");
			message.Append(CultureInfo.InvariantCulture,
				$"  wins             {evaluation.CandidateWins} - {evaluation.ControlWins}\n");
			message.Append(CultureInfo.InvariantCulture,
				$"  median delta     {evaluation.MedianPairedFitnessDelta:+0.####;-0.####;0}\n");
			message.Append(CultureInfo.InvariantCulture,
				$"  pairs            {evaluation.CandidateFitnessPairs} better, " +
				$"{evaluation.ControlFitnessPairs} worse, {evaluation.TiedFitnessPairs} tied\n");
			message.Append(CultureInfo.InvariantCulture,
				$"  compared         {evaluation.PairsCompared}");
			if (evaluation.DroppedPairs > 0)
				message.Append(CultureInfo.InvariantCulture, $", {evaluation.DroppedPairs} dropped");
			message.Append('\n');
			if (!string.IsNullOrWhiteSpace(evaluation.Batch))
				message.Append(CultureInfo.InvariantCulture, $"  batch            {evaluation.Batch}\n");

			message.Append("\nPromoted by scripts/train-loop.ps1 against the reigning champion, so this is a\n");
			message.Append("measured improvement rather than an edit that merely compiled.\n");
			return message.ToString();
		}

		void Restore(string directory)
		{
			var run = TrainingRun.Load(directory) ?? throw new ArgumentException("No training run found: " + directory);
			using var workspace = TrainingRun.AcquireWorkspaceMutation(run);
			using var mutation = TrainingRun.AcquireMutation(run);
			RestoreSnapshot(mutation.Run);
		}

		/// <summary>Puts back a run's pre-agent source; the caller holds the workspace and run locks.</summary>
		void RestoreSnapshot(TrainingRun run)
		{
			if (run.IsBusy)
				throw new InvalidOperationException("An active worker still owns this run; stop it before restoring.");
			if (!run.HasUnresolvedContinuousExperiment)
				throw new InvalidOperationException("RestoreRun requires an unresolved continuous experiment.");

			// Read before anything changes, so an unreadable snapshot leaves the run as it was.
			var changes = WorkspaceSnapshot.Compare(run);
			output(changes.Count == 0
				? "No source edits since the pre-agent snapshot."
				: $"Discarding {changes.Count} source edit(s) since the pre-agent snapshot:");
			foreach (var change in changes)
				output($"  {change.Kind} {change.RelativePath}");

			run.MarkContinuousRestoring();
			WorkspaceSnapshot.Restore(run);
			output($"Restored the pre-agent source snapshot: {run.Manifest.BotDirectory}");
		}

		/// <summary>
		/// Removes a run that no longer blocks training. The restore already made training safe, so a
		/// run that cannot be deleted is reported and training carries on.
		/// </summary>
		void DeleteRestoredRun(TrainingRun run)
		{
			try
			{
				run.Delete(options.RunsRoot);
				output($"Deleted the blocking run: {run.RunDirectory}");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException or InvalidDataException)
			{
				output($"Restored, but could not delete {run.RunDirectory}: {ex.Message} It no longer blocks training.");
			}
		}

		TrainingScriptResult Execute(string script, IReadOnlyList<string> arguments, string title,
			CancellationToken token, string cancellationFile = null)
		{
			token.ThrowIfCancellationRequested();
			output($"==> {title}");
			var result = execute(new ScriptJob
			{
				Title = title, ScriptPath = script, Arguments = arguments,
				// The agent decides whether to colour its output by what the environment tells
				// it, and a redirected pipe alone reads as "not a terminal". Without this the
				// loop showed a monochrome transcript of a tool that had colour all along.
				PreserveColor = options.Color,
				WorkerOwnershipFile = activeRun?.WorkerOwnershipPath, CancellationFile = cancellationFile
			}, token);
			token.ThrowIfCancellationRequested();
			return result;
		}

		TrainingScriptResult ExecuteScript(ScriptJob job, CancellationToken token)
		{
			var runner = new ScriptRunner();
			var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
			runner.Output += line => output(options.Color ? line.AnsiText : line.PlainText);
			runner.Finished += code => finished.TrySetResult(code);
			runner.Start(job, repo.Root);
			using var registration = token.Register(runner.Stop);
			var code = finished.Task.GetAwaiter().GetResult();
			return new TrainingScriptResult(code, runner.LastOutput);
		}

		static void RequireSuccess(TrainingScriptResult result)
		{
			if (result.ExitCode != 0)
				throw new InvalidOperationException($"The script exited with code {result.ExitCode}; see the preceding output.");
		}

		static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
	}
}
