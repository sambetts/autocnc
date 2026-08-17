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
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// The curated, deterministic API surface a <see cref="IUnitMode"/> is given.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Two jobs. First, <b>sensing</b>: flatten the live engine world into engine-free
	/// <see cref="ThreatSnapshot"/> values that pure decision logic can consume. Second,
	/// <b>acting</b>: turn a <see cref="UnitDecision"/> back into queued activities.
	/// </para>
	/// <para>
	/// Everything here is deliberately lockstep-safe. There is intentionally no accessor for
	/// <c>LocalPlayer</c>, <c>Selection</c>, <c>LocalRandom</c> or <c>ScreenMap</c> — if a mode
	/// cannot reach client-local state, it cannot desync the match.
	/// </para>
	/// <para>One instance is created per unit and reused for the unit's lifetime.</para>
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

		/// <summary>Synced group state. Never OpenRA's client-local <c>ControlGroups</c>.</summary>
		public GroupManager Groups { get; }

		/// <summary>The only random source a mode may use: seeded from the synced lobby seed.</summary>
		public MersenneTwister Random => World.SharedRandom;

		public int WorldTick => World.WorldTick;

		/// <summary>Synced control group (1-9), or 0 if unassigned.</summary>
		public int GroupId => controller.GroupId;

		/// <summary>The position this unit treats as home. Defaults to where it was created.</summary>
		public CPos Anchor => controller.Anchor;

		public bool CanMove => move != null;
		public bool HasWeapon => attackBases.Length > 0;
		public bool IsIdle => self.IsIdle;

		internal ModeContext(ProgrammableController controller, Actor self, GroupManager groups)
		{
			this.controller = controller;
			this.self = self;
			World = self.World;
			Groups = groups;

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

		/// <summary>
		/// Flattens every visible enemy within <paramref name="radius"/> into engine-free snapshots.
		/// </summary>
		/// <remarks>
		/// Results are sorted by <c>ActorID</c>. Spatial query order is already consistent across
		/// clients, but sorting makes downstream tie-breaking provably stable and costs nothing at
		/// these list sizes. The returned list is a reused buffer — do not retain it across ticks.
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

		/// <summary>Enemy structures within <paramref name="radius"/>, for objective selection.</summary>
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

			// Respect fog and cloaking. Shroud is synced simulation state, so this is safe.
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
				return info.HasTraitInfo<CargoInfo>() || !info.HasTraitInfo<ValuedInfo>()
					? ThreatKind.Vehicle
					: ClassifyMobile(actor);

			return ThreatKind.Unknown;
		}

		static ThreatKind ClassifyMobile(Actor actor)
		{
			// Infantry and vehicles both use Mobile, so fall back to the mod's own target types,
			// which is the moddable, faction-agnostic way to tell them apart.
			var types = actor.GetEnabledTargetTypes();
			return types.Contains("Infantry") ? ThreatKind.Infantry : ThreatKind.Vehicle;
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
		public Actor FindRepairBay()
		{
			Actor best = null;
			var bestDistance = int.MaxValue;

			foreach (var candidate in World.ActorsHavingTrait<RepairsUnits>())
			{
				if (candidate.IsDead || !candidate.IsInWorld)
					continue;

				if (!candidate.Owner.IsAlliedWith(self.Owner))
					continue;

				var distance = (candidate.CenterPosition - self.CenterPosition).HorizontalLength;

				// Deterministic tie-break: never let enumeration order decide.
				if (distance < bestDistance || (distance == bestDistance && best != null && candidate.ActorID < best.ActorID))
				{
					bestDistance = distance;
					best = candidate;
				}
			}

			return best;
		}

		#endregion

		#region Acting

		/// <summary>
		/// Applies a decision to the unit. This is the single place a mode's intent becomes
		/// engine state, which keeps activity handling consistent across every mode.
		/// </summary>
		public void Apply(in UnitDecision decision)
		{
			switch (decision.Action)
			{
				case UnitAction.Continue:
					break;

				case UnitAction.Hold:
					Stop();
					break;

				case UnitAction.Attack:
				{
					var target = ResolveActor(decision.TargetActorId);
					if (target != null)
						Attack(target);
					break;
				}

				case UnitAction.ReturnToAnchor:
					MoveTo(controller.Anchor);
					break;

				case UnitAction.Retreat:
				{
					var bay = FindRepairBay();
					if (bay != null)
						MoveTo(bay.Location, 2);
					else
						MoveTo(controller.Anchor);
					break;
				}

				case UnitAction.AdvanceToObjective:
				{
					var objective = ResolveActor(decision.TargetActorId);
					if (objective != null)
						AttackMoveTo(objective.Location);
					break;
				}
			}
		}

		/// <summary>
		/// Attacks a target by queueing an attack activity directly.
		/// </summary>
		/// <remarks>
		/// This mirrors the engine's own AutoTarget, which calls
		/// <c>AttackBase.AttackTarget(...)</c> and never constructs an <see cref="Order"/>.
		/// Trait ticks already run on every client, so no network round-trip is needed — and
		/// issuing orders from here would flood the order stream. See docs/determinism.md.
		/// </remarks>
		public void Attack(Actor target, bool allowMove = true)
		{
			if (target == null || target.IsDead || !target.IsInWorld)
				return;

			var t = Target.FromActor(target);
			foreach (var ab in ActiveAttackBases)
				ab.AttackTarget(t, AttackSource.Default, false, allowMove);
		}

		public void MoveTo(CPos cell, int nearEnoughCells = 0)
		{
			if (move == null)
				return;

			self.QueueActivity(false, move.MoveTo(cell, nearEnoughCells));
		}

		/// <summary>Advances on a cell while still engaging targets of opportunity en route.</summary>
		public void AttackMoveTo(CPos cell)
		{
			if (move == null)
				return;

			var destination = cell;
			self.QueueActivity(false, new OpenRA.Mods.Common.Activities.AttackMoveActivity(
				self, () => move.MoveTo(destination, 1)));
		}

		public void Stop()
		{
			if (!self.IsIdle)
				self.CancelActivity();
		}

		#endregion
	}
}
