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
using System.Runtime.CompilerServices;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// What kind of army this side has watched the enemy field, shared by every unit it owns.
	/// </summary>
	/// <remarks>
	/// The sibling of <see cref="EnemyBaseSightings"/>, and for the same reason. Sensing is
	/// per-unit, shroud-filtered and momentary, so the barracks deciding what to train can see
	/// nothing at all while the army twenty cells away is being taken apart by tanks. Somebody
	/// has to remember, and what the enemy is <em>made of</em> belongs to the side rather than
	/// to whichever scout happened to live long enough to notice.
	/// <para>
	/// Counted once per enemy actor rather than once per evaluation. An army in contact
	/// re-senses the same tank several times a second, so tallying observations would measure
	/// how long this side stood next to something instead of how much of it there was — and a
	/// single harvester watched for four minutes would outvote a whole tank column.
	/// </para>
	/// <para>
	/// Keyed on the owning player rather than kept in a plain static field, so the memory belongs
	/// to one side of one match: it cannot leak into the next match, and two bots playing each
	/// other cannot read one another's sightings.
	/// </para>
	/// <para>
	/// Fair play throughout. Nothing here is recorded that a unit of this side did not sense
	/// through <c>ModeContext</c>, which is shroud-filtered exactly as a human player's screen
	/// is; there is no telemetry, no map knowledge and no enemy roster.
	/// </para>
	/// </remarks>
	public static class EnemySightings
	{
		/// <summary>One enemy actor as this side last saw it.</summary>
		sealed class Seen
		{
			public string ActorType;
			public int Value;
			public int Tick;
		}

		sealed class Tally
		{
			/// <summary>
			/// Enemy actors already counted, so a long look is not mistaken for a big army.
			/// </summary>
			/// <remarks>
			/// Bounded by how many enemy actors this side ever lays eyes on, which is tens over a
			/// match rather than thousands, so it never needs pruning. Ids are not reused inside
			/// a match, so a dead actor staying in the set is correct: it was still something the
			/// enemy fielded.
			/// </remarks>
			public readonly HashSet<uint> Counted = [];

			public int Infantry;
			public int Armour;

			/// <summary>Every enemy combat unit seen, by actor id, as of its latest sighting.</summary>
			/// <remarks>
			/// What <see cref="RecentThreats"/> reads. Keyed on the id so a unit watched for a
			/// minute counts once, at the value it was last seen with.
			/// </remarks>
			public readonly Dictionary<uint, Seen> Units = [];

			/// <summary>Enemy structure types seen, and when each was last seen.</summary>
			public readonly Dictionary<string, int> Structures = new(StringComparer.OrdinalIgnoreCase);
		}

		static readonly ConditionalWeakTable<Player, Tally> Tallies = new();

		/// <summary>Notes every enemy this unit can currently see.</summary>
		/// <remarks>
		/// The list belongs to the sensing call and is reused, so it is read here and never kept.
		/// </remarks>
		public static void Record(Player owner, IReadOnlyList<ThreatSnapshot> threats)
		{
			if (owner == null || threats == null || threats.Count == 0)
				return;

			var tally = Tallies.GetOrCreateValue(owner);
			var tick = owner.World.WorldTick;

			for (var i = 0; i < threats.Count; i++)
			{
				var kind = threats[i].Kind;
				if (kind == ThreatKind.Structure || kind == ThreatKind.Defence)
				{
					NoteStructure(tally, threats[i].ActorType, tick);
					continue;
				}

				// Mobile only. Structures and static defences are permanent scenery once found,
				// so counting them would say "the enemy is mostly buildings" for the rest of
				// every match and the answer would never move again.
				var armour = kind == ThreatKind.Vehicle
					|| kind == ThreatKind.Aircraft
					|| kind == ThreatKind.Economy;

				if (!armour && kind != ThreatKind.Infantry)
					continue;

				if (kind != ThreatKind.Economy)
					NoteUnit(tally, threats[i].ActorId, threats[i].ActorType, threats[i].Value, tick);

				if (!tally.Counted.Add(threats[i].ActorId))
					continue;

				if (armour)
					tally.Armour++;
				else
					tally.Infantry++;
			}
		}

		/// <summary>Notes the enemy structures a unit can currently see, for what they unlock.</summary>
		public static void RecordStructures(Player owner, IReadOnlyList<ThreatSnapshot> structures)
		{
			if (owner == null || structures == null || structures.Count == 0)
				return;

			var tally = Tallies.GetOrCreateValue(owner);
			var tick = owner.World.WorldTick;
			for (var i = 0; i < structures.Count; i++)
				NoteStructure(tally, structures[i].ActorType, tick);
		}

		/// <summary>
		/// Notes whatever just damaged one of this side's actors, seen or not.
		/// </summary>
		/// <remarks>
		/// The guide's one sanctioned exception to visibility: the attacked unit is told who hit
		/// it, exactly as a player is. This is how artillery firing from beyond sight gets into
		/// the threat mix at all — in 16 fights at d1a695b, enemy msam and arty usually hit
		/// this side before anything of it had seen them. Only the attacker's type and value are
		/// kept; nothing here is generalised into map knowledge.
		/// </remarks>
		public static void RecordAttacker(Player owner, Actor attacker)
		{
			if (owner == null || attacker == null || attacker.IsDead || !attacker.IsInWorld
				|| attacker.Owner == owner || attacker.Owner.NonCombatant)
				return;

			var kind = ModeContext.Classify(attacker);
			if (kind != ThreatKind.Infantry && kind != ThreatKind.Vehicle && kind != ThreatKind.Aircraft)
				return;

			var value = attacker.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			NoteUnit(Tallies.GetOrCreateValue(owner), attacker.ActorID, attacker.Info.Name, value, owner.World.WorldTick);
		}

		/// <summary>What this side has seen so far, or <see cref="EnemyMix.None"/> if nothing.</summary>
		public static EnemyMix Mix(Player owner) =>
			owner != null && Tallies.TryGetValue(owner, out var tally)
				? new EnemyMix(tally.Infantry, tally.Armour)
				: EnemyMix.None;

		/// <summary>
		/// The enemy combat units seen within the last <paramref name="windowTicks"/>, summed by
		/// type, plus aircraft anticipated from a helipad seen before any aircraft were.
		/// </summary>
		public static IReadOnlyList<Threat> RecentThreats(Player owner, int tick, int windowTicks, int anticipatedAircraftValue)
		{
			if (owner == null || !Tallies.TryGetValue(owner, out var tally))
				return [];

			var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var aircraftSeen = false;
			foreach (var seen in tally.Units.Values)
			{
				if (tick - seen.Tick > windowTicks)
					continue;

				byType.TryGetValue(seen.ActorType, out var total);
				byType[seen.ActorType] = total + seen.Value;
				aircraftSeen |= IsAircraft(seen.ActorType);
			}

			// A pad seen is a pad that builds. The matchup table knows which of this side's units
			// answer each aircraft, so anticipation only has to name them.
			if (!aircraftSeen && anticipatedAircraftValue > 0 && tally.Structures.ContainsKey("hpad"))
			{
				byType["orca"] = anticipatedAircraftValue / 2;
				byType["heli"] = anticipatedAircraftValue / 2;
			}

			var threats = new List<Threat>(byType.Count);
			foreach (var kv in byType)
				threats.Add(new Threat(kv.Key, kv.Value));

			// Stable for identical sightings, so the same fight always produces the same plan.
			threats.Sort((a, b) => string.CompareOrdinal(a.ActorType, b.ActorType));
			return threats;
		}

		/// <summary>Whether this side has ever seen an enemy structure of this type.</summary>
		public static bool HasSeenStructure(Player owner, string actorType) =>
			owner != null && Tallies.TryGetValue(owner, out var tally) && tally.Structures.ContainsKey(actorType);

		static bool IsAircraft(string actorType) =>
			string.Equals(actorType, "orca", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(actorType, "heli", StringComparison.OrdinalIgnoreCase);

		static void NoteUnit(Tally tally, uint actorId, string actorType, int value, int tick)
		{
			if (actorId == 0 || string.IsNullOrEmpty(actorType))
				return;

			if (!tally.Units.TryGetValue(actorId, out var seen))
			{
				seen = new Seen { ActorType = actorType };
				tally.Units[actorId] = seen;
			}

			seen.Value = value;
			seen.Tick = tick;
		}

		static void NoteStructure(Tally tally, string actorType, int tick)
		{
			if (!string.IsNullOrEmpty(actorType))
				tally.Structures[actorType] = tick;
		}
	}
}
