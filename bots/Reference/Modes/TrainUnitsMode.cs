// ============================================================================
//  TrainUnitsMode — keeps the army topped up from the module's production plan.
//
//  Runs on production buildings (barracks, war factory). Each building drives
//  only the queue it owns, so two barracks don't both order the same infantry.
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
	public sealed class TrainUnitsMode : UnitMode
	{
		/// <summary>
		/// How big a harvester fleet the standing refineries want. See <see cref="ExpansionLogic"/>.
		/// </summary>
		readonly ExpansionTuning expansion = ExpansionTuning.Default;

		/// <summary>
		/// How short the harvester fleet may be before its rung stops holding the whole queue.
		/// See <see cref="ArmyBalanceLogic"/>.
		/// </summary>
		readonly BalanceTuning balance = BalanceTuning.Default;

		// The scaled plan, kept until the refinery count changes. Sizing it is a cheap walk, but
		// it allocates, and this ticks on every barracks and every factory the side owns.
		IReadOnlyList<ProductionStep> scaledFrom;
		IReadOnlyList<ProductionStep> scaled;
		int scaledRefineries = -1;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			scaledFrom = null;
			scaled = null;
			scaledRefineries = -1;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			var plan = ctx.ProductionPlan;
			if (plan == null || plan.Count == 0)
				return UnitDecision.Continue;

			// --- Sense -------------------------------------------------------------
			var owned = ctx.OwnedUnitCounts();
			var state = new ArmyPlanState(
				Cash: ctx.Cash,
				Queues: ctx.QueueStates(),
				Owned: owned);

			// A refinery has one docking bay and feeds two harvesters, so both harvester rungs are
			// a function of the refineries standing rather than constants. They have to be. The
			// floor is written as RefineryCore = 4 and a refinery hands out a free harvester, so
			// four refineries satisfied it four-out-of-four from 263s and it never fired; the
			// saturation rung underneath sits below SiegeVehicles, which dies (six msam built,
			// six lost) and so is never permanently met, and ChooseNext returns the first unmet
			// step. Between them the Vehicle queue issued twelve orders in 1,289 seconds and
			// bought exactly one harvester, at 1,144s, 248 seconds after the last one had died.
			// Reinforce raises the floor and Saturate raises the ceiling; both only ever raise,
			// so a plan already asking for more gets its plan back unchanged, by reference.
			var refineries = ExpansionLogic.Standing(ctx.OwnedBuildingCounts(), ReferencePlans.Refineries);
			if (refineries != scaledRefineries || !ReferenceEquals(plan, scaledFrom))
			{
				scaledFrom = plan;
				scaledRefineries = refineries;
				scaled = ExpansionLogic.Saturate(
					ExpansionLogic.Reinforce(plan, refineries, ReferencePlans.HarvesterUnits, expansion),
					refineries, ReferencePlans.HarvesterUnits, expansion);
			}

			plan = scaled;

			// Sizing the rungs is a function of the refineries standing, so it is cached above.
			// Whether a rung is *blocking* is a function of how many harvesters are alive right
			// now, which that cache does not track and which changes on the second a harvester
			// dies — so this is re-asked every evaluation. It returns the plan it was handed, by
			// reference, whenever nothing needs rewriting.
			var release = ArmyBalanceLogic.Release(plan, owned, ReferencePlans.HarvesterUnits, balance);
			plan = release.Plan;

			// --- Decide ------------------------------------------------------------
			var choice = UnitProductionLogic.ChooseNext(state, plan);
			if (!choice.IsValid)
				return UnitDecision.Continue;

			// Only the building that owns this queue should issue the order, or every
			// barracks would race to queue the same unit.
			if (!ctx.OwnsQueue(choice.Queue))
				return UnitDecision.Continue;

			// --- Act ---------------------------------------------------------------
			// The reason says when the plan was resized, so the next fight's decision trace can
			// be grepped for whether the saturation rung ever grew rather than inferred from
			// what got built — and it now says when a floor was released too, because that is
			// not inferable at all: a Vehicle queue buying artillery looks identical whether the
			// harvester floor stood aside or the fleet happened to be full.
			var why = ReferenceEquals(scaled, scaledFrom)
				? $"training {choice.ActorType}"
				: $"training {choice.ActorType} for {refineries} refineries";

			if (release.Released)
				why += $", harvester floor released at {release.Standing}/{release.Target}";

			return UnitDecision.Produce(choice.Queue, choice.ActorType, why);
		}
	}
}
