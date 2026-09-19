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
using AutoCnC.Core;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace AutoCnC.Sdk
{
	/// <summary>
	/// Everything a mode uses to read the world and command its unit.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Modes execute <b>outside the lockstep simulation</b>, on the owning player's client only,
	/// and their decisions leave as <see cref="Order"/>s — exactly like a human's clicks. So the
	/// simulation's determinism rules do not apply to your code: floating point, LINQ,
	/// <c>System.Random</c> and wall-clock time are all safe here.
	/// </para>
	/// <para>One instance is created per unit and reused for that unit's lifetime.</para>
	/// </remarks>
	public sealed class ModeContext
	{
		readonly IUnitState state;
		readonly IModeHost host;
		readonly Actor self;
		readonly AttackBase[] attackBases;
		readonly IHealth health;
		readonly IMove move;
		readonly IResourceLayer resourceLayer;
		readonly HarvesterInfo harvesterInfo;
		readonly List<ThreatSnapshot> threatBuffer = [];

		public World World { get; }

		/// <summary>The actor this mode is driving.</summary>
		public Actor Self => self;

		public Player Owner => self.Owner;

		/// <summary>A random source. <c>System.Random</c> is equally safe here.</summary>
		public MersenneTwister Random => World.SharedRandom;

		public int WorldTick => World.WorldTick;

		/// <summary>Control group this unit belongs to (1-9), or 0 if unassigned.</summary>
		public int GroupId => state.GroupId;

		/// <summary>Name of the mode currently running on this unit.</summary>
		public string ModeName => state.ActiveModeName;

		/// <summary>The position this unit treats as home.</summary>
		public CPos Anchor
		{
			get => state.Anchor;
			set => state.Anchor = value;
		}

		/// <summary>The loaded doctrine's base construction plan.</summary>
		public IReadOnlyList<BuildStep> BuildPlan => host.BuildPlan;

		/// <summary>The loaded doctrine's unit production plan.</summary>
		public IReadOnlyList<ProductionStep> ProductionPlan => host.ProductionPlan;

		/// <summary>The doctrine this mode is part of.</summary>
		public string Doctrine => host.ActiveDoctrine;

		/// <summary>
		/// The other doctrines the bot could be running instead, this one included.
		/// </summary>
		/// <remarks>
		/// A doctrine is a plan among siblings rather than the whole of a strategy, and sometimes
		/// the unit on the ground is the first to know the plan is finished — a scout that has
		/// found the enemy base has answered the only question its doctrine existed to ask.
		/// </remarks>
		public IReadOnlyList<string> Doctrines => host.DoctrineNames;

		/// <summary>
		/// Ask the bot to change doctrine, and say why.
		/// </summary>
		/// <remarks>
		/// A request, not a command: it is applied at the next assessment and still subject to the
		/// bot's minimum dwell time, so calling this every tick is harmless. The reason reaches
		/// the battle log, where it sits next to what happened afterwards.
		/// </remarks>
		public void SwitchDoctrine(string doctrine, string reason) => host.RequestDoctrine(doctrine, reason);

		/// <summary>
		/// Ask the bot to change doctrine with a stable identifier for evidence queries.
		/// </summary>
		public void SwitchDoctrine(string doctrine, string reason, string reasonId) =>
			host.RequestDoctrine(doctrine, reason, reasonId);

		public bool CanMove => move != null;
		public bool HasWeapon => attackBases.Length > 0;
		public bool IsIdle => self.IsIdle;

		public ModeContext(IUnitState state, IModeHost host, Actor self)
		{
			this.state = state;
			this.host = host;
			this.self = self;
			World = self.World;

			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
			health = self.TraitOrDefault<IHealth>();
			move = self.TraitOrDefault<IMove>();
			resourceLayer = World.WorldActor.TraitOrDefault<IResourceLayer>();
			harvesterInfo = self.Info.TraitInfoOrDefault<HarvesterInfo>();
		}

		/// <summary>
		/// Evaluate this unit next tick and re-send its order even if unchanged. Useful from
		/// <see cref="IUnitMode.OnDamaged"/>, which fires between evaluations.
		/// </summary>
		public void RequestReevaluation() => state.RequestReevaluation();

		IEnumerable<AttackBase> ActiveAttackBases =>
			attackBases.Where(ab => !ab.IsTraitDisabled && !ab.IsTraitPaused);

		#region Sensing

		/// <summary>Current health as an integer percentage (0-100).</summary>
		public int HealthPercent
		{
			get
			{
				if (health == null || health.MaxHP <= 0)
					return 100;

				return health.HP * 100 / health.MaxHP;
			}
		}

		/// <summary>Longest maximum weapon range across all enabled armaments, in world units.</summary>
		public int WeaponRangeUnits
		{
			get
			{
				var best = 0;
				foreach (var ab in ActiveAttackBases)
				{
					var range = ab.GetMaximumRange().Length;
					if (range > best)
						best = range;
				}

				return best;
			}
		}

		public int DistanceFromAnchorUnits =>
			(self.CenterPosition - World.Map.CenterOfCell(state.Anchor)).HorizontalLength;

		public int DistanceTo(Actor other) =>
			other == null ? int.MaxValue : (other.CenterPosition - self.CenterPosition).HorizontalLength;

		public int DistanceTo(CPos cell) =>
			(World.Map.CenterOfCell(cell) - self.CenterPosition).HorizontalLength;

		/// <summary>
		/// Visible enemies within <paramref name="radius"/> as engine-free snapshots.
		/// The list is a reused buffer — consume it within the tick, do not retain it.
		/// </summary>
		public IReadOnlyList<ThreatSnapshot> SenseThreats(WDist radius)
		{
			threatBuffer.Clear();

			foreach (var actor in World.FindActorsInCircle(self.CenterPosition, radius))
			{
				if (!IsHostileAndVisible(actor))
					continue;

				threatBuffer.Add(Snapshot(actor));
			}

			threatBuffer.Sort(static (a, b) => a.ActorId.CompareTo(b.ActorId));
			return threatBuffer;
		}

		/// <summary>Visible enemy structures within <paramref name="radius"/>.</summary>
		public IReadOnlyList<ThreatSnapshot> SenseStructures(WDist radius)
		{
			var results = new List<ThreatSnapshot>();

			foreach (var actor in World.FindActorsInCircle(self.CenterPosition, radius))
			{
				if (!IsHostileAndVisible(actor) || !actor.Info.HasTraitInfo<BuildingInfo>())
					continue;

				results.Add(Snapshot(actor));
			}

			results.Sort(static (a, b) => a.ActorId.CompareTo(b.ActorId));
			return results;
		}

		/// <summary>Allied actors within <paramref name="radius"/>, optionally filtered by type.</summary>
		public IEnumerable<Actor> SenseAllies(WDist radius, string actorType = null)
		{
			return World.FindActorsInCircle(self.CenterPosition, radius)
				.Where(a => a != self && !a.IsDead && a.IsInWorld && a.Owner.IsAlliedWith(self.Owner)
					&& (actorType == null || string.Equals(a.Info.Name, actorType, StringComparison.OrdinalIgnoreCase)));
		}

		public ThreatSnapshot Snapshot(Actor actor)
		{
			var actorHealth = actor.TraitOrDefault<IHealth>();
			var hp = actorHealth == null || actorHealth.MaxHP <= 0 ? 100 : actorHealth.HP * 100 / actorHealth.MaxHP;

			return new ThreatSnapshot(
				ActorId: actor.ActorID,
				DistanceUnits: (actor.CenterPosition - self.CenterPosition).HorizontalLength,
				HealthPercent: hp,
				Kind: Classify(actor),
				IsAttackable: CanAttack(actor),
				CanHitUs: CanHitUs(actor))
			{
				ActorType = actor.Info.Name,
				CellX = actor.Location.X,
				CellY = actor.Location.Y,
				Value = actor.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0,
				WeaponRangeUnits = MaximumWeaponRange(actor)
			};
		}

		bool IsHostileAndVisible(Actor actor) => actor != self && IsVisibleEnemy(self.Owner, actor);

		/// <summary>
		/// True if <paramref name="actor"/> is an enemy of <paramref name="viewer"/> that the
		/// viewer can see right now.
		/// </summary>
		/// <remarks>
		/// The single definition of "an enemy your code knows about". Fog and cloaking are
		/// respected here, so a mode cannot cheat by seeing through the shroud — and the battle
		/// log records a sighting at exactly the moment a mode could first have reacted to it,
		/// rather than at some slightly different moment its own copy of this rule decided on.
		/// </remarks>
		public static bool IsVisibleEnemy(Player viewer, Actor actor)
		{
			if (viewer == null || actor == null || actor.IsDead || !actor.IsInWorld)
				return false;

			if (viewer.RelationshipWith(actor.Owner) != PlayerRelationship.Enemy)
				return false;

			if (actor.Info.HasTraitInfo<HuskInfo>())
				return false;

			if (actor.GetEnabledTargetTypes().IsEmpty)
				return false;

			return actor.CanBeViewedByPlayer(viewer);
		}

		/// <summary>True if any of this unit's enabled armaments can engage the target.</summary>
		public bool CanAttack(Actor target)
		{
			var t = Target.FromActor(target);
			foreach (var ab in ActiveAttackBases)
				if (ab.HasAnyValidWeapons(t))
					return true;

			return false;
		}

		bool CanHitUs(Actor actor)
		{
			var us = Target.FromActor(self);
			foreach (var ab in actor.TraitsImplementing<AttackBase>())
				if (!ab.IsTraitDisabled && ab.HasAnyValidWeapons(us))
					return true;

			return false;
		}

		static int MaximumWeaponRange(Actor actor)
		{
			var best = 0;
			foreach (var ab in actor.TraitsImplementing<AttackBase>())
			{
				if (ab.IsTraitDisabled || ab.IsTraitPaused)
					continue;

				var range = ab.GetMaximumRange().Length;
				if (range > best)
					best = range;
			}

			return best;
		}

		/// <summary>What kind of thing an actor is, as a mode sees it.</summary>
		public static ThreatKind Classify(Actor actor)
		{
			var info = actor.Info;

			if (info.HasTraitInfo<AircraftInfo>())
				return ThreatKind.Aircraft;

			if (info.HasTraitInfo<BuildingInfo>())
			{
				if (info.HasTraitInfo<AttackBaseInfo>())
					return ThreatKind.Defence;

				if (info.HasTraitInfo<RefineryInfo>())
					return ThreatKind.Economy;

				return ThreatKind.Structure;
			}

			if (info.HasTraitInfo<HarvesterInfo>())
				return ThreatKind.Economy;

			if (info.HasTraitInfo<MobileInfo>())
				return actor.GetEnabledTargetTypes().Contains("Infantry")
					? ThreatKind.Infantry
					: ThreatKind.Vehicle;

			return ThreatKind.Unknown;
		}

		/// <summary>Resolves an ActorID back into a live actor, or null.</summary>
		public Actor ResolveActor(uint actorId)
		{
			if (actorId == 0)
				return null;

			var actor = World.GetActorById(actorId);
			return actor != null && actor.IsInWorld && !actor.IsDead ? actor : null;
		}

		public Actor FindRepairBay() => FindNearestAllied<RepairsUnits>();

		public Actor FindRefinery() => FindNearestAllied<Refinery>();

		/// <summary>
		/// Every live building this player owns, including its health and building-repair state.
		/// </summary>
		public IReadOnlyCollection<OwnedBuildingState> OwnedBuildingStates()
		{
			var results = new List<OwnedBuildingState>();
			foreach (var actor in World.ActorsHavingTrait<Building>())
				if (TryGetOwnedBuildingState(actor, out var building))
					results.Add(building);

			results.Sort(static (a, b) => a.ActorId.CompareTo(b.ActorId));
			return results;
		}

		bool TryGetOwnedBuildingState(Actor actor, out OwnedBuildingState state)
		{
			if (actor == null || actor.Owner != self.Owner || actor.IsDead || !actor.IsInWorld ||
				!actor.Info.HasTraitInfo<BuildingInfo>())
			{
				state = default;
				return false;
			}

			var actorHealth = actor.TraitOrDefault<IHealth>();
			var repairable = actor.TraitOrDefault<RepairableBuilding>();
			var isRepairable = repairable != null && !repairable.IsTraitDisabled;
			state = new OwnedBuildingState(
				ActorId: actor.ActorID,
				ActorType: actor.Info.Name,
				CellX: actor.Location.X,
				CellY: actor.Location.Y,
				HealthPercent: PercentHealth(actorHealth),
				IsRepairable: isRepairable,
				RepairRequested: isRepairable && repairable.Repairers.Contains(self.Owner),
				RepairActive: isRepairable && repairable.RepairActive);
			return true;
		}

		/// <summary>Nearest allied actor with trait <typeparamref name="T"/>, or null.</summary>
		public Actor FindNearestAllied<T>()
		{
			Actor best = null;
			var bestDistance = int.MaxValue;

			foreach (var candidate in World.ActorsHavingTrait<T>())
			{
				if (candidate.IsDead || !candidate.IsInWorld || !candidate.Owner.IsAlliedWith(self.Owner))
					continue;

				var distance = (candidate.CenterPosition - self.CenterPosition).HorizontalLength;
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = candidate;
				}
			}

			return best;
		}

		static int PercentHealth(IHealth actorHealth)
		{
			if (actorHealth == null || actorHealth.MaxHP <= 0)
				return 100;

			return Math.Clamp((int)((long)actorHealth.HP * 100 / actorHealth.MaxHP), 0, 100);
		}

		#endregion

		#region Resources

		/// <summary>True if this map has a resource layer to read at all.</summary>
		public bool HasResourceLayer => resourceLayer != null;

		/// <summary>True if there is no resource left anywhere on the map.</summary>
		public bool ResourcesExhausted => resourceLayer == null || resourceLayer.IsEmpty;

		/// <summary>True if this unit is a harvester, i.e. it can cut resource at all.</summary>
		public bool IsHarvester => harvesterInfo != null;

		/// <summary>
		/// What is in a cell: tiberium type and how much of it.
		/// </summary>
		/// <remarks>
		/// <see cref="ResourceCell.Empty"/> for bare ground, for cells off the map, and for cells
		/// this player has never explored — the same rule the engine applies to a human's own
		/// harvest cursor, so a mode cannot read tiberium out of unexplored shroud.
		/// </remarks>
		public ResourceCell ResourceAt(CPos cell)
		{
			if (resourceLayer == null || cell.Layer != 0 || !World.Map.Contains(cell))
				return ResourceCell.Empty;

			if (!self.Owner.Shroud.IsExplored(cell))
				return ResourceCell.Empty;

			var contents = resourceLayer.GetResource(cell);
			if (contents.Type == null || contents.Density == 0)
				return ResourceCell.Empty;

			return new ResourceCell(cell.X, cell.Y, contents.Type, contents.Density);
		}

		/// <summary>True if there is anything at all to cut in this cell.</summary>
		public bool HasResource(CPos cell) => ResourceAt(cell).HasResource;

		/// <summary>True if <i>this</i> unit can cut what is in the cell.</summary>
		/// <remarks>Always false for a unit that is not a harvester.</remarks>
		public bool CanHarvest(CPos cell)
		{
			if (harvesterInfo == null)
				return false;

			var contents = ResourceAt(cell);
			return contents.HasResource && harvesterInfo.Resources.Contains(contents.ResourceType);
		}

		/// <summary>
		/// Cells this unit should count as a field: what it can cut if it is a harvester, and any
		/// resource at all if it is not, so a base planner can still find tiberium to build near.
		/// </summary>
		bool IsFieldCell(CPos cell) =>
			harvesterInfo != null ? CanHarvest(cell) : HasResource(cell);

		/// <summary>
		/// The nearest harvestable cell, or null if there is none within
		/// <paramref name="radiusCells"/>.
		/// </summary>
		/// <remarks>
		/// Cheap: cells come back nearest-first, so this stops at the first hit. Capped at the
		/// engine's tile search limit of 50 cells — use <see cref="FindResourceFields"/> to find
		/// tiberium further out than that.
		/// </remarks>
		public CPos? FindNearestResource(int radiusCells = 24, CPos? origin = null)
		{
			if (ResourcesExhausted)
				return null;

			var limit = World.Map.Grid.MaximumTileSearchRange;
			if (radiusCells > limit)
				radiusCells = limit;

			if (radiusCells < 0)
				radiusCells = 0;

			foreach (var cell in World.Map.FindTilesInCircle(origin ?? self.Location, radiusCells))
				if (IsFieldCell(cell))
					return cell;

			return null;
		}

		/// <summary>
		/// Every distinct resource field on the map, nearest first.
		/// </summary>
		/// <remarks>
		/// <para>
		/// This is the map-wide answer to "where is the tiberium", and it is deliberately not
		/// capped by a radius: a harvester that has mined out the ground by its refinery needs to
		/// be told about a field on the far side of the map, and every radius in the engine's own
		/// harvester search is far too short to find one.
		/// </para>
		/// <para>
		/// It walks every cell on the map and flood-fills each patch, so it is a scan rather than
		/// a lookup. Call it when a harvester has run out of work, not every tick.
		/// </para>
		/// </remarks>
		/// <param name="minCells">
		/// Ignore patches smaller than this, so a harvester is not sent across the map for three
		/// stray cells.
		/// </param>
		/// <param name="maxFields">How many of the nearest fields to return.</param>
		/// <param name="origin">Measure distance from here; defaults to this unit's cell.</param>
		public IReadOnlyList<ResourceField> FindResourceFields(int minCells = 4, int maxFields = 16, CPos? origin = null)
		{
			var fields = new List<ResourceField>();
			if (ResourcesExhausted || maxFields <= 0)
				return fields;

			var from = origin ?? self.Location;
			var fromPos = World.Map.CenterOfCell(from);
			var visited = new HashSet<CPos>();
			var pending = new Queue<CPos>();

			foreach (var seed in World.Map.AllCells)
			{
				if (!visited.Add(seed))
					continue;

				if (!IsFieldCell(seed))
					continue;

				var cellCount = 0;
				var totalDensity = 0;
				var sumX = 0L;
				var sumY = 0L;
				var nearest = seed;
				var nearestDistance = int.MaxValue;
				string type = null;

				pending.Enqueue(seed);

				while (pending.Count > 0)
				{
					var cell = pending.Dequeue();
					var contents = ResourceAt(cell);

					cellCount++;
					totalDensity += contents.Density;
					sumX += cell.X;
					sumY += cell.Y;
					type ??= contents.ResourceType;

					var distance = (World.Map.CenterOfCell(cell) - fromPos).HorizontalLength;
					if (distance < nearestDistance)
					{
						nearestDistance = distance;
						nearest = cell;
					}

					foreach (var direction in CVec.Directions)
					{
						var neighbour = cell + direction;
						if (!visited.Add(neighbour))
							continue;

						if (IsFieldCell(neighbour))
							pending.Enqueue(neighbour);
					}
				}

				if (cellCount >= minCells)
					fields.Add(new ResourceField(
						CenterX: (int)(sumX / cellCount),
						CenterY: (int)(sumY / cellCount),
						NearestX: nearest.X,
						NearestY: nearest.Y,
						DistanceUnits: nearestDistance,
						CellCount: cellCount,
						TotalDensity: totalDensity,
						ResourceType: type));
			}

			fields.Sort(static (a, b) => a.DistanceUnits.CompareTo(b.DistanceUnits));
			if (fields.Count > maxFields)
				fields.RemoveRange(maxFields, fields.Count - maxFields);

			return fields;
		}

		/// <summary>
		/// The nearest field with resource still in it, or null when the map is mined out.
		/// </summary>
		/// <remarks>
		/// The one call a stalled harvester needs: aim at <see cref="ResourceField.NearestX"/>/
		/// <see cref="ResourceField.NearestY"/> with <see cref="UnitDecision.Harvest"/>.
		/// </remarks>
		public ResourceField? FindNearestResourceField(int minCells = 4, CPos? origin = null)
		{
			var fields = FindResourceFields(minCells, 1, origin);
			return fields.Count > 0 ? fields[0] : null;
		}

		#endregion

		#region Construction and production

		/// <summary>True if this actor is a building.</summary>
		public bool IsBuilding => self.Info.HasTraitInfo<BuildingInfo>();

		/// <summary>
		/// True if this unit can transform right now.
		/// </summary>
		/// <remarks>
		/// Careful: a Tiberian Dawn construction yard can transform too — back into an MCV. Use
		/// <see cref="DeploysIntoBuilding"/> if you mean "unfold into a base".
		/// </remarks>
		public bool CanDeploy
		{
			get
			{
				var transforms = self.TraitOrDefault<Transforms>();
				return transforms != null && !transforms.IsTraitDisabled && !transforms.IsTraitPaused && transforms.CanDeploy();
			}
		}

		/// <summary>
		/// True if transforming turns this unit into a building, e.g. an MCV into a construction
		/// yard. This is the check that stops a construction yard packing itself up forever.
		/// </summary>
		public bool DeploysIntoBuilding
		{
			get
			{
				var transforms = self.TraitOrDefault<Transforms>();
				if (transforms == null)
					return false;

				var into = transforms.Info.IntoActor;
				if (string.IsNullOrEmpty(into) || !World.Map.Rules.Actors.TryGetValue(into.ToLowerInvariant(), out var intoInfo))
					return false;

				return intoInfo.HasTraitInfo<BuildingInfo>();
			}
		}

		/// <summary>Current cash plus banked resources.</summary>
		public int Cash
		{
			get
			{
				var resources = self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
				return resources?.GetCashAndResources() ?? 0;
			}
		}

		/// <summary>Spare power. Negative means a brownout.</summary>
		public int PowerBalance
		{
			get
			{
				var power = self.Owner.PlayerActor.TraitOrDefault<PowerManager>();
				return power?.ExcessPower ?? 0;
			}
		}

		/// <summary>
		/// The player's production queue for a category, or null.
		/// </summary>
		/// <remarks>
		/// Matches the queue's <c>Group</c> first, then its <c>Type</c>. In Tiberian Dawn the
		/// building queue's Type is faction-specific (<c>Building.GDI</c> / <c>Building.Nod</c>)
		/// while its Group is plainly <c>Building</c>, so matching Type alone silently finds
		/// nothing.
		/// </remarks>
		public ProductionQueue QueueFor(string category)
		{
			ProductionQueue typeMatch = null;

			foreach (var pair in World.ActorsWithTrait<ProductionQueue>())
			{
				if (pair.Actor.Owner != self.Owner || !pair.Trait.Enabled)
					continue;

				var queueInfo = pair.Trait.Info;
				if (string.Equals(queueInfo.Group, category, StringComparison.OrdinalIgnoreCase))
					return pair.Trait;

				if (typeMatch == null && string.Equals(queueInfo.Type, category, StringComparison.OrdinalIgnoreCase))
					typeMatch = pair.Trait;
			}

			return typeMatch;
		}

		/// <summary>True if this actor owns the queue for a category, so it should drive it.</summary>
		public bool OwnsQueue(string category)
		{
			var queue = QueueFor(category);
			return queue != null && queue.Actor == self;
		}

		/// <summary>Actor names currently producible from a category.</summary>
		public IReadOnlyCollection<string> BuildableItems(string category)
		{
			var queue = QueueFor(category);
			if (queue == null)
				return [];

			return queue.BuildableItems().Select(a => a.Name).ToArray();
		}

		/// <summary>The item a queue is working on, or null if idle.</summary>
		public string ProducingItem(string category) => QueueFor(category)?.CurrentItem()?.Item;

		/// <summary>The finished building waiting to be placed, or null.</summary>
		public string ItemReadyToPlace(string category)
		{
			var current = QueueFor(category)?.CurrentItem();
			return current != null && current.Done ? current.Item : null;
		}

		/// <summary>Snapshot of every production queue, for the unit production planner.</summary>
		public IReadOnlyCollection<ProductionQueueState> QueueStates()
		{
			var results = new List<ProductionQueueState>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var pair in World.ActorsWithTrait<ProductionQueue>())
			{
				if (pair.Actor.Owner != self.Owner || !pair.Trait.Enabled)
					continue;

				var name = pair.Trait.Info.Group ?? pair.Trait.Info.Type;
				if (name == null || !seen.Add(name))
					continue;

				var queued = pair.Trait.AllQueued().ToArray();
				var current = queued.FirstOrDefault();
				results.Add(new ProductionQueueState(
					Queue: name,
					IsIdle: current == null,
					Buildable: pair.Trait.BuildableItems().Select(a => a.Name).ToArray())
				{
					CurrentItem = current?.Item,
					CurrentProgressPercent = current == null
						? 0
						: ActionOrderBuilder.ProgressPercent(current.TotalTime, current.RemainingTime, current.Done),
					CurrentCost = current?.TotalCost ?? 0,
					CurrentRemainingCost = current?.RemainingCost ?? 0,
					CurrentItemCount = current == null
						? 0
						: queued.Count(item => string.Equals(
							item.Item, current.Item, StringComparison.OrdinalIgnoreCase)),
					QueuedCount = queued.Length
				});
			}

			return results;
		}

		/// <summary>
		/// Every support power currently registered for this player.
		/// </summary>
		/// <remarks>
		/// Both the manager key and configured order name are exposed. Use either with
		/// <see cref="UnitDecision.ActivateSupportPower"/>.
		/// </remarks>
		public IReadOnlyCollection<SupportPowerState> SupportPowerStates()
		{
			var manager = GetSupportPowerManager();
			if (manager == null)
				return [];

			var results = new List<SupportPowerState>();
			foreach (var pair in manager.Powers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
			{
				var power = pair.Value;
				var info = power.Info;
				if (info == null)
					continue;

				results.Add(new SupportPowerState(
					Key: pair.Key,
					OrderName: info.OrderName,
					Active: power.Active,
					Ready: power.Ready,
					Disabled: power.Disabled,
					RemainingTicks: power.RemainingTicks,
					TotalTicks: power.TotalTicks));
			}

			return results;
		}

		SupportPowerManager GetSupportPowerManager() =>
			self.Owner.PlayerActor.TraitOrDefault<SupportPowerManager>();

		/// <summary>How many of each owned building type this player has, including queued.</summary>
		public IReadOnlyDictionary<string, int> OwnedBuildingCounts() => CountOwned<Building>("Building");

		/// <summary>How many of each owned mobile unit this player has, including queued.</summary>
		public IReadOnlyDictionary<string, int> OwnedUnitCounts()
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			foreach (var actor in World.ActorsHavingTrait<Mobile>())
			{
				if (actor.Owner != self.Owner || actor.IsDead || !actor.IsInWorld)
					continue;

				counts.TryGetValue(actor.Info.Name, out var existing);
				counts[actor.Info.Name] = existing + 1;
			}

			AddQueued(counts, "Infantry");
			AddQueued(counts, "Vehicle");
			AddQueued(counts, "Aircraft");

			return counts;
		}

		Dictionary<string, int> CountOwned<T>(string queueCategory)
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			foreach (var actor in World.ActorsHavingTrait<T>())
			{
				if (actor.Owner != self.Owner || actor.IsDead)
					continue;

				counts.TryGetValue(actor.Info.Name, out var existing);
				counts[actor.Info.Name] = existing + 1;
			}

			AddQueued(counts, queueCategory);
			return counts;
		}

		/// <summary>
		/// Counts items already queued, or a planner orders three refineries before the first
		/// one finishes.
		/// </summary>
		void AddQueued(Dictionary<string, int> counts, string category)
		{
			var queue = QueueFor(category);
			if (queue == null)
				return;

			foreach (var item in queue.AllQueued())
			{
				counts.TryGetValue(item.Item, out var existing);
				counts[item.Item] = existing + 1;
			}
		}

		/// <summary>
		/// Finds somewhere valid to put a building, searching outwards from the base centre.
		/// </summary>
		public CPos? FindBuildLocation(string actorType, int minRange = 2, int maxRange = 14)
		{
			if (!World.Map.Rules.Actors.TryGetValue(actorType.ToLowerInvariant(), out var actorInfo))
				return null;

			var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null)
				return null;

			foreach (var cell in World.Map.FindTilesInAnnulus(BaseCenter, minRange, maxRange))
			{
				if (!World.CanPlaceBuilding(cell, actorInfo, buildingInfo, null))
					continue;

				if (!buildingInfo.IsCloseEnoughToBase(World, self.Owner, actorInfo, cell))
					continue;

				return cell;
			}

			return null;
		}

		/// <summary>The player's construction yard location, falling back to this unit's own.</summary>
		public CPos BaseCenter
		{
			get
			{
				foreach (var actor in World.ActorsHavingTrait<BaseProvider>())
					if (actor.Owner == self.Owner && !actor.IsDead && actor.IsInWorld)
						return actor.Location;

				return self.Location;
			}
		}

		#endregion

		#region Acting

		internal UnitDecision? ResolveAction(in UnitDecision decision)
		{
			switch (decision.Action)
			{
				case UnitAction.CancelProduction:
					{
						var queue = QueueFor(decision.Queue);
						if (queue == null)
							return null;

						var queued = queue.AllQueued().ToArray();
						return ActionOrderBuilder.ResolveCancellation(
							decision,
							queue.Actor.ActorID,
							queue.Info.Group ?? queue.Info.Type,
							queued.Select(item => item.Item));
					}

				case UnitAction.ActivateSupportPower:
					return ActionOrderBuilder.ResolveSupportPower(
						decision, SupportPowerStates());

				default:
					return decision;
			}
		}

		/// <summary>
		/// Translates a decision into the order that carries it out, or null.
		/// </summary>
		/// <remarks>
		/// Orders are not issued here: the platform compares intent against the last command
		/// before sending, so a mode returning the same decision every tick sends nothing.
		/// </remarks>
		public Order BuildOrder(in UnitDecision decision)
		{
			switch (decision.Action)
			{
				case UnitAction.Continue:
					return null;

				case UnitAction.Hold:
					return new Order("Stop", self, false);

				case UnitAction.Attack:
				{
					var target = ResolveActor(decision.TargetActorId);
					return target == null ? null : new Order("Attack", self, Target.FromActor(target), false);
				}

				case UnitAction.ReturnToAnchor:
					return MoveOrder(state.Anchor);

				case UnitAction.Retreat:
					return MoveOrder(FindRepairBay()?.Location ?? state.Anchor);

				case UnitAction.AdvanceToObjective:
				{
					var objective = ResolveActor(decision.TargetActorId);
					return objective == null ? null : AttackMoveOrder(objective.Location);
				}

				case UnitAction.MoveTo:
					return MoveOrder(new CPos(decision.TargetX, decision.TargetY));

				case UnitAction.AttackMoveTo:
					return AttackMoveOrder(new CPos(decision.TargetX, decision.TargetY));

				case UnitAction.Harvest:
				{
					if (harvesterInfo == null)
						return null;

					// Targeting the cell rather than the actor is what makes this work at range:
					// the engine re-centres its harvest search on the ordered cell, so a harvester
					// will cross the map to a field that its own search radius could never reach.
					return new Order("Harvest", self,
						Target.FromCell(World, new CPos(decision.TargetX, decision.TargetY)), false);
				}

				case UnitAction.RepairBuilding:
				{
					var target = ResolveActor(decision.TargetActorId);
					if (!TryGetOwnedBuildingState(target, out var building) ||
						!ActionOrderBuilder.CanStartRepair(building))
						return null;

					return ActionOrderBuilder.RequestRepairBuilding(
						self.Owner.PlayerActor, Target.FromActor(target));
				}

				case UnitAction.Deploy:
					return new Order("DeployTransform", self, false);

				case UnitAction.Produce:
				{
					var queue = QueueFor(decision.Queue);
					if (queue == null || string.IsNullOrEmpty(decision.ItemName))
						return null;

					return Order.StartProduction(queue.Actor, decision.ItemName, 1);
				}

				case UnitAction.CancelProduction:
				{
					var queue = QueueFor(decision.Queue);
					if (queue == null ||
						(decision.TargetActorId != 0 && queue.Actor.ActorID != decision.TargetActorId))
						return null;

					var queued = queue.AllQueued().ToArray();
					var item = ActionOrderBuilder.FindQueuedItem(
						queued.Select(queuedItem => queuedItem.Item),
						decision.ItemName,
						decision.Count);
					var queueIndex = item == null ? -1 : QueueIndex(queue);
					return queueIndex < 0
						? null
						: ActionOrderBuilder.RequestCancelProduction(
							self.Owner.PlayerActor,
							Target.FromActor(queue.Actor),
							queueIndex,
							item,
							decision.Count,
							queued
								.Select(queuedItem =>
									new ProductionQueueEntry(queuedItem.Item, queuedItem.Infinite))
								.ToArray());
				}

				case UnitAction.PlaceBuilding:
				{
					var queue = QueueFor(decision.Queue);
					if (queue == null || string.IsNullOrEmpty(decision.ItemName))
						return null;

					// Placement is player-scoped: the subject is the player actor and the queue is
					// identified by ExtraData, mirroring how the engine's own base-builder does it.
					return new Order("PlaceBuilding", self.Owner.PlayerActor,
						Target.FromCell(World, new CPos(decision.TargetX, decision.TargetY)), false)
					{
						TargetString = decision.ItemName,
						ExtraLocation = CPos.Zero,
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					};
				}

				case UnitAction.ActivateSupportPower:
				{
					var cell = new CPos(decision.TargetX, decision.TargetY);
					if (!World.Map.Contains(cell))
						return null;

					var manager = GetSupportPowerManager();
					if (manager == null)
						return null;

					var key = ActionOrderBuilder.FindReadySupportPower(
						SupportPowerStates(), decision.Power);
					if (key == null || !manager.Powers.TryGetValue(key, out var power) ||
						!power.Active || !power.Ready || power.Disabled)
						return null;

					return ActionOrderBuilder.ActivateSupportPower(
						manager.Self, key, Target.FromCell(World, cell));
				}

				default:
					return null;
			}
		}

		Order MoveOrder(CPos cell) =>
			move == null ? null : new Order("Move", self, Target.FromCell(World, cell), false);

		Order AttackMoveOrder(CPos cell) =>
			move == null ? null : new Order("AttackMove", self, Target.FromCell(World, cell), false);

		static int QueueIndex(ProductionQueue queue)
		{
			var index = 0;
			foreach (var candidate in queue.Actor.TraitsImplementing<ProductionQueue>())
			{
				if (ReferenceEquals(candidate, queue))
					return index;

				index++;
			}

			return -1;
		}

		#endregion
	}
}
