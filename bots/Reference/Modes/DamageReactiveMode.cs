using System;
using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>Answers actual incoming fire before a mode resumes its normal job.</summary>
	public abstract class DamageReactiveMode : UnitMode
	{
		const int ResponseTicks = 125;
		uint attackerId;
		int hitTick = int.MinValue;
		int damageTick = int.MinValue;
		CPos lastAttackerCell;
		CPos escapeCell;
		bool aircraft;

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			var attacker = e.Attacker;
			if (attacker == null || attacker.IsDead || !attacker.IsInWorld
				|| self.Owner.RelationshipWith(attacker.Owner) != PlayerRelationship.Enemy)
				return;

			damageTick = ctx.WorldTick;
			ctx.RequestReevaluation();
			if (!ModeContext.HasPosition(attacker))
				return;

			// Keep answering one live shooter rather than alternating on every impact.
			if (attackerId != 0 && attackerId != attacker.ActorID
				&& ctx.WorldTick - hitTick <= ResponseTicks)
			{
				var held = ctx.ResolveActor(attackerId);
				if (held != null && held.IsInWorld && !held.IsDead)
					return;
			}

			attackerId = attacker.ActorID;
			hitTick = ctx.WorldTick;
			lastAttackerCell = attacker.Location;
			aircraft = ModeContext.Classify(attacker) == ThreatKind.Aircraft;
			if (!ctx.CanMove || !ModeContext.HasPosition(self))
				return;

			var here = self.Location;
			var dx = Math.Sign(here.X - lastAttackerCell.X);
			var dy = Math.Sign(here.Y - lastAttackerCell.Y);
			if (dx == 0 && dy == 0)
			{
				dx = Math.Sign(ctx.BaseCenter.X - here.X);
				dy = Math.Sign(ctx.BaseCenter.Y - here.Y);
				if (dx == 0 && dy == 0)
					dx = 1;
			}

			var bounds = ctx.World.Map.Bounds;
			escapeCell = new CPos(
				Math.Clamp(here.X + dx * 6, bounds.Left, bounds.Right - 1),
				Math.Clamp(here.Y + dy * 6, bounds.Top, bounds.Bottom - 1));
		}

		protected UnitDecision? RespondToIncomingFire(Actor self, ModeContext ctx, bool evadeOnly = false)
		{
			// Buildings can repair even when the damage was credited to a positionless
			// superweapon owner. Never try to turn that owner into a map target.
			if (ctx.IsBuilding && damageTick != int.MinValue
				&& ctx.WorldTick >= damageTick && ctx.WorldTick - damageTick <= ResponseTicks)
			{
				var canReturnFire = false;
				var shooter = attackerId == 0 ? null : ctx.ResolveActor(attackerId);
				if (!evadeOnly && shooter != null && ctx.CanAttack(shooter)
					&& ctx.DistanceTo(shooter) <= ctx.WeaponRangeUnits)
					canReturnFire = true;

				if (!canReturnFire)
					foreach (var building in ctx.OwnedBuildingStates())
						if (building.ActorId == self.ActorID && building.IsRepairable
							&& building.HealthPercent < 100 && !building.RepairActive && !building.RepairRequested)
							return UnitDecision.RepairBuilding(self.ActorID,
								"repairing this building in response to incoming fire", "reaction.repair-under-fire");
			}

			if (attackerId == 0 || ctx.WorldTick < hitTick || ctx.WorldTick - hitTick > ResponseTicks)
				return null;

			var attacker = ctx.ResolveActor(attackerId);
			if (attacker != null
				&& (attacker.IsDead || !attacker.IsInWorld
					|| self.Owner.RelationshipWith(attacker.Owner) != PlayerRelationship.Enemy))
			{
				attackerId = 0;
				return null;
			}

			if (!evadeOnly && attacker != null && ctx.CanAttack(attacker)
				&& (!aircraft || ctx.DistanceTo(attacker) <= ctx.WeaponRangeUnits))
				return UnitDecision.Attack(attackerId,
					"answering incoming fire before normal orders", "reaction.return-fire");

			// Fog can hide a ground shooter after its shot. Use the reported cell, not
			// a hidden actor's current position, and never pursue an aircraft.
			if (!evadeOnly && ctx.HasWeapon && ctx.CanMove && !aircraft && attacker == null)
				return UnitDecision.AttackMoveTo(lastAttackerCell.X, lastAttackerCell.Y,
					"answering fire from the last reported ground position", "reaction.answer-reported-fire");

			if (ctx.CanMove && ModeContext.HasPosition(self))
				return UnitDecision.MoveTo(escapeCell.X, escapeCell.Y,
					"evading incoming fire that this unit cannot answer safely", "reaction.evade-fire");

			return null;
		}
	}
}
