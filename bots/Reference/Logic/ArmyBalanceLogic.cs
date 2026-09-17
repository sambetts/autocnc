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
	/// <summary>Tunable knobs for <see cref="ArmyBalanceLogic"/>.</summary>
	public readonly record struct BalanceTuning(int ReleaseNumerator, int ReleaseDenominator)
	{
		public static BalanceTuning Default { get; } = new(
			// Three quarters. The bound this fraction must not cross is exact, and it is bounded
			// on both sides.
			//
			// UPPER: it must be strictly below 1, or the rung reads exactly as it does now and
			// nothing changes.
			//
			// LOWER: ExpansionLogic.Reinforce sizes the harvester floor at
			// HarvestersPerRefinery (2) x refineries, and a refinery hands out exactly *one*
			// FreeActor harvester. So free actors alone are always 1/2 of the floor, and any
			// share at or below 1/2 releases the rung before the bot has bought a single
			// harvester — which is the failure that pinned income at roughly 12 credits a second
			// two rounds ago, when every harvester the bot ever owned was a refinery's free one.
			//
			// ceil(3n/4) > n/2 for every n > 0, so 3/4 is strictly inside the band at every
			// refinery count. Worked through at the counts this bot actually reaches: a floor of
			// 10 (five refineries) releases at 8, so three harvesters must be bought on top of
			// the five free ones; a floor of 8 releases at 6, so two must be bought; a floor of
			// 4 releases at 3, so one must be. Targets of 1 and 2 release at 1 and 2, which is
			// their own target, so small rungs never release at all.
			ReleaseNumerator: 3,
			ReleaseDenominator: 4);
	}

	/// <summary>What <see cref="ArmyBalanceLogic.Release"/> did, and to what.</summary>
	/// <remarks>
	/// The standing and target come back so the caller can put them in the decision reason.
	/// Whether this rule ever fires is not inferable from what got built — a queue that buys
	/// artillery looks the same whether the floor released or the fleet was simply full — so it
	/// has to say so itself.
	/// </remarks>
	public readonly record struct FloorRelease(
		IReadOnlyList<ProductionStep> Plan,
		int Standing,
		int Target)
	{
		public bool Released => Target > 0;
	}

	/// <summary>
	/// Stops a production rung that can never be permanently met from starving everything below
	/// it in the same queue.
	/// </summary>
	/// <remarks>
	/// <c>UnitProductionLogic.ChooseNext</c> returns the <em>first unmet</em> step whose
	/// candidates are buildable in an idle queue. That is a sound rule for a ladder of rungs
	/// which, once climbed, stay climbed — and every rung naming a unit that dies is not one.
	/// Such a rung goes unmet the moment the unit dies and, while unmet, it is the answer to
	/// every evaluation of its queue for the rest of the match.
	/// <para>
	/// <b>This shape has now decided four matches.</b> Tanks once blocked harvesters; harvesters
	/// once blocked tanks; siege once blocked harvesters; and on the badland-ridges loss
	/// <see cref="ExpansionLogic.Reinforce"/> sized the harvester <em>floor</em> at two per
	/// standing refinery — ten, with five refineries up at 585s — against the five free actors
	/// those refineries hand out. Ten harvesters were built and all ten were hunted down between
	/// 660s and 780s, so the floor was unmet for essentially the whole game. It sits above the
	/// siege rung, so the <c>Vehicle</c> queue answered <c>bggy</c> and <c>harv</c> twelve times
	/// in the 1,134 seconds after the airfield stood at 394s and reached the siege rung
	/// <b>zero</b> times: no <c>arty</c>, no <c>msam</c>, no <c>mtnk</c>, no <c>ltnk</c>, in a
	/// match whose single largest killer was enemy <c>msam</c> reaching 11 cells against an army
	/// whose longest weapon reached 6.
	/// </para>
	/// <para>
	/// Every previous fix moved a rung, and the bug came back one rung higher. So this one does
	/// not move anything. It is a <em>preference with a fallback</em>: income keeps absolute
	/// priority while the fleet is genuinely in trouble, and a fleet that is merely one or two
	/// short stops holding the whole queue hostage. A rung is marked met — by rewriting its
	/// target down to what is standing — only while the standing count is inside the slack band
	/// <see cref="BalanceTuning.ReleaseNumerator"/>/<see cref="BalanceTuning.ReleaseDenominator"/>
	/// of its target. Drop below the band and the original target comes straight back.
	/// </para>
	/// <para>
	/// It is deliberately applied to a <em>named role</em> rather than to every rung. A blanket
	/// rule would release <c>new("Vehicle", ["jeep", "bggy"], 1)</c> at ceil(3/4) = 1 &gt; 0
	/// standing, which is no change, but a rule with any rounding the other way would release it
	/// at zero and the bot would never scout again. Roles not named here keep the previous
	/// behaviour exactly.
	/// </para>
	/// </remarks>
	public static class ArmyBalanceLogic
	{
		/// <summary>
		/// The standing count at which a rung of the given size stops blocking its queue.
		/// </summary>
		/// <remarks>
		/// Rounded up, so the band demands more rather than less. A tuning whose numerator is
		/// not strictly below its denominator returns the target itself, which can never be
		/// reached by a rung that is unmet, so a misconfigured tuning is a no-op rather than a
		/// silent release.
		/// </remarks>
		public static int ReleaseAt(int target, in BalanceTuning t)
		{
			if (target <= 0 || t.ReleaseDenominator <= 0 || t.ReleaseNumerator <= 0)
				return target;

			if (t.ReleaseNumerator >= t.ReleaseDenominator)
				return target;

			var scaled = ((long)target * t.ReleaseNumerator + t.ReleaseDenominator - 1) / t.ReleaseDenominator;
			return (int)scaled;
		}

		/// <summary>
		/// The plan with every rung naming <paramref name="role"/> marked met while the role is
		/// inside its slack band, so the queue can reach what is below it.
		/// </summary>
		/// <remarks>
		/// Returns the plan it was given, by reference, when nothing needed rewriting — which is
		/// the common case, so this is free on a healthy economy and free again on a dead one.
		/// Endless rungs are skipped: nothing sits below them, and scaling
		/// <see cref="int.MaxValue"/> is arithmetic with no meaning.
		/// </remarks>
		public static FloorRelease Release(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<string> role,
			in BalanceTuning t)
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

				if (standing < ReleaseAt(target, t))
					continue;

				if (rewritten == null)
				{
					rewritten = new List<ProductionStep>(plan.Count);
					for (var c = 0; c < plan.Count; c++)
						rewritten.Add(plan[c]);

					// The first rung released is the one the queue was actually stuck on, so it
					// is the one worth naming in the decision reason.
					reportedTarget = target;
				}

				rewritten[i] = new ProductionStep(step.Queue, step.Candidates, standing);
			}

			return rewritten == null
				? new FloorRelease(plan, standing, 0)
				: new FloorRelease(rewritten, standing, reportedTarget);
		}

		/// <summary>
		/// Whether <em>every</em> candidate a rung lists belongs to the role.
		/// </summary>
		/// <remarks>
		/// "All", not "any", and for the reason <see cref="ExpansionLogic.LastRungNaming"/>
		/// spells out: <c>Until(n)</c> counts every candidate a step lists, so a rung written
		/// <c>["harv", "mtnk"]</c> is satisfied by tanks and is not a harvester rung however it
		/// reads. Releasing such a rung on the harvester count alone would be wrong twice over.
		/// </remarks>
		static bool AllNamed(IReadOnlyList<string> role, string[] candidates)
		{
			if (candidates == null || candidates.Length == 0)
				return false;

			for (var c = 0; c < candidates.Length; c++)
			{
				var named = false;
				for (var i = 0; i < role.Count && !named; i++)
					named = string.Equals(role[i], candidates[c], System.StringComparison.OrdinalIgnoreCase);

				if (!named)
					return false;
			}

			return true;
		}
	}
}
