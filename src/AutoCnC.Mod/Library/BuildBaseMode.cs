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
using AutoCnC.Mod.Modes;
using AutoCnC.Modes.Core;
using OpenRA;

namespace AutoCnC.Mod.Library
{
	/// <summary>
	/// Unfolds an MCV and then grows the base: picks the next structure from a build plan, waits
	/// for it, and places it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the mode that makes a skirmish actually progress. Every other mode assumes a base
	/// already exists to produce units and repair them; without this one you sit next to an
	/// undeployed MCV forever.
	/// </para>
	/// <para>
	/// It runs on both the MCV and the construction yard it becomes, because deploying replaces
	/// one actor with the other. The MCV branch deploys; the construction yard branch builds.
	/// </para>
	/// <para>
	/// The judgement — what to build next — lives in <see cref="BaseBuildLogic"/>, which has no
	/// engine dependency, so a build order can be tested without launching the game.
	/// </para>
	/// </remarks>
	public sealed class BuildBaseMode : UnitMode
	{
		const string BuildingQueue = "Building";

		/// <summary>Where we decided to put the current building, so the choice doesn't wander.</summary>
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
			//    Note the DeploysIntoBuilding check. A Tiberian Dawn construction yard can also
			//    transform — back into an MCV — so testing CanDeploy alone makes this mode deploy,
			//    pack up, and deploy again forever.
			if (ctx.CanDeploy && ctx.DeploysIntoBuilding)
				return UnitDecision.Deploy("deploying to found the base");

			// Only the actor that owns the construction queue should drive it, otherwise every
			// unit running this mode would race to queue the same building.
			var queue = ctx.QueueFor(BuildingQueue);
			if (queue == null || queue.Actor != self)
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

				return UnitDecision.PlaceBuilding(ready, plannedLocation.Value.X, plannedLocation.Value.Y,
					$"placing {ready}");
			}

			// 3. Queue busy? Let it work.
			if (ctx.ProducingItem(BuildingQueue) != null)
				return UnitDecision.Continue;

			plannedItem = null;
			plannedLocation = null;

			// 4. Queue idle: decide what to build next.
			var state = new BasePlanState(
				Cash: ctx.Cash,
				PowerBalance: ctx.PowerBalance,
				Buildable: ctx.BuildableItems(BuildingQueue),
				Owned: ctx.OwnedBuildingCounts());

			var next = BaseBuildLogic.ChooseNext(state, Plan);
			if (next == null)
				return UnitDecision.Continue;   // plan complete, or nothing affordable yet

			return UnitDecision.Produce(next, $"building {next}");
		}

		/// <summary>
		/// The build order. Override this in your own copy of the mode to change the opening.
		/// </summary>
		static IReadOnlyList<BuildStep> Plan => BaseBuildLogic.DefaultPlan;
	}
}
