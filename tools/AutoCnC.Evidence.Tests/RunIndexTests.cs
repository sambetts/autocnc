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
