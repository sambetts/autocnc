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
using AutoCnC.Core;

namespace AutoCnC.Platform.Traits
{
	internal sealed class ProductionBudgetLease
	{
		ProductionBudget current;
		int expiresAtTick;

		public ProductionBudget Current(int worldTick) =>
			worldTick < expiresAtTick ? current : ProductionBudget.None;

		public void Refresh(in ProductionBudget proposed, int nextAssessmentTick)
		{
			current = Normalize(proposed);
			expiresAtTick = nextAssessmentTick;
		}

		public void Clear()
		{
			current = ProductionBudget.None;
			expiresAtTick = 0;
		}

		internal static ProductionBudget Normalize(in ProductionBudget proposed)
		{
			if (!proposed.IsActive)
				return ProductionBudget.None;

			return proposed with { Queue = proposed.Queue.Trim() };
		}
	}

	internal readonly record struct ProductionQueueIdentity(string Group, string Type);

	internal readonly record struct ProductionBudgetScope(
		ProductionBudget Budget,
		bool UsesGroup)
	{
		public bool OwnsQueue(string group, string type) =>
			string.Equals(
				Budget.Queue,
				UsesGroup ? group : type,
				StringComparison.OrdinalIgnoreCase);
	}

	internal readonly record struct ProductionBudgetCandidate(
		uint ControllerActorId,
		uint QueueActorId,
		string Queue,
		string Item,
		int Cost,
		bool OwnsReservation);

	internal enum ProductionBudgetOutcome : byte
	{
		Allowed,
		BudgetSuppressed,
		OrderLimit
	}

	internal readonly record struct ProductionBudgetEvaluation(
		ProductionBudgetCandidate Candidate,
		ProductionBudgetOutcome Outcome,
		long CurrentCash,
		long PostOrderCash,
		long ReservedCashRemaining);

	internal static class ProductionBudgetArbitrator
	{
		public static bool TryResolveScope(
			in ProductionBudget proposed,
			IEnumerable<ProductionQueueIdentity> queues,
			out ProductionBudgetScope scope)
		{
			scope = default;
			var budget = ProductionBudgetLease.Normalize(proposed);
			if (!budget.IsActive)
				return false;

			var available = queues?.ToArray() ?? [];
			if (available.Any(queue =>
				string.Equals(budget.Queue, queue.Group, StringComparison.OrdinalIgnoreCase)))
			{
				scope = new ProductionBudgetScope(budget, UsesGroup: true);
				return true;
			}

			if (available.Any(queue =>
				string.Equals(budget.Queue, queue.Type, StringComparison.OrdinalIgnoreCase)))
			{
				scope = new ProductionBudgetScope(budget, UsesGroup: false);
				return true;
			}

			return false;
		}

		public static IReadOnlyList<ProductionBudgetEvaluation> Evaluate(
			in ProductionBudget proposed,
			int currentCash,
			IEnumerable<ProductionBudgetCandidate> candidates,
			int maxOrders)
		{
			var budget = ProductionBudgetLease.Normalize(proposed);
			var ordered = candidates
				.OrderBy(candidate => candidate.OwnsReservation ? 0 : 1)
				.ThenBy(candidate => candidate.Queue, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.Queue, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.QueueActorId)
				.ThenBy(candidate => candidate.Item, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.Item, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.ControllerActorId)
				.ToArray();

			var cash = Math.Max(0L, currentCash);
			var reserved = budget.IsActive ? (long)budget.ReservedCash : 0L;
			var remainingSlots = Math.Max(0, maxOrders);
			var ownerSpend = 0L;
			var nonOwnerSpend = 0L;
			var results = new List<ProductionBudgetEvaluation>(ordered.Length);

			foreach (var candidate in ordered.Where(candidate => candidate.OwnsReservation))
			{
				var cost = Math.Max(0L, candidate.Cost);
				if (remainingSlots == 0)
				{
					results.Add(new ProductionBudgetEvaluation(
						candidate,
						ProductionBudgetOutcome.OrderLimit,
						cash,
						cash - ownerSpend - nonOwnerSpend,
						Math.Max(0L, reserved - ownerSpend)));
					continue;
				}

				ownerSpend = AddSaturating(ownerSpend, cost);
				remainingSlots--;
				results.Add(new ProductionBudgetEvaluation(
					candidate,
					ProductionBudgetOutcome.Allowed,
					cash,
					cash - ownerSpend - nonOwnerSpend,
					Math.Max(0L, reserved - ownerSpend)));
			}

			foreach (var candidate in ordered.Where(candidate => !candidate.OwnsReservation))
			{
				var cost = Math.Max(0L, candidate.Cost);
				var reservedRemaining = Math.Max(0L, reserved - ownerSpend);
				var postOrderCash = cash - ownerSpend - nonOwnerSpend - cost;

				if (remainingSlots == 0)
				{
					results.Add(new ProductionBudgetEvaluation(
						candidate,
						ProductionBudgetOutcome.OrderLimit,
						cash,
						postOrderCash,
						reservedRemaining));
					continue;
				}

				if (budget.IsActive && postOrderCash < reservedRemaining)
				{
					results.Add(new ProductionBudgetEvaluation(
						candidate,
						ProductionBudgetOutcome.BudgetSuppressed,
						cash,
						postOrderCash,
						reservedRemaining));
					continue;
				}

				nonOwnerSpend = AddSaturating(nonOwnerSpend, cost);
				remainingSlots--;
				results.Add(new ProductionBudgetEvaluation(
					candidate,
					ProductionBudgetOutcome.Allowed,
					cash,
					cash - ownerSpend - nonOwnerSpend,
					reservedRemaining));
			}

			return results;
		}

		static long AddSaturating(long value, long addition) =>
			addition > long.MaxValue - value ? long.MaxValue : value + addition;
	}
}
