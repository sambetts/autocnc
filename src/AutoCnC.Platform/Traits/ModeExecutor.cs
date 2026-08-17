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
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Runs the loaded battle module for the local player and turns its decisions into orders.",
		"Attach this to the world actor.")]
	public class ModeExecutorInfo : TraitInfo
	{
		[Desc("Battle module to load at start. Leave empty to load the only installed module,",
			"or to wait for the player to pick one with /module.")]
		public readonly string DefaultModule = null;

		[Desc("Number of addressable control groups.")]
		public readonly int GroupCount = 9;

		[Desc("Maximum orders emitted per tick, to keep the order stream sane with a large army.")]
		public readonly int MaxOrdersPerTick = 20;

		[Desc("Log every decision to debug.log. Can also be toggled in-game with /modelog.")]
		public readonly bool LogDecisions = false;

		public override object Create(ActorInitializer init) { return new ModeExecutor(init.World, this); }
	}

	/// <summary>
	/// The host: runs whichever battle module is loaded, for the local player's units only.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The platform contains <b>no strategy</b>. With no module loaded this trait does nothing at
	/// all — nothing deploys, builds or shoots. All behaviour arrives from a
	/// <see cref="IBattleModule"/> the player authored or installed.
	/// </para>
	/// <para>
	/// Execution is outside the lockstep simulation: this is client-local and only ever looks at
	/// <c>world.LocalPlayer</c>'s units, emitting <see cref="Order"/>s — the same channel a human
	/// player's clicks use. So a module you wrote never has to exist on an opponent's machine.
	/// </para>
	/// </remarks>
	public class ModeExecutor : ITick, IWorldLoaded, IModeHost
	{
		readonly World world;
		readonly ModeExecutorInfo info;
		readonly List<Order> pending = [];

		/// <summary>The loaded module, or null if none.</summary>
		public BattleModuleDefinition Module { get; private set; }

		/// <summary>
		/// Live assignment state. Seeded from the module, then mutable so a player can override
		/// in-game without editing code.
		/// </summary>
		public ModeAssignments Assignments { get; private set; }

		/// <summary>Base construction plan from the loaded module. Empty if none.</summary>
		public IReadOnlyList<BuildStep> BuildPlan => Module?.BuildPlan ?? [];

		/// <summary>Unit production plan from the loaded module. Empty if none.</summary>
		public IReadOnlyList<ProductionStep> ProductionPlan => Module?.ProductionPlan ?? [];

		/// <summary>When set, every issued decision is written to debug.log. Toggle with /modelog.</summary>
		public bool LogDecisions { get; set; }

		public ModeExecutor(World world, ModeExecutorInfo info)
		{
			this.world = world;
			this.info = info;
			LogDecisions = info.LogDecisions;
			Assignments = new ModeAssignments(null, info.GroupCount);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// Load the configured module, or the only one installed. Anything more ambiguous is
			// left to the player so we never silently pick a strategy for them.
			var modules = ModuleLoader.Modules;

			if (!string.IsNullOrEmpty(info.DefaultModule))
				LoadModule(info.DefaultModule);
			else if (modules.Count == 1)
				LoadModule(modules[0].Definition.Name);
		}

		/// <summary>Loads a module by name. Returns false if it isn't installed.</summary>
		public bool LoadModule(string name)
		{
			var found = ModuleLoader.Find(name);
			if (found == null)
				return false;

			Module = found.Definition;
			Assignments = new ModeAssignments(found.Definition.Assignments.GlobalMode, info.GroupCount);

			// Copy the module's declared assignments into live, player-editable state.
			foreach (var kv in found.Definition.Assignments.UnitTypeAssignments)
				Assignments.SetUnitType(kv.Key, kv.Value);

			foreach (var kv in found.Definition.Assignments.GroupAssignments)
				Assignments.SetGroup(kv.Key, kv.Value);

			// Every unit re-resolves on the next tick, so a swap takes effect mid-match.
			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
				pair.Trait.ModeOverride = null;

			return true;
		}

		/// <summary>Creates a mode instance from the loaded module, or null.</summary>
		public IUnitMode CreateMode(string modeName)
		{
			if (Module == null || string.IsNullOrEmpty(modeName))
				return null;

			if (!Module.Modes.TryGetValue(modeName, out var type))
				return null;

			try
			{
				return (IUnitMode)Activator.CreateInstance(type);
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to construct mode '{modeName}': {ex}");
				return null;
			}
		}

		public bool IsKnownMode(string modeName) =>
			Module != null && !string.IsNullOrEmpty(modeName) && Module.Modes.ContainsKey(modeName);

		public string CanonicalModeName(string modeName)
		{
			if (Module == null || string.IsNullOrEmpty(modeName))
				return null;

			return Module.Modes.TryGetValue(modeName, out var type) ? type.Name : null;
		}

		public IEnumerable<string> AvailableModeNames =>
			Module == null
				? Array.Empty<string>()
				: Module.Modes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

		void ITick.Tick(Actor self)
		{
			var player = world.LocalPlayer;
			if (player == null || world.IsReplay || Module == null)
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

				if (world.WorldTick < controller.NextEvaluationTick)
					continue;

				var interval = Math.Max(1, controller.Info.TickInterval);
				controller.NextEvaluationTick = world.WorldTick + interval;

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
			catch (Exception ex)
			{
				// Module code runs here. One bad mode must not take the game down.
				Log.Write("debug", $"Mode '{controller.ActiveModeName}' threw on {actor.Info.Name}: {ex}");
				TextNotificationsManager.Debug($"Mode '{controller.ActiveModeName}' threw: {ex.Message}");
				controller.ModeOverride = null;
				controller.ApplyMode(null, this);
				return;
			}

			if (decision.Action == UnitAction.Continue)
				return;

			// A unit that is already idle has achieved Hold, so re-sending Stop would spam the
			// order stream every evaluation for every idle unit — which is most of an army, most
			// of the time.
			if (decision.Action == UnitAction.Hold && actor.IsIdle)
			{
				controller.LastIssued = decision;
				return;
			}

			// Re-issue only when the intent changed, or the unit has gone idle and still wants
			// something done. Otherwise a steady decision would emit an order every evaluation.
			var repeat = decision.SameIntent(controller.LastIssued);
			if (repeat && !actor.IsIdle)
				return;

			var order = controller.Context.BuildOrder(decision);
			if (order == null)
				return;

			if (LogDecisions)
				Log.Write("debug", $"[mode] {actor.Info.Name}#{actor.ActorID} {controller.ActiveModeName}: " +
					$"{decision.Action}{(decision.ItemName != null ? " " + decision.ItemName : "")} " +
					$"-> {order.OrderString} ({decision.Reason})");

			controller.LastIssued = decision;
			pending.Add(order);
		}

		/// <summary>Mirrors the engine's client-local control groups onto the controller.</summary>
		void SyncGroup(Actor actor, ProgrammableController controller)
		{
			var group = world.ControlGroups.GetControlGroupForActor(actor);
			controller.GroupId = group.HasValue ? group.Value + 1 : 0;
		}

		void SyncMode(Actor actor, ProgrammableController controller)
		{
			// Role lets a module say "whatever builds the base" without naming actor types.
			var role = controller.Info.Role;
			var byRole = role != null ? Assignments.GetUnitType(role) : null;

			var resolved = Assignments.Resolve(
				controller.ModeOverride, controller.GroupId, actor.Info.Name, byRole);

			if (resolved != null && !IsKnownMode(resolved))
				resolved = null;

			controller.ApplyMode(resolved, this);
		}

		/// <summary>Clears every per-unit override, so the module's assignments apply again.</summary>
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
