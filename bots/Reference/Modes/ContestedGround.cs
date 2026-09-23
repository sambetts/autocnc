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
	/// The tiberium field this side was last driven off, shared by every harvester it owns.
	/// </summary>
	/// <remarks>
	/// <b>A lesson that dies with the unit that learned it is not memory.</b>
	/// <see cref="Logic.HarvesterWatchdog.ContestedX"/> already records the field a harvester was
	/// shot off, and <see cref="Logic.HarvesterLogic.SelectFieldAvoiding"/> already steers the
	/// next assignment away from it. Both live in the mode instance, and there is one mode
	/// instance per unit, reset in <see cref="HarvesterMode.OnEnter"/> — so the knowledge is
	/// destroyed at exactly the moment it is proven, and the replacement harvester starts with a
	/// blank sheet and drives to the best-scoring field, which is the one that just killed its
	/// predecessor.
	/// <para>
	/// On 16:9 that was the economy. Five of the seven harvesters this side built died inside a
	/// four-cell circle — at (25,28), (28,29), (24,28), (25,28) and (28,30) — one after another
	/// between 538s and 676s, and <c>summary.lossClusters</c> carries all of them in a single
	/// 67-unit, 20,850-credit cluster that ran from 188s to 949s. The score that sent the first
	/// one there is a pure statement of what a patch holds, and it cannot see the rocket infantry
	/// standing on it; nothing else in the rule was left to notice, because the only thing that
	/// had noticed was dead. The fleet spent 28 withdrawal episodes and 663 follow-up escape and
	/// shelter evaluations on ground it never stopped going back to, earned 10.8 credits per
	/// harvester-second against the 16.3 a working harvester manages, and left 335 of 958 seconds
	/// with no live harvester at all. Income finished at 23.0 credits a second against a
	/// reference of 50.
	/// </para>
	/// <para>
	/// So the report is promoted to the side. One slot, most recent wins, exactly mirroring the
	/// single slot it shadows: the question a harvester needs answered is "where did this side
	/// last get shot off" and a list would only let a stale entry outvote a live one.
	/// </para>
	/// <para>
	/// <b>It expires, and that is load-bearing.</b> Tiberium is finite and raiders are not
	/// stationary, so ground given up forever is ground handed to the opponent one patch at a
	/// time. The entry is refreshed every time any harvester is driven off that field, so a patch
	/// that is still being camped stays excluded for as long as the camping lasts, and one whose
	/// attacker has moved on is offered back <see cref="Logic.HarvesterTuning.ContestedMemoryTicks"/>
	/// after the shooting stops — or <see cref="Logic.HarvesterTuning.HomeContestedMemoryTicks"/>
	/// for a field on home ground, where the screen and the towers answer the raid (see
	/// <see cref="Logic.HarvesterLogic.ContestedStillHot"/>). The avoidance it feeds is a soft
	/// preference with a fallback, so even an unexpired entry can never refuse the last field
	/// on the map.
	/// </para>
	/// <para>
	/// Keyed on the owning player, like <see cref="BaseDamageWatch"/> and
	/// <see cref="EnemySightings"/>, so the memory belongs to one side of one match and cannot
	/// leak into the next one. Nothing here is map knowledge: every coordinate is a place one of
	/// this side's own harvesters was standing when it was shot.
	/// </para>
	/// </remarks>
	public static class ContestedGround
	{
		sealed class Ground
		{
			public int X = int.MinValue;
			public int Y = int.MinValue;
			public int Tick = int.MinValue;
		}

		static readonly ConditionalWeakTable<Player, Ground> Fields = new();

		/// <summary>Notes that a harvester was driven off the field centred on this cell.</summary>
		public static void Record(Player owner, int centerX, int centerY, int tick)
		{
			if (owner == null || centerX == int.MinValue || centerY == int.MinValue)
				return;

			var ground = Fields.GetOrCreateValue(owner);
			ground.X = centerX;
			ground.Y = centerY;
			ground.Tick = tick;
		}

		/// <summary>The field this side was last driven off, while that report is still fresh.</summary>
		public static bool TryGet(
			Player owner, int tick, int memoryTicks, out int x, out int y, out int reportedTick)
		{
			x = int.MinValue;
			y = int.MinValue;
			reportedTick = int.MinValue;

			if (owner == null || !Fields.TryGetValue(owner, out var ground))
				return false;

			if (ground.X == int.MinValue || ground.Tick == int.MinValue)
				return false;

			if (memoryTicks > 0 && tick - ground.Tick > memoryTicks)
				return false;

			x = ground.X;
			y = ground.Y;
			reportedTick = ground.Tick;
			return true;
		}
	}
}
