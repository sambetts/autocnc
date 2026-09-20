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
//  cell ring around the base centre, which is wrong for a refinery: harvesters
//  work the closest tiberium to the refinery they dock with, so refineries
//  stacked in one ring share one patch and run it dry. Asking for one far ring
//  and falling home when it misses is no better, so this walks a ladder of
//  rings inward and takes the furthest one that is legal. Power plants lead the
//  way out, because they are the cheapest structure here that gives buildable
//  area and so the only affordable way to move the frontier at all.
//  See BasePlacementLogic.
//
//  A ring counted off the refineries already standing still knows nothing about
//  where the tiberium is, so refineries go up at the right distance in the wrong
//  direction. RefinerySitingLogic answers that from the resource layer, and this
//  mode asks it first for a refinery, falling through to the counted ladder when
//  it has no opinion. See Place and RefineryRings.
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
		static readonly string[] ConstructionQueues = ["Building", SupportQueue];

		/// <summary>The queue every defensive emplacement comes from.</summary>
		const string SupportQueue = ReferencePlans.SupportQueue;

		static readonly string[] PowerCandidates = [.. ReferencePlans.PowerPlants];

		/// <summary>Refinery names for either faction, as an array because a plan step wants one.</summary>
		static readonly string[] RefineryCandidates = [.. ReferencePlans.Refineries];

		/// <summary>Harvester names for either faction.</summary>
		static readonly string[] HarvesterCandidates = [.. ReferencePlans.HarvesterUnits];

		/// <summary>Vehicle factory names for either faction.</summary>
		static readonly string[] VehicleFactoryCandidates = [.. ReferencePlans.VehicleFactories];

		/// <summary>Anti-air tower names for either faction. See <see cref="ExpansionLogic.Shield"/>.</summary>
		static readonly string[] AirDefenceCandidates = [.. ReferencePlans.AntiAirStructures];

		/// <summary>Ground-defence tower names for either faction.</summary>
		static readonly string[] GroundDefenceCandidates = [.. ReferencePlans.GuardTowers];

		/// <summary>
		/// Every defensive structure, so the anti-air rung sizes itself from the base rather
		/// than from the towers already covering it.
		/// </summary>
		static readonly string[] DefenceStructures = [.. ReferencePlans.SupportQueueStructures];

		/// <summary>
		/// Structures that should be pushed outward as they multiply, rather than stacked at home.
		/// </summary>
		/// <remarks>
		/// Refineries, because harvesters work the closest tiberium to the refinery they dock
		/// with; and power plants, because they are the cheapest thing this bot builds that
		/// carries <c>GivesBuildableArea</c> and so the only affordable way to move the frontier
		/// the refineries need. See <see cref="BasePlacementLogic"/> and
		/// <see cref="ReferencePlans.ExpandingRoles"/>.
		/// </remarks>
		static readonly IReadOnlyList<ExpandingRole> ExpandingRoles = ReferencePlans.ExpandingRoles;

		/// <summary>
		/// Structures that follow the economy out, because a tower is only defence where its
		/// weapon reaches.
		/// </summary>
		/// <remarks>
		/// The economy expands and the defences did not, so they stacked on the construction
		/// yard while the refineries they were bought to protect stood 11 to 13 cells away and
		/// the harvesters working them died to infantry no tower could reach. See
		/// <see cref="ReferencePlans.CoveringRoles"/>.
		/// </remarks>
		static readonly IReadOnlyList<CoveringRole> CoveringRoles = ReferencePlans.CoveringRoles;

		/// <summary>
		/// How big an economy the tiberium this side has found is worth, when the plan has run out.
		/// </summary>
		/// <remarks>
		/// Consulted only after <see cref="BaseConstructionLogic.ChooseNext"/> has answered
		/// "nothing to do" over the doctrine's own plan, so it cannot delay, displace or outbid a
		/// single rung the doctrine wrote. See <see cref="ExpansionLogic"/> for why a fixed
		/// ladder is the wrong shape for a map.
		/// </remarks>
		readonly ExpansionTuning expansion = ExpansionTuning.Default;

		/// <summary>
		/// How far a refinery may sit from the field it is meant to serve, and how the rings
		/// that bracket that field are walked. See <see cref="RefinerySitingLogic"/>.
		/// </summary>
		readonly RefinerySitingTuning siting = RefinerySitingTuning.Default;

		/// <summary>The production plan's existing release band for an understrength role.</summary>
		readonly BalanceTuning balance = BalanceTuning.Default;

		/// <summary>How long the harvester-recovery hold may wait before it has failed.</summary>
		readonly RecoveryHoldTuning recoveryHold = RecoveryHoldTuning.Default;

		readonly List<FieldOption> fields = [];

		/// <summary>Where this side's refineries already stand, for <see cref="RefineryRings"/>.</summary>
		readonly List<SiteCell> refinerySites = [];

		/// <summary>The rings the resource layer asked for, per queue. Empty means "no opinion".</summary>
		static readonly PlacementRing[] NoRings = [];

		/// <summary>
		/// Evaluations since the last map scan, so the scan is paid for on a clock rather than
		/// every tick. <c>FindResourceFields</c> walks every cell; see
		/// <see cref="ExpansionTuning.RescanEvaluations"/>.
		/// </summary>
		int evaluationsSinceScan = int.MaxValue;

		// Sensing buffers are reused by the host, so what we read from one queue would be
		// overwritten by reading the next. Copy into buffers we own instead.
		readonly List<string>[] buildable = NewBuffers();

		readonly ConstructionQueueState[] queues = new ConstructionQueueState[ConstructionQueues.Length];

		// A one-queue view of Support, so the opening-emplacement exemption can ask that queue
		// what the plan wants from it without disturbing the shared buffer above.
		readonly ConstructionQueueState[] supportOnly = new ConstructionQueueState[1];

		// Where we decided to put the current building, per queue, so the choice doesn't wander.
		readonly CPos?[] plannedLocation = new CPos?[ConstructionQueues.Length];
		readonly string[] plannedItem = new string[ConstructionQueues.Length];

		// The resource layer's opinion about where the current building goes, per queue. Scanned
		// once when the item changes rather than on every evaluation: FindResourceFields walks
		// every cell of the map.
		readonly IReadOnlyList<PlacementRing>[] plannedRings =
			new IReadOnlyList<PlacementRing>[ConstructionQueues.Length];

		// The field those rings were asked for, so a cell the ring produced on the wrong side of
		// the base can be rejected rather than believed.
		readonly FieldOption[] plannedField = new FieldOption[ConstructionQueues.Length];
		readonly bool[] plannedFieldKnown = new bool[ConstructionQueues.Length];

		// A one-cell list, so a candidate site can be asked the same "does this claim the field"
		// question a standing refinery is asked. Reused rather than allocated per probe.
		readonly List<SiteCell> probe = [default];

		// Whether the cell finally chosen came from those rings, so the decision trace can say
		// so and a check can prove the branch ran.
		readonly bool[] plannedOnField = new bool[ConstructionQueues.Length];

		bool deferredConstructionForHarvester;

		/// <summary>
		/// What the recovery hold has seen the vehicle queue pay towards a harvester, so a hold
		/// that has stopped funding one can end. Per-yard memory; see
		/// <see cref="IncomeFirstLogic.TrackRecovery"/>.
		/// </summary>
		RecoveryHoldWatch recoveryWatch = RecoveryHoldWatch.Start;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			for (var i = 0; i < ConstructionQueues.Length; i++)
			{
				plannedLocation[i] = null;
				plannedItem[i] = null;
				plannedRings[i] = NoRings;
				plannedOnField[i] = false;
				plannedFieldKnown[i] = false;
			}

			fields.Clear();
			evaluationsSinceScan = int.MaxValue;
			deferredConstructionForHarvester = false;
			recoveryWatch = RecoveryHoldWatch.Start;
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
					plannedRings[i] = NoRings;
					plannedOnField[i] = false;
					plannedFieldKnown[i] = false;
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

			if (ctx.Doctrine == ReferenceDoctrines.Defence
				&& order.Action == ConstructionAction.Produce
				&& Named(AirDefenceCandidates, order.Item)
				&& ExpansionLogic.Standing(owned, AirDefenceCandidates) == 0
				&& ExpansionLogic.Standing(owned, GroundDefenceCandidates) <= 1)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Reason}, defensive anti-air released after first ground emplacement");
			}

			if (ctx.Doctrine == ReferenceDoctrines.Defence
				&& order.Action == ConstructionAction.Place
				&& Named(AirDefenceCandidates, order.Item)
				&& ExpansionLogic.Standing(owned, AirDefenceCandidates) == 0)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Reason}, placing first defensive anti-air from early release");
			}

			// The factory rung used to be re-tagged here as "opening replacement factory before
			// second refinery", from when it sat above that refinery in the ladder. It sits
			// below it now — see ReferencePlans.Economy for the 8,150 credits and one refinery
			// that paid for the move — so the condition can no longer be true and the tag would
			// only describe a plan this bot no longer has.

			// The doctrine's own ladder is finite and a map is not. When it has nothing left to
			// ask for, ask the ground instead: every patch of tiberium this side has explored is
			// worth a refinery, and a refinery is what makes a field close enough to work safely.
			//
			// Reached only on "nothing to do", so the doctrine's plan always wins outright and
			// the opening is bit-for-bit what it was. On badland-ridges this branch is the whole
			// difference: the Attack plan was fully satisfied when the fifth proc landed at 744s
			// and the Building queue issued no planned order in the remaining 842 seconds, while
			// 32,950 credits — 86% of everything spent after 744s — went on an army that was
			// worth 0 at the end.
			if (order.Action == ConstructionAction.None)
				order = Frontier(ctx, owned);

			// The economy frontier is a Building-queue rule, and the Support queue has no
			// equivalent: every doctrine's defence ladder is finite, so once it is met the yard
			// has nothing to ask Support for ever again. On badland-ridges that queue issued six
			// orders and idled 1,017 of 1,581 seconds while enemy orca took 13 of the 21
			// buildings this side lost. Asked last, so the economy keeps absolute priority and
			// nothing about the branch above changes. See ExpansionLogic.Shield.
			if (order.Action == ConstructionAction.None)
				order = AirDefence(ctx, owned);

			// Starting a construction item is not a cash reservation: it draws from the same
			// income as every production queue. Do not let a new refinery or tower strand an
			// active recovery harvester when the completed fleet is still below its established
			// release band. Owned counts include an active production item once its order is
			// visible, so exclude that item here: starting a harvester does not mean its income
			// has arrived. TrainUnitsMode keeps the harvester rung first until that floor is
			// restored. Finished structures still place, and emergency power remains available
			// because a brownout would slow the harvester too. Two things the hold is not allowed
			// to swallow are handled below: a refinery, which *is* harvester recovery, and a hold
			// that has stopped paying for the harvester it names.
			var standingHarvesters = ExpansionLogic.Standing(
				ctx.OwnedUnitCounts(), HarvesterCandidates);
			if (standingHarvesters > 0
				&& Named(HarvesterCandidates, ctx.ProducingItem("Vehicle")))
				standingHarvesters--;

			var refineryCount = ExpansionLogic.Standing(owned, RefineryCandidates);
			var harvesterRelease = ArmyBalanceLogic.ReleaseAt(
				ExpansionLogic.DesiredHarvesters(refineryCount, 0, expansion), balance);
			var factoryBackedHarvesterRecovery =
				standingHarvesters < harvesterRelease
				&& ExpansionLogic.Standing(owned, VehicleFactoryCandidates) > 0;

			// A deferral with no deadline is a deadlock. Measure the hold against the harvester
			// it claims to be funding: the vehicle queue's remaining cost falls as the item is
			// paid off, so a remaining cost that has not moved for a minute of game time is a
			// harvester nothing is being spent on. On 16:9 the yard held 293 times, 149 of them
			// in a window where lifetime earnings were frozen and cash read 0 at every sample —
			// reserving credits that did not exist, while the Support queue went unasked and
			// nothing was repaired. See IncomeFirstLogic.TrackRecovery.
			var harvesterInProduction = Named(HarvesterCandidates, ctx.ProducingItem("Vehicle"));
			var recovery = IncomeFirstLogic.TrackRecovery(
				factoryBackedHarvesterRecovery,
				harvesterInProduction,
				harvesterInProduction ? VehicleRemainingCost(ctx) : int.MaxValue,
				ctx.WorldTick,
				recoveryWatch,
				recoveryHold);

			recoveryWatch = recovery.Watch;
			if (recovery.Stalled)
				factoryBackedHarvesterRecovery = false;

			// Tag only the orders the hold would otherwise have swallowed, so the trace says what
			// the release actually bought. Power was already exempt, so releasing it buys nothing.
			var orderReasonId = recovery.Stalled
				&& order.Action == ConstructionAction.Produce
				&& !Named(PowerCandidates, order.Item)
					? "economy.recovery-hold-stalled"
					: null;

			var fundingCriticalRefinery = IncomeFirstLogic.ShouldFundCriticalRefinery(
				refineryCount,
				Named(RefineryCandidates, ctx.ProducingItem("Building")));

			if (fundingCriticalRefinery
				&& order.Action == ConstructionAction.Produce
				&& !string.Equals(order.Queue, "Building", System.StringComparison.OrdinalIgnoreCase))
			{
				if (factoryBackedHarvesterRecovery)
					deferredConstructionForHarvester = true;

				return UnitDecision.Hold("support queue yielding shared cash to active critical refinery");
			}

			// A refinery is not something the recovery hold should be saving against — it is the
			// recovery. proc carries FreeActor, so 1,500 credits buys an 1,100 credit harvester
			// and the dock it has to reach, and it is the only harvester on offer at all once the
			// war factory is dead. Three of this side's seven harvesters on 16:9 arrived that
			// way against four bought. See IncomeFirstLogic.IsRecoveryRefinery.
			var recoveryRefinery = order.Action == ConstructionAction.Produce
				&& factoryBackedHarvesterRecovery
				&& IncomeFirstLogic.IsRecoveryRefinery(order.Item, ReferencePlans.Refineries);

			if (recoveryRefinery)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Reason} through harvester recovery: a refinery ships a harvester with it");
				orderReasonId = "economy.refinery-is-recovery";
			}

			if (factoryBackedHarvesterRecovery
				&& !recoveryRefinery
				&& order.Action == ConstructionAction.Produce
				&& !Named(PowerCandidates, order.Item))
			{
				// The rescue hold must not leave the base naked. It is keyed on a condition the
				// opponent controls — harvesters below the release band — so against a side that
				// hunts them it never releases, and the yard it freezes is the only thing that
				// can build the emplacement that stops the hunting. Power is already exempt on
				// the weaker argument that a brownout slows the harvester; losing the harvester
				// outright is worse. Bounded to one emplacement of each role, so it can never
				// become a turtle. See IncomeFirstLogic.IsOpeningEmplacement.
				//
				// Not granted while a critical refinery is in production: that hold is narrow,
				// self-terminating, and protects the redundancy this base is rescued for.
				var emplacement = fundingCriticalRefinery ? null : OpeningEmplacement(ctx, owned);
				if (emplacement != null)
					return UnitDecision.Produce(SupportQueue, emplacement,
						$"first {emplacement} emplacement ahead of harvester recovery: base has no standing defence of that role",
						"defence.opening-emplacement");

				deferredConstructionForHarvester = true;
				return UnitDecision.Hold(
					"construction cash held until recovery harvester delivery");
			}

			if (deferredConstructionForHarvester
				&& !factoryBackedHarvesterRecovery
				&& order.Action == ConstructionAction.Produce)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					recovery.Stalled
						? $"{order.Reason}, construction resumed: the recovery harvester has not been paid towards for {recoveryHold.StallTicks} ticks"
						: $"{order.Reason}, construction resumed after recovery harvester delivered");
				deferredConstructionForHarvester = false;
			}

			// --- Act ---------------------------------------------------------------
			switch (order.Action)
			{
				case ConstructionAction.Place:
					return Place(ctx, order, owned);

				case ConstructionAction.Produce:
					return orderReasonId == null
						? UnitDecision.Produce(order.Queue, order.Item, order.Reason)
						: UnitDecision.Produce(order.Queue, order.Item, order.Reason, orderReasonId);

				default:
					// Plan complete, or nothing affordable yet. The yard's evaluation is going
					// spare, so spend it keeping what is already standing: this bot issued no
					// repair order at all on 16:9 and lost fourteen of its thirteen peak
					// buildings. Asked last and only outside the economy holds, so income keeps
					// the absolute priority every rule above it assumes. See
					// BaseRepairLogic.RepairBelowHealthPercent for which structures qualify.
					if (!fundingCriticalRefinery && !factoryBackedHarvesterRecovery)
					{
						var damaged = ctx.OwnedBuildingStates();
						var repair = BaseRepairLogic.Choose(damaged, ctx.Cash);
						if (repair != 0)
							return UnitDecision.RepairBuilding(repair,
								$"repairing {BaseRepairLogic.TypeOf(damaged, repair)} at {BaseRepairLogic.HealthOf(damaged, repair)}%: replacing it costs more than buying it back",
								"defence.repair-building");
					}

					return UnitDecision.Continue;   // plan complete, or nothing affordable yet
			}
		}

		/// <summary>
		/// What the vehicle queue still owes on the item it is building, or
		/// <see cref="int.MaxValue"/> when it is building nothing.
		/// </summary>
		/// <remarks>
		/// Read and discarded in one pass: sensing collections are reused by the host, so nothing
		/// from <c>QueueStates</c> is retained. This is the measurement
		/// <see cref="IncomeFirstLogic.TrackRecovery"/> uses to tell a harvester being paid for
		/// from one merely queued behind an economy that has stopped earning.
		/// </remarks>
		static int VehicleRemainingCost(ModeContext ctx)
		{
			var states = ctx.QueueStates();
			if (states == null)
				return int.MaxValue;

			foreach (var state in states)
				if (string.Equals(state.Queue, "Vehicle", System.StringComparison.OrdinalIgnoreCase))
					return state.CurrentRemainingCost;

			return int.MaxValue;
		}

		/// <summary>
		/// What to build once the doctrine's plan is met: the next rung of an economy sized by
		/// the tiberium this side has actually explored.
		/// </summary>
		/// <remarks>
		/// The scan is a map walk rather than a lookup, so it is paid for on a clock. Nothing
		/// returned by it is retained — sensing buffers are reused — so the fields are copied
		/// into <see cref="FieldOption"/>, exactly as <see cref="HarvesterMode"/> does.
		/// <para>
		/// Resource reads are shroud-filtered, so this counts only ground the side has been to.
		/// That is the honest number and it is the useful one: it makes scouting pay for itself
		/// twice, once in finding the enemy and once in raising the economy's ceiling.
		/// </para>
		/// </remarks>
		ConstructionOrder Frontier(ModeContext ctx, IReadOnlyDictionary<string, int> owned)
		{
			if (!ctx.HasResourceLayer)
				return ConstructionOrder.Nothing;

			if (evaluationsSinceScan < expansion.RescanEvaluations)
			{
				evaluationsSinceScan++;
				return ConstructionOrder.Nothing;
			}

			evaluationsSinceScan = 0;
			fields.Clear();

			var found = ctx.FindResourceFields(expansion.MinFieldCells, expansion.MaxFieldsConsidered);
			for (var i = 0; i < found.Count; i++)
			{
				var f = found[i];
				fields.Add(new FieldOption(f.NearestX, f.NearestY, f.CenterX, f.CenterY, f.CellCount, f.TotalDensity, f.DistanceUnits));
			}

			var plan = ExpansionLogic.Expand(
				ctx.BuildPlan, fields, owned, ctx.PowerBalance,
				RefineryCandidates, PowerCandidates, expansion);

			if (ReferenceEquals(plan, ctx.BuildPlan))
				return ConstructionOrder.Nothing;

			var order = BaseConstructionLogic.ChooseNext(
				queues, ctx.Cash, ctx.PowerBalance, owned, plan, PowerCandidates);

			return order.Action == ConstructionAction.Produce
				? new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Item} for {ExpansionLogic.FieldsWorthWorking(fields, expansion)} fields found")
				: order;
		}

		/// <summary>
		/// The next anti-air tower, once the doctrine's defence ladder has run out.
		/// </summary>
		/// <remarks>
		/// The counterpart of <see cref="Frontier"/> for the <c>Support</c> queue, and cheap for
		/// the same reason that one is not: this reads counts the yard already has rather than
		/// walking the map, so it needs no rescan clock.
		/// <para>
		/// It can only ever yield the appended rung. Everything above it in the plan has already
		/// answered "nothing to do" on this evaluation, and a rung whose candidates the queue
		/// cannot build is skipped silently — so a faction or a tech state with no anti-air
		/// tower available falls straight through. See <see cref="ExpansionLogic.Shield"/>.
		/// </para>
		/// </remarks>
		ConstructionOrder AirDefence(ModeContext ctx, IReadOnlyDictionary<string, int> owned)
		{
			var plan = ExpansionLogic.Shield(
				ctx.BuildPlan, owned, AirDefenceCandidates, DefenceStructures, expansion);

			if (ReferenceEquals(plan, ctx.BuildPlan))
				return ConstructionOrder.Nothing;

			var order = BaseConstructionLogic.ChooseNext(
				queues, ctx.Cash, ctx.PowerBalance, owned, plan, PowerCandidates);

			// Only claim the reason when the order is actually the appended rung. The power
			// override inside BaseBuildLogic can answer any plan with a power plant, and a
			// decision trace that labelled one of those "covering the base from the air" would
			// make the check that proves this branch works unfalsifiable.
			return order.Action == ConstructionAction.Produce && Named(AirDefenceCandidates, order.Item)
				? new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Item} covering {ExpansionLogic.StructuresWorthCovering(owned, DefenceStructures)} structures from the air")
				: order;
		}

		/// <summary>
		/// The emplacement the plan wants from <c>Support</c>, when the base has no standing
		/// defence of that role and the economy hold would otherwise block it.
		/// </summary>
		/// <remarks>
		/// Asked only after <see cref="BaseConstructionLogic.ChooseNext"/> has already chosen an
		/// order the hold refuses. That order is usually a <c>Building</c> one — the yard is a
		/// single decision per evaluation, so a blocked economy step hides the <c>Support</c>
		/// queue behind it entirely — which is why this re-asks with a one-queue view rather
		/// than inspecting the order it was handed.
		/// <para>
		/// The plan still decides what and whether: this routes the doctrine's own ladder rather
		/// than naming an actor, so cash, power and buildability are gated exactly as they are on
		/// the main path, and a rung whose candidates this faction cannot build yet is skipped
		/// silently. The answer is then accepted only when it really is a first emplacement, so
		/// a plan that offers depth instead falls through to the hold.
		/// </para>
		/// </remarks>
		string OpeningEmplacement(ModeContext ctx, IReadOnlyDictionary<string, int> owned)
		{
			var support = queues[IndexOf(SupportQueue)];
			if (!support.Owned || support.Busy || support.Buildable == null)
				return null;

			supportOnly[0] = support;

			var order = BaseConstructionLogic.ChooseNext(
				supportOnly, ctx.Cash, ctx.PowerBalance, owned, ctx.BuildPlan);

			if (order.Action != ConstructionAction.Produce)
				return null;

			return IncomeFirstLogic.IsOpeningEmplacement(
				order.Item, owned, GroundDefenceCandidates, AirDefenceCandidates)
				? order.Item
				: null;
		}

		static bool Named(string[] candidates, string item)
		{
			for (var i = 0; i < candidates.Length; i++)
				if (string.Equals(candidates[i], item, System.StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		UnitDecision Place(ModeContext ctx, in ConstructionOrder order, IReadOnlyDictionary<string, int> owned)
		{
			var i = IndexOf(order.Queue);

			// The map scan is paid for once per structure, not once per evaluation: the rings
			// only change when the item waiting to be placed does.
			if (plannedItem[i] != order.Item)
			{
				plannedItem[i] = order.Item;
				plannedLocation[i] = null;
				plannedOnField[i] = false;
				plannedRings[i] = RefineryRings(ctx, order.Item, out plannedField[i], out plannedFieldKnown[i]);
			}

			if (plannedLocation[i] == null)
			{
				// Income and the ground it needs expand outward, and so does the defence that
				// covers them — a tower only defends what its weapon reaches, and the default
				// ring puts every tower on the yard. The ring is an ambition and the ladder is
				// what is reachable: the near edge walks inward a couple of cells at a time
				// while the far edge stays put, so the first rung that matches is the
				// furthest-out band this base can legally build in. The last rung is the default
				// ring, so a structure already paid for always has somewhere to go.
				var ladder = BasePlacementLogic.LadderFor(order.Item, owned, ExpandingRoles, CoveringRoles);

				// A refinery asks the ground first. These rungs bracket the distance of the
				// nearest field no refinery is close enough to work — but a ring has a radius
				// and no direction, so a cell it offers is only accepted when it really would
				// claim that field. A rejected ring falls through to the counted ladder, which
				// is exactly the behaviour this bot had before. See RefinerySitingLogic.
				var sited = plannedRings[i];
				CPos? chosen = null;
				for (var rung = 0; rung < sited.Count && chosen == null; rung++)
				{
					var candidate = ctx.FindBuildLocation(
						order.Item, sited[rung].MinRangeCells, sited[rung].MaxRangeCells);

					if (candidate == null)
						continue;

					probe[0] = new SiteCell(candidate.Value.X, candidate.Value.Y);
					if (RefinerySitingLogic.IsClaimed(plannedField[i], probe, siting.ClaimRadiusCells))
						chosen = candidate;
				}

				plannedOnField[i] = chosen != null;

				for (var rung = 0; rung < ladder.Count && chosen == null; rung++)
					chosen = ctx.FindBuildLocation(order.Item, ladder[rung].MinRangeCells, ladder[rung].MaxRangeCells);

				plannedLocation[i] = chosen ?? ctx.FindBuildLocation(order.Item);
			}

			if (plannedLocation[i] == null)
				return UnitDecision.Continue;   // nowhere to put it; try again next tick

			var cell = plannedLocation[i].Value;

			return plannedOnField[i]
				? UnitDecision.PlaceBuilding(order.Queue, order.Item, cell.X, cell.Y,
					$"{order.Reason} beside the nearest field no refinery works",
					"economy.refinery-sited-on-field")
				: UnitDecision.PlaceBuilding(order.Queue, order.Item, cell.X, cell.Y, order.Reason);
		}

		/// <summary>
		/// Where the resource layer says the next refinery belongs, or no opinion at all.
		/// </summary>
		/// <remarks>
		/// <b><see cref="RefinerySitingLogic"/> was written to fix this and was never called.</b>
		/// Placement went through <see cref="BasePlacementLogic.LadderFor"/> alone, which sizes a
		/// ring from the number of refineries already standing and therefore knows the distance
		/// the next one should be at and nothing whatever about the direction.
		/// <para>
		/// On 16:9 that put both refineries on the wrong side of the yard. The yard stood at
		/// (76,19); <c>proc</c> one went up at (69,20) and <c>proc</c> two at (63,20) — six and
		/// thirteen cells <em>west</em> — while the only tiberium either harvester ever worked
		/// was east, and both harvest orders said so: "17 cells out" at 51s and "23 cells out"
		/// at 103s. A <c>harv</c> moves 1.758 cells a game second and carries about 700 credits,
		/// so those are round trips of 19 and 26 seconds of pure driving. The side earned
		/// <b>2,100 credits in 977 seconds</b> — about 9.5 credits per harvester-second against
		/// the 16.3 this bot's own notes call a healthy fleet — and a refinery 17 cells from its
		/// field is also outside the 12-cell radius OpenRA's built-in harvester search will look
		/// in, so nothing about it self-corrects.
		/// </para>
		/// <para>
		/// Only refineries ask. Everything else keeps the counted ladder exactly as it was, and
		/// a side with no resource layer, no explored field, or every field already claimed gets
		/// an empty list and the behaviour it had before.
		/// </para>
		/// </remarks>
		IReadOnlyList<PlacementRing> RefineryRings(
			ModeContext ctx, string item, out FieldOption field, out bool known)
		{
			field = default;
			known = false;

			if (!Named(RefineryCandidates, item) || !ctx.HasResourceLayer)
				return NoRings;

			// Sensing buffers are reused by the host, so copy before anything else is sensed.
			fields.Clear();
			var found = ctx.FindResourceFields(expansion.MinFieldCells, expansion.MaxFieldsConsidered);
			for (var i = 0; i < found.Count; i++)
			{
				var f = found[i];
				fields.Add(new FieldOption(f.NearestX, f.NearestY, f.CenterX, f.CenterY, f.CellCount, f.TotalDensity, f.DistanceUnits));
			}

			refinerySites.Clear();
			var standing = ctx.OwnedBuildingStates();
			if (standing != null)
				foreach (var b in standing)
					if (Named(RefineryCandidates, b.ActorType))
						refinerySites.Add(new SiteCell(b.CellX, b.CellY));

			var index = RefinerySitingLogic.ChooseField(fields, refinerySites, siting);
			if (index < 0)
				return NoRings;

			field = fields[index];
			known = true;

			// DistanceUnits is measured from whatever the scan was centred on — the yard, which
			// is also the origin FindBuildLocation measures its ring from. 1024 units is a cell.
			return RefinerySitingLogic.RingsFor(field.DistanceUnits / 1024, siting);
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
