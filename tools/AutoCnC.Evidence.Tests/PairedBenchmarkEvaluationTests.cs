#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System.IO;
using System.Linq;
using System.Text.Json;
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
		public void Hard169EightMatchResultIsAccepted()
		{
			var scenarios = Enumerable.Range(0, 8)
				.Select(index => (
					CandidateOutcome: index < 5 ? "Won" : "Lost",
					ControlOutcome: index < 3 ? "Won" : "Lost",
					CandidateFitness: 0.6 + index / 100d,
					ControlFitness: 0.5 + index / 100d))
				.ToArray();

			var evaluation = PairedBenchmarkEvaluator.Evaluate(Result(scenarios));

			Assert.That(evaluation.Benchmark, Is.EqualTo("hard-16-9"));
			Assert.That(evaluation.ExpectedMatchesPerArm, Is.EqualTo(8));
			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Promote));
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

		[TestCase("Undefined", "completed", true)]
		[TestCase("Failed", "completed", true)]
		[TestCase("Won", "failed", false)]
		[TestCase("Won", "completed", false)]
		public void FailedOrUndefinedRunsCannotPromote(string outcome, string status, bool succeeded)
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
		public void NullMetricsOnExplicitFailureDeserializeAndYieldUndefined()
		{
			var result = Result(("Won", "Lost", 0.7, 0.5));
			var candidate = result.Matches.Single(match => match.Arm == "candidate");
			candidate.Succeeded = false;
			candidate.Status = "failed";
			candidate.Error = "process exited";
			candidate.Outcome = "Undefined";
			candidate.Fitness = null;
			candidate.EarnedPerSecond = null;
			candidate.SpentPerSecond = null;
			candidate.Exchange = null;
			candidate.BuildingsKilled = null;
			candidate.DurationSeconds = null;
			result.Paired[0].CandidateOutcome = "Undefined";
			result.Paired[0].FitnessDelta = null;
			result.Paired[0].EarnedPerSecondDelta = null;
			result.Paired[0].SpentPerSecondDelta = null;
			result.Paired[0].ExchangeDelta = null;
			result.Paired[0].BuildingsKilledDelta = null;
			result.Candidate.Wins = 0;
			result.Candidate.MedianFitness = null;

			var parsed = JsonSerializer.Deserialize<BenchmarkResultDocument>(
				JsonSerializer.Serialize(result));
			var evaluation = PairedBenchmarkEvaluator.Evaluate(parsed);

			Assert.That(parsed.Matches.Single(match => match.Arm == "candidate").Fitness,
				Is.Null);
			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
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

		[Test]
		public void SymmetricScenarioOmissionIsUndefinedAgainstTheExpectedPlan()
		{
			var result = Result(
				("Won", "Lost", 0.7, 0.5),
				("Lost", "Won", 0.4, 0.6));
			result.Matches.RemoveAll(match => match.Scenario == 2);
			result.Paired.RemoveAll(pair => pair.Scenario == 2);
			result.Candidate.Played = 1;
			result.Control.Played = 1;

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Undefined));
			Assert.That(evaluation.Reason, Does.Contain("serialized match plan is incomplete"));
		}

		[Test]
		public void TwoSingleArmResultsComposeIntoOneExplicitPairedBatch()
		{
			var candidate = SingleArm("candidate-raw", "Won", 0.7);
			var control = SingleArm("control-raw", "Lost", 0.5);

			var combined = PairedBenchmarkResultComposer.Combine(
				candidate, control, "promotion-20260919-110459");
			var evaluation = PairedBenchmarkEvaluator.Evaluate(combined);

			Assert.That(combined.ExpectedMatchesPerArm, Is.EqualTo(1));
			Assert.That(combined.Matches.Select(match => match.Arm),
				Is.EquivalentTo(new[] { "candidate", "control" }));
			Assert.That(combined.Matches.All(match =>
				match.Succeeded && match.Status == "completed" && match.Error == ""), Is.True);
			Assert.That(combined.Paired, Has.Count.EqualTo(1));
			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Promote));
		}

		[Test]
		public void DifferentMatchTimeLimitsCannotCompose()
		{
			var candidate = SingleArm("candidate-raw", "Won", 0.7);
			var control = SingleArm("control-raw", "Lost", 0.5);
			control.MaxGameSeconds = candidate.MaxGameSeconds / 2;

			Assert.That(() => PairedBenchmarkResultComposer.Combine(
				candidate, control, "promotion-20260920-060000"),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("time limit"));
		}

		[Test]
		public void MissingMatchTimeLimitCannotCompose()
		{
			var candidate = SingleArm("candidate-raw", "Won", 0.7);
			var control = SingleArm("control-raw", "Lost", 0.5);
			candidate.MaxGameSeconds = null;

			Assert.That(() => PairedBenchmarkResultComposer.Combine(
				candidate, control, "promotion-20260920-060100"),
				Throws.TypeOf<InvalidDataException>().With.Message.Contains("time limit"));
		}

		static BenchmarkResultDocument Result(
			params (string CandidateOutcome, string ControlOutcome,
				double CandidateFitness, double ControlFitness)[] scenarios)
		{
			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				Benchmark = "hard-16-9",
				Batch = "hard-16-9-20260919-110459-test",
				Difficulty = "Hard",
				MaxGameSeconds = 2400,
				ExpectedMatchesPerArm = scenarios.Length,
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
					EarnedPerSecond = 12,
					SpentPerSecond = 10,
					Exchange = 1.2,
					BuildingsKilled = 2,
					DurationSeconds = 600,
					Succeeded = true,
					Status = "completed",
					Error = ""
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
					EarnedPerSecond = 10,
					SpentPerSecond = 9,
					Exchange = 1,
					BuildingsKilled = 1,
					DurationSeconds = 600,
					Succeeded = true,
					Status = "completed",
					Error = ""
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
						scenario.CandidateFitness - scenario.ControlFitness, 4),
					EarnedPerSecondDelta = 2,
					SpentPerSecondDelta = 1,
					ExchangeDelta = 0.2,
					BuildingsKilledDelta = 1
				});
			}

			result.Candidate.Played = scenarios.Length;
			result.Control.Played = scenarios.Length;
			result.Candidate.Wins = scenarios.Count(s => s.CandidateOutcome == "Won");
			result.Control.Wins = scenarios.Count(s => s.ControlOutcome == "Won");
			return result;
		}

		static BenchmarkResultDocument SingleArm(string batch, string outcome, double fitness)
		{
			return new BenchmarkResultDocument
			{
				SchemaVersion = 1,
				Benchmark = "hard-16-9",
				Batch = batch,
				Difficulty = "Hard",
				MaxGameSeconds = 2400,
				ExpectedMatchesPerArm = 1,
				Candidate = new BenchmarkArmResult
				{
					Arm = "candidate",
					Wins = outcome == "Won" ? 1 : 0,
					Played = 1,
					MedianFitness = fitness
				},
				Matches =
				[
					new BenchmarkMatchResult
					{
						RunId = batch,
						Arm = "candidate",
						Repeat = 1,
						Scenario = 1,
						Map = "map",
						Faction = "gdi",
						BotFaction = "nod",
						Seed = 123,
						Outcome = outcome,
						Fitness = fitness,
						EarnedPerSecond = 12,
						SpentPerSecond = 10,
						Exchange = 1.2,
						BuildingsKilled = 2,
						DurationSeconds = 600,
						Succeeded = true,
						Status = "completed",
						Error = ""
					}
				]
			};
		}
	}
}
