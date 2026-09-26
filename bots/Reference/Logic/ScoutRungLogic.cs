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
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>
	/// When the Opening and Scout doctrines stop buying scout vehicles: once a new one has
	/// nothing left to find.
	/// </summary>
	/// <remarks>
	/// In those two doctrines a <c>jeep</c> or <c>bggy</c> runs <see cref="Modes.ScoutMode"/>, which
	/// has exactly two jobs: fly the home tiberium survey, and find their base. Once the survey is
	/// flown and somebody on this side has seen one of their structures inside the corroboration
	/// window, a fresh scout starts its search ladder at rung 0 — the mirror of our base — drives
	/// there, and parks beside the first structure it sees. That is where it dies.
	/// <para>
	/// <b>What that cost on 16:9</b> (GDI against Nod, lost at 918s, spending 57% of what the
	/// opponent spent). The survey finished at 265s and their base was seen at 270s. The side
	/// built nine <c>jeep</c>s in all: nine lost, zero kills, a mean life of 37 seconds, 3,600
	/// credits, 6.4% of everything it spent. Five were bought by the Scout and Opening scout rungs
	/// after 270s and every one was sent to rung 0 at (78,21) and killed there in 17 to 43 seconds.
	/// The rung also paid for itself twice over in things that are not jeeps: the Scout rung
	/// bought two at 275s and 286s <em>ahead of</em> the recovery harvester ("first light screen
	/// before harvester recovery") and capped the barracks at four bodies a rung to fund them,
	/// while the construction yard held its <c>hq</c> for that same harvester. The harvester was
	/// ordered at 310s instead of 275s, the <c>hq</c> at 361s, and the siege rung it unlocks first
	/// fired at 386s. The Opening rung then bought three more at 466s, 500s and 537s while cash
	/// read 0 and the war factory had <c>msam</c> to build.
	/// </para>
	/// <para>
	/// So the rung stands down — marked met at what is standing, never deleted — while both
	/// questions are answered, and comes back by itself the moment either reopens: the survey
	/// is incomplete, or nobody has seen their base inside
	/// <see cref="SightingMemoryTuning.CorroborationTicks"/>. The second condition is the fresh
	/// sighting rather than a remembered one on purpose. A stale memory is the case in which
	/// looking again is worth a jeep — the Scout doctrine entered on lost contact needs a searcher,
	/// and a push that starts on a stale memory is the one that forgets a standing base — so the
	/// old behaviour is kept exactly there. A scout already alive is untouched and still parks by
	/// their base to keep the sighting fresh; this only declines to buy the next one.
	/// </para>
	/// <para>
	/// Only the Opening and Scout plans are stood down, because only those doctrines put screen
	/// vehicles in <see cref="Modes.ScoutMode"/>. Defence buys them as harvester escorts and
	/// Attack names no screen rung at all.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.</para>
	/// </remarks>
	public static class ScoutRungLogic
	{
		/// <summary>The trace's name for a production order the stand-down changed.</summary>
		public const string StoodDownReasonId = "production.scout-rung-stood-down";

		/// <summary>
		/// Whether a new scout vehicle has anything left to find: false while the home survey is
		/// unflown or while this side's sighting of their base is stale or absent.
		/// </summary>
		public static bool NothingLeftToFind(bool homeSurveyFlown, bool theirBaseSeenRecently) =>
			homeSurveyFlown && theirBaseSeenRecently;

		/// <summary>
		/// The plan with every bounded rung naming only <paramref name="role"/> marked met at what
		/// is standing.
		/// </summary>
		/// <remarks>
		/// Rewritten down to the standing count, as <see cref="ArmyBalanceLogic.Release"/> and
		/// <see cref="AttritionLogic.Release"/> do, so the rung's target returns intact the moment
		/// the caller stops asking for the stand-down. Returns the plan it was given, by reference,
		/// when no such rung was unmet — which is also how the caller knows there is nothing to
		/// claim.
		/// </remarks>
		public static FloorRelease StandDown(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<string> role)
		{
			if (plan == null || owned == null || role == null || role.Count == 0)
				return new FloorRelease(plan, 0, 0);

			var standing = ExpansionLogic.Standing(owned, role);
			List<ProductionStep> rewritten = null;
			var reportedTarget = 0;

			for (var i = 0; i < plan.Count; i++)
			{
				var step = plan[i];
				if (!AllNamed(role, step.Candidates))
					continue;

				var target = step.DesiredCount;
				if (target <= 0 || target == int.MaxValue || standing >= target)
					continue;

				if (rewritten == null)
				{
					rewritten = new List<ProductionStep>(plan.Count);
					for (var c = 0; c < plan.Count; c++)
						rewritten.Add(plan[c]);

					// The first rung stood down is the one the queue would have bought from.
					reportedTarget = target;
				}

				rewritten[i] = new ProductionStep(step.Queue, step.Candidates, standing);
			}

			return rewritten == null
				? new FloorRelease(plan, standing, 0)
				: new FloorRelease(rewritten, standing, reportedTarget);
		}

		/// <summary>Whether <em>every</em> candidate a rung lists belongs to the role.</summary>
		/// <remarks>
		/// "All", not "any", for the reason <see cref="ArmyBalanceLogic"/> gives: a rung that also
		/// names a tank is satisfied by the tank and is not a scout rung however it reads.
		/// </remarks>
		static bool AllNamed(IReadOnlyList<string> role, string[] candidates)
		{
			if (candidates == null || candidates.Length == 0)
				return false;

			for (var c = 0; c < candidates.Length; c++)
				if (!ArmyMixLogic.Names(role, candidates[c]))
					return false;

			return true;
		}
	}
}
