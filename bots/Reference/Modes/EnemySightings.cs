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

			for (var i = 0; i < threats.Count; i++)
			{
				var kind = threats[i].Kind;

				// Mobile only. Structures and static defences are permanent scenery once found,
				// so counting them would say "the enemy is mostly buildings" for the rest of
				// every match and the answer would never move again.
				var armour = kind == ThreatKind.Vehicle
					|| kind == ThreatKind.Aircraft
					|| kind == ThreatKind.Economy;

				if (!armour && kind != ThreatKind.Infantry)
					continue;

				if (!tally.Counted.Add(threats[i].ActorId))
					continue;

				if (armour)
					tally.Armour++;
				else
					tally.Infantry++;
			}
		}

		/// <summary>What this side has seen so far, or <see cref="EnemyMix.None"/> if nothing.</summary>
		public static EnemyMix Mix(Player owner) =>
			owner != null && Tallies.TryGetValue(owner, out var tally)
				? new EnemyMix(tally.Infantry, tally.Armour)
				: EnemyMix.None;
	}
}
