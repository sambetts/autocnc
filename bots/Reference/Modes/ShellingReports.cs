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
	/// The gun that is taking this side apart from further away than the things it is hitting
	/// can answer.
	/// </summary>
	/// <remarks>
	/// The fifth sibling of <see cref="BaseDamageWatch"/>, <see cref="EarnerUnderFire"/>,
	/// <see cref="ContestedGround"/> and <see cref="EnemySightings"/>, and it exists because
	/// every one of them reports <em>where</em> this side is being hurt and none of them reports
	/// <em>what is doing it</em>. On 16:9 that distinction was the match.
	/// <see cref="BaseDamageWatch"/> did its job: the screen was re-anchored onto the buildings
	/// that were losing health. It arrived, found nothing inside a rifle's four cells, and
	/// returned <c>Hold("on post, no threats")</c> 7,368 times while <c>msam</c> shelled the
	/// base from eleven. Standing on the target is not an answer to artillery.
	/// <para>
	/// Written from the damage notification rather than from a scan, for the reason the guide
	/// sanctions and <see cref="HarvesterThreats"/> already relies on: the attacked actor is
	/// told who hit it, at the tick it happens, exactly as a human player's would be. It is
	/// deliberately not generalised into map knowledge. One gun is remembered, for the twelve
	/// seconds of <see cref="CounterBatteryTuning.MemoryTicks"/>, and a unit that reads it still
	/// has to be within <see cref="CounterBatteryTuning.ReachUnits"/> of it to act — so nothing
	/// is learned about anywhere this side is not already being shot.
	/// </para>
	/// <para>
	/// One slot, because the question is "what is killing us that we cannot reach", and a list
	/// would only let the jeep that scratched a power plant outvote the battery levelling the
	/// refineries. Which of two live reports wins is
	/// <see cref="CounterBatteryLogic.Choose"/>'s business and it is sticky, because a besieged
	/// base is hit several times a second and a rule that took the latest hit would re-aim the
	/// screen every tick and deliver it nowhere.
	/// </para>
	/// <para>
	/// Keyed on the owning player, like its siblings, so the memory belongs to one side of one
	/// match and cannot leak into the next one.
	/// </para>
	/// </remarks>
	public static class ShellingReports
	{
		sealed class Report
		{
			public ShellingReport Current = ShellingReport.None;
		}

		static readonly ConditionalWeakTable<Player, Report> Reports = new();

		/// <summary>
		/// Notes that something hit one of this side's actors from <paramref name="standoffUnits"/>
		/// away, which was further than that actor could answer.
		/// </summary>
		public static void Record(
			Player owner,
			uint attackerId,
			int x,
			int y,
			int standoffUnits,
			int tick,
			in CounterBatteryTuning tuning)
		{
			if (owner == null || attackerId == 0)
				return;

			var report = Reports.GetOrCreateValue(owner);
			report.Current = CounterBatteryLogic.Choose(
				report.Current,
				new ShellingReport(true, attackerId, x, y, standoffUnits, tick),
				tick,
				tuning);
		}

		/// <summary>The gun this side is currently losing to, while that report is still fresh.</summary>
		public static bool TryGet(
			Player owner, int tick, in CounterBatteryTuning tuning, out ShellingReport report)
		{
			report = ShellingReport.None;

			if (owner == null || !Reports.TryGetValue(owner, out var stored))
				return false;

			if (!CounterBatteryLogic.StillHot(stored.Current, tick, tuning))
				return false;

			report = stored.Current;
			return true;
		}
	}
}
