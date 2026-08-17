// ============================================================================
//  BuildBaseMode — unfolds the MCV, then grows the base from the module's plan.
//
//  This is the mode that makes a match actually start. Every other mode assumes
//  a base already exists to produce units and repair them.
//
//  Note it reads ctx.BuildPlan rather than hardcoding an order, so the same mode
//  works for any module: change the plan in your IBattleModule, not here.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	public sealed class BuildBaseMode : UnitMode
	{
		const string BuildingQueue = "Building";

		static readonly string[] PowerCandidates = ["powr", "nuke"];

		// Where we decided to put the current building, so the choice doesn't wander.
		CPos? plannedLocation;
		string plannedItem;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			plannedLocation = null;
			plannedItem = null;
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

			// Only the actor owning the construction queue drives it.
			if (!ctx.OwnsQueue(BuildingQueue))
				return UnitDecision.Continue;

			// 2. Something finished and is waiting to be placed.
			var ready = ctx.ItemReadyToPlace(BuildingQueue);
			if (ready != null)
			{
				if (plannedItem != ready || plannedLocation == null)
				{
					plannedItem = ready;
					plannedLocation = ctx.FindBuildLocation(ready);
				}

				if (plannedLocation == null)
					return UnitDecision.Continue;   // nowhere to put it; try again next tick

				return UnitDecision.PlaceBuilding(BuildingQueue, ready,
					plannedLocation.Value.X, plannedLocation.Value.Y, $"placing {ready}");
			}

			// 3. Queue busy? Let it work.
			if (ctx.ProducingItem(BuildingQueue) != null)
				return UnitDecision.Continue;

			plannedItem = null;
			plannedLocation = null;

			// 4. Queue idle: ask the module's plan what comes next.
			var state = new BasePlanState(
				Cash: ctx.Cash,
				PowerBalance: ctx.PowerBalance,
				Buildable: ctx.BuildableItems(BuildingQueue),
				Owned: ctx.OwnedBuildingCounts());

			var next = BaseBuildLogic.ChooseNext(state, ctx.BuildPlan, PowerCandidates);
			if (next == null)
				return UnitDecision.Continue;   // plan complete, or nothing affordable yet

			return UnitDecision.Produce(BuildingQueue, next, $"building {next}");
		}
	}
}
