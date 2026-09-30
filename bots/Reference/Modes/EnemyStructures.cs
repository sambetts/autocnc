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
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Every enemy building this side has seen, and where, shared by every unit it owns.
	/// </summary>
	/// <remarks>
	/// The sibling of <see cref="EnemyBaseSightings"/>, which keeps one cell to march on. This keeps
	/// each building, because a siege piece that cannot see as far as it shoots needs to know what
	/// stands where before anybody walks up to look (<see cref="SiegeTargetLogic"/>), and a line
	/// unit that cannot see a tower from outside its reach needs to know the tower is there
	/// (<see cref="StandOffLogic"/>).
	/// <para>
	/// Fair play: a building is recorded only when <c>SenseStructures</c> or <c>SenseThreats</c>
	/// returns it, and both are shroud-filtered. The side-wide survey is one
	/// <c>SenseStructures</c> call over the whole map, which returns only what some unit of this side
	/// can see right now — what a human sees on the minimap. A building is forgotten when
	/// <c>ResolveActor</c> no longer finds it, the same test <see cref="AttackBaseMode"/> has always
	/// used to drop a destroyed objective, and the same one the SDK applies before it will build an
	/// attack order at all.
	/// </para>
	/// <para>
	/// Keyed on the owning player, so the memory belongs to one side of one match.
	/// </para>
	/// </remarks>
	public static class EnemyStructures
	{
		sealed class Entry
		{
			public string ActorType;
			public ThreatKind Kind;
			public int CellX;
			public int CellY;
			public int RangeUnits;
			public bool GroundDefence;
		}

		sealed class Memory
		{
			public readonly Dictionary<uint, Entry> Seen = [];
			public readonly List<uint> Gone = [];
			public int SurveyTick = int.MinValue;
			public int SiegeTick = int.MinValue;
			public int SiegeCount;
		}

		static readonly ConditionalWeakTable<Player, Memory> Memories = new();

		/// <summary>How often the side looks over everything it can see, in ticks.</summary>
		/// <remarks>
		/// Ten ticks, 0.4 game seconds: a scout that glimpses a tower and dies to it in the next
		/// second (258-259s on 16:9) is still caught, and one whole-map sense per 0.4s is cheap.
		/// </remarks>
		const int SurveyIntervalTicks = 10;

		/// <summary>How often the side recounts its own siege pieces, in ticks.</summary>
		const int SiegeCountIntervalTicks = 25;

		/// <summary>Further than any two cells on a skirmish map, so the survey sees everything visible.</summary>
		static readonly WDist SurveyRadius = WDist.FromCells(160);

		/// <summary>
		/// Records every enemy building any unit of this side can see right now, at most once every
		/// <see cref="SurveyIntervalTicks"/> for the side however many units call it.
		/// </summary>
		public static void Survey(Actor self, ModeContext ctx)
		{
			if (self == null || ctx == null || self.Owner == null)
				return;

			var memory = Memories.GetOrCreateValue(self.Owner);
			var tick = ctx.WorldTick;
			if (memory.SurveyTick != int.MinValue
				&& tick >= memory.SurveyTick
				&& tick - memory.SurveyTick < SurveyIntervalTicks)
				return;

			memory.SurveyTick = tick;
			var seen = ctx.SenseStructures(SurveyRadius);
			for (var i = 0; i < seen.Count; i++)
				Remember(memory, seen[i]);
		}

		/// <summary>
		/// Records the enemy buildings in a list this unit has just sensed.
		/// </summary>
		/// <remarks>
		/// <paramref name="sensed"/> may be a <c>SenseThreats</c> buffer, which mixes units with
		/// buildings, so only kinds that are always buildings are taken from it; a refinery and a
		/// harvester share <see cref="ThreatKind.Economy"/>, and refineries arrive through
		/// <see cref="Survey"/>. Read here and never kept, because the buffer is reused.
		/// </remarks>
		public static void Record(Player owner, IReadOnlyList<ThreatSnapshot> sensed)
		{
			if (owner == null || sensed == null || sensed.Count == 0)
				return;

			Memory memory = null;
			for (var i = 0; i < sensed.Count; i++)
			{
				var kind = sensed[i].Kind;
				if (kind != ThreatKind.Defence && kind != ThreatKind.Structure)
					continue;

				memory ??= Memories.GetOrCreateValue(owner);
				Remember(memory, sensed[i]);
			}
		}

		static void Remember(Memory memory, in ThreatSnapshot s)
		{
			if (s.Kind != ThreatKind.Defence && s.Kind != ThreatKind.Structure && s.Kind != ThreatKind.Economy)
				return;

			if (!memory.Seen.TryGetValue(s.ActorId, out var entry))
			{
				entry = new Entry();
				memory.Seen[s.ActorId] = entry;
			}

			entry.ActorType = s.ActorType;
			entry.Kind = s.Kind;
			entry.CellX = s.CellX;
			entry.CellY = s.CellY;

			// A tower seen while its owner was short of power reads as unarmed. Keep the best
			// reading rather than the latest, so a brown-out does not make this side forget one.
			if (s.WeaponRangeUnits > entry.RangeUnits)
				entry.RangeUnits = s.WeaponRangeUnits;

			entry.GroundDefence |= s.Kind == ThreatKind.Defence && s.CanHitUs;
		}

		/// <summary>
		/// The live buildings this side remembers within <paramref name="maxDistanceUnits"/> of this
		/// unit, measured from it, into <paramref name="into"/>.
		/// </summary>
		/// <param name="self">The asking unit.</param>
		/// <param name="ctx">Its context.</param>
		/// <param name="maxDistanceUnits">How far from it to look.</param>
		/// <param name="groundDefencesOnly">Only towers that can shoot a ground unit.</param>
		/// <param name="into">Cleared and filled; the caller owns it.</param>
		public static void Nearby(
			Actor self,
			ModeContext ctx,
			int maxDistanceUnits,
			bool groundDefencesOnly,
			List<RememberedStructure> into)
		{
			into.Clear();
			if (self == null || ctx == null || self.Owner == null
				|| !Memories.TryGetValue(self.Owner, out var memory) || memory.Seen.Count == 0)
				return;

			memory.Gone.Clear();
			foreach (var pair in memory.Seen)
			{
				var e = pair.Value;
				if (groundDefencesOnly && !e.GroundDefence)
					continue;

				var distance = ctx.DistanceTo(new CPos(e.CellX, e.CellY));
				if (distance > maxDistanceUnits)
					continue;

				var actor = ctx.ResolveActor(pair.Key);
				if (actor == null)
				{
					memory.Gone.Add(pair.Key);
					continue;
				}

				into.Add(new RememberedStructure(
					ActorId: pair.Key,
					ActorType: e.ActorType,
					Kind: e.Kind,
					CellX: e.CellX,
					CellY: e.CellY,
					RangeUnits: e.RangeUnits,
					GroundDefence: e.GroundDefence,
					DistanceUnits: distance,
					Visible: ModeContext.IsVisibleEnemy(self.Owner, actor),
					IsAttackable: ctx.CanAttack(actor)));
			}

			for (var i = 0; i < memory.Gone.Count; i++)
				memory.Seen.Remove(memory.Gone[i]);
		}

		/// <summary>
		/// How many siege pieces this side has alive anywhere, recounted at most once a second.
		/// </summary>
		/// <remarks>
		/// This side's own units, which it knows exactly. Live pieces only: a queued one may wait
		/// minutes for cash, and a line unit waiting on it would wait for nothing.
		/// </remarks>
		public static int OwnSiege(Actor self, ModeContext ctx)
		{
			if (self == null || ctx == null || self.Owner == null)
				return 0;

			var memory = Memories.GetOrCreateValue(self.Owner);
			var tick = ctx.WorldTick;
			if (memory.SiegeTick != int.MinValue
				&& tick >= memory.SiegeTick
				&& tick - memory.SiegeTick < SiegeCountIntervalTicks)
				return memory.SiegeCount;

			var count = 0;
			foreach (var actor in ctx.World.ActorsHavingTrait<Mobile>())
			{
				if (actor.Owner != self.Owner || actor.IsDead || !actor.IsInWorld)
					continue;

				if (ArmyMixLogic.Names(ReferencePlans.SiegeVehicles, actor.Info.Name))
					count++;
			}

			memory.SiegeTick = tick;
			memory.SiegeCount = count;
			return count;
		}
	}
}
