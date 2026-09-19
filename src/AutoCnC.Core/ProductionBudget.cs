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

namespace AutoCnC.Core
{
	/// <summary>
	/// Cash reserved for one production queue category until the next strategic assessment.
	/// </summary>
	/// <remarks>
	/// The owning queue may spend the reservation. Other queues may only start production when
	/// the full rules cost of their requested item would leave at least
	/// <see cref="ReservedCash"/> available. Queue names match production queue Group first and
	/// Type second, just like <c>ModeContext.QueueFor</c>.
	/// </remarks>
	public readonly record struct ProductionBudget(int ReservedCash, string Queue, string Reason)
	{
		/// <summary>
		/// Stable machine-readable explanation for this reservation.
		/// </summary>
		public string ReasonId { get; init; }

		/// <summary>Whether this value describes an enforceable reservation.</summary>
		public bool IsActive => ReservedCash > 0 && !string.IsNullOrWhiteSpace(Queue);

		/// <summary>No cash reservation. This is the compatibility default.</summary>
		public static ProductionBudget None => default;

		/// <summary>Reserve cash for a production queue category.</summary>
		public static ProductionBudget Reserve(int reservedCash, string queue, string reason) =>
			Reserve(reservedCash, queue, reason, null);

		/// <summary>
		/// Reserve cash for a production queue category with a stable evidence identifier.
		/// </summary>
		public static ProductionBudget Reserve(
			int reservedCash, string queue, string reason, string reasonId) =>
			new(reservedCash, queue, reason) { ReasonId = reasonId };
	}
}
