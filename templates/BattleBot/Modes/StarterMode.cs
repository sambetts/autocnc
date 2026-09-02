using __AUTOCNC_BOT_ROOT_NAMESPACE__.Logic;
using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;

namespace __AUTOCNC_BOT_ROOT_NAMESPACE__.Modes
{
	public sealed class StarterMode : UnitMode
	{
		const string BuildingQueue = "Building";
		static readonly string[] PowerPlants = ["powr", "nuke"];
		static readonly WDist ContactRadius = new(10 * 1024);

		CPos? plannedLocation;
		string plannedItem;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			plannedLocation = null;
			plannedItem = null;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			if (ctx.CanDeploy && ctx.DeploysIntoBuilding)
				return UnitDecision.Deploy("deploying the construction vehicle");

			if (ctx.OwnsQueue(BuildingQueue))
				return BuildBase(ctx);

			var production = UnitProductionLogic.ChooseNext(
				new ArmyPlanState(
					Cash: ctx.Cash,
					Queues: ctx.QueueStates(),
					Owned: ctx.OwnedUnitCounts()),
				ctx.ProductionPlan);

			if (production.IsValid && ctx.OwnsQueue(production.Queue))
				return UnitDecision.Produce(
					production.Queue,
					production.ActorType,
					$"training {production.ActorType}");

			if (ctx.IsBuilding || !ctx.CanMove)
				return UnitDecision.Continue;

			return StarterLogic.Decide(new StarterState(
				HasWeapon: ctx.HasWeapon,
				IsIdle: ctx.IsIdle,
				Threats: ctx.SenseThreats(ContactRadius)));
		}

		UnitDecision BuildBase(ModeContext ctx)
		{
			var ready = ctx.ItemReadyToPlace(BuildingQueue);
			if (ready != null)
			{
				if (plannedItem != ready || plannedLocation == null)
				{
					plannedItem = ready;
					plannedLocation = ctx.FindBuildLocation(ready);
				}

				if (plannedLocation == null)
					return UnitDecision.Continue;

				return UnitDecision.PlaceBuilding(
					BuildingQueue,
					ready,
					plannedLocation.Value.X,
					plannedLocation.Value.Y,
					$"placing {ready}");
			}

			if (ctx.ProducingItem(BuildingQueue) != null)
				return UnitDecision.Continue;

			plannedItem = null;
			plannedLocation = null;

			var next = BaseBuildLogic.ChooseNext(
				new BasePlanState(
					Cash: ctx.Cash,
					PowerBalance: ctx.PowerBalance,
					Buildable: ctx.BuildableItems(BuildingQueue),
					Owned: ctx.OwnedBuildingCounts()),
				ctx.BuildPlan,
				PowerPlants);

			return next == null
				? UnitDecision.Continue
				: UnitDecision.Produce(BuildingQueue, next, $"building {next}");
		}
	}
}
