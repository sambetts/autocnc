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
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class SequentialGateTests
	{
		static SequentialSetDefinition Definition(int stagePairs = 16, int maxPairs = 64) => new()
		{
			Maps = ["16-9.oramap"],
			Pairings =
			[
				new() { Faction = "gdi", BotFaction = "nod" },
				new() { Faction = "nod", BotFaction = "gdi" },
				new() { Faction = "gdi", BotFaction = "gdi" },
				new() { Faction = "nod", BotFaction = "nod" }
			],
			SeedBase = 1000001,
			StagePairs = stagePairs,
			MaxPairs = maxPairs
		};

		[Test]
		public void APoolIsFreshSeedsStratifiedSoEveryStageHoldsEachPairingEqually()
		{
			var state = new SequentialGateState();
			var pool = SequentialGate.Draw(state, Definition(), "champion", "harness", "run");

			Assert.That(pool.Matches.Select(m => m.Seed), Is.EqualTo(Enumerable.Range(1000001, 64)));
			Assert.That(pool.Matches.Select(m => m.Scenario), Is.EqualTo(Enumerable.Range(1, 64)));
			for (var stage = 0; stage < 4; stage++)
			{
				var slice = SequentialGate.Stage(pool, Definition(), stage);
				Assert.That(slice.Count, Is.EqualTo(16));
				Assert.That(slice.GroupBy(m => m.Faction + "/" + m.BotFaction).Select(g => g.Count()),
					Is.All.EqualTo(4), $"stage {stage + 1} is stratified");
			}

			Assert.That(state.NextSeed, Is.EqualTo(1000065));
		}

		[Test]
		public void OnlyANewChampionRetiresAPoolAndNoSeedIsEverDrawnTwice()
		{
			var definition = Definition();
			var state = new SequentialGateState();
			Assert.That(SequentialGate.EnsurePool(state, definition, "a", "h1", "r1", out _), Is.True);
			var first = state.Pool;
			first.Champion.Add(new SequentialChampionResult { Scenario = 1, Seed = 1000001, Outcome = "Won" });

			Assert.That(SequentialGate.EnsurePool(state, definition, "a", "h1", "r2", out var same), Is.False);
			Assert.That(same, Is.Null);
			Assert.That(state.Pool, Is.SameAs(first));
			Assert.That(state.Pool.Champion, Has.Count.EqualTo(1), "the same champion keeps its cache");

			Assert.That(SequentialGate.EnsurePool(state, definition, "a", "h2", "r3", out var harness), Is.False,
				"a harness change keeps the seeds, which the champion was never selected on");
			Assert.That(harness, Does.Contain("discarded"));
			Assert.That(state.Pool.Champion, Is.Empty, "but its cached games are stale");
			Assert.That(state.Pool.Matches[0].Seed, Is.EqualTo(1000001));

			Assert.That(SequentialGate.EnsurePool(state, definition, "b", "h2", "r4", out var retired), Is.True);
			Assert.That(retired, Does.Contain("champion changed"));
			Assert.That(state.Pool.Epoch, Is.EqualTo(2));
			Assert.That(state.Pool.Matches.Min(m => m.Seed), Is.EqualTo(1000065),
				"a promotion draws seeds nobody has played");
			Assert.That(state.Retired.Single().LastSeed, Is.EqualTo(1000064));
		}

		[Test]
		public void SevenDecisiveWinsWithoutALossPromoteAndThreeLossesRestore()
		{
			var definition = Definition();
			var promote = SequentialGate.Decide(Pairs((true, false, 7), (true, true, 9)), definition, final: false);
			Assert.That(promote.Verdict, Is.EqualTo(SequentialDecision.Promote));
			Assert.That(promote.Conclusion, Is.EqualTo("measured-better"));
			Assert.That(promote.CandidateOnlyWins, Is.EqualTo(7));
			Assert.That(promote.BothWon, Is.EqualTo(9));

			var six = SequentialGate.Decide(Pairs((true, false, 6)), definition, final: false);
			Assert.That(six.Verdict, Is.EqualTo(SequentialDecision.Continue), "six is not yet enough");

			var restore = SequentialGate.Decide(Pairs((false, true, 3), (false, false, 13)), definition, final: false);
			Assert.That(restore.Verdict, Is.EqualTo(SequentialDecision.Restore));
			Assert.That(restore.Conclusion, Is.EqualTo("measured-worse"));
		}

		[Test]
		public void AnUndecidedTestEndsInconclusiveOnlyWhenThePoolRunsOut()
		{
			var definition = Definition();
			var pairs = Pairs((true, false, 3), (false, true, 1), (true, true, 12));

			Assert.That(SequentialGate.Decide(pairs, definition, final: false).Verdict,
				Is.EqualTo(SequentialDecision.Continue));
			var final = SequentialGate.Decide(pairs, definition, final: true);
			Assert.That(final.Verdict, Is.EqualTo(SequentialDecision.Restore));
			Assert.That(final.Conclusion, Is.EqualTo("inconclusive"));
			Assert.That(final.Reason, Does.Contain("not refuted"));
		}

		[Test]
		public void GamesBothArmsWonOrBothLostCarryNoWeight()
		{
			var decision = SequentialGate.Decide(Pairs((true, true, 40), (false, false, 24)), Definition(), final: true);

			Assert.That(decision.LogLikelihoodRatio, Is.EqualTo(0));
			Assert.That(decision.Conclusion, Is.EqualTo("inconclusive"));
		}

		[Test]
		public void ACrashedCandidateGameIsALossAndACrashedChampionGameDropsThePair()
		{
			var pool = SequentialGate.Draw(new SequentialGateState(), Definition(), "c", "h", "r");
			var candidate = new[]
			{
				Row(1, "Failed", succeeded: false),
				Row(2, "Won"),
				Row(3, "TimedOut")
			};
			var control = new Dictionary<int, BenchmarkMatchResult>
			{
				[1] = Row(1, "Won"),
				[2] = Row(2, "Failed", succeeded: false),
				[3] = Row(3, "Won")
			};

			var pairs = SequentialGate.Pairs(pool, candidate, control, new HashSet<int>(), out var dropped);

			Assert.That(dropped, Is.EqualTo(1));
			Assert.That(pairs.Select(p => p.Match.Scenario), Is.EqualTo(new[] { 1, 3 }));
			Assert.That(pairs.All(p => !p.CandidateWon && p.ControlWon), Is.True,
				"a candidate crash and a candidate stalemate are both games the champion won and it did not");
			Assert.That(pairs[0].CandidateOutcome, Is.EqualTo("Failed"));
		}

		[Test]
		public void TheComposedEvaluationCarriesTheTestAndPromotesOnlyThroughIt()
		{
			var definition = Definition();
			var pool = SequentialGate.Draw(new SequentialGateState(), definition, "c", "h", "r");
			var candidate = Enumerable.Range(1, 16).Select(s => Row(s, s <= 8 ? "Won" : "Lost")).ToList();
			var control = Enumerable.Range(1, 16).ToDictionary(s => s, s => Row(s, "Lost"));
			var pairs = SequentialGate.Pairs(pool, candidate, control, new HashSet<int> { 3, 4 }, out var dropped);
			var decision = SequentialGate.Decide(pairs, definition, final: false);

			var (result, evaluation) = SequentialGate.Compose("hard-16-9-fresh", "batch", "Hard", 2400,
				definition, pool, pairs, dropped, 1, decision, 5, true, null);

			Assert.Multiple(() =>
			{
				Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Promote));
				Assert.That(evaluation.Basis, Is.EqualTo("sequential"));
				Assert.That(evaluation.CandidateWins, Is.EqualTo(8));
				Assert.That(evaluation.ControlWins, Is.Zero);
				Assert.That(evaluation.PairsCompared, Is.EqualTo(16));
				Assert.That(evaluation.Sequential.CandidateOnlyWins, Is.EqualTo(8));
				Assert.That(evaluation.Sequential.CachedControlPairs, Is.EqualTo(2));
				Assert.That(evaluation.Sequential.CanaryReproduced, Is.True);
				Assert.That(evaluation.Sequential.FirstSeed, Is.EqualTo(1000001));
				Assert.That(result.ExpectedMatchesPerArm, Is.EqualTo(16));
				Assert.That(result.Matches.Count(m => m.Arm == "candidate"), Is.EqualTo(16));
				Assert.That(result.Matches.Count(m => m.Arm == "control"), Is.EqualTo(16));
				Assert.That(result.Paired, Has.Count.EqualTo(16));
				Assert.That(result.Candidate.Wins, Is.EqualTo(8));
			});
		}

		[Test]
		public void ACachedGameReproducesOnlyWhenItsReplayMatchesExactly()
		{
			var cached = SequentialChampionResult.From(Row(4, "Won"), "rules", "sha");

			Assert.That(cached.Reproduces(Row(4, "Won")), Is.True);
			var slower = Row(4, "Won");
			slower.DurationSeconds = 601;
			Assert.That(cached.Reproduces(slower), Is.False);
			Assert.That(cached.Reproduces(Row(4, "Lost")), Is.False);
		}

		[Test]
		public void StateRoundTripsThroughItsFile()
		{
			var path = Path.Combine(Path.GetTempPath(), "autocnc-gate-" + Guid.NewGuid().ToString("N"), "gate.json");
			try
			{
				var state = new SequentialGateState { Benchmark = "hard-16-9-fresh" };
				SequentialGate.EnsurePool(state, Definition(), "c", "h", "r", out _);
				state.Pool.Champion.Add(SequentialChampionResult.From(Row(1, "Won"), "rules", "sha"));
				SequentialGate.Write(path, state);

				var read = SequentialGate.Read(path, "ignored");
				Assert.That(read.Benchmark, Is.EqualTo("hard-16-9-fresh"));
				Assert.That(read.Pool.Matches, Has.Count.EqualTo(64));
				Assert.That(read.Pool.Cached(1)?.Outcome, Is.EqualTo("Won"));
				Assert.That(read.NextSeed, Is.EqualTo(state.NextSeed));
			}
			finally
			{
				Directory.Delete(Path.GetDirectoryName(path), true);
			}
		}

		[Test]
		public void TheShippedSequentialSetIsValid()
		{
			var catalogue = Path.Combine(RepoRoot(), "scripts", "benchmarks.json");
			using var document = JsonDocument.Parse(File.ReadAllText(catalogue));
			var set = document.RootElement.GetProperty("sets").EnumerateArray()
				.Single(s => s.GetProperty("name").GetString() == "hard-16-9-fresh");

			var definition = SequentialSetDefinition.Parse(set.GetProperty("sequential"));

			Assert.That(definition.Validate(), Is.Null);
			Assert.That(definition.MaxPairs % (definition.StagePairs) == 0, Is.True);
			Assert.That(definition.StagePairs % definition.Pairings.Count, Is.Zero,
				"every stage holds each faction pairing equally");
			var used = document.RootElement.GetProperty("sets").EnumerateArray()
				.Where(s => s.TryGetProperty("matches", out _))
				.SelectMany(s => s.GetProperty("matches").EnumerateArray())
				.Select(m => m.GetProperty("seed").GetInt32());
			Assert.That(used.All(seed => seed < definition.SeedBase), Is.True,
				"fresh seeds never collide with a pinned set's");
		}

		static string RepoRoot()
		{
			var directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AutoCnC.sln")))
				directory = directory.Parent;
			return directory?.FullName ?? throw new DirectoryNotFoundException("The checkout was not found.");
		}

		static IEnumerable<SequentialOutcomePair> Pairs(params (bool Candidate, bool Control, int Count)[] groups) =>
			groups.SelectMany(group => Enumerable.Repeat(new SequentialOutcomePair(group.Candidate, group.Control), group.Count));

		static BenchmarkMatchResult Row(int scenario, string outcome, bool succeeded = true) => new()
		{
			RunId = "run-" + scenario,
			Arm = "candidate",
			Repeat = 1,
			Scenario = scenario,
			Map = "16-9.oramap",
			Faction = "gdi",
			BotFaction = "nod",
			Seed = 1000000 + scenario,
			Outcome = outcome,
			Status = succeeded ? "Completed" : "Undefined",
			Succeeded = succeeded,
			Fitness = outcome == "Won" ? 1 : 0.5,
			EarnedPerSecond = 60,
			SpentPerSecond = 60,
			Exchange = 2,
			BuildingsKilled = 10,
			DurationSeconds = 600
		};
	}

	[TestFixture]
	public sealed class ExperimentLedgerTests
	{
		string root;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "autocnc-ledger-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, true);

		[Test]
		public void NewReasonIdsAreTheReasonLiteralsOnlyTheCandidateHas()
		{
			var champion = Directory.CreateDirectory(Path.Combine(root, "champion")).FullName;
			var candidate = Directory.CreateDirectory(Path.Combine(root, "candidate")).FullName;
			File.WriteAllText(Path.Combine(champion, "A.cs"),
				"var a = \"economy.harvester-flees\"; var file = \"summary.json\";");
			File.WriteAllText(Path.Combine(candidate, "A.cs"),
				"var a = \"economy.harvester-flees\"; var b = \"assault.new-rule\"; var c = \"Not.A.Reason\";");
			Directory.CreateDirectory(Path.Combine(candidate, "obj"));
			File.WriteAllText(Path.Combine(candidate, "obj", "Generated.cs"), "var d = \"obj.generated-id\";");

			Assert.That(ExperimentLedger.NewReasonIds(candidate, champion), Is.EqualTo(new[] { "assault.new-rule" }));
		}

		[Test]
		public void ActivationsAndCheckTalliesAreCountedAcrossTheCandidatesGames()
		{
			var games = Enumerable.Range(1, 3).Select(i => Directory.CreateDirectory(Path.Combine(root, "g" + i)).FullName).ToList();
			ExperimentLedger.WriteReasonIdCounts(games[0], new Dictionary<string, int> { ["assault.new-rule"] = 4 });
			ExperimentLedger.WriteReasonIdCounts(games[1], new Dictionary<string, int> { ["other"] = 1 });
			ExperimentLedger.WriteReasonIdCounts(games[2], new Dictionary<string, int> { ["assault.new-rule"] = 1 });
			foreach (var (game, passed) in games.Zip(new[] { true, false, true }))
				Checks.Write(Path.Combine(game, "check-results.json"), new CheckReport
				{
					Results = [new CheckResult { Id = "rule-fires", Category = "activation", Passed = passed }]
				});

			var activation = ExperimentLedger.Activations(["assault.new-rule"], games).Single();
			var tally = ExperimentLedger.TallyChecks(games).Single();

			Assert.That(activation.GamesFired, Is.EqualTo(2));
			Assert.That(activation.Games, Is.EqualTo(3));
			Assert.That(activation.Mentions, Is.EqualTo(5));
			Assert.That(tally.Passed, Is.EqualTo(2));
			Assert.That(tally.Failed, Is.EqualTo(1));
			Assert.That(tally.Games, Is.EqualTo(3));
		}

		[Test]
		public void TheRenderedLedgerSaysWhatWasTriedWhetherItRanAndWhatTheGateConcluded()
		{
			var path = Path.Combine(root, ExperimentLedger.FileName);
			ExperimentLedger.Append(path, Record("20260928-100000", "inconclusive", "Restore", "Won", "champ"));
			ExperimentLedger.Append(path, Record("20260928-110000", "measured-worse", "Restore", "Lost", "champ",
				activation: new ReasonIdActivation { ReasonId = "assault.new-rule", Games = 32, GamesFired = 0 }));
			File.AppendAllText(path, "{ not json\n");

			var records = ExperimentLedger.Read(path);
			var rendered = ExperimentLedger.Render(records);

			Assert.That(records, Has.Count.EqualTo(2), "a damaged line is skipped");
			Assert.That(rendered, Does.Contain("restored, measured worse"));
			Assert.That(rendered, Does.Contain("restored, inconclusive"));
			Assert.That(rendered, Does.Contain("`assault.new-rule` in 0/32 games"));
			Assert.That(rendered, Does.Contain("won 1 of the 2 random-seed opening fight(s)"));
			Assert.That(rendered.IndexOf("20260928-110000", StringComparison.Ordinal),
				Is.LessThan(rendered.IndexOf("20260928-100000", StringComparison.Ordinal)), "newest first");
		}

		[Test]
		public void TheWinsARoundPassedOverCountInTheChampionRecordAndAreNamed()
		{
			var record = Record("20260929-100000", "inconclusive", "Restore", "Lost", "champ");
			record.EarlierFights = ["Won", "Won", "Won"];
			var other = Record("20260929-110000", "not-better", "Restore", "Won", "champ");

			Assert.That(ExperimentLedger.ChampionRecord([record, other]), Does.Contain("won 4 of the 5"),
				"a round that fought until it lost must not make the champion look worse than it is");
			Assert.That(ExperimentLedger.Render([record]), Does.Contain("Read a Lost, after 3 fight(s) on other seeds it won."));
		}

		[Test]
		public void AfterAPromotionTheChampionRecordStartsAgain()
		{
			var records = new List<ExperimentRecord>
			{
				Record("20260928-100000", "inconclusive", "Restore", "Won", "old"),
				Record("20260928-110000", "promoted", "Promote", "Won", "old", candidate: "new")
			};

			Assert.That(ExperimentLedger.ChampionRecord(records), Does.Contain("not yet played"));
			records.Add(Record("20260928-120000", "not-better", "Restore", "Lost", "new"));
			Assert.That(ExperimentLedger.ChampionRecord(records), Does.Contain("won 0 of the 1"));
		}

		[Test]
		public void TheWilsonIntervalIsSensibleAtTheEdges()
		{
			var (low, high) = ExperimentLedger.Wilson(4, 7);
			Assert.That(low, Is.EqualTo(0.25).Within(0.01));
			Assert.That(high, Is.EqualTo(0.84).Within(0.01));
			Assert.That(ExperimentLedger.Wilson(0, 0), Is.EqualTo((0d, 1d)));
			Assert.That(ExperimentLedger.Wilson(5, 5).High, Is.EqualTo(1).Within(1e-9));
		}

		static ExperimentRecord Record(string run, string conclusion, string verdict, string fight, string champion,
			string candidate = "cand", ReasonIdActivation activation = null) => new()
		{
			RunId = run,
			RecordedUtc = DateTime.ParseExact(run, "yyyyMMdd-HHmmss", null),
			Conclusion = conclusion,
			Verdict = verdict,
			FightOutcome = fight,
			ChampionFingerprint = champion,
			CandidateFingerprint = candidate,
			PairsCompared = 32,
			CandidateOnlyWins = 1,
			ControlOnlyWins = 4,
			LogLikelihoodRatio = -2.37,
			NewReasonIds = activation == null ? [] : [activation]
		};
	}

	[TestFixture]
	public sealed class TimedOutBenchmarkGameTests
	{
		[Test]
		public void AStalemateIsANonWinThatKeepsItsPair()
		{
			var result = new BenchmarkResultDocument
			{
				SchemaVersion = 1, Benchmark = "hard-16-9", Batch = "b", Difficulty = "Hard", MaxGameSeconds = 2400,
				ExpectedMatchesPerArm = 2,
				Candidate = new BenchmarkArmResult { Arm = "candidate", Wins = 1 },
				Control = new BenchmarkArmResult { Arm = "control", Wins = 2 }
			};
			foreach (var (scenario, candidate, control) in new[] { (1, "TimedOut", "Won"), (2, "Won", "Won") })
			{
				result.Matches.Add(Match("candidate", scenario, candidate));
				result.Matches.Add(Match("control", scenario, control));
				result.Paired.Add(new BenchmarkPairResult
				{
					Repeat = 1, Scenario = scenario, Map = "m", Faction = "gdi", BotFaction = "nod", Seed = scenario,
					CandidateOutcome = candidate, ControlOutcome = control, FitnessDelta = 0,
					EarnedPerSecondDelta = 0, SpentPerSecondDelta = 0, ExchangeDelta = 0, BuildingsKilledDelta = 0
				});
			}

			var evaluation = PairedBenchmarkEvaluator.Evaluate(result);

			Assert.That(evaluation.DroppedPairs, Is.Zero, "the champion's win on that seed still counts");
			Assert.That(evaluation.PairsCompared, Is.EqualTo(2));
			Assert.That(evaluation.CandidateWins, Is.EqualTo(1));
			Assert.That(evaluation.ControlWins, Is.EqualTo(2));
			Assert.That(evaluation.Verdict, Is.EqualTo(PromotionVerdicts.Restore));
		}

		static BenchmarkMatchResult Match(string arm, int scenario, string outcome) => new()
		{
			RunId = arm + scenario, Arm = arm, Repeat = 1, Scenario = scenario, Map = "m", Faction = "gdi",
			BotFaction = "nod", Seed = scenario, Outcome = outcome, Fitness = 0.9, EarnedPerSecond = 1,
			SpentPerSecond = 1, Exchange = 1, BuildingsKilled = 1, DurationSeconds = 2400, Succeeded = true,
			Status = "Completed", Error = ""
		};
	}
}
