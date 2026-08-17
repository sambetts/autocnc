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
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Tracks synced control-group membership and per-group default modes.",
		"This is deliberately NOT OpenRA's built-in ControlGroups trait: that one reads",
		"world.Selection and filters on world.LocalPlayer, making it client-local UI state.",
		"Unit behaviour must be driven by synced simulation state or clients desync.")]
	public class GroupManagerInfo : TraitInfo
	{
		[Desc("Number of addressable control groups. Groups are numbered 1..GroupCount.")]
		public readonly int GroupCount = 9;

		public override object Create(ActorInitializer init) { return new GroupManager(init.World, this); }
	}

	/// <summary>
	/// World trait owning the authoritative, synced mapping of units to control groups.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Membership lives on each <see cref="ProgrammableController"/> (the synced source of truth);
	/// this trait maintains a per-player index over those controllers so group-wide queries and
	/// broadcasts are cheap, and remembers each group's default mode so that newly produced units
	/// joining a group immediately adopt its behaviour.
	/// </para>
	/// <para>
	/// Every mutation here originates from an <see cref="Order"/>, so it is applied identically
	/// and in the same tick on every client.
	/// </para>
	/// </remarks>
	public class GroupManager : ITick
	{
		readonly World world;
		readonly GroupManagerInfo info;

		readonly Dictionary<Player, List<ProgrammableController>[]> members = [];
		readonly Dictionary<Player, string[]> groupModes = [];

		public int GroupCount => info.GroupCount;

		public GroupManager(World world, GroupManagerInfo info)
		{
			this.world = world;
			this.info = info;
		}

		public bool IsValidGroup(int group) => group >= 1 && group <= info.GroupCount;

		List<ProgrammableController>[] MembersFor(Player player)
		{
			if (!members.TryGetValue(player, out var slots))
			{
				// Index 0 is the "unassigned" bucket, so group N maps directly to slots[N].
				slots = new List<ProgrammableController>[info.GroupCount + 1];
				for (var i = 0; i < slots.Length; i++)
					slots[i] = [];

				members[player] = slots;
			}

			return slots;
		}

		string[] ModesFor(Player player)
		{
			if (!groupModes.TryGetValue(player, out var modes))
			{
				modes = new string[info.GroupCount + 1];
				groupModes[player] = modes;
			}

			return modes;
		}

		internal void Register(ProgrammableController controller)
		{
			var slots = MembersFor(controller.Owner);
			var group = IsValidGroup(controller.GroupId) ? controller.GroupId : 0;
			if (!slots[group].Contains(controller))
				slots[group].Add(controller);
		}

		internal void Unregister(ProgrammableController controller)
		{
			if (!members.TryGetValue(controller.Owner, out var slots))
				return;

			foreach (var slot in slots)
				slot.Remove(controller);
		}

		/// <summary>Moves a controller between group buckets. Called by the controller itself.</summary>
		internal void Reindex(ProgrammableController controller, Player owner, int previousGroup, int newGroup)
		{
			var slots = MembersFor(owner);

			var from = IsValidGroup(previousGroup) ? previousGroup : 0;
			var to = IsValidGroup(newGroup) ? newGroup : 0;

			slots[from].Remove(controller);
			if (!slots[to].Contains(controller))
				slots[to].Add(controller);
		}

		public IEnumerable<ProgrammableController> ControllersInGroup(Player player, int group)
		{
			if (!IsValidGroup(group) || !members.TryGetValue(player, out var slots))
				return [];

			return slots[group].Where(c => !c.Self.IsDead && c.Self.IsInWorld);
		}

		/// <summary>The mode newly assigned units adopt when they join this group, or null.</summary>
		public string GetGroupMode(Player player, int group)
		{
			if (!IsValidGroup(group))
				return null;

			return ModesFor(player)[group];
		}

		/// <summary>
		/// Sets a group's default mode and applies it to every current member.
		/// </summary>
		/// <remarks>
		/// Must only be called from order resolution so that all clients apply it on the same tick.
		/// </remarks>
		public void SetGroupMode(Player player, int group, string modeName)
		{
			if (!IsValidGroup(group))
				return;

			ModesFor(player)[group] = modeName;

			foreach (var controller in ControllersInGroup(player, group).ToArray())
				controller.SetMode(modeName);
		}

		void ITick.Tick(Actor self)
		{
			// Reap dead units. Cheap, and keeps group queries from growing unboundedly over a
			// long match. Removal is by identity, so it stays deterministic.
			foreach (var slots in members.Values)
				foreach (var slot in slots)
					slot.RemoveAll(c => c.Self.Disposed);
		}

		// TODO: implement IGameSaveTraitData to persist group membership and per-group modes
		// across save/load, mirroring how the engine's ControlGroups trait does it.
	}
}
