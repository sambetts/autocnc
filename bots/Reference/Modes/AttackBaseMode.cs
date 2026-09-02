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

using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using AutoCnC.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Pushes into the enemy base and destroys high-value structures, ignoring distractions.
	/// </summary>
	/// <remarks>
	/// The defining rule is that this mode <b>never chases</b>: it only fires on what is already
	/// inside its weapon range, so a single enemy scout cannot peel an assault force off its
	/// objective. That is the deliberate inverse of AutoTarget's behaviour.
	/// <para>
	/// Objectives are sensed, and sensing is limited to what is visible now and near this unit.
	/// A push that starts at home therefore begins with nothing to attack, so it marches on the
	/// last place this side saw an enemy structure until something comes into view.
	/// </para>
	/// </remarks>
	public sealed class AttackBaseMode : UnitMode
	{
		static readonly WDist ObjectiveSearchRadius = WDist.FromCells(40);

		/// <summary>
		/// How close counts as "standing on the remembered spot".
		/// </summary>
		/// <remarks>
		/// Deliberately far tighter than the objective search radius. Sensing shows only what is
		/// visible, and a unit forty cells away can see none of it — so treating that as "arrived
		/// and found nothing" would throw away a perfectly good sighting on behalf of the whole
		/// army. Standing on it and still seeing nothing is evidence; being in the same postcode
		/// is not.
		/// </remarks>
		static readonly WDist ArrivedRadius = WDist.FromCells(5);

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
				var visible = ctx.SenseStructures(ObjectiveSearchRadius);
				objectiveId = AttackBaseLogic.SelectObjective(visible) ?? 0;
				objective = ctx.ResolveActor(objectiveId);
			}

			// Anything we can see is worth remembering for the units that cannot.
			if (objective != null)
				EnemyBaseSightings.Record(self.Owner, objective.Location);

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
			return AttackBaseLogic.Decide(state, tuning, objective != null ? ApproachOrders.None : Approach(self, ctx));
		}

		/// <summary>
		/// Where to march with nothing in sight, and the housekeeping that keeps that honest.
		/// </summary>
		/// <remarks>
		/// A push that has run out of target ends its own doctrine, exactly as
		/// <see cref="ScoutMode"/> ends its own the moment it has an answer. Forgetting a stale
		/// sighting on its own is not enough: only a unit that can already see an enemy structure
		/// ever records a new one, so an army left with nowhere to march stops moving, and a
		/// stopped army never sees anything to record. Asking for the doctrine whose job is
		/// finding a target is the way out of that.
		/// <para>
		/// The bot's own assessment still wins where it has an opinion — this carries in the
		/// window where it has none, and <c>ReferenceBotLogic</c> rule 4 reaches the same
		/// conclusion from <c>SecondsSinceContact</c> shortly afterwards either way.
		/// </para>
		/// </remarks>
		static ApproachOrders Approach(Actor self, ModeContext ctx)
		{
			if (!EnemyBaseSightings.TryGetLastKnown(self.Owner, out var cell))
			{
				ctx.SwitchDoctrine(ReferenceDoctrines.Scout, "nothing left to attack, going looking");
				return ApproachOrders.None;
			}

			// Arrived, and there is nothing here after all: the sighting is stale, so drop it
			// rather than hold the whole push in front of an empty crater.
			var distance = ctx.DistanceTo(cell);
			if (distance <= ArrivedRadius.Length)
			{
				EnemyBaseSightings.Forget(self.Owner);
				ctx.SwitchDoctrine(ReferenceDoctrines.Scout, "their base is not there any more, going looking");
				return ApproachOrders.None;
			}

			return new ApproachOrders(true, cell.X, cell.Y, distance);
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Intentionally does nothing. Taking fire is the expected cost of a base assault;
			// reacting to it is exactly the distraction this mode exists to avoid. Static defences
			// are handled as blockers by AttackBaseLogic once they come into range.
		}
	}
}
