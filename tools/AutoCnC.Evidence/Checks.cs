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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Evidence
{
	public static class CheckCategories
	{
		public const string Activation = "activation";
		public const string Invariant = "invariant";
		public const string Outcome = "outcome";
		public const string Uncategorized = "uncategorized";

		public static string Normalize(string category)
		{
			if (string.IsNullOrWhiteSpace(category))
				return null;

			var normalized = category.Trim().ToLowerInvariant();
			return normalized is Activation or Invariant or Outcome
				? normalized
				: Uncategorized;
		}
	}

	/// <summary>One falsifiable claim about the next fight.</summary>
	/// <remarks>
	/// <para>
	/// Rounds have always ended by writing a prediction into a README in English, which the next
	/// round was supposed to verify by hand and usually did not. This is the same prediction
	/// written so the harness can settle it.
	/// </para>
	/// <para>
	/// The <c>reason-id:</c> query is the important one. A round that adds a code path gives the
	/// path a stable identifier and asserts it appears; the check then distinguishes
	/// "the branch is wrong" from "the branch never ran at all", which are the two explanations
	/// that get confused when the same class of bug is re-introduced four times under four names.
	/// </para>
	/// </remarks>
	public sealed class Check
	{
		public string Id { get; set; }
		public string Description { get; set; }

		/// <summary>
		/// <c>activation</c> proves a path ran, <c>invariant</c> protects a safety property, and
		/// <c>outcome</c> records an observed result without becoming a promotion pass-rate gate.
		/// Null keeps schema 1 checks authored before categories fully valid.
		/// </summary>
		public string Category { get; set; }

		/// <summary>
		/// What to measure. One of:
		/// <list type="bullet">
		/// <item><c>summary.&lt;dotted.path&gt;</c> — any field of <c>summary.json</c>.</item>
		/// <item><c>summary.unitTypes[e1].creditsPerKill</c> — a unit type's ledger entry.</item>
		/// <item><c>summary.production[Infantry].deepestPlanStepIndex</c> — one queue.</item>
		/// <item><c>reason-id:&lt;id&gt;</c> — exact machine-readable identifier across unit
		/// evaluations, assessment decisions, and doctrine changes.</item>
		/// <item><c>reason:&lt;literal&gt;</c> — exact ID when present, otherwise the legacy
		/// case-insensitive prose substring query.</item>
		/// <item><c>units.count(type=e1)</c>, <c>units.sum(kills,type=e1)</c>,
		/// <c>units.mean(lifetimeSeconds,type=e1)</c>, <c>units.max(...)</c>,
		/// <c>units.min(...)</c> — aggregates over <c>units.csv</c>.</item>
		/// </list>
		/// </summary>
		public string Query { get; set; }

		/// <summary>One of <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>==</c>, <c>!=</c>, <c>contains</c>, <c>present</c>, <c>absent</c>.</summary>
		public string Operator { get; set; }

		public string Value { get; set; }
	}

	public sealed class CheckDocument
	{
		public int SchemaVersion { get; set; } = 1;
		public string AuthoredForRevision { get; set; }
		public string AuthoredUtc { get; set; }
		public List<Check> Checks { get; set; } = [];
	}

	public sealed class CheckResult
	{
		public string Id { get; set; }
		public string Description { get; set; }
		public string Category { get; set; }
		public string Query { get; set; }
		public string Expected { get; set; }
		public string Actual { get; set; }
		public bool Passed { get; set; }
		public string Error { get; set; }
	}

	public sealed class CheckCategorySummary
	{
		public string Category { get; set; }
		public int Total { get; set; }
		public int Passed { get; set; }
		public int Failed { get; set; }
	}

	public sealed class CheckReport
	{
		public int SchemaVersion { get; set; } = 1;
		public string RunId { get; set; }
		public string AuthoredForRevision { get; set; }
		public string EvaluatedAgainstRevision { get; set; }
		public int Total { get; set; }
		public int Passed { get; set; }
		public int Failed { get; set; }
		public List<CheckResult> Results { get; set; } = [];
		public List<CheckCategorySummary> Categories { get; set; } = [];

		/// <summary>
		/// The block the next prompt injects verbatim, so the "already diagnosed — verify each in
		/// one line" section is generated rather than hand-maintained.
		/// </summary>
		public string Rendered { get; set; }
	}

	/// <summary>Evaluates a previous round's checks against the fight that followed it.</summary>
	public static class Checks
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		public static CheckDocument Read(string path)
		{
			if (!File.Exists(path))
				return null;

			try
			{
				return JsonSerializer.Deserialize<CheckDocument>(File.ReadAllText(path), JsonOptions);
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public static CheckReport Evaluate(CheckDocument document, FightSummary summary,
			IReadOnlyList<UnitRecord> units, DecisionTrace trace)
		{
			var report = new CheckReport
			{
				RunId = summary?.RunId,
				AuthoredForRevision = document?.AuthoredForRevision,
				EvaluatedAgainstRevision = summary?.SourceRevision
			};

			foreach (var check in document?.Checks ?? [])
			{
				// A null element is what a hand-edited or truncated checks.json produces, and one
				// malformed entry must not cost the other nine their verdict.
				if (check == null)
				{
					report.Results.Add(new CheckResult
					{
						Id = "(malformed)",
						Description = "an empty entry in checks.json",
						Passed = false,
						Actual = "unresolved",
						Error = "the checks array contains a null entry"
					});

					continue;
				}

				var result = new CheckResult
				{
					Id = check.Id,
					Description = check.Description,
					Category = CheckCategories.Normalize(check.Category),
					Query = check.Query,
					Expected = $"{check.Operator} {check.Value}".Trim()
				};

				try
				{
					var actual = Resolve(check.Query, summary, units, trace);
					result.Actual = Render(actual);
					result.Passed = Compare(actual, check.Operator, check.Value);
				}
				catch (Exception ex)
				{
					result.Passed = false;
					result.Error = ex.Message;
					result.Actual = "unresolved";
				}

				report.Results.Add(result);
			}

			report.Total = report.Results.Count;
			report.Passed = report.Results.Count(r => r.Passed);
			report.Failed = report.Total - report.Passed;
			report.Categories = report.Results
				.GroupBy(r => r.Category ?? CheckCategories.Uncategorized,
					StringComparer.OrdinalIgnoreCase)
				.Select(group => new CheckCategorySummary
				{
					Category = group.Key,
					Total = group.Count(),
					Passed = group.Count(r => r.Passed),
					Failed = group.Count(r => !r.Passed)
				})
				.OrderBy(category => category.Category, StringComparer.Ordinal)
				.ToList();
			report.Rendered = Render(report);
			return report;
		}

		/// <summary>Turns a report into the exact lines the next prompt shows the agent.</summary>
		public static string Render(CheckReport report)
		{
			if (report.Total == 0)
				return "No checks were carried into this round.";

			var text = new StringBuilder();
			text.Append(CultureInfo.InvariantCulture,
				$"{report.Passed} of {report.Total} checks from the previous round passed.\n");

			foreach (var result in report.Results)
				text.Append(CultureInfo.InvariantCulture,
					$"- [{(result.Passed ? "PASS" : "FAIL")}]" +
					$"{(result.Category != null ? " [" + result.Category + "]" : "")} " +
					$"{result.Id}: {result.Description}\n" +
					$"    query {result.Query} expected {result.Expected}, actual {result.Actual}" +
					$"{(result.Error != null ? " (" + result.Error + ")" : "")}\n");

			return text.ToString().TrimEnd('\n');
		}

		/// <summary>
		/// Resolves a query to a number or a string.
		/// </summary>
		/// <remarks>
		/// Deliberately a tiny total language rather than an expression evaluator. Everything it
		/// can express is answerable from <c>summary.json</c>, <c>units.csv</c> or the reason
		/// index, all of which are already in memory; anything it cannot express is a sign the
		/// summary is missing a field, which is a better thing to discover than a check that
		/// quietly computes the wrong number.
		/// </remarks>
		internal static object Resolve(string query, FightSummary summary,
			IReadOnlyList<UnitRecord> units, DecisionTrace trace)
		{
			if (string.IsNullOrWhiteSpace(query))
				throw new ArgumentException("the check has no query");

			query = query.Trim();

			if (query.StartsWith("reason-id:", StringComparison.OrdinalIgnoreCase))
				return trace?.ReasonIdMentions(query["reason-id:".Length..].Trim()) ?? 0;

			if (query.StartsWith("reason:", StringComparison.OrdinalIgnoreCase))
				return trace?.ReasonMentions(query["reason:".Length..].Trim()) ?? 0;

			if (query.StartsWith("units.", StringComparison.OrdinalIgnoreCase))
				return Units(query, units);

			if (query.StartsWith("summary.", StringComparison.OrdinalIgnoreCase))
				return Summary(query["summary.".Length..], summary);

			throw new ArgumentException($"unknown query '{query}'");
		}

		static object Units(string query, IReadOnlyList<UnitRecord> units)
		{
			var open = query.IndexOf('(');
			var close = query.LastIndexOf(')');
			if (open < 0 || close < open)
				throw new ArgumentException($"units query needs the form units.sum(field,type=x): '{query}'");

			var function = query[6..open].Trim().ToLowerInvariant();
			var arguments = query[(open + 1)..close]
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			var rows = (IEnumerable<UnitRecord>)(units ?? []);
			string field = null;

			foreach (var argument in arguments)
			{
				var split = argument.IndexOf('=');
				if (split < 0)
				{
					field ??= argument;
					continue;
				}

				var key = argument[..split].Trim();
				var value = argument[(split + 1)..].Trim();
				rows = key.ToLowerInvariant() switch
				{
					"type" => rows.Where(u => string.Equals(u.Type, value, StringComparison.OrdinalIgnoreCase)),
					"owner" => rows.Where(u => string.Equals(u.Owner, value, StringComparison.OrdinalIgnoreCase)),
					"mode" => rows.Where(u => (u.ModesUsed ?? "").Contains(value, StringComparison.OrdinalIgnoreCase)),
					_ => throw new ArgumentException($"unknown units filter '{key}'")
				};
			}

			var matched = rows.ToList();
			if (function == "count")
				return matched.Count;

			if (field == null)
				throw new ArgumentException($"units.{function} needs a field");

			var values = matched.Select(u => Field(u, field)).ToList();
			if (values.Count == 0)
				return 0d;

			return function switch
			{
				"sum" => values.Sum(),
				"mean" => Math.Round(values.Average(), 3),
				"max" => values.Max(),
				"min" => values.Min(),
				_ => throw new ArgumentException($"unknown units function '{function}'")
			};
		}

		static double Field(UnitRecord unit, string field) => field.ToLowerInvariant() switch
		{
			"cost" => unit.Cost,
			"kills" => unit.Kills,
			"creditskilled" => unit.CreditsKilled,
			"damagedealt" => unit.DamageDealt,
			"damagetaken" => unit.DamageTaken,
			"lifetimeseconds" => unit.LifetimeSeconds ?? 0,
			"cellstravelled" => unit.CellsTravelled,
			"secondsidle" => unit.SecondsIdle,
			"decisioncount" => unit.DecisionCount,
			"bornseconds" => unit.BornSeconds ?? 0,
			"diedseconds" => unit.DiedSeconds ?? 0,
			"survived" => unit.BornSeconds != null && unit.DiedSeconds == null ? 1 : 0,
			_ => throw new ArgumentException($"unknown unit field '{field}'")
		};

		/// <summary>Walks a dotted path through the summary, honouring <c>[key]</c> lookups.</summary>
		static object Summary(string path, FightSummary summary)
		{
			object current = summary ?? throw new ArgumentException("there is no summary to query");

			foreach (var rawStep in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
			{
				var step = rawStep;
				string key = null;

				var open = step.IndexOf('[');
				if (open >= 0)
				{
					var close = step.IndexOf(']', open);
					if (close < 0)
						throw new ArgumentException($"unclosed [ in '{rawStep}'");

					key = step[(open + 1)..close];
					step = step[..open];
				}

				// Existence and value are kept apart deliberately. A field that exists and is null
				// is a real answer — "this side never fell behind on army" is exactly
				// crossover.army.firstBehindSeconds being null — and collapsing that into "no such
				// field" would make the `absent` operator impossible to satisfy.
				if (!TryProperty(current, step, out current))
					throw new ArgumentException($"'{step}' is not a field of the summary");

				if (key == null)
					continue;

				if (current == null)
					throw new ArgumentException($"'{step}' is empty, so it has no entry '{key}'");

				current = Keyed(current, key)
					?? throw new ArgumentException($"'{step}' has no entry '{key}'");
			}

			return current;
		}

		/// <summary>
		/// Reads a member by name, reporting whether it exists rather than whether it has a value.
		/// </summary>
		static bool TryProperty(object subject, string name, out object value)
		{
			value = null;
			if (subject == null)
				return false;

			// A table row resolved by [key] comes back as a column lookup, so the step after it
			// is a column name rather than a CLR property.
			if (subject is IDictionary<string, object> row)
				return row.TryGetValue(name, out value);

			var property = subject.GetType().GetProperties()
				.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

			if (property == null)
				return false;

			value = property.GetValue(subject);
			return true;
		}

		/// <summary>Finds the member of a list or table whose natural key matches.</summary>
		static object Keyed(object subject, string key)
		{
			// Every table in the summary puts its natural key in the first column.
			if (subject is Table table)
				return table.Row(key);

			if (subject is IEnumerable<FitnessComponent> components)
				return components.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));

			return null;
		}

		static bool Compare(object actual, string @operator, string expected)
		{
			// Normalised once, so a missing operator reports itself as one rather than as a null
			// reference from somewhere further down.
			var comparison = (@operator ?? "").Trim();

			switch (comparison.ToLowerInvariant())
			{
				case "present":
					return actual != null && Render(actual).Length > 0 && Render(actual) != "0";

				case "absent":
					return actual == null || Render(actual).Length == 0 || Render(actual) == "0";

				case "contains":
					return Render(actual).Contains(expected ?? "", StringComparison.OrdinalIgnoreCase);

				case "==":
					return Equal(actual, expected);

				case "!=":
					return !Equal(actual, expected);
			}

			if (!TryNumber(actual, out var left) || !double.TryParse(expected,
					NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
				throw new ArgumentException($"'{comparison}' needs two numbers");

			return comparison switch
			{
				">=" => left >= right,
				">" => left > right,
				"<=" => left <= right,
				"<" => left < right,
				_ => throw new ArgumentException($"unknown operator '{comparison}'")
			};
		}

		static bool Equal(object actual, string expected)
		{
			if (TryNumber(actual, out var left) && double.TryParse(expected,
					NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
				return Math.Abs(left - right) < 0.0005;

			return string.Equals(Render(actual), expected, StringComparison.OrdinalIgnoreCase);
		}

		static bool TryNumber(object value, out double number)
		{
			switch (value)
			{
				case null:
					number = 0;
					return false;

				case bool flag:
					number = flag ? 1 : 0;
					return true;

				case string text:
					return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
			}

			try
			{
				number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
				return true;
			}
			catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
			{
				number = 0;
				return false;
			}
		}

		static string Render(object value) => value switch
		{
			null => "",
			bool flag => flag ? "true" : "false",
			double number => number.ToString("0.###", CultureInfo.InvariantCulture),
			float number => number.ToString("0.###", CultureInfo.InvariantCulture),
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString()
		};

		public static void Write(string path, CheckReport report)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(report, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}

		public static void WriteDocument(string path, CheckDocument document)
		{
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, JsonSerializer.Serialize(document, JsonOptions) + "\n",
				new UTF8Encoding(false));
		}
	}
}
