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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Chatbox commands for assigning unit modes and control groups.",
		"Intended for play-testing until a proper UI exists. Attach to the world actor.")]
	public class ModeCommandsInfo : TraitInfo<ModeCommands> { }

	/// <summary>
	/// Lets a player drive the mode system from the chatbox:
	/// <list type="bullet">
	/// <item><c>/modes</c> — list available modes</item>
	/// <item><c>/mode &lt;ModeName&gt;</c> — set the current selection's mode</item>
	/// <item><c>/mode &lt;1-9&gt; &lt;ModeName&gt;</c> — set a control group's mode</item>
	/// <item><c>/group &lt;1-9&gt;</c> — assign the selection to a synced group</item>
	/// <item><c>/whatmode</c> — report the selection's current modes</item>
	/// </list>
	/// </summary>
	/// <remarks>
	/// This is the <b>input layer</b>, so unlike mode logic it may legitimately read
	/// client-local state (<c>world.Selection</c>, <c>world.LocalPlayer</c>, the engine's
	/// client-local <c>ControlGroups</c>). Its entire job is to convert local player intent into
	/// a synced <see cref="Order"/>, which is then resolved identically on every client.
	/// Nothing here touches the simulation directly.
	/// </remarks>
	public class ModeCommands : IChatCommand, IWorldLoaded
	{
		World world;

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;

			var console = world.WorldActor.TraitOrDefault<ChatCommands>();
			if (console == null)
				return;

			console.RegisterCommand("mode", this);
			console.RegisterCommand("modes", this);
			console.RegisterCommand("group", this);
			console.RegisterCommand("whatmode", this);
		}

		public void InvokeCommand(string name, string arg)
		{
			if (world.LocalPlayer == null)
				return;

			switch (name)
			{
				case "modes":
					TextNotificationsManager.Debug(
						"Available modes: " + string.Join(", ", ModeRegistry.AvailableModeNames));
					break;

				case "mode":
					SetMode(arg);
					break;

				case "group":
					SetGroup(arg);
					break;

				case "whatmode":
					ReportModes();
					break;
			}
		}

		void SetMode(string arg)
		{
			var parts = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				TextNotificationsManager.Debug("Usage: /mode <ModeName>  or  /mode <1-9> <ModeName>");
				return;
			}

			IEnumerable<Actor> targets;
			string modeName;

			if (parts.Length >= 2 && int.TryParse(parts[0], out var group))
			{
				modeName = parts[1];
				targets = ActorsInControlGroup(group);
				if (!targets.Any())
				{
					TextNotificationsManager.Debug($"Control group {group} is empty.");
					return;
				}
			}
			else
			{
				modeName = parts[0];
				targets = OwnedSelection();
				if (!targets.Any())
				{
					TextNotificationsManager.Debug("Nothing selected.");
					return;
				}
			}

			if (!ModeRegistry.IsKnownMode(modeName))
			{
				TextNotificationsManager.Debug(
					$"Unknown mode '{modeName}'. Available: {string.Join(", ", ModeRegistry.AvailableModeNames)}");
				return;
			}

			var order = ProgrammableController.CreateSetModeOrder(targets, modeName);
			if (order == null)
				return;

			world.IssueOrder(order);
			TextNotificationsManager.Debug($"Set {targets.Count()} unit(s) to {modeName}.");
		}

		void SetGroup(string arg)
		{
			if (!int.TryParse(arg.Trim(), out var group) || group < 1 || group > 9)
			{
				TextNotificationsManager.Debug("Usage: /group <1-9>");
				return;
			}

			var targets = OwnedSelection().ToArray();
			if (targets.Length == 0)
			{
				TextNotificationsManager.Debug("Nothing selected.");
				return;
			}

			var order = ProgrammableController.CreateSetGroupOrder(targets, group);
			if (order == null)
				return;

			world.IssueOrder(order);
			TextNotificationsManager.Debug($"Assigned {targets.Length} unit(s) to group {group}.");
		}

		void ReportModes()
		{
			var counts = new Dictionary<string, int>();
			foreach (var actor in OwnedSelection())
			{
				var controller = actor.TraitOrDefault<ProgrammableController>();
				var mode = controller?.ActiveModeName ?? "<none>";
				counts[mode] = counts.GetValueOrDefault(mode) + 1;
			}

			if (counts.Count == 0)
			{
				TextNotificationsManager.Debug("Nothing selected.");
				return;
			}

			TextNotificationsManager.Debug("Selection: " +
				string.Join(", ", counts.OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
					.Select(kv => $"{kv.Value}x {kv.Key}")));
		}

		IEnumerable<Actor> OwnedSelection()
		{
			return world.Selection.Actors.Where(a =>
				a.Owner == world.LocalPlayer && !a.IsDead && a.IsInWorld &&
				a.TraitOrDefault<ProgrammableController>() != null);
		}

		IEnumerable<Actor> ActorsInControlGroup(int group)
		{
			// The engine's control groups are 0-indexed internally but presented as 1-9.
			return world.ControlGroups.GetActorsInControlGroup(group - 1)
				.Where(a => a.Owner == world.LocalPlayer && !a.IsDead && a.IsInWorld &&
					a.TraitOrDefault<ProgrammableController>() != null);
		}
	}
}
