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
using OpenRA.Mods.Common.Traits;

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

		/// <summary>When a threat is big enough to counter, and how hard. See <see cref="CounterLogic"/>.</summary>
		readonly CounterTuning counterTuning = CounterTuning.Default;

		/// <summary>How long a sighting keeps counting toward the threat mix: two minutes.</summary>
		/// <remarks>
		/// Long enough to remember the army that attacked last, short enough that a composition
		/// the enemy has moved on from stops steering production.
		/// </remarks>
		const int CounterWindowTicks = 120 * 25;

		const string VehicleQueue = "Vehicle";

		/// <summary>
		/// How poor the side has to be before the barracks stops outbidding the harvester.
		/// See <see cref="IncomeFirstLogic"/>.
		/// </summary>
		readonly IncomeFirstTuning income = IncomeFirstTuning.Default;

		/// <summary>
		/// How many of a role may die, without its floor ever being met, before that floor stops
		/// pinning this queue. See <see cref="AttritionLogic"/>.
		/// </summary>
		readonly AttritionTuning attrition = AttritionTuning.Default;

		/// <summary>
		/// This building's tally of light screen vehicles bought and lost since the screen floor
		/// was last actually standing.
		/// </summary>
		/// <remarks>
		/// Per building rather than per player, and reset on entry, so a factory built after the
		/// last one was overrun is entitled to find out for itself whether a screen survives now.
		/// </remarks>
		FloorAttrition screenAttrition;

		/// <summary>
		/// This building's tally of line armour bought and lost since the armour floor was last
		/// actually standing.
		/// </summary>
		/// <remarks>
		/// The armour floor has the screen floor's failure shape and it cost 16:9 the match.
		/// <c>DefenceTrain</c> asks for a pair of faction-equivalent tanks, softened by
		/// <see cref="ArmyBalanceLogic"/> to one survivor. The five <c>mtnk</c> built lived
		/// <b>69, 16, 24, 78 and 37 seconds</b>, so nothing was standing at nearly every
		/// evaluation, zero is below one, and the rung was the Vehicle queue's first unmet step
		/// for the rest of the game — with <see cref="ReferencePlans.SiegeVehicles"/> and the
		/// harvester saturation rung sitting directly underneath it.
		/// <para>
		/// Three of those tanks were already dead by <b>617s</b> without the floor ever standing,
		/// which is where this tally would have released it. Instead the queue bought two more
		/// for 1,800 credits that killed 260 between them, and asked for a harvester <b>zero</b>
		/// times between 377s and 960s. Eight of its forty-six orders were harvesters all match;
		/// ten were tanks bought explicitly because the armour anchor came first. The fleet
		/// peaked at seven against the opponent's thirteen, every one of them died between 939s
		/// and 1,099s with a single 48-second replacement, and lifetime earnings finished at
		/// <b>49,835 against the opponent's 135,845</b>. The side out-traded that opponent 1.44
		/// to 1 on value and 232 kills to 130 losses, and lost anyway, because it was out-earned
		/// nearly three to one.
		/// </para>
		/// <para>
		/// Per building and reset on entry, for the same reason the screen tally is: a factory
		/// built after the last one was overrun is entitled to find out for itself whether
		/// armour survives now. The floor returns intact the moment the pair actually stands.
		/// </para>
		/// </remarks>
		FloorAttrition armourAttrition;

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
			screenAttrition = default;
			armourAttrition = default;
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

			// What the base has standing that shoots on its own, and whether the yard could sell
			// it another one. Both are read here, beside the other building counts, because the
			// gun is bought from a queue this actor does not own and the only thing this actor
			// controls about it is whether it spends the credits first. See
			// IncomeFirstLogic.EmplacementBeforeBodies.
			var emplacements = ExpansionLogic.Standing(buildings, ReferencePlans.GuardTowers);
			var emplacementBuildable = false;
			var supportBuildable = ctx.BuildableItems(ReferencePlans.SupportQueue);
			if (supportBuildable != null)
				foreach (var actorType in supportBuildable)
					if (ArmyMixLogic.Names(ReferencePlans.GuardTowers, actorType))
					{
						emplacementBuildable = true;
						break;
					}

			if (refineries != scaledRefineries || !ReferenceEquals(plan, scaledFrom))
			{
				scaledFrom = plan;
				scaledRefineries = refineries;
				scaled = ExpansionLogic.Saturate(
					ExpansionLogic.Reinforce(plan, refineries, ReferencePlans.HarvesterUnits, expansion),
					refineries, ReferencePlans.HarvesterUnits, expansion);
			}

			plan = scaled;

			// A release band is a function of the floor's *target*, and that is the hole 16:9
			// went through. ArmyBalanceLogic softens a screen of two to one survivor, but bggy
			// lived a mean of 14.2 seconds across that match and about eight for the fourteen
			// built after 580s, so zero were standing at nearly every evaluation, zero is below
			// one, and the rung was the first unmet step of the Vehicle queue for the rest of
			// the game: 20 of the 24 orders issued after the Defence switch were bggy, for 667
			// credits a kill, while the armour and siege rungs beneath it fired once each.
			//
			// So the floor is also asked how many it has already bought and buried. Measured on
			// the standing count rather than on orders issued, because an order repeats every
			// evaluation until the host suppresses it and a drop in what is standing is one unit
			// dying however often it is seen. Read from the doctrine's own plan, before any
			// release has rewritten the target, so the tally is cleared only by genuinely
			// meeting the floor the doctrine asked for.
			var screenFloorRung = ExpansionLogic.FirstRungNaming(plan, ReferencePlans.ScreenVehicles);
			var screenFloor = screenFloorRung >= 0 ? plan[screenFloorRung].DesiredCount : 0;
			var standingScreenVehicles = ExpansionLogic.Standing(owned, ReferencePlans.ScreenVehicles);
			screenAttrition.Observe(standingScreenVehicles, screenFloor);

			// The line-armour floor is read the same way and for the same reason. Read from the
			// doctrine's own plan before any release has rewritten the target, so a tally is
			// cleared only by the pair the doctrine actually asked for standing together.
			var armourFloorRung = ExpansionLogic.FirstRungNaming(
				plan, ReferencePlans.DefenceArmourVehicles);
			var armourFloor = armourFloorRung >= 0 ? plan[armourFloorRung].DesiredCount : 0;
			var standingArmour = ExpansionLogic.Standing(owned, ReferencePlans.DefenceArmourVehicles);
			armourAttrition.Observe(standingArmour, armourFloor);

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

			// ...and that softening is not enough on its own when the screen is being farmed.
			// Write the floor off entirely once it has cost three vehicles without once
			// standing, so the durable rungs below it — armour, reach and income — are reachable
			// while the base is under the pressure that is eating the screen. Kept before the
			// screen's own cash reservation and recovery exemption below, so the whole apparatus
			// that protects this floor stands down together with the floor itself.
			var preAttrition = plan;
			var screenAttritionRelease = AttritionLogic.Release(
				plan, owned, ReferencePlans.ScreenVehicles, screenAttrition.Losses, attrition);
			plan = screenAttritionRelease.Plan;

			// One faction-equivalent tank gives the defensive screen a durable line unit without
			// turning a dying two-tank floor into the next permanent Vehicle-queue blocker.
			// Zero remains strict; one survivor releases the second slot to siege and income.
			var defenceArmourRelease = ctx.Doctrine == ReferenceDoctrines.Defence
				? ArmyBalanceLogic.Release(
					plan, owned, ReferencePlans.DefenceArmourVehicles, defenceScreenBalance)
				: new FloorRelease(plan, 0, 0);
			plan = defenceArmourRelease.Plan;

			// ...and that softening is no more sufficient here than it was for the screen. A
			// release band is computed from the floor's target, so it cannot reach a role that
			// is being destroyed on sight: the tanks that died in 16, 24 and 37 seconds left
			// zero standing, zero is below one, and the rung stayed the Vehicle queue's first
			// unmet step with reach and income directly beneath it. Write the floor off once it
			// has cost three tanks without once standing, so those rungs are reachable during
			// exactly the pressure that is eating the armour. Kept out of the doctrine test on
			// purpose: a role being farmed is being farmed whatever the side is doing, and the
			// endless armour rung is never written off because AttritionLogic skips it.
			var preArmourAttrition = plan;
			var armourAttritionRelease = AttritionLogic.Release(
				plan, owned, ReferencePlans.DefenceArmourVehicles, armourAttrition.Losses, attrition);
			plan = armourAttritionRelease.Plan;

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

			// What they are actually sending, answered with what the duel lab measured beats it
			// credit for credit. Applied after the two-bin mix rule so a measured counter wins
			// that argument, and before the funding caps below so they still cap whatever it
			// buys. Returns the plan by reference until a real army has been seen. See
			// CounterLogic and EnemySightings.RecentThreats.
			var counter = PlanCounter(ctx, plan, owned);
			plan = counter.Plan;

			// Keep the faction's always-buildable light screen funded while it is below the
			// plan's release band. Siege units cannot serve this role until tech exists.
			var screenVehicleRung = ExpansionLogic.FirstRungNaming(plan, ReferencePlans.ScreenVehicles);
			var screenVehicleTarget = screenVehicleRung >= 0
				? plan[screenVehicleRung].DesiredCount
				: 0;
			var screenVehicleShortBelow = ArmyBalanceLogic.ReleaseAt(screenVehicleTarget, balance);
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

			// A cash hold protects the Vehicle queue from other queues, but not from cheaper
			// rungs in the Vehicle queue itself. Recovery normally leads them. Preserve one
			// cheap screen while an earner survives, however, so the replacement is not started
			// behind the same unopposed pressure that killed the fleet. At zero harvesters,
			// recovery remains immediate.
			var preserveFirstScreen =
				standingHarvesters > 0
				&& standingHarvesters < shortBelow
				&& standingScreenVehicles < screenVehicleShortBelow
				&& screenVehicleBuildable;
			var unprioritized = plan;
			if (!preserveFirstScreen)
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

			var standingDefenceArmour = standingArmour;
			var defenceArmourAnchorChoice =
				ctx.Doctrine == ReferenceDoctrines.Defence
					&& standingDefenceArmour < ArmyBalanceLogic.ReleaseAt(
						ReferencePlans.DefenceArmourCore, defenceScreenBalance)
					&& ArmyMixLogic.Names(ReferencePlans.DefenceArmourVehicles, choice.ActorType);

			var harvesterRecoveryChoice =
				standingHarvesters < shortBelow
					&& ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, choice.ActorType);
			if (fundingCriticalRefinery && !harvesterRecoveryChoice)
				return UnitDecision.Hold("unit queue yielding shared cash to active critical refinery");

			// The base's first gun, bought before the next body, because nothing else this side
			// can buy trades anywhere near as well and because every retreat rule it owns
			// assumes that gun is there. Income still outranks it: a tower paid for with the
			// last credits of a dying economy is the last thing the side ever buys.
			if (IncomeFirstLogic.EmplacementBeforeBodies(
					emplacements, emplacementBuildable, ctx.Cash, income)
				&& !harvesterRecoveryChoice)
				return UnitDecision.Hold(
					$"unit queue yielding {ctx.Cash} shared cash to the base's first emplacement, "
						+ $"which costs {income.EmplacementPrice} and has nothing standing",
					"defence.emplacement-before-bodies");

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

			if (defenceArmourAnchorChoice)
				why += ", defence armour anchor first";

			if (defenceArmourRelease.Released)
				why += $", defence armour floor released: {defenceArmourRelease.Standing} of {defenceArmourRelease.Target} standing";

			// The income gate is invisible in what got built — a barracks that buys four rifles
			// looks the same whether it was capped at four or simply had no fifth rung to reach
			// — so it says so itself, on whichever queue's order survived it.
			if (incomeHold.Held)
				why += $", income first: {incomeHold.Standing} of {incomeHold.ShortBelow} harvesters on {incomeHold.Cash} cash, {incomeHold.RungsCapped} infantry rung(s) capped at {income.GarrisonBodies}";

			if (screenHold.Held)
				why += $", light vehicle screen first: {screenHold.Standing} of {screenHold.ShortBelow} on {screenHold.Cash} cash, {screenHold.RungsCapped} infantry rung(s) capped at {income.GarrisonBodies}";

			if (preserveFirstScreen
				&& ArmyMixLogic.Names(ReferencePlans.ScreenVehicles, choice.ActorType))
			{
				var recoveryPlan = IncomeFirstLogic.PrioritizeRecovery(
					unprioritized, ReferencePlans.HarvesterUnits,
					standingHarvesters, shortBelow, factories > 0);
				var recoveryChoice = UnitProductionLogic.ChooseNext(state, recoveryPlan);
				if (recoveryChoice.IsValid
					&& ArmyMixLogic.Names(ReferencePlans.HarvesterUnits, recoveryChoice.ActorType))
					why += ", first light screen before harvester recovery";
			}

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
				if (!preserveFirstScreen)
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

			// Claimed only when the write-off is what changed the answer, not merely when it was
			// active. A Vehicle queue buying a tank looks identical whether the screen floor was
			// written off or the screen simply happened to be standing, so the plan that still
			// carries the floor is re-asked and the branch is claimed only if that plan would
			// have bought another screen vehicle. The baseline carries the same funding caps and
			// the same recovery exemption, both re-derived against the unreleased floor, so the
			// comparison stays to one variable — otherwise a difference those caused would be
			// reported as this rule's doing. One extra walk of thirteen steps, and only while the
			// write-off is on.
			var screenFloorWrittenOff = false;
			if (screenAttritionRelease.Released
				&& !ArmyMixLogic.Names(ReferencePlans.ScreenVehicles, choice.ActorType))
			{
				var floorRung = ExpansionLogic.FirstRungNaming(preAttrition, ReferencePlans.ScreenVehicles);
				var floorShortBelow = ArmyBalanceLogic.ReleaseAt(
					floorRung >= 0 ? preAttrition[floorRung].DesiredCount : 0, balance);

				var baseline = IncomeFirstLogic.HoldForPriority(
					preAttrition, ReferencePlans.InfantryQueue, ctx.Cash,
					standingScreenVehicles, floorShortBelow,
					screenVehicleBuildable, income.ScreenVehiclePrice, income).Plan;
				baseline = IncomeFirstLogic.Hold(
					baseline, ReferencePlans.InfantryQueue, ctx.Cash,
					standingHarvesters, shortBelow, factories > 0, income).Plan;

				var baselinePreservesScreen =
					standingHarvesters > 0
					&& standingHarvesters < shortBelow
					&& standingScreenVehicles < floorShortBelow
					&& screenVehicleBuildable;
				if (!baselinePreservesScreen)
					baseline = IncomeFirstLogic.PrioritizeRecovery(
						baseline, ReferencePlans.HarvesterUnits,
						standingHarvesters, shortBelow, factories > 0);

				var before = UnitProductionLogic.ChooseNext(state, baseline);
				screenFloorWrittenOff = before.IsValid
					&& ArmyMixLogic.Names(ReferencePlans.ScreenVehicles, before.ActorType);
			}

			if (screenFloorWrittenOff)
				why += $", light screen floor written off after {screenAttritionRelease.Losses} lost without {screenAttritionRelease.Target} standing";

			// The armour floor claims its branch on the same terms, for the same reason: a
			// Vehicle queue buying artillery or a harvester looks identical whether the tank
			// floor stood down or the pair simply happened to be standing. Re-ask the plan that
			// still carries the floor, carrying every rewrite that runs after it, and claim the
			// branch only when that plan would have bought another tank.
			var armourFloorWrittenOff = false;
			if (armourAttritionRelease.Released
				&& !ArmyMixLogic.Names(ReferencePlans.DefenceArmourVehicles, choice.ActorType))
			{
				var baseline = preArmourAttrition;
				if (retargeted)
					baseline = ArmyMixLogic.Retarget(
						baseline, ReferencePlans.InfantryQueue,
						ReferencePlans.RifleBodies, ReferencePlans.RocketBodies);

				baseline = IncomeFirstLogic.HoldForPriority(
					baseline, ReferencePlans.InfantryQueue, ctx.Cash,
					standingScreenVehicles, screenVehicleShortBelow,
					screenVehicleBuildable, income.ScreenVehiclePrice, income).Plan;
				baseline = IncomeFirstLogic.Hold(
					baseline, ReferencePlans.InfantryQueue, ctx.Cash,
					standingHarvesters, shortBelow, factories > 0, income).Plan;
				if (!preserveFirstScreen)
					baseline = IncomeFirstLogic.PrioritizeRecovery(
						baseline, ReferencePlans.HarvesterUnits,
						standingHarvesters, shortBelow, factories > 0);

				var before = UnitProductionLogic.ChooseNext(state, baseline);
				armourFloorWrittenOff = before.IsValid
					&& ArmyMixLogic.Names(ReferencePlans.DefenceArmourVehicles, before.ActorType);
			}

			if (armourFloorWrittenOff)
				why += $", armour floor written off after {armourAttritionRelease.Losses} lost without {armourAttritionRelease.Target} standing";

			var counterChoice = counter.Changed
				&& (Picks(counter.Inserted, choice.ActorType)
					|| Picks(counter.InfantryRung, choice.ActorType)
					|| Picks(counter.VehicleRung, choice.ActorType));
			if (counterChoice)
			{
				var score = Picks(counter.Inserted, choice.ActorType) ? counter.Inserted.Score
					: Picks(counter.InfantryRung, choice.ActorType) ? counter.InfantryRung.Score
					: counter.VehicleRung.Score;
				why += $", countering {counter.Summary} with {choice.ActorType}, measured margin {score:+0;-0} per 100";
				if (Picks(counter.Inserted, choice.ActorType))
					why += $", counter rung of {counter.InsertedTarget}";
			}

			if (screenFloorWrittenOff)
				return UnitDecision.Produce(
					choice.Queue, choice.ActorType, why, "production.screen-floor-written-off");

			if (armourFloorWrittenOff)
				return UnitDecision.Produce(
					choice.Queue, choice.ActorType, why, "production.armour-floor-written-off");

			return counterChoice
				? UnitDecision.Produce(choice.Queue, choice.ActorType, why,
					!Picks(counter.Inserted, choice.ActorType) ? "production.counter-endless"
						: counter.AntiAir ? "production.counter-air"
						: "production.counter-rung")
				: UnitDecision.Produce(choice.Queue, choice.ActorType, why);
		}

		static bool Picks(in CounterPick pick, string actorType) =>
			pick.IsValid && string.Equals(pick.ActorType, actorType, System.StringComparison.OrdinalIgnoreCase);

		/// <summary>The best measured counter each queue can build now, folded into the plan.</summary>
		/// <remarks>
		/// Two threat mixes, because anticipation and sightings deserve different answers. A
		/// helipad seen is worth a bounded anti-air rung before the aircraft arrive. It is not
		/// worth re-planning every spare credit for the rest of the match. The first version let
		/// it retarget the endless infantry rung, and a GDI mirror built 19 riflemen where the
		/// champion built 62. So the inserted rung answers what was seen plus what was
		/// anticipated, and the endless rungs answer only what was seen.
		/// </remarks>
		CounterPlan PlanCounter(
			ModeContext ctx,
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyDictionary<string, int> owned)
		{
			var anticipated = EnemySightings.RecentThreats(
				ctx.Owner, ctx.WorldTick, CounterWindowTicks, counterTuning.AnticipatedAircraftValue);
			var floor = System.Math.Min(counterTuning.MinimumThreatValue, counterTuning.MinimumAirThreatValue);
			if (CounterLogic.TotalValue(anticipated) < floor)
				return new CounterPlan(plan, CounterPick.None, 0, CounterPick.None, CounterPick.None, 0, null);

			var seen = EnemySightings.RecentThreats(ctx.Owner, ctx.WorldTick, CounterWindowTicks, 0);
			var infantryItems = ctx.BuildableItems(ReferencePlans.InfantryQueue);
			var vehicleItems = ctx.BuildableItems(VehicleQueue);

			var insertInfantry = CounterLogic.Best(infantryItems, ReferencePlans.InfantryQueue, anticipated);
			var insertVehicle = CounterLogic.Best(vehicleItems, VehicleQueue, anticipated);
			var insert = insertInfantry.Score >= insertVehicle.Score ? insertInfantry : insertVehicle;

			// The best answer to their aircraft alone, from either queue.
			var aircraft = CounterLogic.Aircraft(anticipated);
			var antiAir = CounterPick.None;
			if (aircraft.Count > 0)
			{
				var airInfantry = CounterLogic.Best(infantryItems, ReferencePlans.InfantryQueue, aircraft);
				var airVehicle = CounterLogic.Best(vehicleItems, VehicleQueue, aircraft);
				antiAir = airInfantry.Score >= airVehicle.Score ? airInfantry : airVehicle;
			}

			var enough = CounterLogic.TotalValue(seen) >= counterTuning.MinimumThreatValue;
			var infantry = enough
				? CounterLogic.Best(infantryItems, ReferencePlans.InfantryQueue, seen)
				: CounterPick.None;
			var vehicle = enough ? CounterLogic.Best(vehicleItems, VehicleQueue, seen) : CounterPick.None;

			var rules = ctx.World.Map.Rules.Actors;
			return CounterLogic.Apply(
				plan, anticipated, insert, antiAir, infantry, vehicle, owned,
				actorType => rules.TryGetValue(actorType, out var info)
					? info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0
					: 0,
				ReferencePlans.HarvesterUnits[0],
				ReferencePlans.SiegeVehicles,
				counterTuning);
		}
	}
}
