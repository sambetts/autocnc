#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoCnC.Evidence
{
	/// <summary>One <c>unit-decision</c> record, flattened.</summary>
	public sealed class UnitDecisionRecord
	{
		public int Seconds { get; init; }
		public string Actor { get; init; }
		public uint ActorId { get; init; }
		public string Mode { get; init; }
		public string Action { get; init; }
		public string ItemName { get; init; }
		public string Queue { get; init; }
		public string Reason { get; init; }
		public string Order { get; init; }
	}

	/// <summary>One <c>assessment</c> record's <c>state</c> block, flattened.</summary>
	public sealed class AssessmentRecord
	{
		public int Seconds { get; init; }
		public string Doctrine { get; init; }
		public int Cash { get; init; }
		public int PowerBalance { get; init; }
		public int Harvesters { get; init; }
		public int Refineries { get; init; }
		public int ArmyValue { get; init; }
		public int Buildings { get; init; }
		public int NearestEnemyCells { get; init; }
		public int SecondsSinceContact { get; init; }
		public bool EnemyBaseFound { get; init; }
		public bool BaseUnderAttack { get; init; }
		public bool BlindToEnemy { get; init; }
		public bool Winning { get; init; }
		public string Outcome { get; init; }
	}

	/// <summary>A doctrine change, as the trace recorded it.</summary>
	public sealed class DoctrineChangeRecord
	{
		public int Seconds { get; init; }
		public string From { get; init; }
		public string To { get; init; }
		public string Reason { get; init; }
	}

	/// <summary>
	/// Everything one pass over <c>decisions.jsonl</c> can yield.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The whole point of this type is that it is read once. The trace is the largest artifact a
	/// fight produces and the only one that needs a JSON parser per line, and until now every
	/// analysis paid that cost again in a fresh process. Everything downstream — the summary, the
	/// unit ledger, the checks — is built from one instance of this, and the artifacts it produces
	/// are what later analysis reads instead.
	/// </para>
	/// <para>
	/// Unknown event names and unknown fields are ignored rather than rejected. The trace is
	/// append-only across schema versions, so a reader that insisted on knowing every field would
	/// refuse the next version of a file it can otherwise read perfectly well.
	/// </para>
	/// </remarks>
	public sealed class DecisionTrace
	{
		public int SchemaVersion { get; private set; }
		public string Bot { get; private set; }
		public string Source { get; private set; }
		public string Result { get; private set; }
		public int CompletedSeconds { get; private set; }

		public List<UnitDecisionRecord> UnitDecisions { get; } = [];
		public List<AssessmentRecord> Assessments { get; } = [];
		public List<DoctrineChangeRecord> DoctrineChanges { get; } = [];
		public List<string> Errors { get; } = [];

		/// <summary>
		/// Every distinct <c>reason</c> literal the run produced, with how often it was issued.
		/// </summary>
		/// <remarks>
		/// This is what makes "did the new branch actually run?" a decidable question. A round that
		/// adds a code path gives it a reason string nothing else uses; the check then asserts that
		/// literal appears here. Counting rather than just recording presence means "it ran" and
		/// "it ran twice in a 25-minute match" are distinguishable, which is usually the more
		/// interesting failure.
		/// </remarks>
		public Dictionary<string, int> ReasonCounts { get; } = new(StringComparer.Ordinal);

		public Dictionary<string, int> ModeDecisionCounts { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, int> ActionCounts { get; } = new(StringComparer.Ordinal);

		/// <summary>True when any reason literal contains <paramref name="text"/>.</summary>
		public bool MentionsReason(string text) =>
			!string.IsNullOrEmpty(text) &&
			ReasonCounts.Keys.Any(r => r.Contains(text, StringComparison.OrdinalIgnoreCase));

		/// <summary>How many decisions carried a reason containing <paramref name="text"/>.</summary>
		public int ReasonMentions(string text)
		{
			if (string.IsNullOrEmpty(text))
				return 0;

			var total = 0;
			foreach (var pair in ReasonCounts)
				if (pair.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
					total += pair.Value;

			return total;
		}

		public static DecisionTrace Read(string path)
		{
			var trace = new DecisionTrace();
			if (!File.Exists(path))
				return trace;

			foreach (var line in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;

				// A trace is flushed per line while the match runs, so a cancelled or crashed
				// fight can leave a half-written final line. That is evidence too; it is not a
				// reason to throw away the 2,000 good lines above it.
				try
				{
					using var document = JsonDocument.Parse(line);
					trace.Accept(document.RootElement);
				}
				catch (JsonException)
				{
					trace.Errors.Add("unparseable trace line");
				}
			}

			return trace;
		}

		void Accept(JsonElement root)
		{
			var kind = Text(root, "event");
			switch (kind)
			{
				case "started":
					SchemaVersion = Integer(root, "schemaVersion");
					break;

				case "bot-loaded":
					Bot = Text(root, "bot");
					Source = Text(root, "source");
					break;

				case "completed":
					Result = Text(root, "result");
					CompletedSeconds = Integer(root, "seconds");
					break;

				case "doctrine":
					DoctrineChanges.Add(new DoctrineChangeRecord
					{
						Seconds = Integer(root, "seconds"),
						From = Text(root, "from"),
						To = Text(root, "to"),
						Reason = Text(root, "reason")
					});
					break;

				case "error":
					Errors.Add($"{Text(root, "scope")}/{Text(root, "subject")}: {Text(root, "error")}");
					break;

				case "assessment":
					AcceptAssessment(root);
					break;

				case "unit-decision":
					AcceptUnitDecision(root);
					break;
			}
		}

		void AcceptAssessment(JsonElement root)
		{
			if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
				return;

			Assessments.Add(new AssessmentRecord
			{
				Seconds = Integer(root, "seconds"),
				Doctrine = Text(state, "doctrine"),
				Cash = Integer(state, "cash"),
				PowerBalance = Integer(state, "powerBalance"),
				Harvesters = Integer(state, "harvesters"),
				Refineries = Integer(state, "refineries"),
				ArmyValue = Integer(state, "armyValue"),
				Buildings = Integer(state, "buildings"),
				NearestEnemyCells = Integer(state, "nearestEnemyCells"),
				SecondsSinceContact = Integer(state, "secondsSinceContact"),
				EnemyBaseFound = Flag(state, "enemyBaseFound"),
				BaseUnderAttack = Flag(state, "baseUnderAttack"),
				BlindToEnemy = Flag(state, "blindToEnemy"),
				Winning = Flag(state, "winning"),
				Outcome = Text(root, "outcome")
			});
		}

		void AcceptUnitDecision(JsonElement root)
		{
			var record = new UnitDecisionRecord
			{
				Seconds = Integer(root, "seconds"),
				Actor = Text(root, "actor"),
				ActorId = (uint)Math.Max(0, Integer(root, "actorId")),
				Mode = Text(root, "mode"),
				Action = Text(root, "action"),
				ItemName = Text(root, "itemName"),
				Queue = Text(root, "queue"),
				Reason = Text(root, "reason"),
				Order = Text(root, "order")
			};

			UnitDecisions.Add(record);

			if (!string.IsNullOrEmpty(record.Reason))
				ReasonCounts[record.Reason] = ReasonCounts.GetValueOrDefault(record.Reason) + 1;

			if (!string.IsNullOrEmpty(record.Mode))
				ModeDecisionCounts[record.Mode] = ModeDecisionCounts.GetValueOrDefault(record.Mode) + 1;

			if (!string.IsNullOrEmpty(record.Action))
				ActionCounts[record.Action] = ActionCounts.GetValueOrDefault(record.Action) + 1;
		}

		static string Text(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static int Integer(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetInt64(out var parsed)
				? (int)Math.Clamp(parsed, int.MinValue, int.MaxValue)
				: 0;

		static bool Flag(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
	}
}
