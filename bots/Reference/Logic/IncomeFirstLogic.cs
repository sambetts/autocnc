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

using System;
using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="IncomeFirstLogic"/>.</summary>
	/// <remarks>
	/// <paramref name="HarvesterPrice"/> is a ruleset price, not a tuned number: <c>harv</c>
	/// costs 1,100 for both factions and needs only <c>proc</c>. It is the bar because it is
	/// exactly the sum the barracks has to stop taking for the vehicle queue to finish one.
	/// <para>
	/// <paramref name="GarrisonBodies"/> is the plan's own idea of "enough not to die to a
	/// rush": <see cref="ReferencePlans.OpeningTrain"/> opens on four rifles and four rockets
	/// before it buys anything else, so four is what a barracks is allowed to keep replacing
	/// while the economy is being rescued. It is a cap, never a target — a rung already asking
	/// for four or fewer is left exactly as its doctrine wrote it.
	/// </para>
	/// </remarks>
	public readonly record struct IncomeFirstTuning(int HarvesterPrice, int GarrisonBodies)
	{
		public static IncomeFirstTuning Default { get; } = new(
			HarvesterPrice: 1100,
			GarrisonBodies: 4);
	}

	/// <summary>What <see cref="IncomeFirstLogic.Hold"/> did, and on what evidence.</summary>
	/// <remarks>
	/// The counts come back so the caller can put them in the decision reason. Whether this rule
	/// fired is not inferable from what got built — a barracks that buys four riflemen looks the
	/// same whether it was capped at four or simply had no fifth rung to reach — so it has to
	/// say so itself.
	/// </remarks>
	public readonly record struct IncomeHold(
		IReadOnlyList<ProductionStep> Plan,
		int Standing,
		int ShortBelow,
		int Cash,
		int RungsCapped)
	{
		public bool Held => RungsCapped > 0;
	}

	/// <summary>
	/// Stops the cheapest queue on the field from spending the credits the only queue that can
	/// buy income is waiting for.
	/// </summary>
	/// <remarks>
	/// <b>Rung order cannot solve this, because the queues are separate.</b> Every previous fix
	/// in this bot reordered a production plan so that income sat above armour, and
	/// <see cref="ArmyBalanceLogic"/> then stopped an unmeetable income rung from starving what
	/// sat below it. Both of those arbitrate <em>within</em> one queue. Cash is shared across all
	/// of them, and nothing arbitrated that at all.
	/// <para>
	/// <b>What that cost on badland-ridges.</b> The airfield stood at 474s and lived for roughly
	/// 540 seconds. In that time the <c>Vehicle</c> queue issued <b>two</b> orders — both
	/// <c>harv</c>, 2,200 credits — and spent 520 of those seconds waiting: the harvester ordered
	/// when the airfield opened was not delivered until 617s, <b>143 seconds</b> for an 1,100
	/// credit item. Over the same match the <c>Infantry</c> queue issued <b>59</b> orders worth
	/// 8,900 credits, a 100-credit rifleman roughly every twenty seconds. Cash read 0 or 1 at
	/// every one of the twenty economy samples from 180s onward, and lifetime spend tracked
	/// lifetime earnings to the credit. A queue buying 100-credit items always wins that race
	/// against one buying an 1,100-credit item, and the item it beat was the entire economy: the
	/// fleet averaged <b>1.06 live harvesters</b> across a 1,187-second match with four
	/// refineries standing from 260s, income finished at 17.2 credits a second against a
	/// reference of 50, and 550 seconds of the match had no live harvester at all.
	/// </para>
	/// <para>
	/// The harvesters themselves were not the problem and that is worth stating, because three
	/// previous rounds looked there. Across 1,260 harvester-seconds the side earned 20,475
	/// credits — <b>16.3 credits per harvester per second</b>, which is a full load every
	/// forty-odd seconds and about as well as a harvester can do. There were simply almost none
	/// of them.
	/// </para>
	/// <para>
	/// So the rule is a cap on the cheap queue, conditioned on all three things being true at
	/// once, and it is deliberately narrow on each:
	/// </para>
	/// <list type="number">
	/// <item>A vehicle factory is standing, so a harvester is actually buyable. Before one is up
	/// the only harvester on offer is a refinery's free actor and holding infantry buys
	/// nothing.</item>
	/// <item>The fleet is short by the band <see cref="ArmyBalanceLogic.ReleaseAt"/> already
	/// defines, rather than by a second threshold invented here. A fleet inside that band is the
	/// one that queue is allowed to stop asking about, so it is also the one this rule must stop
	/// protecting.</item>
	/// <item>Cash is below a harvester's price. This is what makes the rule self-releasing: the
	/// instant the side can afford a harvester and a rifleman, ordering the rifleman costs the
	/// harvester nothing and the cap lifts on its own. On a healthy economy it never fires.</item>
	/// </list>
	/// <para>
	/// It caps rather than silences, so the base is never naked: every rung keeps replacing up to
	/// <see cref="IncomeFirstTuning.GarrisonBodies"/>, and the towers this bot builds from the
	/// <c>Support</c> queue were the most efficient thing on the field anyway — four <c>gtwr</c>
	/// cost 2,400 credits and killed 47 units for 10,740, at 51 credits a kill against the
	/// infantry queue's 171.
	/// </para>
	/// <para>
	/// Endless rungs are capped too, and that is load-bearing. The endless rung is the queue's
	/// answer whenever everything above it is met, so a rule that skipped it would hold the
	/// bounded rungs and leak the whole surplus through the last one.
	/// </para>
	/// </remarks>
	public static class IncomeFirstLogic
	{
		/// <summary>
		/// The plan with every rung of <paramref name="queue"/> capped at the garrison, while the
		/// side cannot afford the harvester it is short of.
		/// </summary>
		/// <remarks>
		/// Returns the plan it was given, by reference, whenever nothing needs rewriting — which
		/// is every evaluation before a factory stands, every evaluation on a healthy fleet, and
		/// every evaluation with a harvester's price in the bank.
		/// </remarks>
		public static IncomeHold Hold(
			IReadOnlyList<ProductionStep> plan,
			string queue,
			int cash,
			int standingHarvesters,
			int shortBelow,
			bool harvestersBuildable,
			in IncomeFirstTuning t)
		{
			var idle = new IncomeHold(plan, standingHarvesters, shortBelow, cash, 0);

			if (plan == null || string.IsNullOrEmpty(queue) || t.GarrisonBodies < 0)
				return idle;

			// Nothing can buy a harvester, so holding the barracks buys nothing either.
			if (!harvestersBuildable || shortBelow <= 0)
				return idle;

			// The fleet is inside the band the vehicle queue is allowed to stop asking about.
			if (standingHarvesters >= shortBelow)
				return idle;

			// Both are affordable this tick, so the rifleman is not costing the harvester
			// anything. This is the clause that lifts the cap the moment income recovers.
			if (cash >= t.HarvesterPrice)
				return idle;

			List<ProductionStep> capped = null;
			var rungs = 0;

			for (var i = 0; i < plan.Count; i++)
			{
				var step = plan[i];
				if (!string.Equals(step.Queue, queue, StringComparison.OrdinalIgnoreCase))
					continue;

				if (step.DesiredCount <= t.GarrisonBodies)
					continue;

				if (capped == null)
				{
					capped = new List<ProductionStep>(plan.Count);
					for (var c = 0; c < plan.Count; c++)
						capped.Add(plan[c]);
				}

				capped[i] = new ProductionStep(step.Queue, step.Candidates, t.GarrisonBodies);
				rungs++;
			}

			return capped == null
				? idle
				: new IncomeHold(capped, standingHarvesters, shortBelow, cash, rungs);
		}
	}
}
