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

using AutoCnC.Mod.Modes;
using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Mod.Library
{
	/// <summary>
	/// Guards a position: holds a tether around its anchor, engages threats that come to it,
	/// refuses to be baited beyond its leash, and withdraws for repair when badly hurt.
	/// </summary>
	/// <remarks>
	/// Reference implementation of the sense/decide/act pattern. Note how little happens here —
	/// the judgement lives in <see cref="DefensiveLogic"/>, which has no engine dependency and is
	/// unit-tested directly.
	/// </remarks>
	public sealed class DefensiveMode : UnitMode
	{
		DefensiveTuning tuning = DefensiveTuning.Default;
		bool recovering;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			// Scale the leash off the unit's own reach, so short-ranged units stay tighter to the
			// anchor than artillery does.
			var range = ctx.WeaponRangeUnits;
			if (range > 0)
				tuning = DefensiveTuning.Default with
				{
					TetherRadiusUnits = range * 2,
					LeashRadiusUnits = range * 3
				};

			recovering = false;

			// Guard wherever we were standing when the mode was assigned.
			ctx.Anchor = self.Location;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			var state = new DefensiveState(
				HealthPercent: ctx.HealthPercent,
				DistanceFromAnchorUnits: ctx.DistanceFromAnchorUnits,
				WeaponRangeUnits: ctx.WeaponRangeUnits,
				IsIdle: ctx.IsIdle,
				HasWeapon: ctx.HasWeapon,
				CanMove: ctx.CanMove,
				RepairAvailable: ctx.FindRepairBay() != null,
				Threats: ctx.SenseThreats(SenseRadius(ctx)));

			// --- Decide ------------------------------------------------------------
			var decision = DefensiveLogic.Decide(state, tuning);

			// Hysteresis, so a unit that limps to the repair bay stays long enough to actually be
			// repaired instead of oscillating in and out of combat at the retreat threshold.
			if (decision.Action == UnitAction.Retreat)
				recovering = true;
			else if (recovering && state.HealthPercent < tuning.ResumeAboveHealthPercent)
				decision = UnitDecision.Retreat("still recovering");
			else
				recovering = false;

			return decision;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Being shot from outside our sense radius is the one case the periodic scan cannot
			// see. Pull the anchor toward the attacker so the next evaluation reacts, rather than
			// issuing an order from here and fighting the executor's order suppression.
			var attacker = e.Attacker;
			if (attacker == null || attacker.IsDead || !attacker.IsInWorld)
				return;

			if (self.Owner.RelationshipWith(attacker.Owner) != PlayerRelationship.Enemy)
				return;

			if (!ctx.CanAttack(attacker))
				return;

			// Bring forward the next evaluation by clearing our record of what we last did.
			ctx.RequestReevaluation();
		}

		WDist SenseRadius(ModeContext ctx)
		{
			// Sense a little past the leash so threats are seen before they are in range.
			var units = tuning.LeashRadiusUnits;
			var weapon = ctx.WeaponRangeUnits;
			return new WDist(units > weapon ? units : weapon);
		}
	}
}
