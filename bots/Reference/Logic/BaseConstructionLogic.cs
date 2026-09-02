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
	/// <summary>What a construction yard should do with one of its queues this tick.</summary>
	public enum ConstructionAction
	{
		/// <summary>Nothing to order and nothing waiting: leave the yard alone.</summary>
		None,

		/// <summary>Start the next thing the build plan asks for.</summary>
		Produce,

		/// <summary>Put down something that has already finished building.</summary>
		Place,
	}

	/// <summary>One construction queue as the yard can see it.</summary>
	/// <param name="Name">The queue category, e.g. "Building" or "Support".</param>
	/// <param name="Owned">Whether this yard actually drives that queue.</param>
	/// <param name="Busy">Something is already being produced in it.</param>
	/// <param name="ReadyToPlace">A finished structure waiting for a location, or null.</param>
	/// <param name="Buildable">What this queue can legally build right now.</param>
	public readonly record struct ConstructionQueueState(
		string Name,
		bool Owned,
		bool Busy,
		string ReadyToPlace,
		IReadOnlyCollection<string> Buildable);

	/// <summary>One instruction for one queue.</summary>
	public readonly record struct ConstructionOrder(
		ConstructionAction Action,
		string Queue,
		string Item,
		string Reason)
	{
		public static ConstructionOrder Nothing { get; } = new(ConstructionAction.None, null, null, null);
	}

	/// <summary>
	/// Which of a construction yard's queues to drive, and what to ask it for.
	/// </summary>
	/// <remarks>
	/// A Tiberian Dawn yard owns more than one queue. Economy and tech come from
	/// <c>Building</c>; every defensive structure — guard tower, turret, SAM site, obelisk —
	/// comes from <c>Support</c>. A yard that only ever drives <c>Building</c> silently ignores
	/// every defence its doctrine asked for: <see cref="BaseBuildLogic.ChooseNext"/> skips any
	/// step whose candidates are not buildable, so those steps neither build nor block. That is
	/// not a slow turtle, it is no turtle at all — the badland-ridges match spent roughly 380
	/// seconds in a doctrine whose whole premise is "static defence first" and finished with
	/// zero towers standing.
	/// <para>
	/// The plan itself does not need to say which queue a step belongs to. Each queue reports
	/// what it can build, and a step lands wherever it is legal — so one flat plan drives every
	/// queue, and a step that no queue can build yet still falls through harmlessly.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class BaseConstructionLogic
	{
		/// <summary>
		/// Picks one order for one queue, or <see cref="ConstructionOrder.Nothing"/>.
		/// </summary>
		/// <param name="queues">
		/// The yard's queues, in priority order. Earlier queues win a tie, so put the one the
		/// match is lost without — the economy — first.
		/// </param>
		public static ConstructionOrder ChooseNext(
			IReadOnlyList<ConstructionQueueState> queues,
			int cash,
			int powerBalance,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<BuildStep> plan,
			IReadOnlyCollection<string> powerCandidates = null)
		{
			if (queues == null || queues.Count == 0)
				return ConstructionOrder.Nothing;

			// 1. Anything already paid for and waiting goes down first, whichever queue built it.
			//    A finished structure held in the queue is money spent that is doing nothing, and
			//    the queue behind it cannot start until it is placed.
			for (var i = 0; i < queues.Count; i++)
			{
				var q = queues[i];
				if (!q.Owned || string.IsNullOrEmpty(q.ReadyToPlace))
					continue;

				return new ConstructionOrder(ConstructionAction.Place, q.Name, q.ReadyToPlace,
					$"placing {q.ReadyToPlace} from {q.Name}");
			}

			// 2. Otherwise start the next thing the plan wants, in the first idle queue that can
			//    legally build it. Queues run in parallel in the engine, so a busy Building queue
			//    is a reason to go and look at Support, not a reason to stop.
			for (var i = 0; i < queues.Count; i++)
			{
				var q = queues[i];
				if (!q.Owned || q.Busy)
					continue;

				var state = new BasePlanState(cash, powerBalance, q.Buildable, owned);
				var next = BaseBuildLogic.ChooseNext(state, plan, powerCandidates);
				if (next != null)
					return new ConstructionOrder(ConstructionAction.Produce, q.Name, next,
						$"building {next} in {q.Name}");
			}

			return ConstructionOrder.Nothing;
		}
	}
}
