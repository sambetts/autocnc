#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class PairedBenchmarkEvaluationTests
	{
		[Test]
		public void WinsRankAheadOfPairedFitness()
		{
			var result = Result(
				("Won", "Lost", 0.10, 0.90),
				("Won", "Lost", 0.20, 0.80),
				("Lost", "Won", 0.30, 0.70));

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Promote));
			Assert.That(evaluation.Basis, Is.EqualTo("wins"));
			Assert.That(evaluation.CandidateWins, Is.EqualTo(2));
			Assert.That(evaluation.MedianPairedFitnessDelta, Is.LessThan(0),
				"paired fitness is only the tie-break after wins");
		}

		[Test]
		public void TiedWinsUseTheMedianOfPairedFitnessDeltas()
		{
			var result = Result(
				("Won", "Won", 0.70, 0.60),
				("Lost", "Lost", 0.45, 0.40),
				("Lost", "Lost", 0.10, 0.80));

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Promote));
			Assert.That(evaluation.Basis, Is.EqualTo("paired-fitness"));
			Assert.That(evaluation.MedianPairedFitnessDelta, Is.EqualTo(0.05).Within(0.0001));
			Assert.That(evaluation.PairsCompared, Is.EqualTo(3));
		}

		[Test]
		public void EqualOrLowerPairedFitnessRestoresInsteadOfPromoting()
		{
			var result = Result(
				("Won", "Won", 0.40, 0.50),
				("Lost", "Lost", 0.50, 0.50));

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Restore));
			Assert.That(evaluation.CanPromote, Is.False);
		}

		[Test]
		public void DifferentScenarioSetsAreUndefined()
		{
			var result = Result(("Won", "Lost", 0.7, 0.5));
			result.Matches.Single(m => m.Arm == "control").Scenario = 2;

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(evaluation.Reason, Does.Contain("same repeat/scenario set"));
		}

		[Test]
		public void DifferentBenchmarkBatchInsideAPairIsUndefined()
		{
			var result = Result(("Won", "Lost", 0.7, 0.5));
			result.Paired[0].Benchmark = result.Benchmark;
			result.Paired[0].Batch = "different-batch";

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(evaluation.Reason, Does.Contain("different batch"));
		}

		[TestCase("Undefined", null, null)]
		[TestCase("Failed", null, null)]
		[TestCase("Won", "failed", null)]
		[TestCase("Won", null, false)]
		public void FailedOrUndefinedRunsCannotPromote(string outcome, string status, bool? succeeded)
		{
			var result = Result(("Won", "Lost", 0.7, 0.5));
			var candidate = result.Matches.Single(m => m.Arm == "candidate");
			candidate.Outcome = outcome;
			candidate.Status = status;
			candidate.Succeeded = succeeded;
			result.Paired[0].CandidateOutcome = outcome;
			result.Candidate.Wins = outcome == "Won" ? 1 : 0;

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(evaluation.CanPromote, Is.False);
		}

		[Test]
		public void MissingPairedRowsAreUndefined()
		{
			var result = Result(
				("Won", "Lost", 0.7, 0.5),
				("Lost", "Won", 0.4, 0.6));
			result.Paired.RemoveAt(1);

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(evaluation.Reason, Does.Contain("incomplete"));
		}

		static BenchmarkResultDocument Result(
			params (string CandidateOutcome, string ControlOutcome,
				double CandidateFitness, double ControlFitness)[] scenarios)
		{
			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				Benchmark = "standard",
				Batch = "standard-20260919-083633-test",
				Difficulty = "Hard",
				Candidate = new BenchmarkArmResult { Arm = "candidate" },
				Control = new BenchmarkArmResult { Arm = "control" }
			};

			for (var i = 0; i < scenarios.Length; i++)
			{
				var scenario = scenarios[i];
				var number = i + 1;
				var map = "map-" + number;
				var seed = 1000 + number;
				result.Matches.Add(new BenchmarkMatchResult
				{
					RunId = "candidate-" + number,
					Arm = "candidate",
					Repeat = 1,
					Scenario = number,
					Map = map,
					Faction = "gdi",
					BotFaction = "nod",
					Seed = seed,
					Outcome = scenario.CandidateOutcome,
					Fitness = scenario.CandidateFitness,
					DurationSeconds = 600
				});
				result.Matches.Add(new BenchmarkMatchResult
				{
					RunId = "control-" + number,
					Arm = "control",
					Repeat = 1,
					Scenario = number,
					Map = map,
					Faction = "gdi",
					BotFaction = "nod",
					Seed = seed,
					Outcome = scenario.ControlOutcome,
					Fitness = scenario.ControlFitness,
					DurationSeconds = 600
				});
				result.Paired.Add(new BenchmarkPairResult
				{
					Repeat = 1,
					Scenario = number,
					Map = map,
					Faction = "gdi",
					BotFaction = "nod",
					Seed = seed,
					CandidateOutcome = scenario.CandidateOutcome,
					ControlOutcome = scenario.ControlOutcome,
					FitnessDelta = System.Math.Round(
						scenario.CandidateFitness - scenario.ControlFitness, 4)
				});
			}

			result.Candidate.Played = scenarios.Length;
			result.Control.Played = scenarios.Length;
			result.Candidate.Wins = scenarios.Count(s => s.CandidateOutcome == "Won");
			result.Control.Wins = scenarios.Count(s => s.ControlOutcome == "Won");
			return result;
		}
	}
}
