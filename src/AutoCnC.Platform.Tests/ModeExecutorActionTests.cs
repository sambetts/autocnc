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

using AutoCnC.Core;
using AutoCnC.Platform.Traits;
using AutoCnC.Sdk;
using NUnit.Framework;

namespace AutoCnC.Platform.Tests
{
	[TestFixture]
	public sealed class ModeExecutorActionTests
	{
		[TestCase(UnitAction.RepairBuilding)]
		[TestCase(UnitAction.ActivateSupportPower)]
		public void PlayerScopedActionsAreNotRepeatedWhenTheControllerActorIsIdle(UnitAction action)
		{
			Assert.Multiple(() =>
			{
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(action, repeat: true, actorIsIdle: true),
					Is.True);
				Assert.That(ModeExecutor.IsSingleShotAction(action), Is.True);
			});
		}

		[Test]
		public void CancellationUsesPersistentIntentInsteadOfControllerIdleState()
		{
			Assert.Multiple(() =>
			{
				Assert.That(ModeExecutor.IsSingleShotAction(UnitAction.CancelProduction), Is.False);
				Assert.That(ModeExecutor.UsesPersistentIntent(UnitAction.CancelProduction), Is.True);
			});
		}

		[Test]
		public void MovementCanStillBeRetriedAfterTheActorBecomesIdle()
		{
			Assert.Multiple(() =>
			{
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.MoveTo, repeat: true, actorIsIdle: true),
					Is.False);
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.MoveTo, repeat: true, actorIsIdle: false),
					Is.True);
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.CancelProduction, repeat: false, actorIsIdle: true),
					Is.False);
			});
		}

		[Test]
		public void PlayerScopedActionsCoalesceAcrossControllers()
		{
			var repair = PlayerScopedActionKey.From(
				UnitDecision.RepairBuilding(12, "repair")).Value;
			var sameRepair = PlayerScopedActionKey.From(
				UnitDecision.RepairBuilding(12, "other reason")).Value;
			var firstPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 4, 5, "fire")).Value;
			var samePowerElsewhere = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 9, 10, "fire")).Value;
			var otherPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_5", 4, 5, "fire")).Value;

			Assert.Multiple(() =>
			{
				Assert.That(repair, Is.EqualTo(sameRepair));
				Assert.That(firstPower, Is.EqualTo(samePowerElsewhere),
					"one concrete power key can only be activated once per tick");
				Assert.That(firstPower, Is.Not.EqualTo(otherPower),
					"different concrete instances remain independently actionable");
				Assert.That(PlayerScopedActionKey.From(UnitDecision.MoveTo(1, 2, "move")), Is.Null);
			});
		}

		[Test]
		public void CancellationIntentPersistsAcrossStaggeredEvaluationLatency()
		{
			var queueItem = new object();
			var revision = new ProductionQueueRevisionState<object>(
				[new ProductionQueueEntry("mtnk", false)],
				[queueItem]);
			var pending = new PendingPlayerActions();
			var first = Cancellation(queueActorId: 20, item: "mtnk", count: 2);
			var firstPayload = ActionOrderBuilder.EncodeCancellationPayload(
				"mtnk", revision.Revision);

			pending.BeginTick(intent => intent.ExpectedQueueRevision == revision.Revision);
			var firstControllerReserved = pending.TryReserve(7, first, firstPayload);

			pending.BeginTick(intent => intent.ExpectedQueueRevision == revision.Revision);
			var staggeredControllerReserved = pending.TryReserve(7, first, firstPayload);

			revision.Advance(
				[new ProductionQueueEntry("mtnk", false)],
				[queueItem]);
			var secondPayload = ActionOrderBuilder.EncodeCancellationPayload(
				"mtnk", revision.Revision);
			pending.BeginTick(intent => intent.ExpectedQueueRevision == revision.Revision);
			var changedQueueReserved = pending.TryReserve(7, first, secondPayload);

			Assert.Multiple(() =>
			{
				Assert.That(firstControllerReserved, Is.True);
				Assert.That(staggeredControllerReserved, Is.False,
					"the in-flight intent must survive local tick boundaries");
				Assert.That(changedQueueReserved, Is.True,
					"a synchronized resolution or queue change releases the pending intent");
			});
		}

		[Test]
		public void CancellationIntentKeyIncludesPlayerQueueItemCountAndRevision()
		{
			var pending = new PendingPlayerActions();
			var baseline = Cancellation(queueActorId: 20, item: "mtnk", count: 2);
			var revisionOne = ActionOrderBuilder.EncodeCancellationPayload("mtnk", 1);
			var revisionTwo = ActionOrderBuilder.EncodeCancellationPayload("mtnk", 2);
			var otherItem = ActionOrderBuilder.EncodeCancellationPayload("e1", 1);

			pending.BeginTick(_ => true);

			Assert.Multiple(() =>
			{
				Assert.That(pending.TryReserve(7, baseline, revisionOne), Is.True);
				Assert.That(pending.TryReserve(7, baseline, revisionOne), Is.False);
				Assert.That(pending.TryReserve(8, baseline, revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(21, "mtnk", 2), revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "e1", 2), otherItem), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 1), revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 2), revisionTwo), Is.True);
			});
		}

		[Test]
		public void QueueRevisionAdvancesAcrossAbaMutation()
		{
			var originalItem = new object();
			var replacementItem = new object();
			var state = new ProductionQueueRevisionState<object>(
				[new ProductionQueueEntry("mtnk", false)],
				[originalItem]);
			var initialRevision = state.Revision;

			state.Advance([new ProductionQueueEntry("mtnk", false)], [originalItem]);
			state.Observe([new ProductionQueueEntry("e1", false)], [replacementItem]);
			state.Advance([new ProductionQueueEntry("e1", false)], [replacementItem]);
			var restoredRevision = state.Observe(
				[new ProductionQueueEntry("mtnk", false)],
				[originalItem]);

			Assert.Multiple(() =>
			{
				Assert.That(initialRevision, Is.EqualTo(1));
				Assert.That(restoredRevision, Is.EqualTo(5));
			});
		}

		[Test]
		public void QueueRevisionDoesNotDependOnTheLegacyFingerprint()
		{
			var first = new[]
			{
				new ProductionQueueEntry("iws", false),
				new ProductionQueueEntry("9975g", false),
				new ProductionQueueEntry("uu", false),
				new ProductionQueueEntry("h6edr", false),
				new ProductionQueueEntry("mtnk", false)
			};
			var collision = new[]
			{
				new ProductionQueueEntry("gmvq", false),
				new ProductionQueueEntry("mtnk", false)
			};
			var state = new ProductionQueueRevisionState<object>(
				first, [new object(), new object(), new object(), new object(), new object()]);

			var revision = state.Observe(collision, [new object(), new object()]);

			Assert.Multiple(() =>
			{
				Assert.That(LegacyQueueFingerprint(first), Is.EqualTo(0x8D6B64F6u));
				Assert.That(LegacyQueueFingerprint(collision), Is.EqualTo(0x8D6B64F6u));
				Assert.That(revision, Is.EqualTo(2));
			});
		}

		[Test]
		public void ExactCancellationRejectsPartialResolution()
		{
			var queued = new[] { "e1", "mtnk" };

			Assert.Multiple(() =>
			{
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "MTNK", 1),
					Is.EqualTo("mtnk"));
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", 2),
					Is.Null);
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", 0),
					Is.Null);
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", uint.MaxValue),
					Is.Null);
			});
		}

		static UnitDecision Cancellation(uint queueActorId, string item, int count) =>
			UnitDecision.CancelProduction("Vehicle", item, count, "cancel") with
			{
				TargetActorId = queueActorId
			};

		static uint LegacyQueueFingerprint(ProductionQueueEntry[] entries)
		{
			const uint Offset = 2166136261;
			const uint Prime = 16777619;

			var hash = Offset;
			var count = 0u;
			foreach (var entry in entries)
			{
				hash = (hash ^ 0xFFu) * Prime;
				foreach (var character in entry.Item)
					hash = (hash ^ character) * Prime;

				hash = (hash ^ (entry.Infinite ? 1u : 0u)) * Prime;
				count++;
			}

			hash = (hash ^ count) * Prime;
			return hash == 0 ? 1u : hash;
		}
	}
}
