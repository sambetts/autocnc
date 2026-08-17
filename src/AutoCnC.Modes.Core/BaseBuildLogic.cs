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
using System.Linq;

namespace AutoCnC.Modes.Core
{
	/// <summary>
	/// One rung of a build order: the first affordable candidate is built until
	/// <see cref="DesiredCount"/> of them exist.
	/// </summary>
	/// <remarks>
	/// Candidates are alternatives for the same role — e.g. GDI's barracks is <c>pyle</c> and
	/// Nod's is <c>hand</c> — so one plan covers both factions without knowing which you are.
	/// </remarks>
	public readonly record struct BuildStep(string[] Candidates, int DesiredCount)
	{
		public BuildStep(string candidate, int desiredCount = 1)
			: this([candidate], desiredCount) { }
	}

	/// <summary>What the planner is allowed to know about the base.</summary>
	public readonly record struct BasePlanState(
		int Cash,
		int PowerBalance,
		IReadOnlyCollection<string> Buildable,
		IReadOnlyDictionary<string, int> Owned);

	/// <summary>
	/// Decides what to build next. Pure, so a build order can be tested without launching a game.
	/// </summary>
	public static class BaseBuildLogic
	{
		/// <summary>
		/// A reasonable Tiberian Dawn opening. Power first because everything else needs it, then
		/// economy, then production, then defence.
		/// </summary>
		public static IReadOnlyList<BuildStep> DefaultPlan { get; } =
		[
			new(["powr", "nuke"], 1),          // power
			new(["proc"], 1),                  // refinery: income before anything else
			new(["powr", "nuke"], 2),
			new(["pyle", "hand"], 1),          // barracks (GDI / Nod)
			new(["proc"], 2),
			new(["weap", "afld"], 1),          // vehicle production
			new(["powr", "nuke"], 3),
			new(["gtwr", "gun"], 2),           // a little static defence
			new(["proc"], 3),
			new(["powr", "nuke"], 5),
			new(["hq", "eye", "tmpl"], 1),     // tech
			new(["weap", "afld"], 2),
		];

		/// <summary>
		/// Picks the next building to start, or null if there is nothing worth doing right now.
		/// </summary>
		/// <param name="state">Current base snapshot.</param>
		/// <param name="plan">Ordered build steps.</param>
		/// <param name="powerCandidates">
		/// Actor names counting as power plants, used for the low-power override.
		/// </param>
		/// <param name="lowPowerThreshold">
		/// Build power immediately when the balance falls below this, regardless of the plan.
		/// </param>
		public static string ChooseNext(
			in BasePlanState state,
			IReadOnlyList<BuildStep> plan,
			IReadOnlyCollection<string> powerCandidates = null,
			int lowPowerThreshold = 20)
		{
			if (plan == null || plan.Count == 0 || state.Buildable == null || state.Buildable.Count == 0)
				return null;

			// Brownouts slow production and disable defences, so power jumps the queue.
			if (state.PowerBalance < lowPowerThreshold)
			{
				var power = FirstBuildable(powerCandidates ?? ["powr", "nuke"], state);
				if (power != null)
					return power;
			}

			foreach (var step in plan)
			{
				if (step.Candidates == null || step.Candidates.Length == 0)
					continue;

				if (CountOwned(step.Candidates, state) >= step.DesiredCount)
					continue;

				var choice = FirstBuildable(step.Candidates, state);
				if (choice != null)
					return choice;

				// Nothing in this step is available yet (wrong faction, or prerequisites
				// missing). Move on rather than stalling the whole plan.
			}

			return null;
		}

		static string FirstBuildable(IReadOnlyCollection<string> candidates, in BasePlanState state)
		{
			foreach (var candidate in candidates)
				if (state.Buildable.Contains(candidate, StringComparer.OrdinalIgnoreCase))
					return candidate;

			return null;
		}

		static int CountOwned(IReadOnlyCollection<string> candidates, in BasePlanState state)
		{
			if (state.Owned == null)
				return 0;

			var total = 0;
			foreach (var candidate in candidates)
				if (state.Owned.TryGetValue(candidate, out var count))
					total += count;

			return total;
		}
	}
}
