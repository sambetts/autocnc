// ============================================================================
//  TrainUnitsMode — keeps the army topped up from the module's production plan.
//
//  Runs on production buildings (barracks, war factory). Each building drives
//  only the queue it owns, so two barracks don't both order the same infantry.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	public sealed class TrainUnitsMode : UnitMode
	{
		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			var plan = ctx.ProductionPlan;
			if (plan == null || plan.Count == 0)
				return UnitDecision.Continue;

			// --- Sense -------------------------------------------------------------
			var state = new ArmyPlanState(
				Cash: ctx.Cash,
				Queues: ctx.QueueStates(),
				Owned: ctx.OwnedUnitCounts());

			// --- Decide ------------------------------------------------------------
			var choice = UnitProductionLogic.ChooseNext(state, plan);
			if (!choice.IsValid)
				return UnitDecision.Continue;

			// Only the building that owns this queue should issue the order, or every
			// barracks would race to queue the same unit.
			if (!ctx.OwnsQueue(choice.Queue))
				return UnitDecision.Continue;

			// --- Act ---------------------------------------------------------------
			return UnitDecision.Produce(choice.Queue, choice.ActorType, $"training {choice.ActorType}");
		}
	}
}
