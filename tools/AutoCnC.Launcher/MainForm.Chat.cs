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

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Talking to the coding agent that improves the selected bot.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A prompt-mode agent is not a process you can hold a conversation with — it answers and
	/// exits. Continuity comes from the fight's pinned session id, which every turn resumes, so
	/// what the player says before a round, the round itself, and what they ask afterwards all
	/// reach one agent with one memory.
	/// </para>
	/// <para>
	/// Turns go through the same queue as everything else, which is what makes it safe to type
	/// during a round: only one agent can be resuming the session at a time, and a message typed
	/// while one is working waits its turn instead of racing it.
	/// </para>
	/// </remarks>
	public partial class MainForm
	{
		/// <summary>
		/// One conversation per run, kept for as long as the launcher is open.
		/// </summary>
		/// <remarks>
		/// Kept rather than rebuilt on demand because a conversation holds live state — the turn
		/// being answered and the messages queued behind it — that is not in the file it was read
		/// from. Looking at another fight and coming back must not be what loses it.
		/// </remarks>
		readonly Dictionary<string, AgentConversation> conversations =
			new(StringComparer.OrdinalIgnoreCase);

		AgentConversation conversation;

		internal AgentConversation ActiveConversation => conversation;

		/// <summary>The conversation for a run, loading its recorded history the first time.</summary>
		AgentConversation ConversationFor(TrainingRun run)
		{
			if (run == null)
				return null;

			var key = Path.GetFullPath(run.RunDirectory);
			if (!conversations.TryGetValue(key, out var found))
				conversations[key] = found = new AgentConversation(run);

			conversation = found;
			return found;
		}

		/// <summary>
		/// Accepts something the player wants to say, sending it now or holding it until the agent
		/// is free.
		/// </summary>
		internal void SendAgentMessage(TrainingRun run, string message)
		{
			if (repo == null || run?.CanChat != true)
				return;

			var thread = ConversationFor(run);
			var sendNow = thread.Post(message, agentBusy: OperationInProgress);
			improvementWindow?.RefreshChat();

			if (sendNow)
			{
				if (!StartQueuedConversation())
					UpdateEnabledState();

				return;
			}

			Status(thread.PendingCount == 1
				? "Message queued. The agent will answer when it is free."
				: $"Message queued. {thread.PendingCount} waiting for the agent.");
		}

		/// <summary>
		/// Starts the next queued message when nothing else is using the agent.
		/// </summary>
		/// <param name="finishedWith">
		/// What the work that just ended left behind, reported alongside the turn taking its
		/// place. Null when this message follows nothing.
		/// </param>
		/// <returns>True when a turn was started and the caller should stand down.</returns>
		bool StartQueuedConversation(string finishedWith = null)
		{
			var thread = conversation;
			if (thread == null || thread.PendingCount == 0 || repo == null ||
				runner.IsRunning || activeJob != null || queue.Count > 0 || battleRunning ||
				closing)
				return false;

			var run = thread.Run;
			if (!run.CanChat || !File.Exists(repo.ChatBotScript))
			{
				thread.Fail("There is no agent to talk to from this checkout.");
				improvementWindow?.RefreshChat();
				return false;
			}

			if (!thread.TryStartNext(out var message))
				return false;

			string continuousFingerprint = null;
			TrainingWorkspaceMutation chatWorkspaceMutation = null;
			try
			{
				chatWorkspaceMutation = TrainingRun.AcquireWorkspaceMutation(run);
				if (continuousLoop.IsRunning &&
					SamePath(continuousCandidateRun?.RunDirectory, run.RunDirectory))
					continuousFingerprint =
						continuousPromotion.CaptureAgentChatFingerprint(run);

				run.EnsureAgentSessionId();

				// The improvement round is what normally records which agent this fight uses, and
				// talking can come first. Written here too so an early question reaches the agent
				// the player configured rather than a stock one.
				TrainingAgent.WriteConfiguration(run, settings.AgentCommand, settings.AgentArguments,
					settings.AgentStdin);

				// Through a file rather than an argument. A message is prose the player wrote:
				// it has newlines and quotes in it, and every layer between here and the agent —
				// PowerShell's parameter binder, the process command line — is one more place for
				// punctuation to change meaning.
				File.WriteAllText(run.ChatMessagePath, message);
			}
			catch (InvalidOperationException ex)
			{
				chatWorkspaceMutation?.Dispose();
				return AbandonTurn(thread, ex.Message);
			}
			catch (IOException ex)
			{
				chatWorkspaceMutation?.Dispose();
				return AbandonTurn(thread, ex.Message);
			}
			catch (UnauthorizedAccessException ex)
			{
				chatWorkspaceMutation?.Dispose();
				return AbandonTurn(thread, ex.Message);
			}

			var window = ShowImprovementWindow();
			window.ShowConversation(thread);
			window.BeginChatTurn();
			queue.Enqueue(new ScriptJob
			{
				// Named for what it is rather than for the agent it reaches. "Improvement" in a
				// status line is read as the improvement, and a message answered the moment a
				// round ends would otherwise report the finished round as still running.
				Title = "Answering your message",
				ScriptPath = repo.ChatBotScript,
				Arguments =
				[
					"-BattleBot", run.Manifest.BotProject,
					"-RunDirectory", run.RunDirectory,
					"-MessageFile", run.ChatMessagePath
				],
				PreserveColor = true,
				Kind = ScriptJobKind.Chat,
				Output = AppendChatOutput,

				// The conversation is captured rather than looked up again, so a turn started
				// against one fight still completes against that fight even if the window has
				// been pointed at another one in the meantime.
				Completed = code =>
				{
					try
					{
						FinishChatTurn(thread, code, continuousFingerprint);
					}
					finally
					{
						chatWorkspaceMutation.Dispose();
					}
				}
			});

			RunNext();

			// After RunNext, which sets the status from the job it started. What just finished is
			// the more valuable half of the sentence and would otherwise never be said at all.
			if (!string.IsNullOrWhiteSpace(finishedWith))
				Status($"{finishedWith} Answering your message…");

			return true;
		}

		bool AbandonTurn(AgentConversation thread, string reason)
		{
			thread.Fail($"The message could not be handed to the agent: {reason}");
			improvementWindow?.RefreshChat();
			return false;
		}

		void FinishChatTurn(AgentConversation thread, int exitCode,
			string continuousFingerprint)
		{
			var run = thread.Run;
			if (exitCode == 0)
				thread.Complete(AgentConversation.ReadReply(run.ChatTranscriptPath) ??
					"The agent answered, but its reply could not be read back.");
			else
				thread.Fail(stopRequested
					? "You stopped the agent before it answered."
					: $"The agent could not answer; it exited with code {exitCode}.");

			try
			{
				if (continuousPromotion.InvalidateAfterAgentChat(
					run, continuousFingerprint))
					thread.Note(
						"The chat changed source, so the continuous candidate will be reevaluated.");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
				InvalidOperationException)
			{
				thread.Note(
					"The launcher could not compare source after chat: " + ex.Message);
				AbortUnresolvedContinuousExperiment(
					"Source could not be reconciled after edit-capable agent chat.");
				continuousLoop.Stop();
				pendingContinuousAction = ContinuousTrainingAction.None;
				Status("Continuous improvement stopped because source could not be verified after chat.");
			}

			if (improvementWindow != null && !improvementWindow.IsDisposed &&
				ReferenceEquals(conversation, thread))
				improvementWindow.EndChatTurn();

			try
			{
				if (File.Exists(run.ChatMessagePath))
					File.Delete(run.ChatMessagePath);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		/// <summary>Forgets every queued message, for when the player calls the operation off.</summary>
		void ClearQueuedConversation()
		{
			foreach (var thread in conversations.Values)
				thread.ClearPending();

			improvementWindow?.RefreshChat();
		}

		void AppendChatOutput(TerminalLine line)
		{
			if (improvementWindow != null && !improvementWindow.IsDisposed)
				improvementWindow.AppendChatOutput(line);
		}
	}
}
