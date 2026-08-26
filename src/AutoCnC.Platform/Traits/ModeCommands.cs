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
using System.Globalization;
using System.Linq;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
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
			console.RegisterCommand("doctrine", this);
			console.RegisterCommand("doctrines", this);
			console.RegisterCommand("assignments", this);
			console.RegisterCommand("whatmode", this);
			console.RegisterCommand("modelog", this);
			console.RegisterCommand("speed", this);
		}

		public void InvokeCommand(string name, string arg)
		{
			// Answerable without a doctrine or a player: an observer watching a 20x match wants
			// to know what it is really running at just as much as the person who launched it.
			if (name == "speed")
			{
				ReportSpeed(arg);
				return;
			}

			if (world.LocalPlayer == null || executor == null)
				return;

			switch (name)
			{
				case "doctrines":
					ListDoctrines();
					break;

				case "doctrine":
					LoadDoctrine(arg);
					break;

				case "modes":
					ListModes();
					break;

				case "assignments":
					ShowAssignments();
					break;

				case "whatmode":
					ReportSelection();
					break;

				case "modelog":
					executor.LogDecisions = !executor.LogDecisions;
					Debug(executor.LogDecisions
						? "Decision logging ON. Every order your modules issue is written to debug.log."
						: "Decision logging OFF.");
					break;

				case "mode":
					HandleMode(arg);
					break;
			}
		}

		void ListDoctrines()
		{
			var modules = DoctrineLoader.Doctrines;
			if (modules.Count == 0)
			{
				Debug("No doctrines installed. AutoC&C ships no strategy of its own —");
				Debug("build one and drop it in: " + string.Join(" or ", DoctrineLoader.SearchPaths));
			}
			else
			{
				foreach (var module in modules)
				{
					var active = executor.Doctrine != null && executor.Doctrine.Name == module.Definition.Name;
					Debug($"{(active ? "* " : "  ")}{module.Definition.Name} — {module.Definition.Description}");
				}

				Debug("Load one with /doctrine <name>.");
			}

			foreach (var error in DoctrineLoader.Errors)
				Debug("Module problem: " + error);
		}

		void LoadDoctrine(string arg)
		{
			var name = (arg ?? string.Empty).Trim();
			if (string.IsNullOrEmpty(name))
			{
				Debug(executor.Doctrine == null
					? "No module loaded. /doctrines to see what's installed."
					: $"Loaded: {executor.Doctrine.Name} — {executor.Doctrine.Description}");
				return;
			}

			if (!executor.LoadDoctrine(name))
			{
				Debug($"No module named '{name}'. /doctrines to see what's installed.");
				return;
			}

			var module = executor.Doctrine;
			Debug($"Loaded {module.Name}: {module.Description}");
			Debug($"{module.BuildPlan.Count} build steps, {module.ProductionPlan.Count} production steps, " +
				$"{module.Modes.Count} modes. Every unit re-resolves on the next tick.");
		}

		void ListModes()
		{
			if (executor.Doctrine == null)
			{
				Debug("No doctrine loaded. /doctrines to see what's installed.");
				return;
			}

			var names = executor.AvailableModeNames.ToArray();
			Debug(names.Length == 0
				? $"Module '{executor.Doctrine.Name}' provides no modes."
				: $"Modes from {executor.Doctrine.Name}: " + string.Join(", ", names));
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
			var canonical = executor.CanonicalModeName(modeName);
			if (canonical == null)
				Debug($"Unknown mode '{modeName}'. Known: {string.Join(", ", executor.AvailableModeNames)}");

			return canonical;
		}

		Actor[] OwnedSelection() =>
			world.Selection.Actors
				.Where(a => a.Owner == world.LocalPlayer && !a.IsDead && a.IsInWorld
					&& a.TraitOrDefault<ProgrammableController>() != null)
				.ToArray();

		/// <summary>
		/// Answers "am I actually watching this at 40x?", which the chosen speed alone cannot, and
		/// sets the playback speed when watching a replay.
		/// </summary>
		/// <remarks>
		/// A doctrine judged at a speed the machine never reached is a doctrine judged against the
		/// wrong clock, so the requested and the measured speed are both reported, always.
		/// </remarks>
		void ReportSpeed(string arg)
		{
			var turbo = world.WorldActor.TraitOrDefault<TurboSpeed>();
			if (turbo == null)
			{
				Debug($"{world.Timestep}ms per tick.");
				return;
			}

			var requested = (arg ?? string.Empty).Trim();
			if (requested.Length > 0 && !ChangeSpeed(turbo, requested))
				return;

			Debug($"Asked for {turbo.RequestedSpeed:0.#}x ({turbo.EffectiveTimestep}ms per tick).");

			if (turbo.AchievedSpeed <= 0)
				Debug("Still measuring what this machine manages.");
			else if (turbo.IsFallingShort)
				Debug($"Managing {turbo.AchievedSpeed:0.#}x — {turbo.ShortfallReason}.");
			else
				Debug($"Managing {turbo.AchievedSpeed:0.#}x.");
		}

		/// <summary>
		/// Applies <c>/speed 2</c>, <c>/speed 0.5</c> or <c>/speed max</c> while watching a replay.
		/// </summary>
		/// <remarks>
		/// Only replays can change speed: a live match's tick rate is a lobby option that every
		/// client agreed on before the first tick, and is fixed for the duration.
		/// </remarks>
		bool ChangeSpeed(TurboSpeed turbo, string requested)
		{
			if (!world.IsReplay)
			{
				Debug("Speed is set in the lobby and fixed once a match starts. It can be changed while watching a replay.");
				return false;
			}

			var multiplier = requested.Equals("max", StringComparison.OrdinalIgnoreCase)
				? TurboSpeed.NominalTimestep
				: ParseMultiplier(requested);

			if (multiplier < 0)
			{
				Debug($"'{requested}' is not a speed. Try /speed 1, /speed 0.5, /speed 8 or /speed max.");
				return false;
			}

			turbo.SetReplaySpeed(multiplier);
			return true;
		}

		/// <summary>Accepts both <c>4</c> and <c>4x</c>, since people type both.</summary>
		static float ParseMultiplier(string requested)
		{
			var value = requested.TrimEnd('x', 'X');
			return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
				? parsed
				: -1;
		}

		void Usage()
		{
			Debug("/mode <ModeName>                 selected units");
			Debug("/mode all <ModeName>             every unit");
			Debug("/mode type <actorType> <Mode>    e.g. /mode type harv RunHomeMode");
			Debug("/mode group <1-9> <ModeName>     e.g. /mode group 1 AttackBaseMode");
			Debug("/mode clear                      drop per-unit overrides");
			Debug("/modes  /assignments  /whatmode  /modelog  /speed");
		}

		static void Debug(string message) => TextNotificationsManager.Debug(message);
	}
}
