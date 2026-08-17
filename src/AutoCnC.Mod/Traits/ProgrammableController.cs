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
using AutoCnC.Mod.Modes;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[Desc("Runs a programmable C# behaviour mode for this unit.",
		"Replaces AutoTarget: remove AutoTarget from any actor that uses this trait,",
		"otherwise the two will fight over the unit's activity queue.")]
	public class ProgrammableControllerInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Mode class name to run when the unit is created, e.g. DefensiveMode.")]
		public readonly string DefaultMode = null;

		[Desc("Modes this unit is permitted to switch to. Empty means any known mode.")]
		public readonly HashSet<string> AvailableModes = [];

		[Desc("Game ticks between mode evaluations. Higher is cheaper but less responsive.")]
		public readonly int TickInterval = 8;

		[Desc("How far the unit senses enemies.")]
		public readonly WDist ScanRadius = WDist.FromCells(8);

		[Desc("Control group (1-9) this unit joins on creation. 0 means unassigned.")]
		public readonly int InitialGroup = 0;

		public override object Create(ActorInitializer init) { return new ProgrammableController(init.Self, this); }
	}

	/// <summary>
	/// The per-unit execution engine for programmable modes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Owns the boring, easy-to-get-wrong parts — tick throttling, mode lifecycle, damage
	/// forwarding, group membership, order handling — so that an <see cref="IUnitMode"/> author
	/// only writes decision logic.
	/// </para>
	/// <para>
	/// <b>Why this queues activities instead of issuing orders.</b> This is a normal trait, so
	/// its <c>Tick</c> runs on every client in the lockstep simulation and is already replicated.
	/// The engine's own AutoTarget does exactly the same thing. Only genuine player input (a mode
	/// or group change) travels as an <see cref="Order"/>. See docs/determinism.md.
	/// </para>
	/// <para>
	/// Inherits <c>INotifyCreated</c> from <see cref="ConditionalTrait{T}"/> via the
	/// <see cref="Created"/> override.
	/// </para>
	/// </remarks>
	public class ProgrammableController : ConditionalTrait<ProgrammableControllerInfo>,
		ITick, INotifyDamage, IResolveOrder, INotifyOwnerChanged, INotifyActorDisposing, ISync
	{
		public const string SetModeOrderString = "AutoCnCSetMode";
		public const string SetGroupOrderString = "AutoCnCSetGroup";
		public const string SetAnchorOrderString = "AutoCnCSetAnchor";

		public Actor Self { get; }
		public Player Owner => Self.Owner;

		/// <summary>Synced control group (1-9), or 0 when unassigned.</summary>
		[VerifySync]
		public int GroupId { get; private set; }

		/// <summary>The position this unit treats as home.</summary>
		[VerifySync]
		public CPos Anchor { get; private set; }

		/// <summary>
		/// Stable hash of the active mode name.
		/// </summary>
		/// <remarks>
		/// Sync fields must be integral, and <c>string.GetHashCode</c> is randomised per process,
		/// so a hand-rolled FNV-1a is used to make mode divergence show up in the desync report.
		/// </remarks>
		[VerifySync]
		public int ActiveModeHash => StableHash(activeModeName);

		public string ActiveModeName => activeModeName;

		GroupManager groups;
		ModeContext context;
		IUnitMode activeMode;
		string activeModeName;

		public ProgrammableController(Actor self, ProgrammableControllerInfo info)
			: base(info)
		{
			Self = self;
			GroupId = info.InitialGroup;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			groups = self.World.WorldActor.TraitOrDefault<GroupManager>();
			context = new ModeContext(this, self, groups);
			Anchor = self.Location;

			groups?.Register(this);

			// A unit produced into an existing group inherits that group's current behaviour,
			// so reinforcements don't arrive on the default mode and wander off.
			var inherited = groups?.GetGroupMode(self.Owner, GroupId);
			SetMode(inherited ?? Info.DefaultMode);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || self.IsDead || !self.IsInWorld)
				return;

			if (activeMode == null)
				return;

			// Stagger evaluations across units so a large army doesn't spike one tick.
			// ActorID is synced, so the stagger is identical on every client.
			var interval = Info.TickInterval < 1 ? 1 : Info.TickInterval;
			if ((self.World.WorldTick + (int)self.ActorID) % interval != 0)
				return;

			activeMode.OnTick(self, context);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || activeMode == null)
				return;

			// Delivered immediately rather than on the next evaluation: reacting to being shot
			// is the one thing that must not wait for the tick interval.
			activeMode.OnDamaged(self, context, e);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case SetModeOrderString:
					SetMode(order.TargetString);
					break;

				case SetGroupOrderString:
					SetGroup((int)order.ExtraData);
					break;

				case SetAnchorOrderString:
					Anchor = order.Target.Type != TargetType.Invalid
						? self.World.Map.CellContaining(order.Target.CenterPosition)
						: self.Location;
					break;
			}
		}

		/// <summary>
		/// Switches the active mode, running the previous mode's exit hook first.
		/// </summary>
		/// <remarks>
		/// Call only from order resolution, group broadcast, or creation — never from mode logic
		/// reacting to client-local state.
		/// </remarks>
		public void SetMode(string modeName)
		{
			if (string.IsNullOrEmpty(modeName) || modeName == activeModeName)
				return;

			if (Info.AvailableModes.Count > 0 && !Info.AvailableModes.Contains(modeName))
				return;

			if (!ModeRegistry.IsKnownMode(modeName))
				return;

			activeMode?.OnExit(Self, context);

			activeModeName = modeName;
			activeMode = ModeRegistry.Create(modeName);

			// Clear inherited activities so the new mode starts from a clean slate rather than
			// finishing the previous mode's half-completed move.
			if (!Self.IsIdle)
				Self.CancelActivity();

			activeMode.OnEnter(Self, context);
		}

		public void SetGroup(int group)
		{
			if (groups != null && !groups.IsValidGroup(group) && group != 0)
				return;

			if (group == GroupId)
				return;

			var previous = GroupId;
			GroupId = group;
			groups?.Reindex(this, Owner, previous, group);

			var groupMode = groups?.GetGroupMode(Owner, group);
			if (groupMode != null)
				SetMode(groupMode);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// A captured unit must not keep executing its previous owner's plan.
			groups?.Unregister(this);
			GroupId = 0;
			Anchor = self.Location;
			groups?.Register(this);
			SetMode(Info.DefaultMode);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			activeMode?.OnExit(self, context);
			activeMode = null;
			groups?.Unregister(this);
		}

		#region Order factories

		/// <summary>
		/// Builds a mode-switch order for a whole selection.
		/// </summary>
		/// <remarks>
		/// Uses the engine's built-in <c>GroupedActors</c> batching, so one network order covers
		/// the entire group and the engine fans it out via <c>Order.FromGroupedOrder</c>.
		/// </remarks>
		public static Order CreateSetModeOrder(IEnumerable<Actor> actors, string modeName)
		{
			var targets = actors.Where(a => a != null && !a.IsDead && a.IsInWorld).ToArray();
			if (targets.Length == 0)
				return null;

			return new Order(SetModeOrderString, targets[0], false, groupedActors: targets)
			{
				TargetString = modeName
			};
		}

		public static Order CreateSetGroupOrder(IEnumerable<Actor> actors, int group)
		{
			var targets = actors.Where(a => a != null && !a.IsDead && a.IsInWorld).ToArray();
			if (targets.Length == 0)
				return null;

			return new Order(SetGroupOrderString, targets[0], false, groupedActors: targets)
			{
				ExtraData = (uint)group
			};
		}

		#endregion

		/// <summary>FNV-1a. Deterministic across processes, unlike string.GetHashCode.</summary>
		static int StableHash(string value)
		{
			if (string.IsNullOrEmpty(value))
				return 0;

			unchecked
			{
				var hash = (uint)2166136261;
				foreach (var c in value)
				{
					hash ^= c;
					hash *= 16777619;
				}

				return (int)hash;
			}
		}
	}
}
