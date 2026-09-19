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
using NUnit.Framework;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Sdk.Tests
{
	[TestFixture]
	public sealed class ActionOrderBuilderTests
	{
		[Test]
		public void RepairRequestUsesAPlayerScopedSynchronizedOrder()
		{
			var target = Target.FromPos(new WPos(1024, 2048, 0));
			var order = ActionOrderBuilder.RequestRepairBuilding(null, target);

			Assert.Multiple(() =>
			{
				Assert.That(order.OrderString, Is.EqualTo(ActionOrderBuilder.EnsureRepairOrder));
				Assert.That(order.Subject, Is.Null);
				Assert.That(order.Queued, Is.False);
				Assert.That(order.Target.Type, Is.EqualTo(TargetType.Terrain));
				Assert.That(order.Target.CenterPosition, Is.EqualTo(target.CenterPosition));
				Assert.That(order.SuppressVisualFeedback, Is.True);
			});
		}

		[Test]
		public void RepairResolutionUsesTheActualOpenRaPlayerOrder()
		{
			var target = Target.FromPos(new WPos(1024, 2048, 0));
			var order = ActionOrderBuilder.RepairBuilding(null, target);

			Assert.Multiple(() =>
			{
				Assert.That(order.OrderString, Is.EqualTo("RepairBuilding"));
				Assert.That(order.Subject, Is.Null);
				Assert.That(order.Queued, Is.False);
				Assert.That(order.Target.Type, Is.EqualTo(TargetType.Terrain));
				Assert.That(order.Target.CenterPosition, Is.EqualTo(target.CenterPosition));
			});
		}

		[Test]
		public void CancellationRequestIdentifiesQueueItemAndCount()
		{
			var target = Target.FromPos(new WPos(3072, 4096, 0));
			var order = ActionOrderBuilder.RequestCancelProduction(
				null, target, queueIndex: 3, item: "mtnk", count: 2);

			Assert.Multiple(() =>
			{
				Assert.That(order.OrderString, Is.EqualTo(ActionOrderBuilder.ExactCancelProductionOrder));
				Assert.That(order.Subject, Is.Null);
				Assert.That(order.Target.Type, Is.EqualTo(TargetType.Terrain));
				Assert.That(order.Target.CenterPosition, Is.EqualTo(target.CenterPosition));
				Assert.That(order.TargetString, Is.EqualTo("mtnk"));
				Assert.That(order.ExtraLocation.X, Is.EqualTo(3));
				Assert.That(order.ExtraData, Is.EqualTo(2u));
				Assert.That(order.Queued, Is.False);
				Assert.That(order.SuppressVisualFeedback, Is.True);
			});
		}

		[Test]
		public void CancellationResolutionUsesTheActualProductionQueueOrderShape()
		{
			var order = ActionOrderBuilder.CancelProduction(null, "mtnk", 2);

			Assert.Multiple(() =>
			{
				Assert.That(order.OrderString, Is.EqualTo("CancelProduction"));
				Assert.That(order.Subject, Is.Null);
				Assert.That(order.TargetString, Is.EqualTo("mtnk"));
				Assert.That(order.ExtraData, Is.EqualTo(2u));
				Assert.That(order.Queued, Is.False);
			});
		}

		[Test]
		public void CancellationRequiresTheRequestedNumberOfExactQueueItems()
		{
			var queued = new[] { "e1", "mtnk", "MTNK" };

			Assert.Multiple(() =>
			{
				Assert.That(ActionOrderBuilder.FindQueuedItem(queued, "MTnk", 2), Is.EqualTo("mtnk"));
				Assert.That(ActionOrderBuilder.FindQueuedItem(queued, "mtnk", 3), Is.Null);
				Assert.That(ActionOrderBuilder.FindQueuedItem(queued, "mtnk", 0), Is.Null);
				Assert.That(ActionOrderBuilder.FindQueuedItem(queued, "arty", 1), Is.Null);
			});
		}

		[Test]
		public void CancellationResolutionCanonicalizesTheQueueActorAndItem()
		{
			var requested = UnitDecision.CancelProduction("Vehicle.GDI", "MTNK", 2, "cancel");
			var resolved = ActionOrderBuilder.ResolveCancellation(
				requested, 41, "Vehicle", new[] { "mtnk", "mtnk" }).Value;

			Assert.Multiple(() =>
			{
				Assert.That(resolved.TargetActorId, Is.EqualTo(41u));
				Assert.That(resolved.Queue, Is.EqualTo("Vehicle"));
				Assert.That(resolved.ItemName, Is.EqualTo("mtnk"));
				Assert.That(resolved.Count, Is.EqualTo(2));
			});
		}

		[Test]
		public void RepairRequiresDamageCapabilityAndNoExistingRequest()
		{
			var damaged = Building(75, repairable: true, requested: false);

			Assert.Multiple(() =>
			{
				Assert.That(ActionOrderBuilder.CanStartRepair(damaged), Is.True);
				Assert.That(ActionOrderBuilder.CanStartRepair(
					Building(100, repairable: true, requested: false)), Is.False);
				Assert.That(ActionOrderBuilder.CanStartRepair(
					Building(75, repairable: false, requested: false)), Is.False);
				Assert.That(ActionOrderBuilder.CanStartRepair(
					Building(75, repairable: true, requested: true)), Is.False);
				Assert.That(ActionOrderBuilder.CanStartRepair(
					Building(75, repairable: true, requested: false, active: true)), Is.False);
			});
		}

		[Test]
		public void SupportPowerResolutionPrefersExactKeysAndRequiresReadyActiveState()
		{
			var powers = new[]
			{
				Power("AirstrikeOrder_9", "AirstrikeOrder", active: false, ready: false),
				Power("AirstrikeOrder_3", "AirstrikeOrder", active: true, ready: true),
				Power("AirstrikeOrder_5", "AirstrikeOrder", active: true, ready: true),
				Power("IonOrder", "IonOrder", active: true, ready: true)
			};

			Assert.Multiple(() =>
			{
				Assert.That(ActionOrderBuilder.FindReadySupportPower(powers, "ionorder"),
					Is.EqualTo("IonOrder"));
				Assert.That(ActionOrderBuilder.FindReadySupportPower(powers, "AirstrikeOrder"),
					Is.EqualTo("AirstrikeOrder_3"),
					"order-name fallback chooses one ready instance deterministically");
				Assert.That(ActionOrderBuilder.FindReadySupportPower(powers, "AirstrikeOrder_9"),
					Is.Null,
					"an explicitly named unavailable key must not silently activate a sibling");
				Assert.That(ActionOrderBuilder.FindReadySupportPower(powers, "unknown"), Is.Null);
			});
		}

		[Test]
		public void ConcreteSupportPowerKeysProduceDistinctSuccessiveIntents()
		{
			var requested = UnitDecision.ActivateSupportPower("AirstrikeOrder", 4, 5, "fire");
			var firstPowers = new[]
			{
				Power("AirstrikeOrder_3", "AirstrikeOrder", active: true, ready: true),
				Power("AirstrikeOrder_5", "AirstrikeOrder", active: true, ready: true)
			};
			var secondPowers = new[]
			{
				Power("AirstrikeOrder_3", "AirstrikeOrder", active: true, ready: false),
				Power("AirstrikeOrder_5", "AirstrikeOrder", active: true, ready: true)
			};

			var first = ActionOrderBuilder.ResolveSupportPower(requested, firstPowers).Value;
			var second = ActionOrderBuilder.ResolveSupportPower(requested, secondPowers).Value;

			Assert.Multiple(() =>
			{
				Assert.That(first.Power, Is.EqualTo("AirstrikeOrder_3"));
				Assert.That(second.Power, Is.EqualTo("AirstrikeOrder_5"));
				Assert.That(first.SameIntent(second), Is.False);
			});
		}

		[Test]
		public void SupportPowerOrderUsesTheResolvedKeyAndTarget()
		{
			var target = Target.FromPos(new WPos(3072, 4096, 0));
			var order = ActionOrderBuilder.ActivateSupportPower(null, "AirstrikeOrder_3", target);

			Assert.Multiple(() =>
			{
				Assert.That(order.OrderString, Is.EqualTo("AirstrikeOrder_3"));
				Assert.That(order.Subject, Is.Null);
				Assert.That(order.Target.Type, Is.EqualTo(TargetType.Terrain));
				Assert.That(order.Target.CenterPosition, Is.EqualTo(target.CenterPosition));
				Assert.That(order.ExtraData, Is.EqualTo(uint.MaxValue));
				Assert.That(order.SuppressVisualFeedback, Is.True);
			});
		}

		[TestCase(100, 100, false, 0)]
		[TestCase(100, 75, false, 25)]
		[TestCase(3, 1, false, 66)]
		[TestCase(100, 1, true, 100)]
		[TestCase(0, 0, false, 0)]
		public void ProductionProgressIsStableAndBounded(
			int totalTime, int remainingTime, bool done, int expected) =>
			Assert.That(ActionOrderBuilder.ProgressPercent(totalTime, remainingTime, done), Is.EqualTo(expected));

		static OwnedBuildingState Building(
			int health, bool repairable, bool requested, bool active = false) =>
			new(1, "proc", 2, 3, health, repairable, requested, active);

		static SupportPowerState Power(string key, string orderName, bool active, bool ready) =>
			new(key, orderName, active, ready, false, ready ? 0 : 10, 100);
	}
}
