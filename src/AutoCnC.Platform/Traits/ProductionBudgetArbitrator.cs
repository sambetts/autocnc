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
			var sanitized = Sanitize(proposed);
			return sanitized.IsActive ? sanitized : ProductionBudget.None;
		}

		internal static ProductionBudget Sanitize(in ProductionBudget proposed) =>
			proposed with
			{
				ReservedCash = Math.Max(0, proposed.ReservedCash),
				Queue = proposed.Queue?.Trim()
			};
	}

	internal readonly record struct ProductionQueueIdentity(string Group, string Type);

	internal enum ProductionBudgetStatus : byte
	{
		Inactive,
		Active,
		Invalid,
		Unmatched,
		Error
	}

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

	internal readonly record struct ProductionBudgetResolution(
		ProductionBudget Budget,
		ProductionBudgetStatus Status,
		ProductionBudgetScope Scope)
	{
		public bool IsActive => Status == ProductionBudgetStatus.Active;
	}

	internal readonly record struct ProductionBudgetCandidate(
		uint ControllerActorId,
		uint QueueActorId,
		int QueueIndex,
		string QueueGroup,
		string QueueType,
		string Queue,
		string Item,
		int Cost,
		bool OwnsReservation,
		ulong QueueRevision,
		bool HasQueueRevision,
		ulong OrderRevision,
		bool HasOrderRevision);

	internal readonly record struct ProductionCommitmentKey(
		ulong OrderId,
		uint QueueActorId,
		int QueueIndex,
		string QueueGroup,
		string QueueType,
		string Item,
		int Cost,
		ulong IssuedQueueRevision,
		ulong ExpectedQueueRevision,
		bool HasQueueRevision,
		ulong IssuedOrderRevision,
		ulong ExpectedOrderRevision,
		bool HasOrderRevision);

	internal readonly record struct ProductionCommitmentObservation(
		bool IsValid,
		ulong QueueRevision,
		bool HasQueueRevision,
		ulong OrderRevision,
		bool HasOrderRevision);

	internal readonly record struct ProductionCommitmentSpend(
		long OwnerCost,
		long NonOwnerCost);

	internal sealed class ProductionCommitmentLedger
	{
		readonly Dictionary<ProductionCommitmentKey, long> commitments = [];
		ulong nextOrderId;

		public IReadOnlyCollection<ProductionCommitmentKey> Current => commitments.Keys;

		public ProductionCommitmentKey Commit(
			ProductionBudgetCandidate candidate,
			int issuedTick,
			long timeoutTicks)
		{
			nextOrderId++;
			if (nextOrderId == 0)
				nextOrderId++;

			var expectedQueueRevision = candidate.HasQueueRevision
				? NextExpectedRevision(
					candidate.QueueRevision,
					commitments.Keys
						.Where(commitment =>
							SameQueue(commitment, candidate) &&
							commitment.HasQueueRevision)
						.Select(commitment => commitment.ExpectedQueueRevision))
				: 0;
			var expectedOrderRevision = candidate.HasOrderRevision
				? NextExpectedRevision(
					candidate.OrderRevision,
					commitments.Keys
						.Where(commitment =>
							SameQueueItem(commitment, candidate) &&
							commitment.HasOrderRevision)
						.Select(commitment => commitment.ExpectedOrderRevision))
				: 0;
			var key = new ProductionCommitmentKey(
				nextOrderId,
				candidate.QueueActorId,
				candidate.QueueIndex,
				candidate.QueueGroup,
				candidate.QueueType,
				candidate.Item,
				Math.Max(0, candidate.Cost),
				candidate.QueueRevision,
				expectedQueueRevision,
				candidate.HasQueueRevision,
				candidate.OrderRevision,
				expectedOrderRevision,
				candidate.HasOrderRevision);
			commitments.Add(key, AddSaturating(issuedTick, Math.Max(1L, timeoutTicks)));
			return key;
		}

		public void Refresh(
			long worldTick,
			Func<ProductionCommitmentKey, ProductionCommitmentObservation> observe)
		{
			foreach (var pair in commitments.ToArray())
			{
				if (worldTick >= pair.Value)
				{
					commitments.Remove(pair.Key);
					continue;
				}

				var observation = observe(pair.Key);
				var acknowledged = pair.Key.HasOrderRevision && observation.HasOrderRevision
					? RevisionReached(observation.OrderRevision, pair.Key.ExpectedOrderRevision)
					: pair.Key.HasQueueRevision && observation.HasQueueRevision &&
						RevisionReached(observation.QueueRevision, pair.Key.ExpectedQueueRevision);
				if (!observation.IsValid || acknowledged)
					commitments.Remove(pair.Key);
			}
		}

		public ProductionCommitmentSpend ProjectedSpend(in ProductionBudgetScope scope)
		{
			var ownerCost = 0L;
			var nonOwnerCost = 0L;
			foreach (var commitment in commitments.Keys)
			{
				if (scope.OwnsQueue(commitment.QueueGroup, commitment.QueueType))
					ownerCost = AddSaturating(ownerCost, commitment.Cost);
				else
					nonOwnerCost = AddSaturating(nonOwnerCost, commitment.Cost);
			}

			return new ProductionCommitmentSpend(ownerCost, nonOwnerCost);
		}

		public static long SafeTimeoutTicks(
			int orderLatency,
			int netFrameInterval,
			int marginNetFrames = 2)
		{
			var frames = AddSaturating(
				Math.Max(0L, orderLatency),
				Math.Max(1L, marginNetFrames));
			return MultiplySaturating(frames, Math.Max(1L, netFrameInterval));
		}

		static bool SameQueue(
			in ProductionCommitmentKey commitment,
			in ProductionBudgetCandidate candidate) =>
			commitment.QueueActorId == candidate.QueueActorId &&
			commitment.QueueIndex == candidate.QueueIndex;

		static bool SameQueueItem(
			in ProductionCommitmentKey commitment,
			in ProductionBudgetCandidate candidate) =>
			SameQueue(commitment, candidate) &&
			string.Equals(commitment.Item, candidate.Item, StringComparison.Ordinal);

		static ulong NextExpectedRevision(ulong observed, IEnumerable<ulong> pending)
		{
			var latest = observed;
			foreach (var revision in pending)
				if (revision > latest)
					latest = revision;

			latest++;
			return latest == 0 ? 1 : latest;
		}

		static bool RevisionReached(ulong current, ulong expected) => current >= expected;

		static long AddSaturating(long value, long addition) =>
			addition > long.MaxValue - value ? long.MaxValue : value + addition;

		static long MultiplySaturating(long value, long multiplier) =>
			value != 0 && multiplier > long.MaxValue / value
				? long.MaxValue
				: value * multiplier;
	}

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
		public static ProductionBudgetResolution Resolve(
			in ProductionBudget proposed,
			IEnumerable<ProductionQueueIdentity> queues)
		{
			var sanitized = ProductionBudgetLease.Sanitize(proposed);
			if (proposed.ReservedCash < 0 ||
				(proposed.ReservedCash > 0 && string.IsNullOrWhiteSpace(proposed.Queue)))
				return new ProductionBudgetResolution(
					sanitized, ProductionBudgetStatus.Invalid, default);

			if (!sanitized.IsActive)
				return new ProductionBudgetResolution(
					sanitized, ProductionBudgetStatus.Inactive, default);

			return TryResolveScope(sanitized, queues, out var scope)
				? new ProductionBudgetResolution(sanitized, ProductionBudgetStatus.Active, scope)
				: new ProductionBudgetResolution(sanitized, ProductionBudgetStatus.Unmatched, default);
		}

		public static string StatusName(ProductionBudgetStatus status) => status switch
		{
			ProductionBudgetStatus.Active => "active",
			ProductionBudgetStatus.Invalid => "invalid",
			ProductionBudgetStatus.Unmatched => "unmatched",
			ProductionBudgetStatus.Error => "error",
			_ => "inactive"
		};

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
			int maxOrders,
			ulong admissionRound = 0,
			long committedOwnerCost = 0,
			long committedNonOwnerCost = 0)
		{
			var ordered = candidates
				.OrderBy(candidate => candidate.ControllerActorId)
				.ThenBy(candidate => candidate.Queue, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.Queue, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.QueueActorId)
				.ThenBy(candidate => candidate.Item, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.Item, StringComparer.Ordinal)
				.ToArray();
			return EvaluatePrioritized(
				proposed,
				currentCash,
				Rotate(ordered, admissionRound),
				maxOrders,
				committedOwnerCost,
				committedNonOwnerCost);
		}

		public static IReadOnlyList<ProductionBudgetEvaluation> EvaluatePrioritized(
			in ProductionBudget proposed,
			int currentCash,
			IEnumerable<ProductionBudgetCandidate> prioritizedCandidates,
			int maxOrders,
			long committedOwnerCost = 0,
			long committedNonOwnerCost = 0)
		{
			var budget = ProductionBudgetLease.Normalize(proposed);
			var cash = Math.Max(0L, currentCash);
			var reserved = budget.IsActive ? (long)budget.ReservedCash : 0L;
			var admissionOrder = prioritizedCandidates.ToArray();
			var admitted = admissionOrder
				.Take(Math.Max(0, maxOrders))
				.Select(candidate => candidate.ControllerActorId)
				.ToHashSet();
			var ownerSpend = admissionOrder
				.Where(candidate =>
					admitted.Contains(candidate.ControllerActorId) &&
					candidate.OwnsReservation)
				.Aggregate(
					Math.Max(0L, committedOwnerCost),
					(total, candidate) => AddSaturating(total, Math.Max(0L, candidate.Cost)));
			var ownerSpendSoFar = Math.Max(0L, committedOwnerCost);
			var nonOwnerSpend = Math.Max(0L, committedNonOwnerCost);
			var results = new List<ProductionBudgetEvaluation>(admissionOrder.Length);

			foreach (var candidate in admissionOrder)
			{
				var cost = Math.Max(0L, candidate.Cost);
				var reservedRemaining = Math.Max(0L, reserved - ownerSpend);
				if (!admitted.Contains(candidate.ControllerActorId))
				{
					results.Add(new ProductionBudgetEvaluation(
						candidate,
						ProductionBudgetOutcome.OrderLimit,
						cash,
						cash - ownerSpend - nonOwnerSpend,
						reservedRemaining));
					continue;
				}

				if (candidate.OwnsReservation)
				{
					ownerSpendSoFar = AddSaturating(ownerSpendSoFar, cost);
					results.Add(new ProductionBudgetEvaluation(
						candidate,
						ProductionBudgetOutcome.Allowed,
						cash,
						cash - ownerSpendSoFar - nonOwnerSpend,
						Math.Max(0L, reserved - ownerSpendSoFar)));
					continue;
				}

				var postOrderCash = cash - ownerSpend - nonOwnerSpend - cost;

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
				results.Add(new ProductionBudgetEvaluation(
					candidate,
					ProductionBudgetOutcome.Allowed,
					cash,
					cash - ownerSpend - nonOwnerSpend,
					reservedRemaining));
			}

			return results;
		}

		static IEnumerable<T> Rotate<T>(T[] values, ulong admissionRound)
		{
			if (values.Length == 0)
				yield break;

			var offset = (int)(admissionRound % (ulong)values.Length);
			for (var index = 0; index < values.Length; index++)
				yield return values[(offset + index) % values.Length];
		}

		static long AddSaturating(long value, long addition) =>
			addition > long.MaxValue - value ? long.MaxValue : value + addition;
	}
}
