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
using System.Runtime.CompilerServices;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Where this side's own buildings are being destroyed, shared by every unit it owns.
	/// </summary>
	/// <remarks>
	/// The third sibling of <see cref="EnemyBaseSightings"/> and <see cref="EnemySightings"/>,
	/// and the only one that needs no sensing at all: a side knows its own buildings exactly, so
	/// comparing this look's health against the last one says where it is being attacked without
	/// anybody having to see the attacker. That matters, because the things taking this bot's
	/// base apart outrange everything it can put in front of them — a rifleman standing on the
	/// refinery still cannot see the 11-cell gun shelling it, but the refinery's falling health
	/// is a perfectly good report that the fight is here.
	/// <para>
	/// Observed once per tick per side rather than once per unit: the answer is a fact about the
	/// side, and re-deriving it forty times a tick would cost forty walks of the building list
	/// for the same result. Whichever unit ticks first does the work and the rest read it, which
	/// is deterministic because the input is identical either way.
	/// </para>
	/// <para>
	/// Keyed on the owning player, like its siblings, so the memory belongs to one side of one
	/// match and cannot leak into the next one.
	/// </para>
	/// </remarks>
	public static class BaseDamageWatch
	{
		sealed class Watch
		{
			/// <summary>
			/// The health each owned building had at the last look.
			/// </summary>
			/// <remarks>
			/// Bounded by how many buildings this side ever owns, which is tens over a match, so
			/// it never needs pruning. A destroyed building's entry is inert: it is never seen
			/// again, so it can never look like a fresh hit.
			/// </remarks>
			public readonly Dictionary<uint, int> Health = [];

			public GuardPost Post;
			public int ObservedTick = int.MinValue;
		}

		static readonly ConditionalWeakTable<Player, Watch> Watches = new();

		/// <summary>Whether this tick's look has not been taken yet.</summary>
		/// <remarks>
		/// Asked before <see cref="Observe"/> so a unit that is not going to do the work does not
		/// have to pay for the building list to find that out.
		/// </remarks>
		public static bool NeedsObservation(Player owner, int tick) =>
			owner != null && (!Watches.TryGetValue(owner, out var watch) || watch.ObservedTick != tick);

		/// <summary>
		/// Notes the health of every owned building and moves the guard post to whatever just
		/// lost some.
		/// </summary>
		/// <remarks>
		/// The collection belongs to the call that produced it and is reused, so it is read here
		/// and never kept.
		/// </remarks>
		public static void Observe(
			Player owner,
			IReadOnlyCollection<OwnedBuildingState> buildings,
			int tick,
			in GuardPostTuning tuning)
		{
			if (owner == null || buildings == null)
				return;

			var watch = Watches.GetOrCreateValue(owner);
			if (watch.ObservedTick == tick)
				return;

			watch.ObservedTick = tick;

			var held = watch.Post;
			var heldStillStanding = false;
			var worstHitThisLook = GuardPost.None;

			foreach (var building in buildings)
			{
				var health = building.HealthPercent;
				var hit = watch.Health.TryGetValue(building.ActorId, out var previous) && health < previous;
				watch.Health[building.ActorId] = health;

				if (held.HasPost && building.ActorId == held.ActorId)
				{
					heldStillStanding = true;

					// Still standing, so keep the post on it — and keep it hot for as long as it
					// is still being shot, which is what stops the screen wandering off a fight
					// that has not finished.
					held = held with { X = building.CellX, Y = building.CellY, HealthPercent = health };
					if (hit)
						held = held with { Tick = tick };
				}

				if (!hit)
					continue;

				var candidate = new GuardPost(
					true, building.ActorId, building.ActorType, building.CellX, building.CellY, health, tick);

				if (!worstHitThisLook.HasPost || BaseGuardLogic.Prefer(candidate, worstHitThisLook))
					worstHitThisLook = candidate;
			}

			// A post whose building is gone is a crater. Drop it now rather than guarding the
			// spot until the memory expires.
			if (!heldStillStanding)
				held = GuardPost.None;

			watch.Post = BaseGuardLogic.Choose(held, worstHitThisLook, tick, tuning);
		}

		/// <summary>Where the base is currently being taken apart, if anywhere.</summary>
		public static bool TryGetPost(Player owner, int tick, in GuardPostTuning tuning, out GuardPost post)
		{
			post = GuardPost.None;

			if (owner == null || !Watches.TryGetValue(owner, out var watch))
				return false;

			if (!BaseGuardLogic.StillHot(watch.Post, tick, tuning))
				return false;

			post = watch.Post;
			return true;
		}
	}
}
