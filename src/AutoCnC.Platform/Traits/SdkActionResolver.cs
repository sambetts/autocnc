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
using System.Collections.Generic;
using System.Linq;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Network;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	internal sealed class ProductionQueueRevisionState<TItem>
		where TItem : class
	{
		ProductionQueueEntry[] entries;
		TItem[] items;

		public ulong Revision { get; private set; } = 1;

		public ProductionQueueRevisionState(
			ProductionQueueEntry[] entries, TItem[] items)
		{
			this.entries = entries;
			this.items = items;
		}

		public ulong Observe(ProductionQueueEntry[] currentEntries, TItem[] currentItems)
		{
			if (!Matches(currentEntries, currentItems))
				Advance(currentEntries, currentItems);

			return Revision;
		}

		public void Advance(ProductionQueueEntry[] currentEntries, TItem[] currentItems)
		{
			Revision++;
			if (Revision == 0)
				Revision = 1;

			entries = currentEntries;
			items = currentItems;
		}

		bool Matches(ProductionQueueEntry[] currentEntries, TItem[] currentItems)
		{
			if (items.Length != currentItems.Length)
				return false;

			for (var i = 0; i < items.Length; i++)
				if (!ReferenceEquals(items[i], currentItems[i]) ||
					entries[i] != currentEntries[i])
					return false;

			return true;
		}
	}

	internal readonly record struct RepairStateSnapshot(
		int HitPoints,
		int MaxHitPoints,
		bool IsRepairable,
		bool RepairRequested,
		bool RepairActive);

	internal sealed class RepairStateRevision
	{
		RepairStateSnapshot snapshot;

		public ulong Revision { get; private set; } = 1;

		public RepairStateRevision(in RepairStateSnapshot snapshot)
		{
			this.snapshot = snapshot;
		}

		public ulong Observe(in RepairStateSnapshot current)
		{
			if (current != snapshot)
				Advance(current);

			return Revision;
		}

		public void Advance(in RepairStateSnapshot current)
		{
			Revision++;
			if (Revision == 0)
				Revision = 1;

			snapshot = current;
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Resolves synchronized player-scoped orders emitted by the AutoC&C SDK.")]
	public sealed class SdkActionResolverInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new SdkActionResolver(init); }
	}

	sealed class SdkActionResolver : IResolveOrder, ITick, INotifyCreated, ISync,
		IProductionQueueRevisionProvider, IRepairStateRevisionProvider
	{
		readonly Actor self;
		readonly World world;
		readonly Dictionary<ProductionQueue, ProductionQueueRevisionState<ProductionItem>> queues = [];
		readonly Dictionary<Actor, RepairStateRevision> repairs = [];

		[VerifySync]
		public int RevisionsHash
		{
			get
			{
				unchecked
				{
					var hash = 17;
					foreach (var actor in queues.Keys
						.Select(queue => queue.Actor)
						.Distinct()
						.OrderBy(actor => actor.ActorID))
					{
						hash = hash * 31 + (int)actor.ActorID;
						foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
							if (queues.TryGetValue(queue, out var state))
							{
								hash = hash * 31 + (int)state.Revision;
								hash = hash * 31 + (int)(state.Revision >> 32);
							}
					}

					foreach (var pair in repairs.OrderBy(pair => pair.Key.ActorID))
					{
						hash = hash * 31 + (int)pair.Key.ActorID;
						hash = hash * 31 + (int)pair.Value.Revision;
						hash = hash * 31 + (int)(pair.Value.Revision >> 32);
					}

					return hash;
				}
			}
		}

		public SdkActionResolver(ActorInitializer init)
		{
			self = init.Self;
			world = self.World;
			world.ActorAdded += ActorAdded;
			world.ActorRemoved += ActorRemoved;
		}

		void INotifyCreated.Created(Actor actor) => RefreshQueues();

		void ITick.Tick(Actor actor) => RefreshQueues();

		void ActorAdded(Actor actor)
		{
			if (actor.Owner != self.Owner)
				return;

			foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				EnsureQueue(queue);

			if (actor.TraitOrDefault<RepairableBuilding>() != null)
				EnsureRepair(actor);
		}

		void ActorRemoved(Actor actor)
		{
			foreach (var queue in actor.TraitsImplementing<ProductionQueue>())
				queues.Remove(queue);

			repairs.Remove(actor);
		}

		internal void RefreshQueues()
		{
			foreach (var queue in queues.Keys
				.Where(queue => queue.Actor.Owner != self.Owner ||
					queue.Actor.IsDead || !queue.Actor.IsInWorld)
				.ToArray())
				queues.Remove(queue);

			foreach (var pair in world.ActorsWithTrait<ProductionQueue>())
				if (pair.Actor.Owner == self.Owner && !pair.Actor.IsDead && pair.Actor.IsInWorld)
					RefreshQueue(pair.Trait);

			foreach (var actor in repairs.Keys
				.Where(actor => actor.Owner != self.Owner || actor.IsDead || !actor.IsInWorld)
				.ToArray())
				repairs.Remove(actor);

			foreach (var pair in world.ActorsWithTrait<RepairableBuilding>())
				if (pair.Actor.Owner == self.Owner && !pair.Actor.IsDead && pair.Actor.IsInWorld)
					RefreshRepair(pair.Actor);
		}

		ProductionQueueRevisionState<ProductionItem> EnsureQueue(ProductionQueue queue)
		{
			if (queues.TryGetValue(queue, out var state))
				return state;

			var snapshot = Snapshot(queue);
			state = new ProductionQueueRevisionState<ProductionItem>(
				snapshot.Entries, snapshot.Items);
			queues.Add(queue, state);
			return state;
		}

		ulong RefreshQueue(ProductionQueue queue)
		{
			var state = EnsureQueue(queue);
			var snapshot = Snapshot(queue);
			return state.Observe(snapshot.Entries, snapshot.Items);
		}

		void AdvanceQueue(ProductionQueue queue)
		{
			var state = EnsureQueue(queue);
			var snapshot = Snapshot(queue);
			state.Advance(snapshot.Entries, snapshot.Items);
		}

		static (ProductionQueueEntry[] Entries, ProductionItem[] Items) Snapshot(
			ProductionQueue queue)
		{
			var items = queue.AllQueued().ToArray();
			var entries = items
				.Select(item => new ProductionQueueEntry(item.Item, item.Infinite))
				.ToArray();
			return (entries, items);
		}

		RepairStateRevision EnsureRepair(Actor building)
		{
			if (repairs.TryGetValue(building, out var state))
				return state;

			state = new RepairStateRevision(RepairSnapshot(building));
			repairs.Add(building, state);
			return state;
		}

		ulong RefreshRepair(Actor building)
		{
			var state = EnsureRepair(building);
			return state.Observe(RepairSnapshot(building));
		}

		void AdvanceRepair(Actor building)
		{
			var state = EnsureRepair(building);
			state.Advance(RepairSnapshot(building));
		}

		RepairStateSnapshot RepairSnapshot(Actor building)
		{
			var health = building.TraitOrDefault<IHealth>();
			var repairable = building.TraitOrDefault<RepairableBuilding>();
			var isRepairable = repairable != null && !repairable.IsTraitDisabled;
			return new RepairStateSnapshot(
				HitPoints: health?.HP ?? 0,
				MaxHitPoints: health?.MaxHP ?? 0,
				IsRepairable: isRepairable,
				RepairRequested: isRepairable && repairable.Repairers.Contains(self.Owner),
				RepairActive: isRepairable && repairable.RepairActive);
		}

		bool IProductionQueueRevisionProvider.TryGetRevision(
			ProductionQueue queue, out ulong revision)
		{
			if (queue != null && queues.TryGetValue(queue, out var state))
			{
				revision = state.Revision;
				return true;
			}

			revision = 0;
			return false;
		}

		bool IRepairStateRevisionProvider.TryGetRevision(
			Actor building, out ulong revision)
		{
			if (building != null && repairs.TryGetValue(building, out var state))
			{
				revision = state.Revision;
				return true;
			}

			revision = 0;
			return false;
		}

		internal void ObservePotentialMutation(Actor queueActor)
		{
			if (queueActor == null || queueActor.Owner != self.Owner)
				return;

			foreach (var queue in queueActor.TraitsImplementing<ProductionQueue>())
			{
				RefreshQueue(queue);
				AdvanceQueue(queue);
			}
		}

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
			if (order.Target.Type != TargetType.Actor ||
				!ActionOrderBuilder.TryDecodeRepairPayload(
					order.TargetString, out var expectedRevision))
				return;

			var building = order.Target.Actor;
			if (building.Owner != playerActor.Owner || building.IsDead || !building.IsInWorld ||
				!building.Info.HasTraitInfo<BuildingInfo>())
				return;

			var resolver = playerActor.Trait<SdkActionResolver>();
			if (resolver.RefreshRepair(building) != expectedRevision)
				return;

			var health = building.TraitOrDefault<IHealth>();
			var repairable = building.TraitOrDefault<RepairableBuilding>();
			if (health != null && health.MaxHP > 0 && health.HP < health.MaxHP &&
				repairable != null && !repairable.IsTraitDisabled &&
				!repairable.RepairActive && !repairable.Repairers.Contains(playerActor.Owner))
				playerActor.ResolveOrder(ActionOrderBuilder.RepairBuilding(
					playerActor, Target.FromActor(building)));

			resolver.AdvanceRepair(building);
		}

		static void ResolveCancelProduction(Actor playerActor, Order order)
		{
			if (order.Target.Type != TargetType.Actor ||
				order.ExtraData == 0 ||
				order.ExtraData > int.MaxValue ||
				!ActionOrderBuilder.TryDecodeCancellationPayload(
					order.TargetString, out var requestedItem, out var expectedRevision))
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

			var resolver = playerActor.Trait<SdkActionResolver>();
			if (resolver.RefreshQueue(queue) != expectedRevision)
				return;

			var queued = queue.AllQueued().ToArray();
			var count = (int)order.ExtraData;
			var item = FindExactCancellationItem(
				queued.Select(queuedItem => queuedItem.Item),
				requestedItem,
				order.ExtraData);
			if (item != null)
				queue.ResolveOrder(
					queueActor,
					ActionOrderBuilder.CancelProduction(queueActor, item, count));

			resolver.AdvanceQueue(queue);
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

	[TraitLocation(SystemActors.World)]
	[Desc("Advances AutoC&C queue revisions before synchronized orders can mutate production.")]
	public sealed class SdkQueueOrderObserverInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new SdkQueueOrderObserver(); }
	}

	public sealed class SdkQueueOrderObserver : IValidateOrder, ITick
	{
		void ITick.Tick(Actor self)
		{
			foreach (var pair in self.World.ActorsWithTrait<SdkActionResolver>())
				pair.Trait.RefreshQueues();
		}

		public bool OrderValidation(
			OrderManager orderManager, World world, int clientId, Order order)
		{
			switch (order.OrderString)
			{
				case "StartProduction":
				case "CancelProduction":
				case "ReturnOrder":
				case "PurchaseOrder":
					Observe(order.Subject);
					break;

				case "PlaceBuilding":
				case "LineBuild":
				case "PlacePlug":
					Observe(world?.GetActorById(order.ExtraData));
					break;
			}

			return true;
		}

		internal static bool Observe(Actor queueActor)
		{
			if (queueActor == null)
				return false;

			var owner = queueActor.Owner;
			if (owner == null)
				return false;

			var playerActor = owner.PlayerActor;
			if (playerActor == null)
				return false;

			var hasProductionQueue = queueActor.TraitsImplementing<ProductionQueue>().Any();
			if (!IsEligibleMutationTarget(
				hasOwner: true, hasPlayerActor: true, hasProductionQueue))
				return false;

			var resolver = playerActor.TraitOrDefault<SdkActionResolver>();
			if (resolver == null)
				return false;

			resolver.ObservePotentialMutation(queueActor);
			return true;
		}

		internal static bool IsEligibleMutationTarget(
			bool hasOwner, bool hasPlayerActor, bool hasProductionQueue) =>
			hasOwner && hasPlayerActor && hasProductionQueue;
	}
}
