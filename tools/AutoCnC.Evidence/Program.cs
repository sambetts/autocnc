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

namespace AutoCnC.Evidence
{
	/// <summary>
	/// Everything the harness does to a finished fight's evidence, in one place.
	/// </summary>
	/// <remarks>
	/// Exposed as a method rather than only as a command line so the launcher can call it in
	/// process the moment a match ends, and so the tests can drive it against a recorded run
	/// without starting anything.
	/// </remarks>
	public static class EvidencePipeline
	{
		public sealed class Outcome
		{
			public FightSummary Summary { get; set; }
			public List<UnitRecord> Units { get; set; } = [];
			public CheckReport Checks { get; set; }
			public TrendReport Trend { get; set; }
			public List<string> Written { get; set; } = [];
		}

		/// <summary>
		/// Builds every derived artifact for one fight and folds it into the bot's history.
		/// </summary>
		/// <param name="evidenceDirectory">The fight's <c>evidence</c> folder.</param>
		/// <param name="historyPath">
		/// The per-bot run index. Null skips history and the trend, which is what a one-off
		/// regeneration of an old run wants.
		/// </param>
		/// <param name="checksPath">
		/// The previous round's <c>checks.json</c>. Null looks for one beside the evidence.
		/// </param>
		/// <param name="bot">Bot name for a history file that does not exist yet.</param>
		public static Outcome Run(string evidenceDirectory, string historyPath = null,
			string checksPath = null, string bot = null)
		{
			var evidence = new EvidenceSet(evidenceDirectory).Load();

			var seconds = evidence.Manifest.DurationSeconds;
			if (evidence.Telemetry.Samples.Count > 0)
				seconds = Math.Max(seconds, evidence.Telemetry.Samples[^1].Seconds);

			var units = UnitLedger.Build(evidence.Battle, evidence.Trace, evidence.Rules, seconds);
			var summary = FightSummaryBuilder.Build(evidence, units);

			var outcome = new Outcome { Summary = summary, Units = units };

			UnitLedger.Write(evidence.UnitsPath, units);
			outcome.Written.Add(evidence.UnitsPath);

			var resolvedChecks = checksPath ?? evidence.ChecksPath;
			var document = Checks.Read(resolvedChecks);
			if (document != null)
			{
				outcome.Checks = Checks.Evaluate(document, summary, units, evidence.Trace);
				Checks.Write(evidence.CheckResultsPath, outcome.Checks);
				outcome.Written.Add(evidence.CheckResultsPath);
			}

			if (!string.IsNullOrEmpty(historyPath))
			{
				var history = RunIndex.Read(historyPath);
				var name = bot ?? summary.Bot ?? history.Bot;

				// The prompt this round was given. It has produced no edit yet — the NEXT fight
				// measures what it caused — so it is recorded here and scored later.
				var prompt = PromptFingerprint.Read(evidence.PromptPath, evidence.MechanicsPath);

				RunIndex.Record(history, name, RunIndex.Entry(summary, outcome.Checks, prompt));
				RunIndex.WriteHistory(historyPath, history);
				outcome.Written.Add(historyPath);

				outcome.Trend = RunIndex.Trend(history);
				RunIndex.WriteTrend(evidence.TrendPath, outcome.Trend);
				outcome.Written.Add(evidence.TrendPath);
			}

			// Written last: the summary is the artifact everything else is judged against, so it
			// only appears once the ledger and the checks it refers to are already on disk.
			FightSummaryBuilder.Write(evidence.SummaryPath, summary);
			outcome.Written.Add(evidence.SummaryPath);
			return outcome;
		}
	}

	public static class Program
	{
		public static int Main(string[] args)
		{
			if (args.Length == 0)
				return Usage();

			try
			{
				return args[0].ToLowerInvariant() switch
				{
					"summarise" or "summarize" => Summarise(args),
					"trend" => Trend(args),
					"prompts" => Prompts(args),
					"audit-bot" => Audit(args),
					"--help" or "-h" or "help" => Usage(),
					_ => Usage($"Unknown command '{args[0]}'.")
				};
			}
			catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
			{
				Console.Error.WriteLine($"autocnc-evidence: {ex.Message}");
				return 1;
			}
		}

		static int Summarise(string[] args)
		{
			if (args.Length < 2)
				return Usage("summarise needs an evidence directory.");

			var options = Options(args);
			var outcome = EvidencePipeline.Run(args[1],
				options.GetValueOrDefault("history"),
				options.GetValueOrDefault("checks"),
				options.GetValueOrDefault("bot"));

			foreach (var path in outcome.Written)
				Console.WriteLine($"wrote {path} ({new FileInfo(path).Length} bytes)");

			Console.WriteLine(FightSummaryBuilder.Describe(outcome.Summary));
			Console.WriteLine($"{outcome.Units.Count} units in the ledger.");

			if (outcome.Checks != null)
				Console.WriteLine(outcome.Checks.Rendered);

			if (outcome.Trend != null)
				Console.WriteLine(outcome.Trend.Rendered);

			// A failed check is information rather than a broken tool, so it does not fail the
			// command. The next prompt reports it; the harness carries on.
			return 0;
		}

