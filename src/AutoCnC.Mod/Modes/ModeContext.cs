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

using System.Collections.Generic;
using System.Linq;
using AutoCnC.Mod.Traits;
using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// The API a <see cref="IUnitMode"/> is given for reading the world and commanding its unit.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Modes execute <b>outside the lockstep simulation</b>, on the owning player's client only.
	/// Decisions leave as <see cref="Order"/>s, exactly like a human player's clicks, so a mode
	/// you wrote does not need to exist on your opponent's machine.
	/// </para>
	/// <para>
	/// The practical consequence for authors: the simulation's determinism rules do not apply to
	/// your code. Floating point, LINQ, <c>System.Random</c> and wall-clock time are all fine —
	/// none of it touches the simulation, only the orders you emit do.
	/// </para>
	/// <para>One instance is created per unit and reused for that unit's lifetime.</para>
	/// </remarks>
	public sealed class ModeContext
	{
		readonly ProgrammableController controller;
		readonly Actor self;
		readonly AttackBase[] attackBases;
		readonly IHealth health;
		readonly IMove move;
		readonly List<ThreatSnapshot> threatBuffer = [];

		public World World { get; }

		/// <summary>The actor this mode is driving.</summary>
		public Actor Self => self;

		public Player Owner => self.Owner;

		/// <summary>A random source. <c>System.Random</c> is equally safe here.</summary>
		public MersenneTwister Random => World.SharedRandom;

		public int WorldTick => World.WorldTick;

		/// <summary>Control group this unit belongs to (1-9), or 0 if unassigned.</summary>
		public int GroupId => controller.GroupId;

		/// <summary>Name of the mode currently running on this unit.</summary>
		public string ModeName => controller.ActiveModeName;

		/// <summary>The position this unit treats as home. Defaults to where it was created.</summary>
		public CPos Anchor
		{
			get => controller.Anchor;
			set => controller.Anchor = value;
		}

		public bool CanMove => move != null;
		public bool HasWeapon => attackBases.Length > 0;
		public bool IsIdle => self.IsIdle;

		/// <summary>
		/// Asks the executor to evaluate this unit on the next tick instead of waiting for its
		/// interval, and to re-send its order even if the decision is unchanged.
		/// </summary>
		/// <remarks>
		/// Useful from <see cref="IUnitMode.OnDamaged"/>, which fires between evaluations.
		/// </remarks>
		public void RequestReevaluation()
		{
			controller.NextEvaluationTick = 0;
			controller.LastIssued = UnitDecision.Continue;
		}

		internal ModeContext(ProgrammableController controller, Actor self)
		{
			this.controller = controller;
			this.self = self;
			World = self.World;

			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
			health = self.TraitOrDefault<IHealth>();
			move = self.TraitOrDefault<IMove>();
		}

		IEnumerable<AttackBase> ActiveAttackBases =>
			attackBases.Where(ab => !ab.IsTraitDisabled && !ab.IsTraitPaused);

		#region Sensing

		/// <summary>Current health as an integer percentage (0-100). 100 if the unit has no health trait.</summary>
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
			(self.CenterPosition - World.Map.CenterOfCell(controller.Anchor)).HorizontalLength;

		public int DistanceTo(Actor other) =>
			other == null ? int.MaxValue : (other.CenterPosition - self.CenterPosition).HorizontalLength;

		public int DistanceTo(CPos cell) =>
			(World.Map.CenterOfCell(cell) - self.CenterPosition).HorizontalLength;

		/// <summary>
		/// Flattens every visible enemy within <paramref name="radius"/> into engine-free snapshots.
		/// </summary>
		/// <remarks>
		/// The returned list is a reused buffer — consume it within the tick, do not retain it.
		/// </remarks>
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

		/// <summary>Visible enemy structures within <paramref name="radius"/>, for objective selection.</summary>
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

		/// <summary>Allied actors within <paramref name="radius"/>, optionally filtered by actor type.</summary>
		public IEnumerable<Actor> SenseAllies(WDist radius, string actorType = null)
		{
			return World.FindActorsInCircle(self.CenterPosition, radius)
				.Where(a => a != self && !a.IsDead && a.IsInWorld && a.Owner.IsAlliedWith(self.Owner)
					&& (actorType == null || string.Equals(a.Info.Name, actorType, System.StringComparison.OrdinalIgnoreCase)));
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
				CanHitUs: CanHitUs(actor));
		}

		bool IsHostileAndVisible(Actor actor)
		{
			if (actor == null || actor == self || actor.IsDead || !actor.IsInWorld)
				return false;

			if (self.Owner.RelationshipWith(actor.Owner) != PlayerRelationship.Enemy)
				return false;

			// Wrecks are not threats and not worth spending ammunition on.
			if (actor.Info.HasTraitInfo<HuskInfo>())
				return false;

			if (actor.GetEnabledTargetTypes().IsEmpty)
				return false;

			// Respect fog and cloaking, so a mode cannot cheat by seeing through the shroud.
			return actor.CanBeViewedByPlayer(self.Owner);
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

		static ThreatKind Classify(Actor actor)
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

		/// <summary>Resolves an ActorID from a decision back into a live actor, or null.</summary>
		public Actor ResolveActor(uint actorId)
		{
			if (actorId == 0)
				return null;

			var actor = World.GetActorById(actorId);
			return actor != null && actor.IsInWorld && !actor.IsDead ? actor : null;
		}

		/// <summary>Nearest allied structure that can repair us, or null.</summary>
		public Actor FindRepairBay() => FindNearestAllied<RepairsUnits>();

		/// <summary>Nearest allied refinery, or null.</summary>
		public Actor FindRefinery() => FindNearestAllied<Refinery>();

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

		#endregion

		#region Base building

		/// <summary>True if this actor is a building.</summary>
		public bool IsBuilding => self.Info.HasTraitInfo<BuildingInfo>();

		/// <summary>True if this unit can transform right now.</summary>
		/// <remarks>
		/// Careful: in Tiberian Dawn a construction yard can transform too — back into an MCV.
		/// Use <see cref="DeploysIntoBuilding"/> if you mean "unfold into a base".
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
		/// True if transforming would turn this unit into a building, e.g. an MCV unfolding into
		/// a construction yard.
		/// </summary>
		/// <remarks>
		/// This is the check that stops a construction yard packing itself back into an MCV and
		/// deploying again forever.
		/// </remarks>
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
		/// Matches on the queue's <c>Group</c> first and only then its <c>Type</c>. In Tiberian
		/// Dawn the building queue's Type is faction-specific (<c>Building.GDI</c> /
		/// <c>Building.Nod</c>) while its Group is plainly <c>Building</c>, so matching Type alone
		/// silently finds nothing and no structure is ever queued.
		/// </remarks>
		public ProductionQueue QueueFor(string category)
		{
			ProductionQueue typeMatch = null;

			foreach (var pair in World.ActorsWithTrait<ProductionQueue>())
			{
				if (pair.Actor.Owner != self.Owner || !pair.Trait.Enabled)
					continue;

				var info = pair.Trait.Info;
				if (string.Equals(info.Group, category, System.StringComparison.OrdinalIgnoreCase))
					return pair.Trait;

				if (typeMatch == null && string.Equals(info.Type, category, System.StringComparison.OrdinalIgnoreCase))
					typeMatch = pair.Trait;
			}

			return typeMatch;
		}

		/// <summary>Actor names this player can currently produce from a category.</summary>
		public IReadOnlyCollection<string> BuildableItems(string category)
		{
			var queue = QueueFor(category);
			if (queue == null)
				return [];

			return queue.BuildableItems().Select(a => a.Name).ToArray();
		}

		/// <summary>The item a queue is currently working on, or null if idle.</summary>
		public string ProducingItem(string category) => QueueFor(category)?.CurrentItem()?.Item;

		/// <summary>The finished building waiting to be placed, or null.</summary>
		public string ItemReadyToPlace(string category)
		{
			var current = QueueFor(category)?.CurrentItem();
			return current != null && current.Done ? current.Item : null;
		}

		/// <summary>How many of each owned building type this player has, including those queued.</summary>
		public IReadOnlyDictionary<string, int> OwnedBuildingCounts()
		{
			var counts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

			foreach (var actor in World.ActorsHavingTrait<Building>())
			{
				if (actor.Owner != self.Owner || actor.IsDead)
					continue;

				counts.TryGetValue(actor.Info.Name, out var existing);
				counts[actor.Info.Name] = existing + 1;
			}

			// Count anything already in the queue too, or the planner orders three refineries
			// before the first one finishes.
			var queue = QueueFor("Building");
			if (queue != null)
			{
				foreach (var item in queue.AllQueued())
				{
					counts.TryGetValue(item.Item, out var existing);
					counts[item.Item] = existing + 1;
				}
			}

			return counts;
		}

		/// <summary>
		/// Finds somewhere valid to put a building, searching outwards from the base centre.
		/// </summary>
		/// <remarks>
		/// Mirrors how the engine's own base-builder places structures: walk an annulus of
		/// candidate cells and take the first that passes <c>CanPlaceBuilding</c> and is close
		/// enough to an existing base.
		/// </remarks>
		public CPos? FindBuildLocation(string actorType, int minRange = 2, int maxRange = 14)
		{
			if (!World.Map.Rules.Actors.TryGetValue(actorType.ToLowerInvariant(), out var actorInfo))
				return null;

			var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null)
				return null;

			var center = BaseCenter;

			foreach (var cell in World.Map.FindTilesInAnnulus(center, minRange, maxRange))
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
				Actor best = null;
				foreach (var actor in World.ActorsHavingTrait<BaseProvider>())
				{
					if (actor.Owner != self.Owner || actor.IsDead || !actor.IsInWorld)
						continue;

					best = actor;
					break;
				}

				return best?.Location ?? self.Location;
			}
		}

		#endregion

		#region Acting

		/// <summary>
		/// Translates a decision into the order that would carry it out, or null if the decision
		/// needs no command.
		/// </summary>
		/// <remarks>
		/// Orders are not issued here. <see cref="ModeExecutor"/> compares intent against the last
		/// command before sending, so a mode returning the same decision every tick does not flood
		/// the order stream.
		/// </remarks>
		internal Order BuildOrder(in UnitDecision decision)
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
					if (target == null)
						return null;

					return new Order("Attack", self, Target.FromActor(target), false);
				}

				case UnitAction.ReturnToAnchor:
					return MoveOrder(controller.Anchor);

				case UnitAction.Retreat:
				{
					var bay = FindRepairBay();
					return MoveOrder(bay?.Location ?? controller.Anchor);
				}

				case UnitAction.AdvanceToObjective:
				{
					var objective = ResolveActor(decision.TargetActorId);
					if (objective == null)
						return null;

					return AttackMoveOrder(objective.Location);
				}

				case UnitAction.MoveTo:
					return MoveOrder(new CPos(decision.TargetX, decision.TargetY));

				case UnitAction.AttackMoveTo:
					return AttackMoveOrder(new CPos(decision.TargetX, decision.TargetY));

				case UnitAction.Deploy:
					return new Order("DeployTransform", self, false);

				case UnitAction.Produce:
				{
					var queue = QueueFor("Building");
					if (queue == null || string.IsNullOrEmpty(decision.ItemName))
						return null;

					return Order.StartProduction(queue.Actor, decision.ItemName, 1);
				}

				case UnitAction.PlaceBuilding:
				{
					var queue = QueueFor("Building");
					if (queue == null || string.IsNullOrEmpty(decision.ItemName))
						return null;

					// Placement is a player-scoped order, not a unit one: the subject is the
					// player actor and the queue is identified by ExtraData. This mirrors how
					// the engine's own base-builder issues it.
					return new Order("PlaceBuilding", self.Owner.PlayerActor,
						Target.FromCell(World, new CPos(decision.TargetX, decision.TargetY)), false)
					{
						TargetString = decision.ItemName,
						ExtraLocation = CPos.Zero,
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					};
				}

				default:
					return null;
			}
		}

		Order MoveOrder(CPos cell)
		{
			if (move == null)
				return null;

			return new Order("Move", self, Target.FromCell(World, cell), false);
		}

		Order AttackMoveOrder(CPos cell)
		{
			if (move == null)
				return null;

			return new Order("AttackMove", self, Target.FromCell(World, cell), false);
		}

		#endregion
	}
}
