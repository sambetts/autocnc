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
			var pending = new PendingPlayerActions();
			var first = Cancellation(queueActorId: 20, item: "mtnk", count: 2);

			pending.BeginTick(_ => true);
			var firstControllerReserved = pending.TryReserve(7, first, "snapshot-a");

			pending.BeginTick(_ => true);
			var staggeredControllerReserved = pending.TryReserve(7, first, "snapshot-a");

			pending.BeginTick(_ => false);
			var changedQueueReserved = pending.TryReserve(7, first, "snapshot-b");

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
		public void CancellationIntentKeyIncludesPlayerQueueItemCountAndSnapshot()
		{
			var pending = new PendingPlayerActions();
			var baseline = Cancellation(queueActorId: 20, item: "mtnk", count: 2);

			pending.BeginTick(_ => true);

			Assert.Multiple(() =>
			{
				Assert.That(pending.TryReserve(7, baseline, "snapshot-a"), Is.True);
				Assert.That(pending.TryReserve(7, baseline, "snapshot-a"), Is.False);
				Assert.That(pending.TryReserve(8, baseline, "snapshot-a"), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(21, "mtnk", 2), "snapshot-a"), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "e1", 2), "snapshot-a"), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 1), "snapshot-a"), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 2), "snapshot-b"), Is.True);
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
	}
}
