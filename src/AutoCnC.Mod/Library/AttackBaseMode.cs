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
	/// Pushes into the enemy base and destroys high-value structures, ignoring distractions.
	/// </summary>
	/// <remarks>
	/// The defining rule is that this mode <b>never chases</b>: it only fires on what is already
	/// inside its weapon range, so a single enemy scout cannot peel an assault force off its
	/// objective. That is the deliberate inverse of AutoTarget's behaviour.
	/// </remarks>
	public sealed class AttackBaseMode : UnitMode
	{
		static readonly WDist ObjectiveSearchRadius = WDist.FromCells(40);

		AssaultTuning tuning = AssaultTuning.Default;
		uint objectiveId;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			tuning = AssaultTuning.Default;
			objectiveId = 0;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			var objective = ctx.ResolveActor(objectiveId);
			if (objective == null)
			{
				// Objective destroyed or never chosen: pick the next one. Deliberately sticky, so
				// the force commits instead of re-evaluating every tick and drifting between
				// buildings.
				objectiveId = AttackBaseLogic.SelectObjective(ctx.SenseStructures(ObjectiveSearchRadius)) ?? 0;
				objective = ctx.ResolveActor(objectiveId);
			}

			var weaponRange = ctx.WeaponRangeUnits;
			var state = new AssaultState(
				HealthPercent: ctx.HealthPercent,
				IsIdle: ctx.IsIdle,
				HasWeapon: ctx.HasWeapon,
				CanMove: ctx.CanMove,
				HasObjective: objective != null,
				ObjectiveActorId: objectiveId,
				DistanceToObjectiveUnits: objective != null ? ctx.DistanceTo(objective) : int.MaxValue,
				WeaponRangeUnits: weaponRange,
				Threats: ctx.SenseThreats(new WDist(weaponRange > 0 ? weaponRange : 1024)));

			// --- Decide ------------------------------------------------------------
			return AttackBaseLogic.Decide(state, tuning);
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Intentionally does nothing. Taking fire is the expected cost of a base assault;
			// reacting to it is exactly the distraction this mode exists to avoid. Static defences
			// are handled as blockers by AttackBaseLogic once they come into range.
		}
	}
}
