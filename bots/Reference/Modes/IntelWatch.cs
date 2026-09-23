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
using System.Runtime.CompilerServices;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// The side's one watcher: which unit it is, and what it is doing. See <see cref="IntelWatchLogic"/>.
	/// </summary>
	/// <remarks>
	/// The watcher is a role, not a unit type, and a doctrine can only assign modes by type. So
	/// every scout-capable type runs a <see cref="WatchOr{TInner}"/> wrapper in every doctrine,
	/// and the first of them alive after their base is found claims the role here. Every other
	/// unit of that type carries on with the doctrine's own mode. Keyed on the owning player like
	/// its siblings, so the role belongs to one side of one match, and it survives doctrine
	/// switches because the platform rebuilds mode instances on every switch.
	/// </remarks>
	public static class IntelWatch
	{
		const int ThreatRadius = 6 * 1024;
		const int SightRadius = 8 * 1024;
		const int StructureRadius = 10 * 1024;

		sealed class Role
		{
			public uint Watcher;
			public WatchMemory Memory;
		}

		static readonly ConditionalWeakTable<Player, Role> Roles = new();
		static readonly WatchTuning Tuning = WatchTuning.Default;

		/// <summary>Off switch for measuring the watcher's contribution on its own.</summary>
		public static bool Enabled { get; set; } = true;

		/// <summary>Whether this unit is the side's watcher, claiming the role if it is vacant.</summary>
		/// <remarks>
		/// Vacant until their base has been found: before that the opening's search is the
		/// better use of every fast unit, and there is nowhere yet to watch the approach to.
		/// </remarks>
		public static bool IsWatcher(Actor self, ModeContext ctx)
		{
			if (!Enabled || !EnemyBaseSightings.TryGetLastKnown(self.Owner, out _))
				return false;

			var role = Roles.GetOrCreateValue(self.Owner);
			if (role.Watcher == self.ActorID)
				return true;

			if (role.Watcher != 0 && ctx.ResolveActor(role.Watcher) != null)
				return false;

			role.Watcher = self.ActorID;
			role.Memory = WatchMemory.Start(ctx.WorldTick, Tuning);
			return true;
		}

		public static UnitDecision Tick(Actor self, ModeContext ctx)
		{
			if (!ctx.CanMove || !EnemyBaseSightings.TryGetLastKnown(self.Owner, out var enemy))
				return UnitDecision.Continue;

			var threats = ctx.SenseThreats(new WDist(SightRadius));
			EnemySightings.Record(self.Owner, threats);

			var structures = ctx.SenseStructures(new WDist(StructureRadius));
			EnemySightings.RecordStructures(self.Owner, structures);
			if (structures.Count > 0)
			{
				var found = ctx.ResolveActor(structures[0].ActorId);
				if (found != null)
					EnemyBaseSightings.Record(self.Owner, found.Location, ctx.WorldTick);
			}

			var threatened = false;
			var threatX = 0;
			var threatY = 0;
			for (var i = 0; i < threats.Count; i++)
			{
				if (!threats[i].CanHitUs || threats[i].DistanceUnits > ThreatRadius)
					continue;

				threatened = true;
				threatX = threats[i].CellX;
				threatY = threats[i].CellY;
				break;
			}

			var home = ctx.BaseCenter;
			var state = new WatchState(
				X: self.Location.X,
				Y: self.Location.Y,
				HomeX: home.X,
				HomeY: home.Y,
				EnemyX: enemy.X,
				EnemyY: enemy.Y,
				Threatened: threatened,
				ThreatX: threatX,
				ThreatY: threatY,
				Tick: ctx.WorldTick);

			var role = Roles.GetOrCreateValue(self.Owner);
			var outcome = IntelWatchLogic.Decide(state, role.Memory, Tuning);
			role.Memory = outcome.Memory;

			var bounds = ctx.World.Map.Bounds;
			var x = Math.Clamp(outcome.X, bounds.Left, bounds.Left + bounds.Width - 1);
			var y = Math.Clamp(outcome.Y, bounds.Top, bounds.Top + bounds.Height - 1);
			return UnitDecision.MoveTo(x, y, outcome.Reason, outcome.ReasonId);
		}
	}

	/// <summary>
	/// Runs the side's watcher duty for the one unit that holds the role, and the doctrine's own
	/// mode for every other unit of the type.
	/// </summary>
	/// <remarks>
	/// Abstract and generic, with one sealed subclass per inner mode, because the platform keys
	/// modes by type name and every closing of one generic type shares a name.
	/// </remarks>
	public abstract class WatchOr<TInner> : UnitMode where TInner : IUnitMode, new()
	{
		readonly TInner inner = new();

		public override void OnEnter(Actor self, ModeContext ctx) => inner.OnEnter(self, ctx);

		public override UnitDecision OnTick(Actor self, ModeContext ctx) =>
			IntelWatch.IsWatcher(self, ctx)
				? IntelWatch.Tick(self, ctx)
				: inner.OnTick(self, ctx);

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			EnemySightings.RecordAttacker(self.Owner, e.Attacker);
			if (IntelWatch.IsWatcher(self, ctx))
				ctx.RequestReevaluation();
			else
				inner.OnDamaged(self, ctx, e);
		}

		public override void OnExit(Actor self, ModeContext ctx) => inner.OnExit(self, ctx);
	}

	/// <summary>The opening's, scouting doctrine's and turtle's riflemen, which otherwise hold ground.</summary>
	/// <remarks>
	/// A rifleman rather than a buggy. The first watcher was a jeep or buggy, and it took the
	/// only fast escort a Nod opening has off its harvesters: every such version lost the Nod
	/// mirror that the champion wins. A rifleman costs 100 credits, the barracks replaces it
	/// without being asked, and it sees 5 cells, which is enough to see an army coming down the
	/// approach 20 cells before it arrives.
	/// </remarks>
	public sealed class DefendOrWatchMode : WatchOr<DefensiveMode> { }

	/// <summary>The push's riflemen, which otherwise join the assault.</summary>
	public sealed class AttackOrWatchMode : WatchOr<AttackBaseMode> { }
}