		static int Trend(string[] args)
		{
			if (args.Length < 2)
				return Usage("trend needs a history file.");

			var options = Options(args);
			var history = RunIndex.Read(args[1]);
			var report = RunIndex.Trend(history);

			var output = options.GetValueOrDefault("out");
			if (!string.IsNullOrEmpty(output))
			{
				RunIndex.WriteTrend(output, report);
				Console.WriteLine($"wrote {output}");
			}

			Console.WriteLine(report.Rendered);
			return 0;
		}

		static int Audit(string[] args)
		{
			if (args.Length < 2)
				return Usage("audit-bot needs a bot source directory.");

			var findings = BotSourceAudit.Scan(args[1]);
			Console.WriteLine(BotSourceAudit.Render(findings));

			// Advisory: reported, never fatal. See BotSourceAudit for why.
			return 0;
		}

		static int Prompts(string[] args)
		{
			if (args.Length < 2)
				return Usage("prompts needs a history file.");

			var options = Options(args);
			var history = RunIndex.Read(args[1]);
			var effects = RunIndex.PromptEffects(history);

			if (effects.Count == 0)
			{
				Console.WriteLine("No run in this history recorded which prompt steered it.");
				return 0;
			}

			// Where the text of each template lives. A prompt is identified by the structure of
			// its learned half, so the first run that used one is where to read it; the launcher's
			// archive holds the template on its own, without the fight's values inlined.
			var firstUse = new Dictionary<string, RunHistoryEntry>(StringComparer.Ordinal);
			foreach (var run in history.Runs.OrderBy(r => r.CompletedUtc))
				if (!string.IsNullOrEmpty(run.PromptId))
					firstUse.TryAdd(run.PromptId, run);

			Console.WriteLine($"{effects.Count} prompt revision(s), worst effect first.");
			Console.WriteLine("Effect is the mean fitness change from a round this prompt steered " +
				"to the round after it.");
			Console.WriteLine();

			foreach (var effect in effects)
			{
				var run = firstUse.GetValueOrDefault(effect.PromptId);
				Console.WriteLine($"{effect.PromptId}  {effect.Characters:N0} chars of learned prompt, " +
					$"{effect.Headings} sections");
				Console.WriteLine($"  effect      {effect.MeanFitnessDelta:+0.###;-0.###;0} mean, " +
					$"{effect.MedianFitnessDelta:+0.###;-0.###;0} median over {effect.RoundsMeasured} round(s) " +
					$"({effect.Improved} better / {effect.Worsened} worse)");
				Console.WriteLine($"  verdict     {effect.Verdict}");
				Console.WriteLine($"  first used  {effect.FirstSeenRunId}");
				Console.WriteLine($"  last used   {effect.LastSeenRunId}");

				if (run != null)
					Console.WriteLine($"  rendered    {RunDirectory(args[1], run.RunId)}");

				Console.WriteLine();
			}

			var archive = options.GetValueOrDefault("archive") ?? DefaultArchive();
			if (Directory.Exists(archive))
			{
				Console.WriteLine($"Templates on their own, without a fight's values inlined, are in:");
				Console.WriteLine($"  {archive}");
				foreach (var file in Directory.EnumerateFiles(archive, "*.txt").OrderBy(f => f))
					Console.WriteLine($"  {Path.GetFileName(file),-28} {new FileInfo(file).Length,7:N0} chars");

				Console.WriteLine();
				Console.WriteLine("Diff two of them to see what a round actually changed, for example:");
				Console.WriteLine($"  git diff --no-index \"{archive}\\0006-baseline.txt\" \"{archive}\\0007-manual.txt\"");
			}

			return 0;
		}

		/// <summary>The rendered prompt a run was given, beside its evidence.</summary>
		static string RunDirectory(string historyPath, string runId)
		{
			var root = Path.GetDirectoryName(Path.GetFullPath(historyPath));
			return Path.Combine(root ?? "", runId, "evidence", "agent-prompt.txt");
		}

		static string DefaultArchive() =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"AutoCnC", "PromptHistory");

		static Dictionary<string, string> Options(string[] args)
		{
			var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < args.Length - 1; i++)
				if (args[i].StartsWith("--", StringComparison.Ordinal))
					options[args[i][2..]] = args[i + 1];

			return options;
		}

		static int Usage(string message = null)
		{
			if (message != null)
				Console.Error.WriteLine(message);

			Console.WriteLine("""
				autocnc-evidence — derives the artifacts an improvement round reads.

				  summarise <evidenceDir> [--history <file>] [--checks <file>] [--bot <name>]
				      Writes units.csv, summary.json, check-results.json and trend.json.

				  trend <historyFile> [--out <file>]
				      Re-renders the cross-run trend from an existing index.

				  prompts <historyFile> [--archive <dir>]
				      Compares prompt revisions: what each one did to the rounds it steered,
				      and where to read the text of each.

				  audit-bot <botSourceDir>
				      Reports map coordinates or opponent names hardcoded into strategy.
				""");

			return message == null ? 0 : 2;
		}
	}
}
