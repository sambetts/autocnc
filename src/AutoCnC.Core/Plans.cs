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

namespace AutoCnC.Core
{
	/// <summary>
	/// One rung of a base build plan: the first available candidate is built until
	/// <see cref="DesiredCount"/> exist.
	/// </summary>
	/// <remarks>
	/// Candidates are alternatives for the same role — GDI's barracks is <c>pyle</c> and Nod's is
	/// <c>hand</c> — so one plan covers both factions without knowing which you are.
	/// </remarks>
	public readonly record struct BuildStep(string[] Candidates, int DesiredCount)
	{
		public BuildStep(string candidate, int desiredCount = 1)
			: this([candidate], desiredCount) { }
	}

	/// <summary>
	/// One rung of a unit production plan. <see cref="Queue"/> is a production category such as
	/// <c>Infantry</c> or <c>Vehicle</c>.
	/// </summary>
	public readonly record struct ProductionStep(string Queue, string[] Candidates, int DesiredCount);

	/// <summary>What a planner is allowed to know about the base.</summary>
	public readonly record struct BasePlanState(
		int Cash,
		int PowerBalance,
		IReadOnlyCollection<string> Buildable,
		IReadOnlyDictionary<string, int> Owned);

	/// <summary>What the unit planner is allowed to know, for one production queue.</summary>
	public readonly record struct ProductionQueueState(
		string Queue,
		bool IsIdle,
		IReadOnlyCollection<string> Buildable);

	/// <summary>What the unit planner is allowed to know overall.</summary>
	public readonly record struct ArmyPlanState(
		int Cash,
		IReadOnlyCollection<ProductionQueueState> Queues,
		IReadOnlyDictionary<string, int> Owned);

	/// <summary>A chosen unit to train, and the queue to train it from.</summary>
	public readonly record struct ProductionChoice(string Queue, string ActorType)
	{
		public bool IsValid => !string.IsNullOrEmpty(ActorType);
		public static readonly ProductionChoice None = default;
	}

	/// <summary>
	/// Decides what to build next. Pure, so a build order can be tested without launching a game.
	/// </summary>
	public static class BaseBuildLogic
	{
		/// <summary>
		/// Picks the next structure to start, or null if there is nothing worth doing right now.
		/// </summary>
		/// <param name="state">Current base snapshot.</param>
		/// <param name="plan">Ordered build steps, declared by the doctrine.</param>
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
			if (powerCandidates != null && powerCandidates.Count > 0 && state.PowerBalance < lowPowerThreshold)
			{
				var power = FirstBuildable(powerCandidates, state.Buildable);
				if (power != null)
					return power;
			}

			foreach (var step in plan)
			{
				if (step.Candidates == null || step.Candidates.Length == 0)
					continue;

				if (CountOwned(step.Candidates, state.Owned) >= step.DesiredCount)
					continue;

				var choice = FirstBuildable(step.Candidates, state.Buildable);
				if (choice != null)
					return choice;

				// Nothing in this step is available yet (wrong faction, or prerequisites
				// missing). Move on rather than stalling the whole plan.
			}

			return null;
		}

		static string FirstBuildable(IReadOnlyCollection<string> candidates, IReadOnlyCollection<string> buildable)
		{
			foreach (var candidate in candidates)
				if (buildable.Contains(candidate, StringComparer.OrdinalIgnoreCase))
					return candidate;

			return null;
		}

		static int CountOwned(IReadOnlyCollection<string> candidates, IReadOnlyDictionary<string, int> owned)
		{
			if (owned == null)
				return 0;

			var total = 0;
			foreach (var candidate in candidates)
				if (owned.TryGetValue(candidate, out var count))
					total += count;

			return total;
		}
	}

	/// <summary>
	/// Decides which unit to train next. Pure, so an army composition can be tested without
	/// launching a game.
	/// </summary>
	public static class UnitProductionLogic
	{
		/// <summary>
		/// Walks the plan and returns the first unmet step whose queue is idle and whose unit is
		/// currently buildable.
		/// </summary>
		/// <remarks>
		/// Ordering is the priority: put what matters most first. A step with
		/// <see cref="int.MaxValue"/> as its count never completes, which is how you say
		/// "keep making these forever" — usually the last step in a plan.
		/// </remarks>
		public static ProductionChoice ChooseNext(in ArmyPlanState state, IReadOnlyList<ProductionStep> plan)
		{
			if (plan == null || plan.Count == 0 || state.Queues == null)
				return ProductionChoice.None;

			foreach (var step in plan)
			{
				if (step.Candidates == null || step.Candidates.Length == 0)
					continue;

				if (CountOwned(step.Candidates, state.Owned) >= step.DesiredCount)
					continue;

				var queue = FindIdleQueue(state, step.Queue);
				if (queue == null)
					continue;

				foreach (var candidate in step.Candidates)
					if (queue.Value.Buildable.Contains(candidate, StringComparer.OrdinalIgnoreCase))
						return new ProductionChoice(step.Queue, candidate);
			}

			return ProductionChoice.None;
		}

		static ProductionQueueState? FindIdleQueue(in ArmyPlanState state, string queueName)
		{
			foreach (var queue in state.Queues)
				if (string.Equals(queue.Queue, queueName, StringComparison.OrdinalIgnoreCase))
					return queue.IsIdle && queue.Buildable != null ? queue : null;

			return null;
		}

		static int CountOwned(IReadOnlyCollection<string> candidates, IReadOnlyDictionary<string, int> owned)
		{
			if (owned == null)
				return 0;

			var total = 0;
			foreach (var candidate in candidates)
				if (owned.TryGetValue(candidate, out var count))
					total += count;

			return total;
		}
	}
}
