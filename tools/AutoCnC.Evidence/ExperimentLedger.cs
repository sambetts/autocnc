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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutoCnC.Evidence
{
	/// <summary>How often a reason id new in a candidate appeared in the candidate's own games.</summary>
	public sealed class ReasonIdActivation
	{
		public string ReasonId { get; set; }
		public int GamesFired { get; set; }
		public int Games { get; set; }
		public int Mentions { get; set; }
	}

	/// <summary>One check a candidate wrote, tallied across the games that judged it.</summary>
	public sealed class CheckTally
	{
		public string Id { get; set; }
		public string Category { get; set; }
		public int Passed { get; set; }
		public int Failed { get; set; }
		public int Errors { get; set; }
		public int Games { get; set; }
	}

	/// <summary>One evaluated round: what was tried, whether it ran, and what the gate concluded.</summary>
	public sealed class ExperimentRecord
	{
		public int SchemaVersion { get; set; } = 1;
		public string RunId { get; set; }
		public DateTime RecordedUtc { get; set; }
		public string Benchmark { get; set; }
		public string Basis { get; set; }
		public string Verdict { get; set; }

		/// <summary>
		/// promoted, measured-worse, not-better, inconclusive, restored, no-change or undefined.
		/// </summary>
		public string Conclusion { get; set; }

		public string Reason { get; set; }
		public string ChampionRevision { get; set; }

		/// <summary>The champion's source fingerprint: its identity, unlike a commit that may only touch docs.</summary>
		public string ChampionFingerprint { get; set; }

		public string CandidateFingerprint { get; set; }

		/// <summary>The candidate's one-sentence claim, from the <c>hypothesis</c> of its checks.json.</summary>
		public string Hypothesis { get; set; }

		public string FightOutcome { get; set; }
		public string FightMatchup { get; set; }

		/// <summary>
		/// Outcomes of the fights the round played and passed over, oldest first, while looking for
		/// one the bot lost. Counted in the champion's record, which would otherwise count only the
		/// losses the round went looking for.
		/// </summary>
		public List<string> EarlierFights { get; set; } = [];

		public List<string> ChangedFiles { get; set; } = [];
		public int PairsCompared { get; set; }
		public int CandidateWins { get; set; }
		public int ControlWins { get; set; }
		public int? CandidateOnlyWins { get; set; }
		public int? ControlOnlyWins { get; set; }
		public double? LogLikelihoodRatio { get; set; }
		public int CandidateGames { get; set; }
		public List<ReasonIdActivation> NewReasonIds { get; set; } = [];
		public List<CheckTally> Checks { get; set; } = [];
	}

	/// <summary>
	/// The loop's memory of its own experiments, kept by the harness rather than by the prompt.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Before this, the only record of what had been tried was a "Tried and not kept" list each
	/// round rewrote in prose. Every rejection went on it as a failed idea, although about four
	/// in ten were decided by a single game and some never ran at all, and nothing said which. A
	/// round could not tell "measured worse" from "never fired" from "the gate could not see it".
	/// </para>
	/// <para>
	/// A record is written once, after the verdict, from evidence the round could not edit: the
	/// files the candidate changed, the reason ids it added and how often its own benchmark games
	/// logged them, its checks tallied across those games, and the gate's conclusion. The next
	/// prompt is given the most recent records verbatim.
	/// </para>
	/// </remarks>
	public static class ExperimentLedger
	{
		public const string FileName = "experiments.jsonl";

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		/// <summary>A string literal shaped like this bot's reason ids: dotted, lower case, hyphenated.</summary>
		static readonly Regex ReasonIdLiteral = new(
			"\"([a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+)\"",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		static readonly string[] FileSuffixes =
			[".json", ".jsonl", ".cs", ".csv", ".md", ".dll", ".txt", ".yaml", ".oramap", ".orarep", ".log"];

		public static void Append(string path, ExperimentRecord record)
		{
			ArgumentNullException.ThrowIfNull(record);
			var full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.AppendAllText(full, JsonSerializer.Serialize(record, JsonOptions) + "\n", new UTF8Encoding(false));
		}

		/// <summary>Every readable record, oldest first. A damaged line is skipped, not fatal.</summary>
		public static List<ExperimentRecord> Read(string path)
		{
			var records = new List<ExperimentRecord>();
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return records;

			foreach (var line in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;
				try
				{
					var record = JsonSerializer.Deserialize<ExperimentRecord>(line, JsonOptions);
					if (record != null)
						records.Add(record);
				}
				catch (JsonException)
				{
				}
			}

			return records;
		}

		/// <summary>Reason-id-shaped string literals in a bot's C# source.</summary>
		public static SortedSet<string> ReasonIdLiterals(string sourceDirectory)
		{
			var ids = new SortedSet<string>(StringComparer.Ordinal);
			if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
				return ids;

			foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
			{
				var relative = Path.GetRelativePath(sourceDirectory, file);
				if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
					relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					continue;

				foreach (Match match in ReasonIdLiteral.Matches(File.ReadAllText(file)))
				{
					var id = match.Groups[1].Value;
					if (!FileSuffixes.Any(suffix => id.EndsWith(suffix, StringComparison.Ordinal)))
						ids.Add(id);
				}
			}

			return ids;
		}

		/// <summary>Reason ids the candidate's source names that the champion's does not.</summary>
		public static List<string> NewReasonIds(string candidateSource, string championSource)
		{
			var champion = ReasonIdLiterals(championSource);
			return ReasonIdLiterals(candidateSource).Where(id => !champion.Contains(id)).ToList();
		}

		/// <summary>Reads a game's <c>reason-ids.json</c>, or an empty set when it has none.</summary>
		public static Dictionary<string, int> ReasonIdCounts(string evidenceDirectory)
		{
			var path = Path.Combine(evidenceDirectory ?? "", ReasonIdCountsFile);
			if (!File.Exists(path))
				return new Dictionary<string, int>(StringComparer.Ordinal);

			try
			{
				var document = JsonSerializer.Deserialize<ReasonIdCountsDocument>(File.ReadAllText(path), JsonOptions);
				return new Dictionary<string, int>(document?.Counts ?? [], StringComparer.Ordinal);
			}
			catch (JsonException)
			{
				return new Dictionary<string, int>(StringComparer.Ordinal);
			}
		}

		public const string ReasonIdCountsFile = "reason-ids.json";

		public sealed class ReasonIdCountsDocument
		{
			public int SchemaVersion { get; set; } = 1;
			public Dictionary<string, int> Counts { get; set; } = [];
		}

		public static void WriteReasonIdCounts(string evidenceDirectory, IReadOnlyDictionary<string, int> counts)
		{
			var document = new ReasonIdCountsDocument
			{
				Counts = counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
					.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
			};
			File.WriteAllText(Path.Combine(evidenceDirectory, ReasonIdCountsFile),
				JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }) + "\n",
				new UTF8Encoding(false));
		}

		/// <summary>How often each id was logged across the given games.</summary>
		public static List<ReasonIdActivation> Activations(IEnumerable<string> reasonIds,
			IReadOnlyList<string> evidenceDirectories)
		{
			var counts = evidenceDirectories.Select(ReasonIdCounts).ToList();
			return reasonIds.Select(id => new ReasonIdActivation
			{
				ReasonId = id,
				Games = counts.Count,
				GamesFired = counts.Count(game => game.GetValueOrDefault(id) > 0),
				Mentions = counts.Sum(game => game.GetValueOrDefault(id))
			}).ToList();
		}

		/// <summary>Every check's results across the games that evaluated it, in the order written.</summary>
		public static List<CheckTally> TallyChecks(IEnumerable<string> evidenceDirectories)
		{
			var tallies = new List<CheckTally>();
			foreach (var directory in evidenceDirectories)
			{
				var path = Path.Combine(directory ?? "", "check-results.json");
				if (!File.Exists(path))
					continue;

				CheckReport report;
				try
				{
					report = JsonSerializer.Deserialize<CheckReport>(File.ReadAllText(path), JsonOptions);
				}
				catch (JsonException)
				{
					continue;
				}

				foreach (var result in report?.Results ?? [])
				{
					if (string.IsNullOrWhiteSpace(result?.Id))
						continue;
					var tally = tallies.FirstOrDefault(t => t.Id == result.Id);
					if (tally == null)
					{
						tally = new CheckTally { Id = result.Id, Category = result.Category };
						tallies.Add(tally);
					}

					tally.Games++;
					if (!string.IsNullOrEmpty(result.Error))
						tally.Errors++;
					else if (result.Passed)
						tally.Passed++;
					else
						tally.Failed++;
				}
			}

			return tallies;
		}

		/// <summary>The newest records, as the next prompt reads them.</summary>
		public static string Render(IReadOnlyList<ExperimentRecord> records, int last = 12)
		{
			if (records == null || records.Count == 0)
				return "No experiment has been recorded for this bot yet.";

			var shown = records.OrderByDescending(r => r.RecordedUtc).Take(Math.Max(1, last)).ToList();
			var text = new StringBuilder();
			var record0 = ChampionRecord(records);
			if (record0 != null)
				text.Append(record0).Append('\n');

			text.Append(string.Create(CultureInfo.InvariantCulture,
				$"The {shown.Count} most recent of {records.Count} recorded experiment(s), newest first. "))
				.Append("Recorded by the harness after each verdict, from the candidate's own benchmark games. ")
				.Append("\"inconclusive\" and \"not better\" mean the gate could not see an effect, not that the idea is refuted; ")
				.Append("a reason id that fired in 0 games means the change never ran.\n");

			foreach (var record in shown)
			{
				text.Append(CultureInfo.InvariantCulture, $"- {Stamp(record)} {Conclusion(record)}");
				var tally = Tally(record);
				if (tally != null)
					text.Append(" (").Append(tally).Append(')');
				text.Append('.');

				if (!string.IsNullOrWhiteSpace(record.FightOutcome))
				{
					text.Append(CultureInfo.InvariantCulture,
						$" Read a {record.FightOutcome}{(string.IsNullOrWhiteSpace(record.FightMatchup) ? "" : " as " + record.FightMatchup)}");
					var passedOver = record.EarlierFights?.Count ?? 0;
					if (passedOver > 0)
						text.Append(CultureInfo.InvariantCulture,
							$", after {passedOver} fight(s) on other seeds it won");
					text.Append('.');
				}

				if (!string.IsNullOrWhiteSpace(record.Hypothesis))
					text.Append(" Hypothesis: ").Append(Clip(record.Hypothesis, 240)).Append('.');

				if (record.ChangedFiles.Count > 0)
					text.Append(" Changed ").Append(string.Join(", ", record.ChangedFiles.Take(6)))
						.Append(record.ChangedFiles.Count > 6 ? $" and {record.ChangedFiles.Count - 6} more" : "").Append('.');

				if (record.NewReasonIds.Count > 0)
					text.Append(" New reason ids: ").Append(string.Join("; ", record.NewReasonIds.Take(8).Select(activation =>
						string.Create(CultureInfo.InvariantCulture,
							$"`{activation.ReasonId}` in {activation.GamesFired}/{activation.Games} games")))).Append('.');

				var checks = record.Checks.Where(check => check.Games > 0).ToList();
				if (checks.Count > 0)
					text.Append(" Its checks: ").Append(string.Join("; ", checks.Take(8).Select(check =>
						string.Create(CultureInfo.InvariantCulture,
							$"{check.Id} passed {check.Passed}/{check.Games}{(check.Errors > 0 ? $", {check.Errors} error(s)" : "")}")))).Append('.');

				text.Append('\n');
			}

			return text.ToString().TrimEnd('\n');
		}

		static string Stamp(ExperimentRecord record) =>
			string.IsNullOrWhiteSpace(record.RunId) ? record.RecordedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
				: record.RunId.Length > 15 ? record.RunId[..15] : record.RunId;

		/// <summary>
		/// The current champion's record in the random-seed opening fights of the rounds it
		/// defended: the one progress measure nothing was selected on.
		/// </summary>
		public static string ChampionRecord(IReadOnlyList<ExperimentRecord> records)
		{
			var ordered = records.Where(r => r != null).OrderBy(r => r.RecordedUtc).ToList();
			if (ordered.Count == 0)
				return null;

			var latest = ordered[^1];
			var champion = string.Equals(latest.Verdict, PromotionVerdicts.Promote, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(latest.Conclusion, "no-change", StringComparison.OrdinalIgnoreCase)
				? latest.CandidateFingerprint
				: latest.ChampionFingerprint;
			if (string.IsNullOrWhiteSpace(champion))
				return null;

			var fights = ordered.Where(r => string.Equals(r.ChampionFingerprint, champion, StringComparison.Ordinal))
				.SelectMany(r => (r.EarlierFights ?? []).Append(r.FightOutcome))
				.Where(outcome => outcome is "Won" or "Lost")
				.ToList();
			var wins = fights.Count(outcome => outcome == "Won");
			if (fights.Count == 0)
				return "The current champion has not yet played an opening fight the ledger recorded.";

			var (low, high) = Wilson(wins, fights.Count);
			return string.Create(CultureInfo.InvariantCulture,
				$"The current champion won {wins} of the {fights.Count} random-seed opening fight(s) it played " +
				$"({100d * wins / fights.Count:0}%, 95% interval {100 * low:0}-{100 * high:0}%).");
		}

		/// <summary>The Wilson score interval for a binomial proportion.</summary>
		public static (double Low, double High) Wilson(int successes, int trials, double z = 1.959964)
		{
			if (trials <= 0)
				return (0, 1);

			var p = (double)successes / trials;
			var denominator = 1 + z * z / trials;
			var centre = (p + z * z / (2 * trials)) / denominator;
			var margin = z * Math.Sqrt(p * (1 - p) / trials + z * z / (4d * trials * trials)) / denominator;
			return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
		}

		static string Conclusion(ExperimentRecord record) => record.Conclusion switch
		{
			"promoted" or "measured-better" => "PROMOTED",
			"measured-worse" => "restored, measured worse",
			"not-better" => "restored, not better",
			"inconclusive" => "restored, inconclusive",
			"no-change" => "no source change",
			"undefined" => "no verdict (evaluation failed)",
			_ => string.Equals(record.Verdict, PromotionVerdicts.Promote, StringComparison.OrdinalIgnoreCase)
				? "PROMOTED"
				: "restored"
		};

		static string Tally(ExperimentRecord record)
		{
			if (record.PairsCompared <= 0)
				return null;

			if (record.CandidateOnlyWins.HasValue && record.ControlOnlyWins.HasValue)
			{
				var tally = string.Create(CultureInfo.InvariantCulture,
					$"{record.PairsCompared} fresh pairs: won {record.CandidateOnlyWins} the champion lost, lost {record.ControlOnlyWins} it won");
				return record.LogLikelihoodRatio.HasValue
					? tally + string.Create(CultureInfo.InvariantCulture, $", LLR {record.LogLikelihoodRatio:0.00}")
					: tally;
			}

			return string.Create(CultureInfo.InvariantCulture,
				$"{record.PairsCompared} pinned pairs: {record.CandidateWins} wins against {record.ControlWins}");
		}

		static string Clip(string value, int length)
		{
			value = value.Trim().TrimEnd('.').Replace('\n', ' ').Replace('\r', ' ');
			return value.Length <= length ? value : value[..(length - 1)] + "…";
		}
	}
}
