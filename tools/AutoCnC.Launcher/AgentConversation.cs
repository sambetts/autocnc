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
using System.Text.Json.Serialization;

namespace AutoCnC.Launcher
{
	public enum AgentChatSpeaker
	{
		Player,
		Agent,
		Launcher
	}

	/// <summary>One thing said, by one party, at one moment.</summary>
	public sealed class AgentChatEntry
	{
		[JsonConverter(typeof(JsonStringEnumConverter))]
		public AgentChatSpeaker Speaker { get; set; }
		public DateTime AtUtc { get; set; }
		public string Text { get; set; }

		/// <summary>
		/// True when this was typed while the agent was busy and delivered afterwards.
		/// </summary>
		/// <remarks>
		/// Worth recording because it changes what the answer means. A question asked mid-round
		/// and answered after it was, from the agent's side, asked after the round — and a
		/// transcript that hid the wait would make the reply look like a non-sequitur.
		/// </remarks>
		public bool Deferred { get; set; }
	}

	/// <summary>
	/// The player's running conversation with one fight's coding agent.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The agent process is not long-lived — a prompt-mode agent exits when it has answered — so
	/// continuity is not a process that stays up but a session id that every turn resumes. What
	/// this class owns is the half that the session id cannot carry: what was said, in what order,
	/// and what is still waiting to be said.
	/// </para>
	/// <para>
	/// Waiting is the interesting part. One agent invocation at a time is a hard limit — two
	/// processes resuming one session would interleave into it — but being unable to run a message
	/// immediately is a poor reason to refuse to accept it. So messages typed during a round are
	/// held here and delivered in order when the round ends, which is what makes it possible to
	/// react to something in the live transcript instead of watching it scroll past and trying to
	/// remember the thought until the agent is free.
	/// </para>
	/// </remarks>
	public sealed class AgentConversation
	{
		public const int MaxMessageLength = 8000;

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNameCaseInsensitive = true
		};

		readonly List<AgentChatEntry> history = [];
		readonly Queue<string> pending = new();

		public AgentConversation(TrainingRun run)
		{
			Run = run ?? throw new ArgumentNullException(nameof(run));
			Load();
		}

		public TrainingRun Run { get; }

		public IReadOnlyList<AgentChatEntry> History => history;
		public int PendingCount => pending.Count;
		public IReadOnlyList<string> Pending => [.. pending];

		/// <summary>The message currently being answered, if any.</summary>
		public string InFlight { get; private set; }

		public bool IsBusy => InFlight != null;

		public static string Normalize(string message)
		{
			var trimmed = (message ?? "").Trim();
			return trimmed.Length <= MaxMessageLength ? trimmed : trimmed[..MaxMessageLength];
		}

		/// <summary>
		/// Accepts a message, recording it as said whether or not it can be sent yet.
		/// </summary>
		/// <returns>
		/// True when the agent is free and the caller should send it now; false when it was queued.
		/// </returns>
		public bool Post(string message, bool agentBusy)
		{
			var text = Normalize(message);
			if (text.Length == 0)
				return false;

			var deferred = agentBusy || IsBusy;
			Append(new AgentChatEntry
			{
				Speaker = AgentChatSpeaker.Player,
				AtUtc = DateTime.UtcNow,
				Text = text,
				Deferred = deferred
			});

			pending.Enqueue(text);
			return !deferred;
		}

		/// <summary>Takes the next message to send, if the agent is free to take one.</summary>
		public bool TryStartNext(out string message)
		{
			message = null;
			if (IsBusy || pending.Count == 0)
				return false;

			message = pending.Dequeue();
			InFlight = message;
			return true;
		}

		/// <summary>Records the agent's reply and frees it for the next message.</summary>
		public void Complete(string reply)
		{
			InFlight = null;
			var text = (reply ?? "").Trim();
			if (text.Length > 0)
				Append(new AgentChatEntry
				{
					Speaker = AgentChatSpeaker.Agent,
					AtUtc = DateTime.UtcNow,
					Text = text
				});
		}

		/// <summary>
		/// Records that a turn could not be delivered, and abandons whatever was queued behind it.
		/// </summary>
		/// <remarks>
		/// Behind it too, because queued messages were written in the belief that the ones before
		/// them had landed. Sending the rest into a session that never heard the first one produces
		/// answers to a conversation that did not happen, which is worse than saying nothing.
		/// </remarks>
		public void Fail(string reason)
		{
			InFlight = null;
			var abandoned = pending.Count;
			pending.Clear();
			Append(new AgentChatEntry
			{
				Speaker = AgentChatSpeaker.Launcher,
				AtUtc = DateTime.UtcNow,
				Text = abandoned == 0
					? reason
					: $"{reason} {abandoned} queued message(s) were not sent."
			});
		}

		public void Note(string text) => Append(new AgentChatEntry
		{
			Speaker = AgentChatSpeaker.Launcher,
			AtUtc = DateTime.UtcNow,
			Text = (text ?? "").Trim()
		});

		/// <summary>Forgets what is queued without touching what was already said.</summary>
		public void ClearPending() => pending.Clear();

		/// <summary>
		/// The agent's reply, recovered from the turn transcript the chat script wrote.
		/// </summary>
		/// <remarks>
		/// The transcript holds the message as well as the answer, under headings the script
		/// writes, so the answer is whatever follows the last heading. Falling back to the whole
		/// text matters more than it looks: an agent that failed before it said anything useful
		/// still said something, and showing it beats showing an empty reply.
		/// </remarks>
		public static string ReadReply(string transcriptPath)
		{
			if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
				return null;

			string text;
			try
			{
				text = File.ReadAllText(transcriptPath);
			}
			catch (IOException)
			{
				return null;
			}
			catch (UnauthorizedAccessException)
			{
				return null;
			}

			var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Replace('\r', '\n').Split('\n');
			var lastHeading = -1;
			for (var i = 0; i < lines.Length; i++)
			{
				if (lines[i].StartsWith("=== ", StringComparison.Ordinal))
					lastHeading = i;
			}

			var reply = string.Join(Environment.NewLine, lines.Skip(lastHeading + 1)).Trim();
			return reply.Length > 0 ? reply : text.Trim();
		}

		void Append(AgentChatEntry entry)
		{
			history.Add(entry);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Run.ChatPath));
				File.AppendAllText(Run.ChatPath,
					JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
			}
			catch (IOException)
			{
				// A conversation that cannot be written down is still worth having.
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		void Load()
		{
			if (!File.Exists(Run.ChatPath))
				return;

			try
			{
				foreach (var line in File.ReadLines(Run.ChatPath))
				{
					if (string.IsNullOrWhiteSpace(line))
						continue;

					var entry = JsonSerializer.Deserialize<AgentChatEntry>(line, JsonOptions);
					if (entry != null)
						history.Add(entry);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (JsonException)
			{
				// A truncated last line is the normal cost of appending; keep what parsed.
			}
		}
	}
}
