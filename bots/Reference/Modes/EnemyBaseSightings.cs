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

using System.Runtime.CompilerServices;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Where this side last saw an enemy structure, shared by every unit it owns.
	/// </summary>
	/// <remarks>
	/// Sensing only ever returns what is visible right now, and it is centred on the unit doing
	/// the sensing. A whole army standing in its own base therefore sees no enemy building at
	/// all, because the other base is far beyond both its sight and the fog — which leaves an
	/// assault with nothing to march on.
	/// <para>
	/// So somebody has to remember. This is the one piece of knowledge that genuinely belongs to
	/// the side rather than to a unit: the scout that found their base is usually dead long
	/// before the tanks that need to know where it was.
	/// </para>
	/// <para>
	/// Keyed on the owning player rather than kept in a plain static field, so the memory
	/// belongs to one side of one match: it cannot leak into the next match, and two bots
	/// playing each other cannot read one another's sightings.
	/// </para>
	/// </remarks>
	public static class EnemyBaseSightings
	{
		sealed class Sighting
		{
			public CPos Cell;
			public bool Known;
		}

		static readonly ConditionalWeakTable<Player, Sighting> Sightings = new();

		/// <summary>Notes an enemy structure this side can currently see.</summary>
		public static void Record(Player owner, CPos cell)
		{
			if (owner == null)
				return;

			var sighting = Sightings.GetOrCreateValue(owner);
			sighting.Cell = cell;
			sighting.Known = true;
		}

		/// <summary>The last enemy structure this side saw, if it has ever seen one.</summary>
		public static bool TryGetLastKnown(Player owner, out CPos cell)
		{
			cell = default;
			if (owner == null || !Sightings.TryGetValue(owner, out var sighting) || !sighting.Known)
				return false;

			cell = sighting.Cell;
			return true;
		}

		/// <summary>
		/// Drops the sighting, for when a unit has arrived and found nothing there.
		/// </summary>
		/// <remarks>
		/// A remembered location that has since been levelled is worse than none: it holds the
		/// whole push staring at an empty crater. Forgetting lets the next thing anyone sees
		/// become the new objective.
		/// </remarks>
		public static void Forget(Player owner)
		{
			if (owner != null && Sightings.TryGetValue(owner, out var sighting))
				sighting.Known = false;
		}
	}
}
