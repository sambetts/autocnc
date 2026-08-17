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
using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Runs unit modes for the local player and turns their decisions into orders.",
		"Attach this to the world actor.")]
	public class ModeExecutorInfo : TraitInfo
	{
		[Desc("Mode every unit starts on before the player assigns anything.")]
		public readonly string DefaultMode = "DefensiveMode";

		[Desc("Number of addressable control groups.")]
		public readonly int GroupCount = 9;

		[Desc("Maximum orders emitted per tick, to keep the order stream sane with a large army.")]
		public readonly int MaxOrdersPerTick = 20;

		public override object Create(ActorInitializer init) { return new ModeExecutor(init.World, this); }
	}

	/// <summary>
	/// Drives every programmable unit the local player owns.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>This runs outside the simulation.</b> It is a client-local trait that only ever looks at
	/// <c>world.LocalPlayer</c>'s units, and its sole output is <see cref="Order"/>s — the same
	/// channel a human player's clicks travel down. Every client runs modes for its own units
	/// only, so your custom mode code never needs to exist on an opponent's machine, and their
	/// code never runs on yours.
	/// </para>
	/// <para>
	/// The cost of that safety is order latency (a few ticks), which is exactly the latency a
	/// human player's input already has.
	/// </para>
	/// </remarks>
	public class ModeExecutor : ITick, IWorldLoaded
	{
		readonly World world;
		readonly ModeExecutorInfo info;
		readonly List<Order> pending = [];

		/// <summary>Client-local mode assignment policy for the local player.</summary>
		public ModeAssignments Assignments { get; }

		public ModeExecutor(World world, ModeExecutorInfo info)
		{
			this.world = world;
			this.info = info;
			Assignments = new ModeAssignments(info.DefaultMode, info.GroupCount);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr) { }

		void ITick.Tick(Actor self)
		{
			var player = world.LocalPlayer;
			if (player == null || world.IsReplay)
				return;

			pending.Clear();

			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
			{
				var actor = pair.Actor;
				var controller = pair.Trait;

				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld || controller.IsTraitDisabled)
					continue;

				SyncGroup(actor, controller);
				SyncMode(actor, controller);

				if (controller.ActiveMode == null)
					continue;

				// Stagger evaluations across units so a large army does not spike a single tick.
				if (world.WorldTick < controller.NextEvaluationTick)
					continue;

				var interval = info.MaxOrdersPerTick > 0 && controller.Info.TickInterval < 1 ? 1 : controller.Info.TickInterval;
				controller.NextEvaluationTick = world.WorldTick + (interval < 1 ? 1 : interval);

				Evaluate(actor, controller);

				if (pending.Count >= info.MaxOrdersPerTick)
					break;
			}

			foreach (var order in pending)
				world.IssueOrder(order);
		}

		void Evaluate(Actor actor, ProgrammableController controller)
		{
			UnitDecision decision;

			try
			{
				decision = controller.ActiveMode.OnTick(actor, controller.Context);
			}
			catch (System.Exception ex)
			{
				// Player-authored code runs here. One bad mode must not take the game down, so
				// report it, drop the unit's mode, and carry on.
				Log.Write("debug", $"Mode '{controller.ActiveModeName}' threw on {actor.Info.Name}: {ex}");
				TextNotificationsManager.Debug($"Mode '{controller.ActiveModeName}' threw: {ex.Message}");
				controller.ModeOverride = null;
				controller.ApplyMode(null);
				return;
			}

			if (decision.Action == UnitAction.Continue)
				return;

			// Re-issue only when the intent actually changed, or when the unit has gone idle and
			// still wants something done. Without this a mode returning a steady decision would
			// emit an order per unit per evaluation and swamp the order stream.
			var repeat = decision.SameIntent(controller.LastIssued);
			if (repeat && !actor.IsIdle)
				return;

			var order = controller.Context.BuildOrder(decision);
			if (order == null)
				return;

			controller.LastIssued = decision;
			pending.Add(order);
		}

		/// <summary>
		/// Mirrors the engine's client-local control groups onto the controller.
		/// </summary>
		/// <remarks>
		/// Now that modes are outside the simulation, OpenRA's built-in ControlGroups trait is
		/// exactly the right source: it is client-local, and so is mode assignment.
		/// </remarks>
		void SyncGroup(Actor actor, ProgrammableController controller)
		{
			var group = world.ControlGroups.GetControlGroupForActor(actor);
			controller.GroupId = group.HasValue ? group.Value + 1 : 0;
		}

		void SyncMode(Actor actor, ProgrammableController controller)
		{
			var resolved = Assignments.Resolve(controller.ModeOverride, controller.GroupId, actor.Info.Name);

			if (resolved != null && !ModeRegistry.IsKnownMode(resolved))
				resolved = null;

			controller.ApplyMode(resolved);
		}

		/// <summary>Clears every per-unit override, so broader assignments take effect again.</summary>
		public void ClearUnitOverrides()
		{
			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
				if (pair.Actor.Owner == world.LocalPlayer)
					pair.Trait.ModeOverride = null;
		}

		/// <summary>Every programmable unit the local player owns.</summary>
		public IEnumerable<Actor> LocalUnits =>
			world.ActorsWithTrait<ProgrammableController>()
				.Where(p => p.Actor.Owner == world.LocalPlayer && !p.Actor.IsDead && p.Actor.IsInWorld)
				.Select(p => p.Actor);
	}
}
