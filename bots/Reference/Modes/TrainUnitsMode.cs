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

		/// <summary>
		/// How plated the enemy has to look before spare credits buy rockets rather than rifles.
		/// See <see cref="ArmyMixLogic"/>.
		/// </summary>
		readonly MixTuning mix = MixTuning.Default;

		/// <summary>
		/// How poor the side has to be before the barracks stops outbidding the harvester.
		/// See <see cref="IncomeFirstLogic"/>.
		/// </summary>
		readonly IncomeFirstTuning income = IncomeFirstTuning.Default;

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
			var buildings = ctx.OwnedBuildingCounts();
			var refineries = ExpansionLogic.Standing(buildings, ReferencePlans.Refineries);
			var factories = ExpansionLogic.Standing(buildings, ReferencePlans.VehicleFactories);
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

			// The endless infantry rung is what every leftover credit buys for the rest of the
			// match, and which body that should be is a property of the enemy rather than of the
			// plan. Both constants have now lost a match: endless e3 against an opponent who was
			// 63% infantry, and endless e1 against one who fielded none at all. So the rung is
			// chosen from what this side has actually watched them field — see ArmyMixLogic for
			// the numbers and EnemySightings for the counting, which is the ordinary
			// shroud-filtered sensing every mode already does.
			//
			// Asked every evaluation rather than cached, because the mix moves with contact and
			// a barracks that keeps buying the wrong body for thirty seconds has bought ten of
			// them. It returns the plan it was handed, by reference, whenever nothing changes,
			// which is every evaluation before first contact.
			var seen = EnemySightings.Mix(ctx.Owner);
			var unretargeted = plan;
			if (ArmyMixLogic.PreferAntiArmour(seen, mix))
				plan = ArmyMixLogic.Retarget(
					plan, ReferencePlans.InfantryQueue,
					ReferencePlans.RifleBodies, ReferencePlans.RocketBodies);

			// Captured before the income cap below rewrites the plan again. Reference equality
			// after both rules have run cannot tell which of them moved it, and attributing the
			// cap's work to the mix rule would make the mix rule's own check unfalsifiable.
			var retargeted = !ReferenceEquals(plan, unretargeted);

			// Income before bodies, across queues rather than within one.
			//
			// Everything above arbitrates rung order inside a single plan; cash is shared by
			// every queue on the field and nothing arbitrated that. The airfield stood for
			// roughly 540 seconds on badland-ridges, issued two orders, and took 143 seconds to
			// deliver one 1,100-credit harvester, while the barracks spent 8,900 credits on 59
			// riflemen and cash read 0 at every economy sample from 180s on. The fleet averaged
			// 1.06 live harvesters and income finished at 17.2 credits a second.
			//
			// Applied last, so it caps whatever body the mix rule just chose rather than racing
			// it, and it returns the plan it was handed, by reference, on any healthy economy.
			// See IncomeFirstLogic for the three conditions and why each is narrow.
			var shortBelow = ArmyBalanceLogic.ReleaseAt(
				ExpansionLogic.DesiredHarvesters(refineries, 0, expansion), balance);
			var standingHarvesters = ExpansionLogic.Standing(owned, ReferencePlans.HarvesterUnits);
			var hold = IncomeFirstLogic.Hold(
				plan, ReferencePlans.InfantryQueue, ctx.Cash,
				standingHarvesters, shortBelow, factories > 0, income);

			plan = hold.Plan;

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

			// The income gate is invisible in what got built — a barracks that buys four rifles
			// looks the same whether it was capped at four or simply had no fifth rung to reach
			// — so it says so itself, on whichever queue's order survived it.
			if (hold.Held)
				why += $", income first: {hold.Standing} of {hold.ShortBelow} harvesters on {hold.Cash} cash, {hold.RungsCapped} infantry rung(s) capped at {income.GarrisonBodies}";

			// Claimed only when the retarget is what changed the answer, not merely when it was
			// active. A bounded e3 rung produces the same actor, so a reason that said "rockets"
			// whenever a rocket came out would pass a `reason:` check without the new branch
			// having done anything — which is precisely the confusion the literal exists to
			// settle. Re-asking the unretargeted plan costs one walk of thirteen steps and only
			// while the swap is on.
			//
			// The baseline has to carry the income cap too, or a difference this rule caused
			// would be reported as the mix rule's doing. Capping is a rewrite of counts and
			// retargeting is a rewrite of candidates, so applying both in either order lands on
			// the same plan; re-deriving it here keeps the comparison to one variable.
			if (retargeted
				&& ArmyMixLogic.Names(ReferencePlans.RocketBodies, choice.ActorType))
			{
				var baseline = hold.Held
					? IncomeFirstLogic.Hold(
						unretargeted, ReferencePlans.InfantryQueue, ctx.Cash,
						standingHarvesters, shortBelow, factories > 0, income).Plan
					: unretargeted;

				var before = UnitProductionLogic.ChooseNext(state, baseline);
				if (before.IsValid && ArmyMixLogic.Names(ReferencePlans.RifleBodies, before.ActorType))
					why += $", rifles swapped for rockets, {seen.Armour} of {seen.Total} seen wear armour";
			}

			return UnitDecision.Produce(choice.Queue, choice.ActorType, why);
		}
	}
}
