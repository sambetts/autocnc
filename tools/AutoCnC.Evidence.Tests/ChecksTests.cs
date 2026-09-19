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

using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class ChecksTests : EvidenceTestBase
	{
		[Test]
		public void EvaluateCoversEveryQueryFormOperatorAndFailureWithoutThrowing()
		{
			var report = Evaluate(
				new Check { Id = "reason-present", Description = "reason count", Query = "reason:EXPAND", Operator = ">=", Value = "2" },
				new Check { Id = "reason-absent", Description = "absent reason count", Query = "reason:missing", Operator = "==", Value = "0" },
				new Check { Id = "headline", Description = "headline number", Query = "summary.headline.creditsSpentPerSecond", Operator = ">=", Value = "12.5" },
				new Check { Id = "unit-type", Description = "table lookup", Query = "summary.unitTypes[e1].creditsPerKill", Operator = "==", Value = "100" },
				new Check { Id = "production", Description = "production lookup", Query = "summary.production[Infantry].deepestPlanStepIndex", Operator = "==", Value = "2" },
				new Check { Id = "unit-count", Description = "unit count", Query = "units.count(type=e1)", Operator = "==", Value = "2" },
				new Check { Id = "unit-sum", Description = "unit sum", Query = "units.sum(kills,type=e1)", Operator = "==", Value = "3" },
				new Check { Id = "unit-mean", Description = "unit mean", Query = "units.mean(lifetimeSeconds,type=e1)", Operator = "==", Value = "15" },
				new Check { Id = "greater", Description = "greater than", Query = "summary.headline.creditsLost", Operator = ">", Value = "20" },
				new Check { Id = "less-equal", Description = "less or equal", Query = "summary.headline.buildingsKilled", Operator = "<=", Value = "1" },
				new Check { Id = "less", Description = "less than", Query = "summary.headline.creditsEarnedPerSecond", Operator = "<", Value = "13" },
				new Check { Id = "not-equal", Description = "not equal", Query = "summary.headline.outcome", Operator = "!=", Value = "Lost" },
				new Check { Id = "contains", Description = "contains", Query = "summary.fight.outcome", Operator = "contains", Value = "wo" },
				new Check { Id = "present", Description = "present", Query = "summary.fight.map", Operator = "present" },
				new Check { Id = "absent", Description = "absent", Query = "reason:never", Operator = "absent" },
				new Check { Id = "unknown-query", Description = "unknown query", Query = "mystery.value", Operator = ">=", Value = "1" },
				new Check { Id = "unknown-field", Description = "unknown field", Query = "summary.headline.noSuchField", Operator = "present" });

			Assert.That(Result(report, "reason-present").Actual, Is.EqualTo("2"));
			Assert.That(Result(report, "reason-absent").Actual, Is.EqualTo("0"));
			Assert.That(report.Passed, Is.EqualTo(15));
			Assert.That(report.Failed, Is.EqualTo(2));
			Assert.That(report.Results.Where(r => r.Id.StartsWith("unknown")).All(r => !r.Passed && r.Error != null), Is.True);
			Assert.That(report.Rendered, Does.Contain("PASS"));
			Assert.That(report.Rendered, Does.Contain("FAIL"));
			Assert.That(report.Rendered, Does.Contain("actual 12.5"));
		}

		[Test]
		public void ReasonQueryMatchesCaseInsensitiveSubstringAndCountsDecisions()
		{
			var report = Evaluate(new Check
			{
				Id = "reason-substring",
				Description = "reason substring",
				Query = "reason:toward",
				Operator = "==",
				Value = "2"
			});

			Assert.That(Result(report, "reason-substring").Actual, Is.EqualTo("2"));
			Assert.That(Result(report, "reason-substring").Passed, Is.True);
		}

		[Test]
		public void ReasonQueryReturnsZeroWhenNothingMatches()
		{
			var report = Evaluate(new Check
			{
				Id = "reason-missing",
				Description = "missing reason",
				Query = "reason:not present",
				Operator = "==",
				Value = "0"
			});

			Assert.That(Result(report, "reason-missing").Actual, Is.EqualTo("0"));
			Assert.That(Result(report, "reason-missing").Passed, Is.True);
		}

		[Test]
		public void ReasonIdQueryIsExactAndReasonQueryPrefersAnExactId()
		{
			var tracePath = WriteFile("reason-ids.jsonl",
				"{\"event\":\"unit-decision\",\"reason\":\"combat.engage primary\",\"reasonId\":\"combat.engage\"}\n" +
				"{\"event\":\"unit-decision\",\"reason\":\"combat.engage nearby\",\"reasonId\":\"combat.engage.nearby\"}\n");
			var trace = DecisionTrace.Read(tracePath);
			var document = new CheckDocument
			{
				Checks =
				[
					new Check { Id = "exact", Query = "reason-id:combat.engage", Operator = "==", Value = "1" },
					new Check { Id = "no-prefix", Query = "reason-id:combat", Operator = "==", Value = "0" },
					new Check { Id = "preferred", Query = "reason:combat.engage", Operator = "==", Value = "1" },
					new Check { Id = "legacy", Query = "reason:engage", Operator = "==", Value = "2" }
				]
			};

			var report = Checks.Evaluate(document, Summary(), Units(), trace);

			Assert.Multiple(() =>
			{
				Assert.That(Result(report, "exact").Passed, Is.True);
				Assert.That(Result(report, "no-prefix").Passed, Is.True);
				Assert.That(Result(report, "preferred").Passed, Is.True);
				Assert.That(Result(report, "legacy").Passed, Is.True);
			});
		}

		[Test]
		public void SummaryHeadlinePathResolvesAndComparesNumerically()
		{
			var report = Evaluate(new Check
			{
				Id = "headline-numeric",
				Description = "headline numeric comparison",
				Query = "summary.headline.creditsSpentPerSecond",
				Operator = ">=",
				Value = "12.5"
			});

			Assert.That(Result(report, "headline-numeric").Actual, Is.EqualTo("12.5"));
			Assert.That(Result(report, "headline-numeric").Passed, Is.True);
		}

		[Test]
		public void UnitTypeTableLookupUsesTheFirstColumnCaseInsensitively()
		{
			var report = Evaluate(new Check
			{
				Id = "unit-type-table",
				Description = "unit table lookup",
				Query = "summary.unitTypes[E1].creditsPerKill",
				Operator = "==",
				Value = "100"
			});

			Assert.That(Result(report, "unit-type-table").Actual, Is.EqualTo("100"));
			Assert.That(Result(report, "unit-type-table").Passed, Is.True);
		}

		[Test]
		public void ProductionTableLookupResolvesTheDeepestPlanStepIndex()
		{
			var report = Evaluate(new Check
			{
				Id = "production-table",
				Description = "production lookup",
				Query = "summary.production[Infantry].deepestPlanStepIndex",
				Operator = "==",
				Value = "2"
			});

			Assert.That(Result(report, "production-table").Actual, Is.EqualTo("2"));
			Assert.That(Result(report, "production-table").Passed, Is.True);
		}

		[Test]
		public void FitnessComponentLookupUsesTheComponentNameBranch()
		{
			var report = Evaluate(new Check
			{
				Id = "fitness-component",
				Description = "fitness component lookup",
				Query = "summary.fitness.components[valueExchange].score",
				Operator = "==",
				Value = "0.75"
			});

			Assert.That(Result(report, "fitness-component").Actual, Is.EqualTo("0.75"));
			Assert.That(Result(report, "fitness-component").Passed, Is.True);
		}

		[Test]
		public void UnitsCountFiltersByType()
		{
			Assert.That(Passed("units-count", "units.count(type=e1)", "==", "2"), Is.True);
		}

		[Test]
		public void UnitsSumFiltersByType()
		{
			Assert.That(Passed("units-sum", "units.sum(kills,type=e1)", "==", "3"), Is.True);
		}

		[Test]
		public void UnitsMeanFiltersByType()
		{
			Assert.That(Passed("units-mean", "units.mean(lifetimeSeconds,type=e1)", "==", "15"), Is.True);
		}

		[Test]
		public void UnitsMaxAggregatesTheFilteredRows()
		{
			Assert.That(Passed("units-max", "units.max(lifetimeSeconds,type=e1)", "==", "20"), Is.True);
		}

		[Test]
		public void UnitsMinAggregatesTheFilteredRows()
		{
			Assert.That(Passed("units-min", "units.min(lifetimeSeconds,type=e1)", "==", "10"), Is.True);
		}

		[Test]
		public void UnitsCountFiltersByModeSubstring()
		{
			Assert.That(Passed("units-mode", "units.count(mode=attack)", "==", "2"), Is.True);
		}

		[Test]
		public void GreaterThanOrEqualOperatorComparesNumbers()
		{
			Assert.That(Passed("gte", "summary.headline.creditsSpentPerSecond", ">=", "12.5"), Is.True);
		}

		[Test]
		public void GreaterThanOperatorComparesNumbers()
		{
			Assert.That(Passed("gt", "summary.headline.creditsLost", ">", "20"), Is.True);
		}

		[Test]
		public void LessThanOrEqualOperatorComparesNumbers()
		{
			Assert.That(Passed("lte", "summary.headline.buildingsKilled", "<=", "1"), Is.True);
		}

		[Test]
		public void LessThanOperatorComparesNumbers()
		{
			Assert.That(Passed("lt", "summary.headline.creditsEarnedPerSecond", "<", "13"), Is.True);
		}

		[Test]
		public void NumericEqualityOperatorUsesInvariantNumbers()
		{
			Assert.That(Passed("numeric-eq", "summary.headline.creditsSpentPerSecond", "==", "12.5"), Is.True);
		}

		[Test]
		public void StringEqualityOperatorComparesCaseInsensitively()
		{
			Assert.That(Passed("string-eq", "summary.fight.outcome", "==", "won"), Is.True);
		}

		[Test]
		public void NotEqualOperatorPassesWhenValuesDiffer()
		{
			Assert.That(Passed("not-eq", "summary.headline.outcome", "!=", "Lost"), Is.True);
		}

		[Test]
		public void ContainsOperatorComparesCaseInsensitively()
		{
			Assert.That(Passed("contains", "summary.fight.map", "contains", "test"), Is.True);
		}

		[Test]
		public void PresentOperatorRequiresANonEmptyNonZeroValue()
		{
			Assert.That(Passed("present", "summary.fight.map", "present", null), Is.True);
		}

		[Test]
		public void AbsentOperatorAcceptsAZeroValue()
		{
			Assert.That(Passed("absent", "reason:missing", "absent", null), Is.True);
		}

		[TestCase("unknown-prefix", "mystery.value", ">=", "1")]
		[TestCase("unknown-unit-field", "units.sum(noSuchField,type=e1)", "==", "0")]
		[TestCase("unknown-units-function", "units.median(kills,type=e1)", "==", "0")]
		[TestCase("malformed-units-query", "units.count", "==", "0")]
		[TestCase("unclosed-summary-key", "summary.unitTypes[e1.creditsPerKill", "==", "0")]
		[TestCase("non-numeric-comparison", "summary.fight.outcome", ">=", "1")]
		[TestCase("missing-table-key", "summary.unitTypes[missing].creditsPerKill", "==", "0")]
		public void ErrorPathsFailWithErrorsWithoutThrowing(string id, string query, string @operator,
			string value)
		{
			var report = Evaluate(new Check
			{
				Id = id,
				Description = id,
				Query = query,
				Operator = @operator,
				Value = value
			});
			var result = Result(report, id);

			Assert.That(result.Passed, Is.False);
			Assert.That(result.Error, Is.Not.Null);
			Assert.That(result.Actual, Is.EqualTo("unresolved"));
		}

		[Test]
		public void ReadReturnsNullForMalformedJson()
		{
			var path = WriteFile("checks.json", "{ not json");

			var document = Checks.Read(path);

			Assert.That(document, Is.Null);
		}

		[Test]
		public void RenderedReportIncludesStatusIdAndActualValue()
		{
			var report = Evaluate(
				new Check { Id = "passes-with-value", Description = "passing check", Query = "summary.headline.creditsSpentPerSecond", Operator = "==", Value = "12.5" },
				new Check { Id = "fails-with-value", Description = "failing check", Query = "summary.headline.creditsSpentPerSecond", Operator = ">", Value = "20" });

			Assert.That(report.Rendered, Does.Contain("PASS"));
			Assert.That(report.Rendered, Does.Contain("FAIL"));
			Assert.That(report.Rendered, Does.Contain("passes-with-value"));
			Assert.That(report.Rendered, Does.Contain("actual 12.5"));
		}

		bool Passed(string id, string query, string @operator, string value)
		{
			return Result(Evaluate(new Check
			{
				Id = id,
				Description = id,
				Query = query,
				Operator = @operator,
				Value = value
			}), id).Passed;
		}

		CheckReport Evaluate(params Check[] checks)
		{
			return Checks.Evaluate(new CheckDocument { Checks = checks.ToList() },
				Summary(), Units(), Trace());
		}

		static CheckResult Result(CheckReport report, string id)
		{
			return report.Results.Single(r => r.Id == id);
		}

		FightSummary Summary()
		{
			return new FightSummary
			{
				RunId = "run-checks",
				SourceRevision = "abc",
				Fight = new SummaryFight { Outcome = "Won", Map = "Test Map" },
				Headline = new SummaryHeadline
				{
					Outcome = "Won",
					CreditsSpentPerSecond = 12.5,
					CreditsEarnedPerSecond = 12.4,
					CreditsLost = 25,
					BuildingsKilled = 1
				},
				Fitness = new FitnessScore
				{
					Components =
					{
						new FitnessComponent { Name = "valueExchange", Score = 0.75 }
					}
				},
				UnitTypes = new Table
				{
					Columns = new[] { "type", "creditsPerKill" },
					Rows = { "e1,100" }
				},
				Production = new Table
				{
					Columns = new[] { "queue", "deepestPlanStepIndex" },
					Rows = { "Infantry,2" }
				}
			};
		}

		static UnitRecord[] Units()
		{
			return new[]
			{
				new UnitRecord { Type = "e1", Kills = 1, LifetimeSeconds = 10, ModesUsed = "Attack|Guard" },
				new UnitRecord { Type = "e1", Kills = 2, LifetimeSeconds = 20, ModesUsed = "attack" },
				new UnitRecord { Type = "e2", Kills = 5, LifetimeSeconds = 100, ModesUsed = "Scout" }
			};
		}

		DecisionTrace Trace()
		{
			var tracePath = WriteFile("decisions.jsonl",
				"{\"event\":\"unit-decision\",\"seconds\":1,\"actor\":\"e1\",\"actorId\":1,\"mode\":\"Attack\",\"action\":\"Move\",\"reason\":\"Expand toward ore\"}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":2,\"actor\":\"e1\",\"actorId\":2,\"mode\":\"Attack\",\"action\":\"Move\",\"reason\":\"Expand toward ore\"}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":3,\"actor\":\"e2\",\"actorId\":3,\"mode\":\"Scout\",\"action\":\"Move\",\"reason\":\"Scout safely\"}\n");

			return DecisionTrace.Read(tracePath);
		}
	}
}
