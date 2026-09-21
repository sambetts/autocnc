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
using AutoCnC.Reference.Logic;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Which rung of the search ladder this side's push is hunting on, shared by every unit in it.
	/// </summary>
	/// <remarks>
	/// The rung is the side's and not the unit's, and that is the whole point. A search each unit
	/// ran privately would send an army of 180 actors to 180 different corners the first time it
	/// ran out of targets, and an army delivered one unit at a time is how this bot has lost
	/// matches before. Sharing it means every unit with nothing in front of it walks at the same
	/// cell, so the push arrives as a push.
	/// <para>
	/// Keyed on the owning player rather than held in a plain static, for the same reason
	/// <see cref="EnemyBaseSightings"/> is: the memory belongs to one side of one match, cannot
	/// leak into the next, and two bots playing each other cannot read one another's.
	/// </para>
	/// <para>
	/// It only ever advances. A side that has swept half the map and then found and levelled
	/// something should carry on from where it had got to rather than re-walk the rungs it has
	/// already answered.
	/// </para>
	/// </remarks>
	public static class AssaultSweeps
	{
		sealed class Sweep
		{
			public bool Started;
			public int Rung;
			public int RungTick;
		}

		static readonly ConditionalWeakTable<Player, Sweep> Sweeps = new();

		/// <summary>
		/// The rung to hunt on now, retiring the current one if it has been answered or outlasted.
		/// </summary>
		/// <param name="arrived">Whether the asking unit is standing on the current rung's cell.</param>
		public static int Rung(Player owner, int tick, bool arrived, in AssaultSweepTuning t)
		{
			if (owner == null)
				return t.StartRung;

			var sweep = Sweeps.GetOrCreateValue(owner);
			if (!sweep.Started)
			{
				sweep.Started = true;
				sweep.Rung = t.StartRung;
				sweep.RungTick = tick;
				return sweep.Rung;
			}

			var next = AssaultSweepLogic.Advance(sweep.Rung, tick - sweep.RungTick, arrived, t);
			if (next != sweep.Rung)
			{
				sweep.Rung = next;
				sweep.RungTick = tick;
			}

			return sweep.Rung;
		}
	}
}
