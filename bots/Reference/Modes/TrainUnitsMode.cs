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
		/// How much of Defence's paired screens must survive before the queue may move on.
		/// </summary>
		/// <remarks>
		/// One survivor preserves each role without letting repeated losses of the second member
		/// pin its queue. Falling back to zero restores the full pair as the first priority.
		/// </remarks>
		readonly BalanceTuning defenceScreenBalance = new(
			ReleaseNumerator: 1,
			ReleaseDenominator: 2);

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

			// At zero anti-air, the first Defence rung is normally strict. That is correct until
			// a large infantry screen is already inside the defensive leash: repeatedly feeding
			// slow rockets into rifles then prevents the barracks from producing the cheap body
			// that can clear them. Temporarily substitute rifles only for that empty floor.
			// Current local pressure is used rather than remembered composition, and any visible
			// aircraft restores the original anti-air request immediately.
			var standingAntiAir = ExpansionLogic.Standing(owned, ReferencePlans.AntiAirUnits);
			var defencePressure = EnemyMix.None;
			var defenceInfantryFloorRedirected = false;
			if (ctx.Doctrine == ReferenceDoctrines.Defence && standingAntiAir == 0)
			{
				var localThreats = ctx.SenseThreats(
					new WDist(DefensiveTuning.Default.LeashRadiusUnits));
				var infantry = 0;
				var armour = 0;
				var aircraftVisible = false;

				for (var i = 0; i < localThreats.Count; i++)
				{
					var threat = localThreats[i];
					if (!threat.CanHitUs)
						continue;

					switch (threat.Kind)
					{
						case ThreatKind.Infantry:
							infantry++;
							break;
						case ThreatKind.Vehicle:
							armour++;
							break;
						case ThreatKind.Aircraft:
							armour++;
							aircraftVisible = true;
							break;
					}
				}

				defencePressure = new EnemyMix(infantry, armour);
				if (!aircraftVisible && ArmyMixLogic.PreferAntiInfantry(defencePressure, mix))
				{
					var original = plan;
					plan = ArmyMixLogic.RetargetFirstBounded(
						plan,
						ReferencePlans.InfantryQueue,
						ReferencePlans.AntiAirUnits,
						ReferencePlans.RifleBodies,
						ReferencePlans.DefenceAntiAirCore);
					defenceInfantryFloorRedirected = !ReferenceEquals(plan, original);
				}
			}

			// A strict standing pair can pin a queue during sustained attrition: the first
			// survivor dies while the second is training, so the first unmet rung never moves.
			// Keep zero-to-one replacement strict, then let one survivor cover the rifle rebuild.
			var defenceAntiAirRelease = ctx.Doctrine == ReferenceDoctrines.Defence
				? ArmyBalanceLogic.Release(
					plan, owned, ReferencePlans.AntiAirUnits, defenceScreenBalance)
				: new FloorRelease(plan, 0, 0);
			plan = defenceAntiAirRelease.Plan;

			// The light screen has the same attrition shape on the Vehicle queue. Keep the first
			// always-buildable escort strict, then let one survivor release the second slot so
			// harvesters and siege vehicles remain reachable during a sustained base attack.
			var defenceVehicleRelease = ctx.Doctrine == ReferenceDoctrines.Defence
				? ArmyBalanceLogic.Release(
					plan, owned, ReferencePlans.ScreenVehicles, defenceScreenBalance)
				: new FloorRelease(plan, 0, 0);
			plan = defenceVehicleRelease.Plan;

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

			// Keep the faction's always-buildable light screen funded while it is below the
			// plan's release band. Siege units cannot serve this role until tech exists.
			var screenVehicleRung = ExpansionLogic.FirstRungNaming(plan, ReferencePlans.ScreenVehicles);
			var screenVehicleTarget = screenVehicleRung >= 0
				? plan[screenVehicleRung].DesiredCount
				: 0;
			var screenVehicleShortBelow = ArmyBalanceLogic.ReleaseAt(screenVehicleTarget, balance);
			var standingScreenVehicles = ExpansionLogic.Standing(owned, ReferencePlans.ScreenVehicles);
			var screenVehicleBuildable = false;
			foreach (var actorType in ctx.BuildableItems("Vehicle"))
				if (ArmyMixLogic.Names(ReferencePlans.ScreenVehicles, actorType))
				{
					screenVehicleBuildable = true;
					break;
				}

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
			var screenHold = IncomeFirstLogic.HoldForPriority(
				plan, ReferencePlans.InfantryQueue, ctx.Cash,
				standingScreenVehicles, screenVehicleShortBelow,
				screenVehicleBuildable, income.ScreenVehiclePrice, income);

			plan = screenHold.Plan;

			var shortBelow = ArmyBalanceLogic.ReleaseAt(
				ExpansionLogic.DesiredHarvesters(refineries, 0, expansion), balance);
			var standingHarvesters = ExpansionLogic.Standing(owned, ReferencePlans.HarvesterUnits);
			var fundingCriticalRefinery = IncomeFirstLogic.ShouldFundCriticalRefinery(
				refineries,
				ArmyMixLogic.Names(ReferencePlans.Refineries, ctx.ProducingItem("Building")));
			var incomeHold = IncomeFirstLogic.Hold(
				plan, ReferencePlans.InfantryQueue, ctx.Cash,
				standingHarvesters, shortBelow, factories > 0, income);

			plan = incomeHold.Plan;

			// A cash hold protects the Vehicle queue from other queues, but not from a cheaper
			// rung in the Vehicle queue itself. Keep the first harvester rung ahead until the
			// fleet reaches the same release floor used by the rest of the production logic.
			// Reaching that floor restores the doctrine's exact order.
			var unprioritized = plan;
			plan = IncomeFirstLogic.PrioritizeRecovery(
				plan, ReferencePlans.HarvesterUnits,
				standingHarvesters, shortBelow, factories > 0);

			// --- Decide ------------------------------------------------------------
			var choice = UnitProductionLogic.ChooseNext(state, plan);
			if (!choice.IsValid)
				return UnitDecision.Continue;

			// Only the building that owns this queue should issue the order, or every
			// barracks would race to queue the same unit.
			if (!ctx.OwnsQueue(choice.Queue))
				return UnitDecision.Continue;

			var harvesterRecoveryChoice =
				standingHarvesters < shortBelow
					&& ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, choice.ActorType);
			if (fundingCriticalRefinery && !harvesterRecoveryChoice)
				return UnitDecision.Hold("unit queue yielding shared cash to active critical refinery");

			// --- Act ---------------------------------------------------------------
			var standingRifles = ExpansionLogic.Standing(owned, ReferencePlans.RifleBodies);
			var rifleRung = ExpansionLogic.FirstRungNaming(plan, ReferencePlans.RifleBodies);
			var antiAirPriorityChangedChoice =
				ctx.Doctrine == ReferenceDoctrines.Defence
				&& standingAntiAir < ReferencePlans.DefenceAntiAirCore
				&& rifleRung >= 0
				&& standingRifles < plan[rifleRung].DesiredCount
				&& ArmyMixLogic.Names(ReferencePlans.AntiAirUnits, choice.ActorType);

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

			if (antiAirPriorityChangedChoice)
				why += $", defence anti-air screen first: {standingAntiAir} of {ReferencePlans.DefenceAntiAirCore}";

			if (defenceInfantryFloorRedirected
				&& ArmyMixLogic.Names(ReferencePlans.RifleBodies, choice.ActorType))
				why += $", defence infantry pressure replacing empty anti-air floor: {defencePressure.Infantry} infantry, {defencePressure.Armour} armour nearby";

			if (defenceAntiAirRelease.Released)
				why += $", defence mixed screen released: {defenceAntiAirRelease.Standing} of {defenceAntiAirRelease.Target} anti-air standing";

			if (defenceVehicleRelease.Released)
				why += $", defence light vehicle screen released: {defenceVehicleRelease.Standing} of {defenceVehicleRelease.Target} standing";

			// The income gate is invisible in what got built — a barracks that buys four rifles
			// looks the same whether it was capped at four or simply had no fifth rung to reach
			// — so it says so itself, on whichever queue's order survived it.
			if (incomeHold.Held)
				why += $", income first: {incomeHold.Standing} of {incomeHold.ShortBelow} harvesters on {incomeHold.Cash} cash, {incomeHold.RungsCapped} infantry rung(s) capped at {income.GarrisonBodies}";

			if (screenHold.Held)
				why += $", light vehicle screen first: {screenHold.Standing} of {screenHold.ShortBelow} on {screenHold.Cash} cash, {screenHold.RungsCapped} infantry rung(s) capped at {income.GarrisonBodies}";

			if (!ReferenceEquals(plan, unprioritized)
				&& ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, choice.ActorType))
			{
				var beforeRecovery = UnitProductionLogic.ChooseNext(state, unprioritized);
				if (!beforeRecovery.IsValid
					|| !ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, beforeRecovery.ActorType))
					why += $", understrength harvester floor first: {standingHarvesters} of {shortBelow}";
			}

			// Claimed only when the retarget is what changed the answer, not merely when it was
			// active. A bounded e3 rung produces the same actor, so a reason that said "rockets"
			// whenever a rocket came out would pass a `reason:` check without the new branch
			// having done anything — which is precisely the confusion the literal exists to
			// settle. Re-asking the unretargeted plan costs one walk of thirteen steps and only
			// while the swap is on.
			//
			// The baseline has to carry both funding caps too, or a difference they caused would
			// be reported as the mix rule's doing. Capping rewrites counts and retargeting
			// rewrites candidates, so re-deriving them here keeps the comparison to one variable.
			if (retargeted
				&& ArmyMixLogic.Names(ReferencePlans.RocketBodies, choice.ActorType))
			{
				var baseline = IncomeFirstLogic.HoldForPriority(
					unretargeted, ReferencePlans.InfantryQueue, ctx.Cash,
					standingScreenVehicles, screenVehicleShortBelow,
					screenVehicleBuildable, income.ScreenVehiclePrice, income).Plan;
				baseline = IncomeFirstLogic.Hold(
					baseline, ReferencePlans.InfantryQueue, ctx.Cash,
					standingHarvesters, shortBelow, factories > 0, income).Plan;
				baseline = IncomeFirstLogic.PrioritizeRecovery(
					baseline, ReferencePlans.HarvesterUnits,
					standingHarvesters, shortBelow, factories > 0);

				var before = UnitProductionLogic.ChooseNext(state, baseline);
				if (before.IsValid && ArmyMixLogic.Names(ReferencePlans.RifleBodies, before.ActorType))
					why += $", rifles swapped for rockets, {seen.Armour} of {seen.Total} seen wear armour";
			}

			if (standingHarvesters < shortBelow
				&& ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, choice.ActorType))
				why += ", reserving construction cash before harvester queue visibility";

			return UnitDecision.Produce(choice.Queue, choice.ActorType, why);
		}
	}
}
