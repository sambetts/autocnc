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
using System.Text.Json;

namespace AutoCnC.Evidence
{
	/// <summary>One issued <c>unit-decision</c> record, flattened.</summary>
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
		public string ReasonId { get; init; }
		public string Order { get; init; }
	}

	/// <summary>One evaluated unit decision, whether or not it became an order.</summary>
	public sealed class UnitDecisionEvaluationRecord
	{
		public int Seconds { get; init; }
		public string Actor { get; init; }
		public uint ActorId { get; init; }
		public string Mode { get; init; }
		public string Action { get; init; }
		public string ItemName { get; init; }
		public string Queue { get; init; }
		public string Reason { get; init; }
		public string ReasonId { get; init; }
		public string Outcome { get; init; }
		public ProductionBudgetTraceRecord ProductionBudget { get; init; }
		public ProductionTraceRecord Production { get; init; }
	}

	/// <summary>Production reservation attached to a budget-suppressed evaluation.</summary>
	public sealed class ProductionBudgetTraceRecord
	{
		public int ReservedCash { get; init; }
		public string OwnerQueue { get; init; }
		public string Reason { get; init; }
		public string ReasonId { get; init; }
	}

	/// <summary>Cash calculation attached to a budget-suppressed evaluation.</summary>
	public sealed class ProductionTraceRecord
	{
		public int ItemCost { get; init; }
		public long CurrentCash { get; init; }
		public long PostOrderCash { get; init; }
		public long ReservedCashRemaining { get; init; }
	}

	/// <summary>Visible enemy count and value for one threat kind in an assessment.</summary>
	public sealed class ThreatValueRecord
	{
		public string Kind { get; init; }
		public int Count { get; init; }
		public int Value { get; init; }
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
		public int Units { get; init; }
		public int ArmyValue { get; init; }
		public int Buildings { get; init; }
		public int BaseValue { get; init; }
		public int WindowSeconds { get; init; }
		public int UnitsLost { get; init; }
		public int BuildingsLost { get; init; }
		public int UnitsKilled { get; init; }
		public int CreditsKilled { get; init; }
		public int CreditsLost { get; init; }
		public bool HasValueTradeData { get; init; }
		public int IncomeEarned { get; init; }
		public int VisibleEnemyValue { get; init; }
		public int EnemyValueNearBase { get; init; }
		public int OwnArmyValueNearBase { get; init; }
		public List<ThreatValueRecord> VisibleEnemyMix { get; init; } = [];
		public int EnemiesInSight { get; init; }
		public int EnemiesNearBase { get; init; }
		public int NearestEnemyCells { get; init; }
		public int SecondsSinceContact { get; init; }
		public bool EnemyBaseFound { get; init; }
		public bool BaseUnderAttack { get; init; }
		public bool BlindToEnemy { get; init; }
		public bool Winning { get; init; }
		public string BotReasonId { get; init; }
		public string ModeRequestReasonId { get; init; }
		public string EffectiveReasonId { get; init; }
		public bool EffectiveDecisionUrgent { get; init; }
		public string Outcome { get; init; }
	}

	/// <summary>A doctrine change, as the trace recorded it.</summary>
	public sealed class DoctrineChangeRecord
	{
		public int Seconds { get; init; }
		public string From { get; init; }
		public string To { get; init; }
		public string Reason { get; init; }
		public string ReasonId { get; init; }
		public bool Urgent { get; init; }
		public bool DwellBypassed { get; init; }
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
	/// <para>
	/// Issued-decision indexes retain their historical meaning. Schema 3 adds a separate
	/// evaluation stream, and machine-readable reason counts use that stream so suppressed and
	/// unbuildable decisions remain observable without double-counting issued orders.
	/// </para>
	/// </remarks>
	public sealed class DecisionTrace
	{
		readonly Dictionary<string, int> issuedReasonIdCounts = new(StringComparer.Ordinal);

		public int SchemaVersion { get; private set; }
		public string Bot { get; private set; }
		public string Source { get; private set; }
		public string Result { get; private set; }
		public int CompletedSeconds { get; private set; }

		public List<UnitDecisionRecord> UnitDecisions { get; } = [];
		public List<UnitDecisionEvaluationRecord> UnitDecisionEvaluations { get; } = [];
		public List<AssessmentRecord> Assessments { get; } = [];
		public List<DoctrineChangeRecord> DoctrineChanges { get; } = [];
		public List<string> Errors { get; } = [];

		/// <summary>
		/// Every distinct legacy unit-decision <c>reason</c> literal, with how often it was issued.
		/// </summary>
		/// <remarks>
		/// Kept for old traces and <c>reason:</c> checks. New code should use
		/// <see cref="ReasonIdCounts"/> so explanatory prose can change without invalidating a
		/// check.
		/// </remarks>
		public Dictionary<string, int> ReasonCounts { get; } = new(StringComparer.Ordinal);

		/// <summary>
		/// Every exact machine-readable reason identifier in evaluations, assessments, and
		/// doctrine changes, including nested production-budget reasons, with occurrence count.
		/// Old traces fall back to issued decisions.
		/// </summary>
		public Dictionary<string, int> ReasonIdCounts { get; } = new(StringComparer.Ordinal);

		public Dictionary<string, int> ModeDecisionCounts { get; } = new(StringComparer.Ordinal);
		public Dictionary<string, int> ActionCounts { get; } = new(StringComparer.Ordinal);

		/// <summary>
		/// True when an exact reason ID exists, or a legacy reason contains
		/// <paramref name="text"/>.
		/// </summary>
		public bool MentionsReason(string text) => ReasonMentions(text) > 0;

		/// <summary>
		/// Counts an exact reason ID when one exists; otherwise falls back to the historical
		/// case-insensitive prose substring match.
		/// </summary>
		public int ReasonMentions(string text)
		{
			if (string.IsNullOrEmpty(text))
				return 0;

			var exact = ReasonIdMentions(text);
			if (exact > 0)
				return exact;

			var total = 0;
			foreach (var pair in ReasonCounts)
				if (pair.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
					total += pair.Value;

			return total;
		}

		/// <summary>How many trace fields carried exactly <paramref name="reasonId"/>.</summary>
		public int ReasonIdMentions(string reasonId) =>
			!string.IsNullOrEmpty(reasonId) && ReasonIdCounts.TryGetValue(reasonId, out var count)
				? count
				: 0;

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

			trace.IncludeLegacyIssuedReasonIds();
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
				{
					var change = new DoctrineChangeRecord
					{
						Seconds = Integer(root, "seconds"),
						From = Text(root, "from"),
						To = Text(root, "to"),
						Reason = Text(root, "reason"),
						ReasonId = Text(root, "reasonId"),
						Urgent = Flag(root, "urgent"),
						DwellBypassed = Flag(root, "dwellBypassed")
					};
					DoctrineChanges.Add(change);
					RegisterReasonId(change.ReasonId);
					break;
				}

				case "error":
					Errors.Add($"{Text(root, "scope")}/{Text(root, "subject")}: {Text(root, "error")}");
					break;

				case "assessment":
					AcceptAssessment(root);
					break;

				case "unit-decision":
					AcceptUnitDecision(root);
					break;

				case "unit-decision-evaluated":
					AcceptUnitDecisionEvaluation(root);
					break;
			}
		}

		void AcceptAssessment(JsonElement root)
		{
			if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
				return;

			var botReasonId = DecisionText(root, "botDecision", "reasonId");
			var modeRequestReasonId = DecisionText(root, "modeRequest", "reasonId");
			var effectiveReasonId = DecisionText(root, "effectiveDecision", "reasonId");
			var record = new AssessmentRecord
			{
				Seconds = Integer(root, "seconds"),
				Doctrine = Text(state, "doctrine"),
				Cash = Integer(state, "cash"),
				PowerBalance = Integer(state, "powerBalance"),
				Harvesters = Integer(state, "harvesters"),
				Refineries = Integer(state, "refineries"),
				Units = Integer(state, "units"),
				ArmyValue = Integer(state, "armyValue"),
				Buildings = Integer(state, "buildings"),
				BaseValue = Integer(state, "baseValue"),
				WindowSeconds = Integer(state, "windowSeconds"),
				UnitsLost = Integer(state, "unitsLost"),
				BuildingsLost = Integer(state, "buildingsLost"),
				UnitsKilled = Integer(state, "unitsKilled"),
				CreditsKilled = Integer(state, "creditsKilled"),
				CreditsLost = Integer(state, "creditsLost"),
				HasValueTradeData = Flag(state, "hasValueTradeData"),
				IncomeEarned = Integer(state, "incomeEarned"),
				VisibleEnemyValue = Integer(state, "visibleEnemyValue"),
				EnemyValueNearBase = Integer(state, "enemyValueNearBase"),
				OwnArmyValueNearBase = Integer(state, "ownArmyValueNearBase"),
				VisibleEnemyMix = ThreatValues(state, "visibleEnemyMix"),
				EnemiesInSight = Integer(state, "enemiesInSight"),
				EnemiesNearBase = Integer(state, "enemiesNearBase"),
				NearestEnemyCells = Integer(state, "nearestEnemyCells"),
				SecondsSinceContact = Integer(state, "secondsSinceContact"),
				EnemyBaseFound = Flag(state, "enemyBaseFound"),
				BaseUnderAttack = Flag(state, "baseUnderAttack"),
				BlindToEnemy = Flag(state, "blindToEnemy"),
				Winning = Flag(state, "winning"),
				BotReasonId = botReasonId,
				ModeRequestReasonId = modeRequestReasonId,
				EffectiveReasonId = effectiveReasonId,
				EffectiveDecisionUrgent = DecisionFlag(root, "effectiveDecision", "isUrgent"),
				Outcome = Text(root, "outcome")
			};

			Assessments.Add(record);
			RegisterReasonId(botReasonId);
			RegisterReasonId(modeRequestReasonId);
			RegisterReasonId(effectiveReasonId);
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
				ReasonId = Text(root, "reasonId"),
				Order = Text(root, "order")
			};

			UnitDecisions.Add(record);

			if (!string.IsNullOrEmpty(record.Reason))
				ReasonCounts[record.Reason] = ReasonCounts.GetValueOrDefault(record.Reason) + 1;

			if (!string.IsNullOrEmpty(record.ReasonId))
				issuedReasonIdCounts[record.ReasonId] =
					issuedReasonIdCounts.GetValueOrDefault(record.ReasonId) + 1;

			if (!string.IsNullOrEmpty(record.Mode))
				ModeDecisionCounts[record.Mode] = ModeDecisionCounts.GetValueOrDefault(record.Mode) + 1;

			if (!string.IsNullOrEmpty(record.Action))
				ActionCounts[record.Action] = ActionCounts.GetValueOrDefault(record.Action) + 1;
		}

		void AcceptUnitDecisionEvaluation(JsonElement root)
		{
			var record = new UnitDecisionEvaluationRecord
			{
				Seconds = Integer(root, "seconds"),
				Actor = Text(root, "actor"),
				ActorId = (uint)Math.Max(0, Integer(root, "actorId")),
				Mode = Text(root, "mode"),
				Action = Text(root, "action"),
				ItemName = Text(root, "itemName"),
				Queue = Text(root, "queue"),
				Reason = Text(root, "reason"),
				ReasonId = Text(root, "reasonId"),
				Outcome = Text(root, "outcome"),
				ProductionBudget = ProductionBudget(root),
				Production = Production(root)
			};

			UnitDecisionEvaluations.Add(record);
			RegisterReasonId(record.ReasonId);
			RegisterReasonId(record.ProductionBudget?.ReasonId);
		}

		void IncludeLegacyIssuedReasonIds()
		{
			if (SchemaVersion >= 3)
				return;

			foreach (var pair in issuedReasonIdCounts)
				ReasonIdCounts[pair.Key] = ReasonIdCounts.GetValueOrDefault(pair.Key) + pair.Value;
		}

		void RegisterReasonId(string reasonId)
		{
			if (!string.IsNullOrEmpty(reasonId))
				ReasonIdCounts[reasonId] = ReasonIdCounts.GetValueOrDefault(reasonId) + 1;
		}

		static string DecisionText(JsonElement root, string decision, string name) =>
			root.TryGetProperty(decision, out var value) && value.ValueKind == JsonValueKind.Object
				? Text(value, name)
				: null;

		static bool DecisionFlag(JsonElement root, string decision, string name) =>
			root.TryGetProperty(decision, out var value) && value.ValueKind == JsonValueKind.Object &&
			Flag(value, name);

		static ProductionBudgetTraceRecord ProductionBudget(JsonElement root)
		{
			if (!root.TryGetProperty("productionBudget", out var budget) ||
				budget.ValueKind != JsonValueKind.Object)
				return null;

			return new ProductionBudgetTraceRecord
			{
				ReservedCash = Integer(budget, "reservedCash"),
				OwnerQueue = Text(budget, "ownerQueue"),
				Reason = Text(budget, "reason"),
				ReasonId = Text(budget, "reasonId")
			};
		}

		static ProductionTraceRecord Production(JsonElement root)
		{
			if (!root.TryGetProperty("production", out var production) ||
				production.ValueKind != JsonValueKind.Object)
				return null;

			return new ProductionTraceRecord
			{
				ItemCost = Integer(production, "itemCost"),
				CurrentCash = Long(production, "currentCash"),
				PostOrderCash = Long(production, "postOrderCash"),
				ReservedCashRemaining = Long(production, "reservedCashRemaining")
			};
		}

		static List<ThreatValueRecord> ThreatValues(JsonElement root, string name)
		{
			var result = new List<ThreatValueRecord>();
			if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
				return result;

			foreach (var value in values.EnumerateArray())
			{
				if (value.ValueKind != JsonValueKind.Object)
					continue;

				result.Add(new ThreatValueRecord
				{
					Kind = Text(value, "kind") ?? Integer(value, "kind").ToString(),
					Count = Integer(value, "count"),
					Value = Integer(value, "value")
				});
			}

			return result;
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

		static long Long(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetInt64(out var parsed)
				? parsed
				: 0;

		static bool Flag(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
	}
}
