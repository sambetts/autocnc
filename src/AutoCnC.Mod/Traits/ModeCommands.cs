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
using AutoCnC.Mod.Modes;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Chatbox commands for assigning modes. Attach this to the world actor.")]
	public class ModeCommandsInfo : TraitInfo<ModeCommands> { }

	/// <summary>
	/// Player-facing commands for the mode system.
	/// </summary>
	/// <remarks>
	/// Assignments are client-local policy, so these mutate <see cref="ModeExecutor.Assignments"/>
	/// directly rather than issuing orders. Only the resulting unit commands travel the network.
	/// </remarks>
	public class ModeCommands : IChatCommand, IWorldLoaded
	{
		World world;
		ModeExecutor executor;

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			executor = world.WorldActor.TraitOrDefault<ModeExecutor>();

			var console = world.WorldActor.TraitOrDefault<ChatCommands>();
			if (console == null)
				return;

			console.RegisterCommand("mode", this);
			console.RegisterCommand("modes", this);
			console.RegisterCommand("assignments", this);
			console.RegisterCommand("whatmode", this);
		}

		public void InvokeCommand(string name, string arg)
		{
			if (world.LocalPlayer == null || executor == null)
				return;

			switch (name)
			{
				case "modes":
					ListModes();
					break;

				case "assignments":
					ShowAssignments();
					break;

				case "whatmode":
					ReportSelection();
					break;

				case "mode":
					HandleMode(arg);
					break;
			}
		}

		void ListModes()
		{
			var names = ModeRegistry.AvailableModeNames.ToArray();
			Debug(names.Length == 0
				? "No modes found. Build the player-modes project and restart."
				: "Modes: " + string.Join(", ", names));

			foreach (var error in ModeRegistry.Errors)
				Debug("Mode problem: " + error);
		}

		void ShowAssignments()
		{
			var a = executor.Assignments;
			Debug($"all -> {a.GlobalMode ?? "<none>"}");

			foreach (var kv in a.UnitTypeAssignments)
				Debug($"type {kv.Key} -> {kv.Value}");

			foreach (var kv in a.GroupAssignments)
				Debug($"group {kv.Key} -> {kv.Value}");

			var overrides = executor.LocalUnits
				.Select(u => u.TraitOrDefault<ProgrammableController>())
				.Count(c => c != null && !string.IsNullOrEmpty(c.ModeOverride));

			if (overrides > 0)
				Debug($"{overrides} unit(s) have a per-unit override. Use '/mode clear' to drop them.");
		}

		void ReportSelection()
		{
			var selection = OwnedSelection();
			if (selection.Length == 0)
			{
				Debug("Nothing selected.");
				return;
			}

			var groups = selection
				.Select(a => a.TraitOrDefault<ProgrammableController>())
				.Where(c => c != null)
				.GroupBy(c => c.ActiveModeName ?? "<none>")
				.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

			Debug("Selection: " + string.Join(", ", groups.Select(g => $"{g.Count()}x {g.Key}")));
		}

		void HandleMode(string arg)
		{
			var parts = (arg ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				Usage();
				return;
			}

			switch (parts[0].ToLowerInvariant())
			{
				case "all":
					AssignAll(parts);
					return;

				case "type":
					AssignType(parts);
					return;

				case "group":
					AssignGroup(parts);
					return;

				case "clear":
					executor.ClearUnitOverrides();
					Debug("Cleared all per-unit overrides.");
					return;
			}

			// Bare "/mode X" applies to the current selection as a per-unit override.
			AssignSelection(parts[0]);
		}

		void AssignAll(string[] parts)
		{
			if (parts.Length < 2)
			{
				Debug("Usage: /mode all <ModeName>");
				return;
			}

			var mode = Canonical(parts[1]);
			if (mode == null)
				return;

			executor.Assignments.SetAll(mode);
			Debug($"All units -> {mode}");
		}

		void AssignType(string[] parts)
		{
			if (parts.Length < 3)
			{
				Debug("Usage: /mode type <actorType> <ModeName>   e.g. /mode type harv RunHomeMode");
				return;
			}

			var actorType = parts[1];
			if (!world.Map.Rules.Actors.ContainsKey(actorType.ToLowerInvariant()))
			{
				Debug($"Unknown actor type '{actorType}'. Select a unit and use /whatis to see its type.");
				return;
			}

			var mode = Canonical(parts[2]);
			if (mode == null)
				return;

			executor.Assignments.SetUnitType(actorType, mode);
			Debug($"All '{actorType}' -> {mode}");
		}

		void AssignGroup(string[] parts)
		{
			if (parts.Length < 3 || !int.TryParse(parts[1], out var group))
			{
				Debug("Usage: /mode group <1-9> <ModeName>");
				return;
			}

			if (!executor.Assignments.IsValidGroup(group))
			{
				Debug($"Group must be 1-{executor.Assignments.GroupCount}.");
				return;
			}

			var mode = Canonical(parts[2]);
			if (mode == null)
				return;

			executor.Assignments.SetGroup(group, mode);
			Debug($"Group {group} -> {mode}");
		}

		void AssignSelection(string modeName)
		{
			var mode = Canonical(modeName);
			if (mode == null)
				return;

			var selection = OwnedSelection();
			if (selection.Length == 0)
			{
				Debug("Nothing selected. Use '/mode all', '/mode type <t>' or '/mode group <n>' instead.");
				return;
			}

			foreach (var actor in selection)
			{
				var controller = actor.TraitOrDefault<ProgrammableController>();
				if (controller != null)
					controller.ModeOverride = mode;
			}

			Debug($"{selection.Length} selected unit(s) -> {mode}");
		}

		/// <summary>Validates a mode name and returns its canonical casing, or null with a message.</summary>
		string Canonical(string modeName)
		{
			var canonical = ModeRegistry.CanonicalName(modeName);
			if (canonical == null)
				Debug($"Unknown mode '{modeName}'. Known: {string.Join(", ", ModeRegistry.AvailableModeNames)}");

			return canonical;
		}

		Actor[] OwnedSelection() =>
			world.Selection.Actors
				.Where(a => a.Owner == world.LocalPlayer && !a.IsDead && a.IsInWorld
					&& a.TraitOrDefault<ProgrammableController>() != null)
				.ToArray();

		void Usage()
		{
			Debug("/mode <ModeName>                 selected units");
			Debug("/mode all <ModeName>             every unit");
			Debug("/mode type <actorType> <Mode>    e.g. /mode type harv RunHomeMode");
			Debug("/mode group <1-9> <ModeName>     e.g. /mode group 1 AttackBaseMode");
			Debug("/mode clear                      drop per-unit overrides");
			Debug("/modes  /assignments  /whatmode");
		}

		static void Debug(string message) => TextNotificationsManager.Debug(message);
	}
}
