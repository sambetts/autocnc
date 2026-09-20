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

		/// <summary>The production plan's existing release band for an understrength role.</summary>
		readonly BalanceTuning balance = BalanceTuning.Default;

		readonly List<FieldOption> fields = [];

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

		// Where we decided to put the current building, per queue, so the choice doesn't wander.
		readonly CPos?[] plannedLocation = new CPos?[ConstructionQueues.Length];
		readonly string[] plannedItem = new string[ConstructionQueues.Length];

		bool deferredConstructionForHarvester;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			for (var i = 0; i < ConstructionQueues.Length; i++)
			{
				plannedLocation[i] = null;
				plannedItem[i] = null;
			}

			fields.Clear();
			evaluationsSinceScan = int.MaxValue;
			deferredConstructionForHarvester = false;
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

			if (order.Action == ConstructionAction.Produce
				&& Named(VehicleFactoryCandidates, order.Item)
				&& ExpansionLogic.Standing(owned, VehicleFactoryCandidates) == 0
				&& ExpansionLogic.Standing(owned, RefineryCandidates) == 1)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Reason}, opening replacement factory before second refinery");
			}

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
			// because a brownout would slow the harvester too.
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

			if (factoryBackedHarvesterRecovery
				&& order.Action == ConstructionAction.Produce
				&& !Named(PowerCandidates, order.Item))
			{
				deferredConstructionForHarvester = true;
				return UnitDecision.Hold(
					"construction cash held until recovery harvester delivery");
			}

			if (deferredConstructionForHarvester
				&& !factoryBackedHarvesterRecovery
				&& order.Action == ConstructionAction.Produce)
			{
				order = new ConstructionOrder(order.Action, order.Queue, order.Item,
					$"{order.Reason}, construction resumed after recovery harvester delivered");
				deferredConstructionForHarvester = false;
			}

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

			if (plannedItem[i] != order.Item || plannedLocation[i] == null)
			{
				plannedItem[i] = order.Item;

				// Income and the ground it needs expand outward, and so does the defence that
				// covers them — a tower only defends what its weapon reaches, and the default
				// ring puts every tower on the yard. The ring is an ambition and the ladder is
				// what is reachable: the near edge walks inward a couple of cells at a time
				// while the far edge stays put, so the first rung that matches is the
				// furthest-out band this base can legally build in. The last rung is the default
				// ring, so a structure already paid for always has somewhere to go.
				var ladder = BasePlacementLogic.LadderFor(order.Item, owned, ExpandingRoles, CoveringRoles);

				CPos? chosen = null;
				for (var rung = 0; rung < ladder.Count && chosen == null; rung++)
					chosen = ctx.FindBuildLocation(order.Item, ladder[rung].MinRangeCells, ladder[rung].MaxRangeCells);

				plannedLocation[i] = chosen ?? ctx.FindBuildLocation(order.Item);
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
