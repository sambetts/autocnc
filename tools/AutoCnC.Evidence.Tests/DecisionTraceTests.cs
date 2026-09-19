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
				"\"harvesters\":1,\"refineries\":1,\"armyValue\":900,\"buildings\":3," +
				"\"creditsKilled\":500,\"creditsLost\":200,\"incomeEarned\":750," +
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
	}
}
