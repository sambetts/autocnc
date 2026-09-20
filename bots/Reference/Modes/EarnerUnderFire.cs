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
	/// Which of this side's earners is being shot, and where it was standing when it was.
	/// </summary>
	/// <remarks>
	/// The fourth sibling of <see cref="BaseDamageWatch"/>, <see cref="ContestedGround"/> and
	/// <see cref="EnemySightings"/>, and it exists because the first of those has a blind spot
	/// that cost a match. <see cref="BaseDamageWatch"/> reports where the base is being taken
	/// apart by comparing the health of owned <em>buildings</em>; a harvester is not a building,
	/// so the entire economy is invisible to it. See <see cref="BaseGuardLogic.WorthGuarding"/>
	/// for what that was worth on 16:9.
	/// <para>
	/// Written from the damage notification rather than from a scan, for the same reason
	/// <see cref="ContestedGround"/> is: the attacked harvester is told directly, at the tick it
	/// happens, so there is nothing to sense and nothing to pay for. It is also the only report
	/// that is dense enough to steer on — this side's harvesters absorbed 394,525 damage across
	/// the match, against twelve building-post moves in the whole of it.
	/// </para>
	/// <para>
	/// One slot, because the question the screen needs answered is "where is this side losing an
	/// earner", and a list would only let a harvester scratched a moment ago outvote the one
	/// currently being destroyed. Which of two live reports wins is
	/// <see cref="BaseGuardLogic.ChooseEarner"/>'s business, and it is deliberately sticky: a
	/// harvester is hit several times a second, so a rule that simply took the latest report
	/// would move the anchor every tick and deliver the screen nowhere.
	/// </para>
	/// <para>
	/// Keyed on the owning player, like its siblings, so the memory belongs to one side of one
	/// match and cannot leak into the next one. Nothing here is map knowledge: every coordinate
	/// is a place one of this side's own units was standing when something shot it, and no
	/// property of the attacker is recorded at all.
	/// </para>
	/// </remarks>
	public static class EarnerUnderFire
	{
		sealed class Report
		{
			public GuardPost Post = GuardPost.None;
		}

		static readonly ConditionalWeakTable<Player, Report> Reports = new();

		/// <summary>Notes that one of this side's earners was just shot, and where it was.</summary>
		public static void Record(
			Player owner,
			uint actorId,
			string actorType,
			int x,
			int y,
			int healthPercent,
			int tick,
			in GuardPostTuning tuning)
		{
			if (owner == null || actorId == 0)
				return;

			var report = Reports.GetOrCreateValue(owner);
			report.Post = BaseGuardLogic.ChooseEarner(
				report.Post,
				new GuardPost(true, actorId, actorType, x, y, healthPercent, tick),
				tick,
				tuning);
		}

		/// <summary>
		/// The earner this side is currently losing, while that report is fresh enough and close
		/// enough to the base to be worth standing on.
		/// </summary>
		public static bool TryGetPost(
			Player owner, int tick, int baseX, int baseY, in GuardPostTuning tuning, out GuardPost post)
		{
			post = GuardPost.None;

			if (owner == null || !Reports.TryGetValue(owner, out var report))
				return false;

			if (!BaseGuardLogic.WorthGuarding(report.Post, tick, baseX, baseY, tuning))
				return false;

			post = report.Post;
			return true;
		}
	}
}
