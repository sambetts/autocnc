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
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Resolves synchronized player-scoped orders emitted by the AutoC&C SDK.")]
	public sealed class SdkActionResolverInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new SdkActionResolver(); }
	}

	public sealed class SdkActionResolver : IResolveOrder
	{
		public void ResolveOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case ActionOrderBuilder.EnsureRepairOrder:
					ResolveRepairBuilding(self, order);
					break;

				case ActionOrderBuilder.ExactCancelProductionOrder:
					ResolveCancelProduction(self, order);
					break;
			}
		}

		static void ResolveRepairBuilding(Actor playerActor, Order order)
		{
			if (order.Target.Type != TargetType.Actor)
				return;

			var building = order.Target.Actor;
			if (building.Owner != playerActor.Owner || building.IsDead || !building.IsInWorld ||
				!building.Info.HasTraitInfo<BuildingInfo>())
				return;

			var health = building.TraitOrDefault<IHealth>();
			var repairable = building.TraitOrDefault<RepairableBuilding>();
			if (health == null || health.MaxHP <= 0 || health.HP >= health.MaxHP ||
				repairable == null || repairable.IsTraitDisabled ||
				repairable.RepairActive || repairable.Repairers.Contains(playerActor.Owner))
				return;

			playerActor.ResolveOrder(ActionOrderBuilder.RepairBuilding(
				playerActor, Target.FromActor(building)));
		}

		static void ResolveCancelProduction(Actor playerActor, Order order)
		{
			if (order.Target.Type != TargetType.Actor ||
				order.ExtraData == 0 ||
				!ActionOrderBuilder.TryDecodeCancellationPayload(
					order.TargetString, out var requestedItem, out var count))
				return;

			var queueActor = order.Target.Actor;
			if (queueActor.Owner != playerActor.Owner || queueActor.IsDead || !queueActor.IsInWorld)
				return;

			var queues = queueActor.TraitsImplementing<ProductionQueue>().ToArray();
			var queueIndex = order.ExtraLocation.X;
			if (queueIndex < 0 || queueIndex >= queues.Length)
				return;

			var queue = queues[queueIndex];
			if (!queue.Enabled)
				return;

			var queued = queue.AllQueued().ToArray();
			var expectedVersion = order.ExtraData;
			var currentVersion = ActionOrderBuilder.ProductionQueueVersion(
				queued.Select(item => new ProductionQueueEntry(item.Item, item.Infinite)));
			if (currentVersion != expectedVersion)
				return;

			var item = FindExactCancellationItem(
				queued.Select(queuedItem => queuedItem.Item),
				requestedItem,
				(uint)count);
			if (item == null)
				return;

			queue.ResolveOrder(
				queueActor,
				ActionOrderBuilder.CancelProduction(queueActor, item, count));
		}

		internal static string FindExactCancellationItem(
			System.Collections.Generic.IEnumerable<string> queuedItems,
			string requestedItem,
			uint requestedCount)
		{
			if (requestedCount == 0 || requestedCount > int.MaxValue)
				return null;

			return ActionOrderBuilder.FindQueuedItem(
				queuedItems, requestedItem, (int)requestedCount);
		}
	}
}
