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
	public sealed class DecisionTraceTests : EvidenceTestBase
	{
		[Test]
		public void ReadsReasonIdsValueStateThreatMixAndUrgentDoctrineOutcome()
		{
			var path = WriteFile("decisions.jsonl",
				"{\"event\":\"started\",\"schemaVersion\":2}\n" +
				"{\"event\":\"assessment\",\"seconds\":12," +
				"\"state\":{\"doctrine\":\"Opening\",\"cash\":100,\"powerBalance\":5," +
				"\"harvesters\":1,\"refineries\":1,\"units\":4,\"armyValue\":900,\"buildings\":3," +
				"\"baseValue\":1500,\"windowSeconds\":60,\"unitsLost\":1,\"buildingsLost\":0," +
				"\"unitsKilled\":2,\"enemiesInSight\":3,\"enemiesNearBase\":1," +
				"\"creditsKilled\":500,\"creditsLost\":200,\"hasValueTradeData\":true,\"incomeEarned\":750," +
				"\"visibleEnemyValue\":600,\"enemyValueNearBase\":400,\"ownArmyValueNearBase\":800," +
				"\"visibleEnemyMix\":[{\"kind\":\"Infantry\",\"count\":2,\"value\":200}," +
				"{\"kind\":\"Vehicle\",\"count\":1,\"value\":400}]," +
				"\"nearestEnemyCells\":4,\"secondsSinceContact\":0,\"enemyBaseFound\":true," +
				"\"baseUnderAttack\":true,\"blindToEnemy\":false,\"winning\":false}," +
				"\"botDecision\":{\"reasonId\":\"doctrine.defence.base-breached\",\"isUrgent\":true}," +
				"\"modeRequest\":{\"reasonId\":\"mode.scout.complete\",\"isUrgent\":false}," +
				"\"effectiveDecision\":{\"reasonId\":\"doctrine.defence.base-breached\",\"isUrgent\":true}," +
				"\"outcome\":\"urgent-dwell-bypass\"}\n" +
				"{\"event\":\"doctrine\",\"seconds\":12,\"from\":\"Opening\",\"to\":\"Defence\"," +
				"\"reason\":\"base breached\",\"reasonId\":\"doctrine.defence.base-breached\"," +
				"\"urgent\":true,\"dwellBypassed\":true}\n" +
				"{\"event\":\"unit-decision\",\"seconds\":13,\"actor\":\"e1\",\"actorId\":7," +
				"\"mode\":\"DefensiveMode\",\"action\":\"Attack\",\"reason\":\"focus armour\"," +
				"\"reasonId\":\"combat.focus-armour\",\"order\":\"Attack\"}\n");

			var trace = DecisionTrace.Read(path);
			var assessment = trace.Assessments.Single();
			var doctrine = trace.DoctrineChanges.Single();
			var unit = trace.UnitDecisions.Single();

			Assert.Multiple(() =>
			{
				Assert.That(trace.SchemaVersion, Is.EqualTo(2));
				Assert.That(assessment.CreditsKilled, Is.EqualTo(500));
				Assert.That(assessment.CreditsLost, Is.EqualTo(200));
				Assert.That(assessment.HasValueTradeData, Is.True);
				Assert.That(assessment.EnemiesNearBase, Is.EqualTo(1));
				Assert.That(assessment.BuildingsLost, Is.Zero);
				Assert.That(assessment.IncomeEarned, Is.EqualTo(750));
				Assert.That(assessment.VisibleEnemyValue, Is.EqualTo(600));
				Assert.That(assessment.EnemyValueNearBase, Is.EqualTo(400));
				Assert.That(assessment.OwnArmyValueNearBase, Is.EqualTo(800));
				Assert.That(assessment.VisibleEnemyMix.Single(v => v.Kind == "Vehicle").Value,
					Is.EqualTo(400));
				Assert.That(assessment.BotReasonId, Is.EqualTo("doctrine.defence.base-breached"));
				Assert.That(assessment.ModeRequestReasonId, Is.EqualTo("mode.scout.complete"));
				Assert.That(assessment.EffectiveReasonId, Is.EqualTo("doctrine.defence.base-breached"));
				Assert.That(assessment.EffectiveDecisionUrgent, Is.True);
				Assert.That(assessment.Outcome, Is.EqualTo("urgent-dwell-bypass"));
				Assert.That(doctrine.ReasonId, Is.EqualTo("doctrine.defence.base-breached"));
				Assert.That(doctrine.Urgent, Is.True);
				Assert.That(doctrine.DwellBypassed, Is.True);
				Assert.That(unit.ReasonId, Is.EqualTo("combat.focus-armour"));
				Assert.That(trace.ReasonIdMentions("combat.focus-armour"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdMentions("doctrine.defence.base-breached"), Is.EqualTo(3),
					"bot, effective, and applied-change fields are each retained");
				Assert.That(trace.ReasonIdMentions("mode.scout.complete"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdMentions("combat.focus"), Is.EqualTo(0));
			});
		}

		[Test]
		public void LegacyTraceWithoutReasonIdsStillParses()
		{
			var path = WriteFile("legacy-decisions.jsonl",
				"{\"event\":\"started\",\"schemaVersion\":1}\n" +
				"{\"event\":\"unit-decision\",\"reason\":\"legacy retreat\"}\n" +
				"{\"event\":\"doctrine\",\"from\":\"Opening\",\"to\":\"Defence\",\"reason\":\"legacy switch\"}\n");

			var trace = DecisionTrace.Read(path);

			Assert.Multiple(() =>
			{
				Assert.That(trace.SchemaVersion, Is.EqualTo(1));
				Assert.That(trace.UnitDecisions.Single().ReasonId, Is.Null);
				Assert.That(trace.DoctrineChanges.Single().ReasonId, Is.Null);
				Assert.That(trace.ReasonMentions("retreat"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdCounts, Is.Empty);
			});
		}

		[Test]
		public void EvaluationReasonIdsCountEveryOutcomeWithoutChangingIssuedDecisionIndexes()
		{
			var path = WriteFile("evaluations.jsonl",
				"{\"event\":\"started\",\"schemaVersion\":3}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"mode\":\"Defence\",\"action\":\"Continue\"," +
				"\"reason\":\"suppressed prose\",\"reasonId\":\"unit.evaluate\",\"outcome\":\"continue\"}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"mode\":\"Defence\",\"action\":\"Hold\"," +
				"\"reason\":\"suppressed prose\",\"reasonId\":\"unit.evaluate\",\"outcome\":\"already-idle\"}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"mode\":\"Defence\",\"action\":\"Attack\"," +
				"\"reason\":\"suppressed prose\",\"reasonId\":\"unit.evaluate\",\"outcome\":\"duplicate-intent\"}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"mode\":\"Defence\",\"action\":\"Produce\"," +
				"\"reason\":\"suppressed prose\",\"reasonId\":\"unit.evaluate\",\"outcome\":\"no-order\"}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"mode\":\"Defence\",\"action\":\"Attack\"," +
				"\"reasonId\":\"unit.evaluate\",\"outcome\":\"issued\"}\n" +
				"{\"event\":\"unit-decision\",\"mode\":\"Defence\",\"action\":\"Attack\"," +
				"\"reason\":\"issued prose\",\"reasonId\":\"unit.evaluate\",\"order\":\"Attack\"}\n");

			var trace = DecisionTrace.Read(path);

			Assert.Multiple(() =>
			{
				Assert.That(trace.UnitDecisionEvaluations.Select(e => e.Outcome), Is.EqualTo(new[]
				{
					"continue", "already-idle", "duplicate-intent", "no-order", "issued"
				}));
				Assert.That(trace.ReasonIdMentions("unit.evaluate"), Is.EqualTo(5),
					"the issued event must not double-count its evaluation");
				Assert.That(trace.UnitDecisions, Has.Count.EqualTo(1),
					"legacy issued-decision records remain issued-only");
				Assert.That(trace.ReasonCounts["issued prose"], Is.EqualTo(1));
				Assert.That(trace.ReasonCounts.ContainsKey("suppressed prose"), Is.False);
				Assert.That(trace.ModeDecisionCounts["Defence"], Is.EqualTo(1));
				Assert.That(trace.ActionCounts["Attack"], Is.EqualTo(1));
			});
		}

		[Test]
		public void ReadsBudgetSuppressionDetailsAndRegistersItsReasonId()
		{
			var path = WriteFile("budget-suppression.jsonl",
				"{\"event\":\"started\",\"schemaVersion\":3}\n" +
				"{\"event\":\"unit-decision-evaluated\",\"seconds\":17,\"actor\":\"weap\"," +
				"\"actorId\":42,\"mode\":\"TrainUnitsMode\",\"action\":\"Produce\"," +
				"\"itemName\":\"mtnk\",\"queue\":\"Vehicle\",\"reason\":\"replace armour\"," +
				"\"reasonId\":\"production.armour\",\"outcome\":\"production-budget-suppressed\"," +
				"\"productionBudget\":{\"reservedCash\":1200,\"ownerQueue\":\"Building\"," +
				"\"reason\":\"save for tech\",\"reasonId\":\"production.reserve.tech\"}," +
				"\"production\":{\"itemCost\":800,\"currentCash\":1700,\"postOrderCash\":900," +
				"\"reservedCashRemaining\":1200}}\n");

			var trace = DecisionTrace.Read(path);
			var evaluation = trace.UnitDecisionEvaluations.Single();

			Assert.Multiple(() =>
			{
				Assert.That(evaluation.Outcome, Is.EqualTo("production-budget-suppressed"));
				Assert.That(evaluation.ProductionBudget.ReservedCash, Is.EqualTo(1200));
				Assert.That(evaluation.ProductionBudget.OwnerQueue, Is.EqualTo("Building"));
				Assert.That(evaluation.ProductionBudget.Reason, Is.EqualTo("save for tech"));
				Assert.That(evaluation.ProductionBudget.ReasonId,
					Is.EqualTo("production.reserve.tech"));
				Assert.That(evaluation.Production.ItemCost, Is.EqualTo(800));
				Assert.That(evaluation.Production.CurrentCash, Is.EqualTo(1700));
				Assert.That(evaluation.Production.PostOrderCash, Is.EqualTo(900));
				Assert.That(evaluation.Production.ReservedCashRemaining, Is.EqualTo(1200));
				Assert.That(trace.ReasonIdMentions("production.armour"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdMentions("production.reserve.tech"), Is.EqualTo(1));
			});
		}

		[Test]
		public void ReadsEveryProductionBudgetRefreshStatusAndRegistersActivationReason()
		{
			var path = WriteFile("production-budgets.jsonl",
				"{\"event\":\"started\",\"schemaVersion\":4}\n" +
				"{\"event\":\"production-budget\",\"seconds\":5,\"doctrine\":\"Opening\"," +
				"\"active\":true,\"status\":\"active\",\"productionBudget\":{\"reservedCash\":1200," +
				"\"ownerQueue\":\"Building\",\"reason\":\"save for tech\"," +
				"\"reasonId\":\"production.reserve.tech\"}}\n" +
				"{\"event\":\"production-budget\",\"seconds\":10,\"doctrine\":\"Opening\"," +
				"\"active\":false,\"status\":\"inactive\",\"productionBudget\":{\"reservedCash\":0}}\n" +
				"{\"event\":\"production-budget\",\"seconds\":15,\"doctrine\":\"Opening\"," +
				"\"active\":false,\"status\":\"invalid\",\"productionBudget\":{\"reservedCash\":0," +
				"\"ownerQueue\":\"Vehicle\",\"reason\":\"bad amount\"," +
				"\"reasonId\":\"production.reserve.invalid\"}}\n" +
				"{\"event\":\"production-budget\",\"seconds\":20,\"doctrine\":\"Attack\"," +
				"\"active\":false,\"status\":\"unmatched\",\"productionBudget\":{\"reservedCash\":900," +
				"\"ownerQueue\":\"Aircraft\",\"reason\":\"save for air\"," +
				"\"reasonId\":\"production.reserve.air\"}}\n");

			var trace = DecisionTrace.Read(path);

			Assert.Multiple(() =>
			{
				Assert.That(trace.SchemaVersion, Is.EqualTo(4));
				Assert.That(trace.ProductionBudgets.Select(record => record.Status), Is.EqualTo(new[]
				{
					"active", "inactive", "invalid", "unmatched"
				}));
				Assert.That(trace.ProductionBudgets[0].Active, Is.True);
				Assert.That(trace.ProductionBudgets[0].ProductionBudget.ReservedCash, Is.EqualTo(1200));
				Assert.That(trace.ProductionBudgets[2].ProductionBudget.OwnerQueue, Is.EqualTo("Vehicle"));
				Assert.That(trace.ProductionBudgets[3].Doctrine, Is.EqualTo("Attack"));
				Assert.That(trace.ReasonIdMentions("production.reserve.tech"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdMentions("production.reserve.invalid"), Is.EqualTo(1));
				Assert.That(trace.ReasonIdMentions("production.reserve.air"), Is.EqualTo(1));
			});
		}
	}
}
