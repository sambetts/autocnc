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
using System.IO;
using System.Text.Json;
using AutoCnC.Core;
using OpenRA;

namespace AutoCnC.Platform.Traits
{
	/// <summary>
	/// Writes the reasoning bridge between a battle log and its outcome as newline-delimited JSON.
	/// </summary>
	/// <remarks>
	/// The battle log says what the side observed and telemetry says how the match went. This trace
	/// records the missing middle: what the bot decided about each assessment and which unit
	/// decisions actually became orders. It is deliberately write-only from the game's point of
	/// view, so bot code cannot inspect or learn from information it did not have during the fight.
	/// </remarks>
	sealed class DecisionTrace : IDisposable
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		StreamWriter writer;

		DecisionTrace(StreamWriter writer)
		{
			this.writer = writer;
			Write(new
			{
				Event = "started",
				SchemaVersion = 1,
				RecordedAtUtc = DateTime.UtcNow
			});
		}

		public static DecisionTrace Open(string file)
		{
			var path = ResolvePath(file);
			if (path == null)
				return null;

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));

				if (File.Exists(path))
					File.Move(path, path + ".1", true);

				var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
					FileShare.ReadWrite | FileShare.Delete);
				return new DecisionTrace(new StreamWriter(stream) { AutoFlush = true });
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Decision trace: could not write {path}: {ex.Message}");
				return null;
			}
		}

		public void BotLoaded(int seconds, string bot, string source) =>
			Write(new
			{
				Event = "bot-loaded",
				Seconds = seconds,
				Bot = bot,
				Source = source
			});

		public void Assessment(int seconds, in BattleState state,
			in DoctrineDecision botDecision, in DoctrineDecision modeRequest,
			in DoctrineDecision effectiveDecision, string outcome) =>
			Write(new
			{
				Event = "assessment",
				Seconds = seconds,
				State = state,
				BotDecision = Decision(botDecision),
				ModeRequest = Decision(modeRequest),
				EffectiveDecision = Decision(effectiveDecision),
				Outcome = outcome
			});

		public void DoctrineChanged(int seconds, string from, string to, string reason) =>
			Write(new
			{
				Event = "doctrine",
				Seconds = seconds,
				From = from,
				To = to,
				Reason = reason
			});

		public void UnitDecisionIssued(int seconds, string actor, uint actorId, string mode,
			in UnitDecision decision, string order) =>
			Write(new
			{
				Event = "unit-decision",
				Seconds = seconds,
				Actor = actor,
				ActorId = actorId,
				Mode = mode,
				Action = decision.Action.ToString(),
				decision.TargetActorId,
				decision.TargetX,
				decision.TargetY,
				decision.ItemName,
				decision.Queue,
				decision.Reason,
				Order = order
			});

		public void Error(int seconds, string scope, string subject, Exception exception) =>
			Write(new
			{
				Event = "error",
				Seconds = seconds,
				Scope = scope,
				Subject = subject,
				Error = exception.Message
			});

		public void Complete(int seconds, string result) =>
			Write(new
			{
				Event = "completed",
				Seconds = seconds,
				Result = result,
				RecordedAtUtc = DateTime.UtcNow
			});

		static object Decision(in DoctrineDecision decision) => new
		{
			WantsChange = decision.WantsChange,
			decision.Doctrine,
			decision.Reason
		};

		void Write(object value)
		{
			if (writer == null)
				return;

			try
			{
				writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Decision trace: stopped recording after {ex.Message}");
				Dispose();
			}
		}

		public void Dispose()
		{
			try
			{
				writer?.Dispose();
			}
			catch (IOException)
			{
			}

			writer = null;
		}

		static string ResolvePath(string file)
		{
			if (string.IsNullOrWhiteSpace(file) ||
				string.Equals(file, "none", StringComparison.OrdinalIgnoreCase))
				return null;

			return Path.IsPathRooted(file)
				? file
				: Path.Combine(OpenRA.Platform.SupportDir, "Logs", file);
		}
	}
}
