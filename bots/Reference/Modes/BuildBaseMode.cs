// ============================================================================
//  BuildBaseMode — unfolds the MCV, then grows the base from the module's plan.
//
//  This is the mode that makes a match actually start. Every other mode assumes
//  a base already exists to produce units and repair them.
//
//  A yard owns more than one construction queue: economy and tech come from
//  "Building", every defensive structure from "Support". This mode drives both,
//  because a plan step that no driven queue can build is a step that silently
//  never happens — which is how a turtling doctrine ends a match with no towers.
//
//  Note it reads ctx.BuildPlan rather than hardcoding an order, so the same mode
//  works for any module: change the plan in your IDoctrine, not here.
//
//  It also decides WHERE, not just what. FindBuildLocation defaults to a 2-14
//  cell ring around the base centre, which is right for a power plant and wrong
//  for a refinery: harvesters work the closest tiberium to the refinery they
//  dock with, so refineries stacked in one ring share one patch and run it dry.
//  See BasePlacementLogic.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	public sealed class BuildBaseMode : UnitMode
	{
		/// <summary>
		/// Every construction queue a yard drives, most important first.
		/// </summary>
		/// <remarks>
		/// "Building" leads because a match is lost without an economy long before it is lost
		/// without a tower. A queue this yard does not own is skipped, so naming one that a
		/// given faction or mod lacks costs nothing.
		/// </remarks>
		static readonly string[] ConstructionQueues = ["Building", "Support"];

		static readonly string[] PowerCandidates = [.. ReferencePlans.PowerPlants];

		/// <summary>
		/// Structures that should be pushed outward as they multiply, rather than stacked at home.
		/// </summary>
		/// <remarks>
		/// Refineries only. See <see cref="BasePlacementLogic"/> for why: harvesters work the
		/// closest tiberium to the refinery they dock with, so refineries built on top of each
		/// other share one patch and starve together once it runs out.
		/// </remarks>
		static readonly string[] ExpandingStructures = [.. ReferencePlans.Refineries];

		// Sensing buffers are reused by the host, so what we read from one queue would be
		// overwritten by reading the next. Copy into buffers we own instead.
		readonly List<string>[] buildable = NewBuffers();

		readonly ConstructionQueueState[] queues = new ConstructionQueueState[ConstructionQueues.Length];

		// Where we decided to put the current building, per queue, so the choice doesn't wander.
		readonly CPos?[] plannedLocation = new CPos?[ConstructionQueues.Length];
		readonly string[] plannedItem = new string[ConstructionQueues.Length];

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			for (var i = 0; i < ConstructionQueues.Length; i++)
			{
				plannedLocation[i] = null;
				plannedItem[i] = null;
			}
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// 1. Still an MCV? Unfold it. Everything else depends on this.
			//
			//    DeploysIntoBuilding matters: a Tiberian Dawn construction yard can also
			//    transform — back into an MCV — so testing CanDeploy alone makes this mode
			//    deploy, pack up, and deploy again forever.
			if (ctx.CanDeploy && ctx.DeploysIntoBuilding)
				return UnitDecision.Deploy("deploying to found the base");

			// --- Sense -------------------------------------------------------------
			for (var i = 0; i < ConstructionQueues.Length; i++)
			{
				var name = ConstructionQueues[i];

				// Only the actor owning a queue drives it.
				if (!ctx.OwnsQueue(name))
				{
					queues[i] = new ConstructionQueueState(name, false, false, null, null);
					continue;
				}

				var items = buildable[i];
				items.Clear();
				var sensed = ctx.BuildableItems(name);
				if (sensed != null)
					items.AddRange(sensed);

				var ready = ctx.ItemReadyToPlace(name);
				if (ready == null)
				{
					plannedItem[i] = null;
					plannedLocation[i] = null;
				}

				queues[i] = new ConstructionQueueState(
					Name: name,
					Owned: true,
					Busy: ctx.ProducingItem(name) != null,
					ReadyToPlace: ready,
					Buildable: items);
			}

			// --- Decide ------------------------------------------------------------
			var owned = ctx.OwnedBuildingCounts();

			var order = BaseConstructionLogic.ChooseNext(
				queues, ctx.Cash, ctx.PowerBalance, owned, ctx.BuildPlan, PowerCandidates);

			// --- Act ---------------------------------------------------------------
			switch (order.Action)
			{
				case ConstructionAction.Place:
					return Place(ctx, order, owned);

				case ConstructionAction.Produce:
					return UnitDecision.Produce(order.Queue, order.Item, order.Reason);

				default:
					return UnitDecision.Continue;   // plan complete, or nothing affordable yet
			}
		}

		UnitDecision Place(ModeContext ctx, in ConstructionOrder order, IReadOnlyDictionary<string, int> owned)
		{
			var i = IndexOf(order.Queue);

			if (plannedItem[i] != order.Item || plannedLocation[i] == null)
			{
				plannedItem[i] = order.Item;

				// Income expands outward; everything else stays behind the defences. The ring is
				// a preference — if nothing out there is legal we take the default rather than
				// stall holding a refinery we have already paid for.
				var ring = BasePlacementLogic.RingFor(order.Item, owned, ExpandingStructures);

				plannedLocation[i] = ctx.FindBuildLocation(order.Item, ring.MinRangeCells, ring.MaxRangeCells)
					?? ctx.FindBuildLocation(order.Item);
			}

			if (plannedLocation[i] == null)
				return UnitDecision.Continue;   // nowhere to put it; try again next tick

			return UnitDecision.PlaceBuilding(order.Queue, order.Item,
				plannedLocation[i].Value.X, plannedLocation[i].Value.Y, order.Reason);
		}

		static int IndexOf(string queue)
		{
			for (var i = 0; i < ConstructionQueues.Length; i++)
				if (ConstructionQueues[i] == queue)
					return i;

			return 0;
		}

		static List<string>[] NewBuffers()
		{
			var buffers = new List<string>[ConstructionQueues.Length];
			for (var i = 0; i < buffers.Length; i++)
				buffers[i] = [];

			return buffers;
		}
	}
}
