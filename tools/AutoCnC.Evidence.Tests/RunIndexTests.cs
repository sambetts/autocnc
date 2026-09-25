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
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class RunIndexTests
	{
		[Test]
		public void TrendFlagsSpendRateRegressionAndCountsControlOnlyInBenchmarkComparison()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Entry("run-1", 0, 46.6, 100, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("control-1", 1, 99.0, 10, "control", "Won"));
			RunIndex.Record(history, "TestBot", Entry("run-2", 2, 46.1, 90, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("run-3", 3, 45.9, 80, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("run-4", 4, 25.4, 120, "candidate", "Won"));

			var trend = RunIndex.Trend(history);
			var spend = trend.Metrics.Single(m => m.Name == "creditsSpentPerSecond");
			var creditsLost = trend.Metrics.Single(m => m.Name == "creditsLost");

			Assert.That(spend.Recent, Is.EqualTo(new[] { 46.6, 46.1, 45.9, 25.4 }));
			Assert.That(spend.Regression, Is.True);
			Assert.That(creditsLost.Regression, Is.True);
			Assert.That(trend.RunsCompared, Is.EqualTo(4));
			Assert.That(trend.Benchmark.CandidateRuns, Is.EqualTo(4));
			Assert.That(trend.Benchmark.ControlRuns, Is.EqualTo(1));
		}

		[Test]
		public void LowerIsBetterMetricsDoNotRegressWhenTheyFall()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Entry("run-1", 0, 40, 100, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("run-2", 1, 40, 110, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("run-3", 2, 40, 120, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("run-4", 3, 40, 80, "candidate", "Lost"));

			var creditsLost = RunIndex.Trend(history).Metrics.Single(m => m.Name == "creditsLost");

			Assert.That(creditsLost.Direction, Is.EqualTo("down"));
			Assert.That(creditsLost.Regression, Is.False);
		}

		[Test]
		public void RecordReplacesRunsWithTheSameRunId()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Entry("same", 0, 46.6, 100, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("same", 1, 25.4, 200, "candidate", "Won"));

			Assert.That(history.Runs.Count, Is.EqualTo(1));
			Assert.That(history.Runs[0].Headline["creditsSpentPerSecond"], Is.EqualTo(25.4));
			Assert.That(history.Runs[0].Outcome, Is.EqualTo("Won"));
		}

		/// <summary>
		/// A re-run of the same benchmark must not be compared against the previous sitting.
		/// </summary>
		/// <remarks>
		/// Selecting every run that shares a benchmark NAME folds an earlier revision's candidates
		/// and a stale control arm into the current win count, which is exactly the confounding
		/// the control arm exists to remove. The batch id scopes it to one invocation.
		/// </remarks>
		[Test]
		public void BenchmarkComparisonCountsOnlyTheLatestBatch()
		{
			var history = new RunHistory { Bot = "TestBot" };

			// An older sitting of the same benchmark, which the candidate swept.
			RunIndex.Record(history, "TestBot", Batched("old-c1", 0, "candidate", "Won", "batch-1"));
			RunIndex.Record(history, "TestBot", Batched("old-c2", 1, "candidate", "Won", "batch-1"));
			RunIndex.Record(history, "TestBot", Batched("old-k1", 2, "control", "Lost", "batch-1"));

			// The current sitting, which went the other way.
			RunIndex.Record(history, "TestBot", Batched("new-c1", 3, "candidate", "Lost", "batch-2"));
			RunIndex.Record(history, "TestBot", Batched("new-k1", 4, "control", "Won", "batch-2"));

			var benchmark = RunIndex.Trend(history).Benchmark;

			Assert.That(benchmark.Batch, Is.EqualTo("batch-2"));
			Assert.That(benchmark.CandidateRuns, Is.EqualTo(1), "batch-1 candidates must not be counted");
			Assert.That(benchmark.CandidateWins, Is.EqualTo(0));
			Assert.That(benchmark.ControlRuns, Is.EqualTo(1));
			Assert.That(benchmark.ControlWins, Is.EqualTo(1));
		}

		/// <summary>Runs recorded before batches existed still compare, by name.</summary>
		[Test]
		public void RunsWithoutABatchStillCompareByBenchmarkName()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Entry("run-1", 0, 40, 100, "candidate", "Won"));
			RunIndex.Record(history, "TestBot", Entry("run-2", 1, 40, 100, "control", "Lost"));

			var benchmark = RunIndex.Trend(history).Benchmark;

			Assert.That(benchmark.Batch, Is.Null.Or.Empty);
			Assert.That(benchmark.CandidateRuns, Is.EqualTo(1));
			Assert.That(benchmark.ControlRuns, Is.EqualTo(1));
		}

		/// <summary>
		/// A metric that only newer runs record starts trending without deleting the old ones.
		/// </summary>
		/// <remarks>
		/// The economy flows arrived with a telemetry schema change, so a bot's existing history
		/// has runs that cannot answer them. Requiring every run in the window to carry a metric
		/// would hide it until the whole window turned over, which would make deleting history the
		/// only way to see a new number — and history is the thing the trend is made of.
		/// </remarks>
		[Test]
		public void MetricsOnlyNewerRunsRecordStillTrendAlongsideOlderRuns()
		{
			var history = new RunHistory { Bot = "TestBot" };

			// Two runs from before telemetry carried earned/spent.
			RunIndex.Record(history, "TestBot", WithoutEconomy("old-1", 0));
			RunIndex.Record(history, "TestBot", WithoutEconomy("old-2", 1));

			// Two after, where the economy collapses.
			RunIndex.Record(history, "TestBot", Entry("new-1", 2, 46.6, 100, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("new-2", 3, 25.4, 100, "candidate", "Lost"));

			var trend = RunIndex.Trend(history);
			var spend = trend.Metrics.SingleOrDefault(m => m.Name == "creditsSpentPerSecond");
			var exchange = trend.Metrics.Single(m => m.Name == "valueExchangeRatio");

			Assert.That(spend, Is.Not.Null, "the new metric must trend over the runs that have it");
			Assert.That(spend.RunsCompared, Is.EqualTo(2), "only the two runs that recorded it");
			Assert.That(spend.Recent, Is.EqualTo(new[] { 46.6, 25.4 }));

			// The older runs are neither deleted nor counted as zero.
			Assert.That(exchange.RunsCompared, Is.EqualTo(4));
			Assert.That(trend.RunsCompared, Is.EqualTo(4));
			Assert.That(history.Runs.Count, Is.EqualTo(4));
		}

		/// <summary>
		/// A prompt is scored by what happened to the round AFTER the one it steered.
		/// </summary>
		/// <remarks>
		/// The causal chain runs forwards and is one step long: a round reads its prompt, edits
		/// the bot, and the next fight measures the edit. Scoring a prompt by the fitness of the
		/// fight it was handed would grade it on its predecessor's work.
		/// </remarks>
		[Test]
		public void PromptEffectIsMeasuredOnTheRoundAfterTheOneItSteered()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Prompted("r1", 0, 0.40, "good"));
			RunIndex.Record(history, "TestBot", Prompted("r2", 1, 0.60, "good"));
			RunIndex.Record(history, "TestBot", Prompted("r3", 2, 0.80, "bad"));
			RunIndex.Record(history, "TestBot", Prompted("r4", 3, 0.30, "bad"));
			RunIndex.Record(history, "TestBot", Prompted("r5", 4, 0.20, "bad"));

			var effects = RunIndex.PromptEffects(history);
			var good = effects.Single(e => e.PromptId == "good");
			var bad = effects.Single(e => e.PromptId == "bad");

			// "good" steered r1 (0.40 -> 0.60) and r2 (0.60 -> 0.80): +0.20 each.
			Assert.That(good.RoundsMeasured, Is.EqualTo(2));
			Assert.That(good.MeanFitnessDelta, Is.EqualTo(0.2).Within(0.0001));
			Assert.That(good.Improved, Is.EqualTo(2));

			// "bad" steered r3 (0.80 -> 0.30) and r4 (0.30 -> 0.20); r5 has no successor yet.
			Assert.That(bad.RoundsMeasured, Is.EqualTo(2));
			Assert.That(bad.MeanFitnessDelta, Is.EqualTo(-0.3).Within(0.0001));
			Assert.That(bad.Worsened, Is.EqualTo(2));

			// Worst first, so a round sees the prompt worth reverting at the top.
			Assert.That(effects[0].PromptId, Is.EqualTo("bad"));
		}

		[Test]
		public void APromptSeenOnceIsReportedButNotJudged()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Prompted("r1", 0, 0.80, "only"));
			RunIndex.Record(history, "TestBot", Prompted("r2", 1, 0.10, "next"));

			var effect = RunIndex.PromptEffects(history).Single(e => e.PromptId == "only");

			Assert.That(effect.RoundsMeasured, Is.EqualTo(1));
			Assert.That(effect.Verdict, Does.Contain("says nothing yet"));
		}

		[Test]
		public void RunsWithNoRecordedPromptAreSkippedRatherThanGroupedTogether()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Prompted("r1", 0, 0.40, null));
			RunIndex.Record(history, "TestBot", Prompted("r2", 1, 0.50, null));
			RunIndex.Record(history, "TestBot", Prompted("r3", 2, 0.60, "p"));
			RunIndex.Record(history, "TestBot", Prompted("r4", 3, 0.90, "p"));

			var effects = RunIndex.PromptEffects(history);

			Assert.That(effects.Count, Is.EqualTo(1));
			Assert.That(effects[0].PromptId, Is.EqualTo("p"));
			Assert.That(effects[0].RoundsMeasured, Is.EqualTo(1), "r4 has no successor");
		}

		/// <summary>
		/// Raising the difficulty must not read as the bot regressing.
		/// </summary>
		/// <remarks>
		/// Difficulty is not a dial on one opponent: it selects a different bot personality and a
		/// different handicap together. The first time this ladder was climbed for real, an
		/// unsegmented trend reported nine simultaneous regressions — economy, exchange, army
		/// value, buildings destroyed, exploration — every one of them the new opponent, and the
		/// next round would have been told to explain all nine before doing anything else.
		/// </remarks>
		[Test]
		public void RaisingTheDifficultyResetsTheComparisonRatherThanReportingARegression()
		{
			var history = new RunHistory { Bot = "TestBot" };
			foreach (var (id, day, fitness) in new[]
			{
				("easy-1", 0, 0.80), ("easy-2", 1, 0.85), ("easy-3", 2, 0.90)
			})
				RunIndex.Record(history, "TestBot", AtDifficulty(id, day, fitness, "Normal"));

			// Same bot, harder opponent: fitness halves for reasons the bot did not cause.
			RunIndex.Record(history, "TestBot", AtDifficulty("hard-1", 3, 0.42, "Hard"));
			RunIndex.Record(history, "TestBot", AtDifficulty("hard-2", 4, 0.46, "Hard"));

			var trend = RunIndex.Trend(history);

			Assert.That(trend.Difficulty, Is.EqualTo("Hard"));
			Assert.That(trend.RunsCompared, Is.EqualTo(2), "only the runs at the current difficulty");
			Assert.That(trend.DifficultyChange, Does.Contain("Normal").And.Contain("Hard"));

			var fitnessMetric = trend.Metrics.Single(m => m.Name == "fitness");
			Assert.That(fitnessMetric.Recent, Is.EqualTo(new[] { 0.42, 0.46 }));
			Assert.That(fitnessMetric.Regression, Is.False, "0.42 -> 0.46 is an improvement at this rung");
			Assert.That(trend.Regressions, Is.Empty);
		}

		/// <summary>A prompt is not blamed for a fitness drop caused by a harder opponent.</summary>
		[Test]
		public void PromptEffectIgnoresDeltasThatStraddleADifficultyChange()
		{
			var history = new RunHistory { Bot = "TestBot" };

			var lastEasy = AtDifficulty("easy-last", 0, 0.90, "Normal");
			lastEasy.PromptId = "steady";
			RunIndex.Record(history, "TestBot", lastEasy);

			var firstHard = AtDifficulty("hard-1", 1, 0.40, "Hard");
			firstHard.PromptId = "steady";
			RunIndex.Record(history, "TestBot", firstHard);

			var secondHard = AtDifficulty("hard-2", 2, 0.50, "Hard");
			secondHard.PromptId = "steady";
			RunIndex.Record(history, "TestBot", secondHard);

			var effect = RunIndex.PromptEffects(history).Single(e => e.PromptId == "steady");

			// The -0.50 across the ladder step is discarded; only +0.10 within Hard counts.
			Assert.That(effect.RoundsMeasured, Is.EqualTo(1));
			Assert.That(effect.MeanFitnessDelta, Is.EqualTo(0.1).Within(0.0001));
		}

		/// <summary>
		/// A rules change must not read as the bot regressing either.
		/// </summary>
		/// <remarks>
		/// When the opponent AI's towers started firing, the same champion went from 8 to 5
		/// benchmark wins in 8, and the first fight under the new rules reported five regressions
		/// against runs that had never faced a working tower.
		/// </remarks>
		[Test]
		public void ChangingTheGameRulesResetsTheComparisonRatherThanReportingARegression()
		{
			var history = new RunHistory { Bot = "TestBot" };
			foreach (var (id, day, fitness) in new[]
			{
				("old-1", 0, 0.90), ("old-2", 1, 0.95), ("old-3", 2, 0.93)
			})
				RunIndex.Record(history, "TestBot", UnderRules(id, day, fitness, null));

			RunIndex.Record(history, "TestBot", UnderRules("new-1", 3, 0.50, "towers-fire"));
			RunIndex.Record(history, "TestBot", UnderRules("new-2", 4, 0.55, "towers-fire"));

			var trend = RunIndex.Trend(history);

			Assert.That(trend.RunsCompared, Is.EqualTo(2), "only the runs under the current rules");
			Assert.That(trend.RulesChange, Does.Contain("unrecorded").And.Contain("towers-fire"));
			Assert.That(trend.DifficultyChange, Is.Null, "the difficulty did not move");
			Assert.That(trend.Metrics.Single(m => m.Name == "fitness").Recent, Is.EqualTo(new[] { 0.50, 0.55 }));
			Assert.That(trend.Regressions, Is.Empty);
			Assert.That(trend.Rendered, Does.Contain("towers-fire"));
		}

		[Test]
		public void TheFirstRunUnderNewRulesIsTheBaselineRatherThanARegression()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", UnderRules("old-1", 0, 0.90, "towers-idle"));
			RunIndex.Record(history, "TestBot", UnderRules("old-2", 1, 0.95, "towers-idle"));
			RunIndex.Record(history, "TestBot", UnderRules("new-1", 2, 0.50, "towers-fire"));

			var trend = RunIndex.Trend(history);

			Assert.That(trend.RunsCompared, Is.EqualTo(1));
			Assert.That(trend.Regressions, Is.Empty);
			Assert.That(trend.Rendered, Does.Contain("towers-idle").And.Contain("towers-fire")
				.And.Contain("under these rules").And.Contain("new baseline"));
		}

		[Test]
		public void RunsUnderTheSameRulesStillTrendAcrossThem()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", UnderRules("r1", 0, 0.90, "same"));
			RunIndex.Record(history, "TestBot", UnderRules("r2", 1, 0.90, "same"));
			RunIndex.Record(history, "TestBot", UnderRules("r3", 2, 0.50, "same"));

			var trend = RunIndex.Trend(history);

			Assert.That(trend.RunsCompared, Is.EqualTo(3));
			Assert.That(trend.RulesChange, Is.Null);
			Assert.That(trend.Regressions.Single(), Does.StartWith("fitness"));
		}

		/// <summary>A prompt is not blamed for a fitness drop caused by a rules change.</summary>
		[Test]
		public void PromptEffectIgnoresDeltasThatStraddleARulesChange()
		{
			var history = new RunHistory { Bot = "TestBot" };

			var lastOld = UnderRules("old-last", 0, 0.90, null);
			lastOld.PromptId = "steady";
			RunIndex.Record(history, "TestBot", lastOld);

			var firstNew = UnderRules("new-1", 1, 0.40, "towers-fire");
			firstNew.PromptId = "steady";
			RunIndex.Record(history, "TestBot", firstNew);

			var secondNew = UnderRules("new-2", 2, 0.50, "towers-fire");
			secondNew.PromptId = "steady";
			RunIndex.Record(history, "TestBot", secondNew);

			var effect = RunIndex.PromptEffects(history).Single(e => e.PromptId == "steady");

			Assert.That(effect.RoundsMeasured, Is.EqualTo(1));
			Assert.That(effect.MeanFitnessDelta, Is.EqualTo(0.1).Within(0.0001));
		}

		/// <summary>
		/// A new fitness scale is not a change in the bot, but it changes only the score: every
		/// measured metric keeps its history.
		/// </summary>
		[Test]
		public void AFitnessScaleChangeLeavesEarlierScoresOutOfTheFitnessTrendOnly()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Scaled("v1-a", 0, 0.99, null));
			RunIndex.Record(history, "TestBot", Scaled("v1-b", 1, 0.99, null));
			RunIndex.Record(history, "TestBot", Scaled("v2-a", 2, 0.60, 2));
			RunIndex.Record(history, "TestBot", Scaled("v2-b", 3, 0.62, 2));

			var trend = RunIndex.Trend(history);

			Assert.That(trend.RunsCompared, Is.EqualTo(4));
			Assert.That(trend.Metrics.Single(m => m.Name == "fitness").Recent, Is.EqualTo(new[] { 0.60, 0.62 }));
			Assert.That(trend.Metrics.Single(m => m.Name == "creditsSpentPerSecond").RunsCompared, Is.EqualTo(4));
			Assert.That(trend.Regressions, Is.Empty, "0.99 -> 0.60 is the scale, not the bot");
			Assert.That(trend.FitnessScaleChange, Does.Contain("scale 2").And.Contain("scale 1").And.Contain("v2-a"));
			Assert.That(trend.Rendered, Does.Contain(trend.FitnessScaleChange));
		}

		[Test]
		public void PromptEffectIgnoresDeltasThatStraddleAFitnessScaleChange()
		{
			var history = new RunHistory { Bot = "TestBot" };
			foreach (var entry in new[]
			{
				Scaled("v1", 0, 0.99, null), Scaled("v2-a", 1, 0.60, 2), Scaled("v2-b", 2, 0.70, 2)
			})
			{
				entry.PromptId = "steady";
				RunIndex.Record(history, "TestBot", entry);
			}

			var effect = RunIndex.PromptEffects(history).Single(e => e.PromptId == "steady");

			Assert.That(effect.RoundsMeasured, Is.EqualTo(1));
			Assert.That(effect.MeanFitnessDelta, Is.EqualTo(0.1).Within(0.0001));
		}

		[Test]
		public void UndefinedAndFailedRunsAreExcludedFromTrendButLegacyWinsAndLossesRemain()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Entry("legacy-lost", 0, 40, 100, "candidate", "Lost"));
			RunIndex.Record(history, "TestBot", Entry("undefined", 1, 1, 999, "candidate", "Undefined"));
			RunIndex.Record(history, "TestBot", Entry("failed", 2, 2, 888, "candidate", "Failed"));
			RunIndex.Record(history, "TestBot", Entry("legacy-won", 3, 60, 50, "candidate", "Won"));

			var trend = RunIndex.Trend(history);
			var spend = trend.Metrics.Single(m => m.Name == "creditsSpentPerSecond");

			Assert.That(trend.RunsCompared, Is.EqualTo(2));
			Assert.That(trend.LatestRunId, Is.EqualTo("legacy-won"));
			Assert.That(spend.Recent, Is.EqualTo(new[] { 40d, 60d }));
			Assert.That(history.Runs, Has.Count.EqualTo(4), "invalid evidence stays durable; it is only excluded");
		}

		[Test]
		public void PromptEffectsDoNotUseOrBridgeAcrossAnUndefinedRun()
		{
			var history = new RunHistory { Bot = "TestBot" };
			RunIndex.Record(history, "TestBot", Prompted("r1", 0, 0.40, "before-failure"));
			var undefined = Prompted("r2", 1, 0.99, "failed");
			undefined.Outcome = "Undefined";
			RunIndex.Record(history, "TestBot", undefined);
			RunIndex.Record(history, "TestBot", Prompted("r3", 2, 0.60, "valid"));
			RunIndex.Record(history, "TestBot", Prompted("r4", 3, 0.70, "next"));

			var effects = RunIndex.PromptEffects(history);

			Assert.That(effects.Select(e => e.PromptId), Is.EqualTo(new[] { "valid" }));
			Assert.That(effects[0].MeanFitnessDelta, Is.EqualTo(0.1).Within(0.0001));
		}

		static RunHistoryEntry AtDifficulty(string id, int days, double fitness, string difficulty)
		{
			var entry = Entry(id, days, 40, 100, "candidate", "Lost");
			entry.Difficulty = difficulty;
			entry.Fitness = fitness;
			entry.Headline["fitness"] = fitness;
			return entry;
		}

		static RunHistoryEntry Scaled(string id, int days, double fitness, int? scale)
		{
			var entry = UnderRules(id, days, fitness, "same");
			entry.FitnessScaleVersion = scale;
			return entry;
		}

		static RunHistoryEntry UnderRules(string id, int days, double fitness, string rules)
		{
			var entry = Entry(id, days, 40, 100, "candidate", "Lost");
			entry.Difficulty = "Hard";
			entry.RulesFingerprint = rules;
			entry.Fitness = fitness;
			entry.Headline["fitness"] = fitness;
			return entry;
		}

		static RunHistoryEntry Prompted(string id, int days, double fitness, string promptId)
		{
			var entry = Entry(id, days, 40, 100, "candidate", "Lost");
			entry.Fitness = fitness;
			entry.Headline["fitness"] = fitness;
			entry.PromptId = promptId;
			entry.PromptCharacters = promptId == null ? 0 : 40000;
			entry.PromptHeadings = promptId == null ? 0 : 18;
			return entry;
		}

		static RunHistoryEntry WithoutEconomy(string id, int days)
		{
			var entry = Entry(id, days, 40, 100, "candidate", "Lost");
			entry.Headline.Remove("creditsEarnedPerSecond");
			entry.Headline.Remove("creditsSpentPerSecond");
			return entry;
		}

		static RunHistoryEntry Batched(string id, int days, string arm, string outcome, string batch)
		{
			var entry = Entry(id, days, 40, 100, arm, outcome);
			entry.Batch = batch;
			return entry;
		}

		static RunHistoryEntry Entry(string id, int days, double creditsSpentPerSecond, double creditsLost,
			string arm, string outcome)
		{
			var entry = new RunHistoryEntry
			{
				RunId = id,
				CompletedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days),
				SourceRevision = "rev-" + id,
				Benchmark = "standard",
				Arm = arm,
				Outcome = outcome,
				Fitness = creditsSpentPerSecond / 100d
			};

			entry.Headline["fitness"] = entry.Fitness;
			entry.Headline["creditsEarnedPerSecond"] = 50;
			entry.Headline["creditsSpentPerSecond"] = creditsSpentPerSecond;
			entry.Headline["valueExchangeRatio"] = 1;
			entry.Headline["meanArmyValue"] = 1000;
			entry.Headline["buildingsKilled"] = 1;
			entry.Headline["creditsKilled"] = 500;
			entry.Headline["creditsLost"] = creditsLost;
			entry.Headline["durationSeconds"] = 600;
			entry.Headline["cellsExplored"] = 100;
			entry.Headline["idleUnitSeconds"] = 10;
			return entry;
		}
	}
}
