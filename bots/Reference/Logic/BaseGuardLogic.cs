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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Where a defensive screen should be standing, and for how long.</summary>
	/// <remarks>
	/// <see cref="MemoryTicks"/> is how long a place that was hit stays worth guarding after the
	/// shooting there stops. It has to outlast the gap between two salvos of the same siege — an
	/// 11-cell gun reloads in seconds — without outlasting the raid itself, or the screen spends
	/// the rest of the match guarding a crater. Thirty seconds at the engine's 25 ticks a second.
	/// <para>
	/// <see cref="EarnerMemoryTicks"/> is the same idea for a harvester, and it is deliberately
	/// much shorter. A building that was hit is still standing where it was hit; a harvester
	/// moves at 1.758 cells a game second, so a fifteen-second-old report names ground it has
	/// already left by twenty-six cells. Fresh enough to send the screen at the raid, stale fast
	/// enough that the screen is not left standing on empty tiberium.
	/// </para>
	/// <para>
	/// <see cref="EarnerRadiusCells"/> is how far from the base centre a shot earner is still the
	/// screen's business. It is the longest weapon reach in the ruleset (11 cells) plus a rifle's
	/// tether (5), so a harvester cut down inside it is being killed on ground this side is
	/// actually holding, and one killed outside it is on a frontier field — which is what the
	/// dedicated escort exists for. Without a bound, one harvester shot at the far end of the map
	/// would walk the whole screen off the base.
	/// </para>
	/// </remarks>
	public readonly record struct GuardPostTuning(
		int MemoryTicks,
		int EarnerMemoryTicks,
		int EarnerRadiusCells)
	{
		public static GuardPostTuning Default { get; } = new(
			MemoryTicks: 25 * 30,
			EarnerMemoryTicks: 25 * 15,
			EarnerRadiusCells: 16);
	}

	/// <summary>A place this side's own buildings are being destroyed, and when it last was.</summary>
	public readonly record struct GuardPost(
		bool HasPost,
		uint ActorId,
		string ActorType,
		int X,
		int Y,
		int HealthPercent,
		int Tick)
	{
		public static GuardPost None { get; } = default;
	}

	/// <summary>
	/// Which part of a base a defensive screen should be standing on.
	/// </summary>
	/// <remarks>
	/// <b>A screen anchored on the middle of a base defends the middle of a base.</b> On 16:9 the
	/// defensive screen produced <b>16,377 <c>Hold</c> decisions against 3,033 <c>Attack</c></b>
	/// for <c>e1</c> alone — 84% of its evaluations were "on post, no threats" — while 27
	/// buildings were destroyed in seven distinct clusters sitting <b>8 to 17 cells</b> from the
	/// construction yard. <see cref="Modes.DefensiveMode"/> anchors every unit on
	/// <c>ctx.BaseCenter</c> and tethers it within twice its own weapon reach, which for a
	/// 4-cell rifle is 8 cells; the outer clusters were never inside anybody's leash. The bot's
	/// best unit per credit stood in the middle of a base it was watching burn at the edges.
	/// <para>
	/// The fix does not need more units, a bigger leash or map knowledge. Own buildings are known
	/// exactly — <c>OwnedBuildingStates()</c> carries an id, a cell and a health percentage — so
	/// a building whose health <em>fell since the last look</em> is a live report of where the
	/// enemy is, from inside the fairness rules and with no sensing at all.
	/// </para>
	/// <para>
	/// Stickiness is the whole design. A screen that chased the most recent hit would flip
	/// between two raids every tick and arrive at neither, so the post only moves to somewhere
	/// <em>strictly worse hurt</em>, stays hot while its building keeps being shot, and is
	/// dropped the moment that building stops existing. Ties break on health and then on actor
	/// id, so every unit on the side reaches the same answer without coordinating.
	/// </para>
	/// No OpenRA types: the memory that feeds this lives in <see cref="Modes.BaseDamageWatch"/>.
	/// </remarks>
	public static class BaseGuardLogic
	{
		/// <summary>Whether a remembered post is recent enough to still be worth standing on.</summary>
		public static bool StillHot(in GuardPost post, int nowTick, in GuardPostTuning tuning) =>
			post.HasPost && nowTick >= post.Tick && nowTick - post.Tick <= tuning.MemoryTicks;

		/// <summary>
		/// Whether a report that one of this side's earners is being shot is still worth sending
		/// the screen to: recent enough that the harvester has not driven away from it, and close
		/// enough to the base that going there is defending rather than sortieing.
		/// </summary>
		/// <remarks>
		/// <b>A screen that only guards buildings does not guard the thing that pays for them.</b>
		/// On 16:9 <see cref="Modes.BaseDamageWatch"/> accounted for twelve decisions in a
		/// 1,145-second match, because it compares the health of owned <em>buildings</em> and a
		/// harvester is not one. Meanwhile <see cref="Modes.DefensiveMode"/> returned
		/// <c>Hold("on post, no threats")</c> on <b>20,180</b> of its 25,543 evaluations — 79% —
		/// against just <b>63</b> <c>ReturnToAnchor</c>, so the screen never moved at all. Enemy
		/// <c>bike</c>, <c>ftnk</c>, <c>e3</c> and <c>arty</c> killed <b>all seven</b> harvesters
		/// between 670s and 832s, and not one of them died out on a frontier field: the death
		/// cells were 1.4, 1.4, 1.4, 5.0, 5.4, 6.7 and 8.5 cells from the construction yard, a
		/// median of <b>5.4</b>. The economy was shot to pieces inside its own base. Lifetime
		/// earnings froze at 35,705 credits at 780s; the last 366 seconds of the match earned
		/// nothing, spent nothing, fielded no army, and cost 24 buildings worth 23,200 credits —
		/// 46% of everything this side lost all match.
		/// <para>
		/// The dedicated escort was not the answer and had already been tried: the
		/// <c>HarvesterGuardInfantry</c> rung bought four <c>e4</c> for 800 credits, they scored
		/// <b>zero</b> kills and were all dead by 450s, 220 seconds before the hunt began. What
		/// was available instead was 45 riflemen — the best trade on the field at <b>74 credits a
		/// kill</b> against 247 for <c>arty</c>, 400 for <c>gtwr</c> and 436 for <c>e3</c> —
		/// waiting on the base centre for the fight to come to them.
		/// </para>
		/// <para>
		/// Own units are known exactly and a damage notification identifies the moment, so this
		/// needs no sensing and reveals nothing about the enemy: every coordinate is a place one
		/// of this side's own harvesters was standing when something shot it. Buildings still
		/// outrank earners in <see cref="Modes.DefensiveMode"/>, so a genuine base assault pulls
		/// the screen straight back off the tiberium.
		/// </para>
		/// </remarks>
		public static bool WorthGuarding(
			in GuardPost post, int nowTick, int baseX, int baseY, in GuardPostTuning tuning)
		{
			if (!EarnerStillHot(post, nowTick, tuning))
				return false;

			if (tuning.EarnerRadiusCells <= 0)
				return false;

			var dx = post.X - baseX;
			var dy = post.Y - baseY;
			return dx * dx + dy * dy <= tuning.EarnerRadiusCells * tuning.EarnerRadiusCells;
		}

		/// <summary>Whether a report that an earner was shot still names where it is.</summary>
		public static bool EarnerStillHot(in GuardPost post, int nowTick, in GuardPostTuning tuning) =>
			post.HasPost
			&& nowTick >= post.Tick
			&& (tuning.EarnerMemoryTicks <= 0 || nowTick - post.Tick <= tuning.EarnerMemoryTicks);

		/// <summary>
		/// Which shot earner the screen should cover, given the one it is already covering.
		/// </summary>
		/// <remarks>
		/// Stickiness matters more here than it does for buildings, not less. A harvester is hit
		/// several times a second and a side works up to eight of them at once, so a rule that
		/// simply took the most recent report would move the whole screen's anchor every tick and
		/// deliver it nowhere. The fight that is already being answered therefore keeps the post
		/// and refreshes it, and another earner takes it only by being strictly worse hurt —
		/// ties broken on actor id, so every unit on the side reaches the same answer without
		/// coordinating.
		/// </remarks>
		public static GuardPost ChooseEarner(
			in GuardPost held, in GuardPost candidate, int nowTick, in GuardPostTuning tuning)
		{
			var heldIsHot = EarnerStillHot(held, nowTick, tuning);

			if (!candidate.HasPost)
				return heldIsHot ? held : GuardPost.None;

			if (!heldIsHot)
				return candidate;

			// The same earner reporting again is this fight continuing, not a new one.
			if (candidate.ActorId == held.ActorId)
				return candidate;

			return Prefer(candidate, held) ? candidate : held;
		}

		/// <summary>
		/// Which of two posts a screen should hold. Lower health wins, then the lower actor id,
		/// so the ordering is total and every unit agrees on it.
		/// </summary>
		public static bool Prefer(in GuardPost candidate, in GuardPost held) =>
			candidate.HealthPercent != held.HealthPercent
				? candidate.HealthPercent < held.HealthPercent
				: candidate.ActorId < held.ActorId;

		/// <summary>
		/// The post to hold, given the one already held and the worst building hit this look.
		/// </summary>
		public static GuardPost Choose(
			in GuardPost held,
			in GuardPost candidate,
			int nowTick,
			in GuardPostTuning tuning)
		{
			var heldIsHot = StillHot(held, nowTick, tuning);

			if (!candidate.HasPost)
				return heldIsHot ? held : GuardPost.None;

			if (!heldIsHot)
				return candidate;

			return Prefer(candidate, held) ? candidate : held;
		}
	}
}
