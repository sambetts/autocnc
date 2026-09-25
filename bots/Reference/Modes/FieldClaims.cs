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
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Which tiberium field each of this side's harvesters is bound for, shared by all of them.
	/// </summary>
	/// <remarks>
	/// <b>A fleet that cannot see itself moves as a herd.</b> Every harvester scores the same
	/// scan the same way, so every harvester picks the same field, and a field with two loads
	/// left receives seven harvesters. Five of them arrive to nothing, the next review calls the
	/// field worked out, and all seven cross to the next-best patch together — which is how the
	/// fleet on 16:9 went back and forth 23 cells between a 1,330-credit and an 840-credit patch
	/// every 31 seconds. <see cref="Logic.FieldOption.Saturated"/> is the rule; this is the
	/// memory it needs: each harvester's current assignment, identified by field centre as
	/// <see cref="Logic.HarvesterWatchdog.AssignedX"/> does, with the tick the assignment began.
	/// <para>
	/// The begin tick is what makes it fair. A harvester asking about its own field counts only
	/// the claims made before its own, so the ones that got there first keep the field and the
	/// last to arrive is the one told it is full. Ties go to the lower actor id, which keeps the
	/// answer identical on every client.
	/// </para>
	/// <para>
	/// Claims are refreshed on every evaluation and lapse after
	/// <see cref="Logic.HarvesterTuning.ClaimFreshTicks"/> without one, which is how a dead
	/// harvester lets go: nothing tells a mode its unit has died. Keyed on the owning player,
	/// like <see cref="ContestedGround"/>, so nothing leaks between sides or matches, and nothing
	/// here is map knowledge — every entry is somewhere this side sent its own harvester.
	/// </para>
	/// </remarks>
	public static class FieldClaims
	{
		sealed class Claim
		{
			public int X;
			public int Y;
			public int ClaimedTick;
			public int SeenTick;
		}

		sealed class Side
		{
			public readonly Dictionary<uint, Claim> ByActor = [];
		}

		static readonly ConditionalWeakTable<Player, Side> Sides = new();

		/// <summary>Notes, or refreshes, which field this harvester is working and since when.</summary>
		public static void Record(Player owner, uint actorId, int centerX, int centerY, int claimedTick, int tick)
		{
			if (owner == null || centerX == int.MinValue || centerY == int.MinValue)
				return;

			var side = Sides.GetOrCreateValue(owner);
			if (!side.ByActor.TryGetValue(actorId, out var claim))
			{
				claim = new Claim();
				side.ByActor[actorId] = claim;
			}

			claim.X = centerX;
			claim.Y = centerY;
			claim.ClaimedTick = claimedTick;
			claim.SeenTick = tick;
		}

		/// <summary>Withdraws this harvester's claim, if it holds one.</summary>
		public static void Release(Player owner, uint actorId)
		{
			if (owner != null && Sides.TryGetValue(owner, out var side))
				side.ByActor.Remove(actorId);
		}

		/// <summary>
		/// How many of this side's other harvesters are bound for the field centred within
		/// <paramref name="matchCells"/> of (x, y), counting only claims begun before
		/// <paramref name="claimedBefore"/>. Pass <c>int.MaxValue</c> to count every claim.
		/// </summary>
		public static int CountOthers(
			Player owner,
			uint actorId,
			int centerX,
			int centerY,
			int matchCells,
			int claimedBefore,
			int tick,
			int freshTicks)
		{
			if (owner == null || !Sides.TryGetValue(owner, out var side))
				return 0;

			var count = 0;
			foreach (var entry in side.ByActor)
			{
				if (entry.Key == actorId)
					continue;

				var claim = entry.Value;
				if (tick - claim.SeenTick > freshTicks)
					continue;

				if (claim.X < centerX - matchCells || claim.X > centerX + matchCells
					|| claim.Y < centerY - matchCells || claim.Y > centerY + matchCells)
					continue;

				if (claim.ClaimedTick > claimedBefore
					|| (claim.ClaimedTick == claimedBefore && entry.Key > actorId))
					continue;

				count++;
			}

			return count;
		}
	}
}
