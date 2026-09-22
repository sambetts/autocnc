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
	/// Guards a position: holds a tether around its anchor, engages threats that come to it,
	/// refuses to be baited beyond its leash, and withdraws for repair when badly hurt.
	/// </summary>
	/// <remarks>
	/// Reference implementation of the sense/decide/act pattern. Note how little happens here —
	/// the judgement lives in <see cref="DefensiveLogic"/>, which has no engine dependency and can
	/// be read on its own.
	/// </remarks>
	public sealed class DefensiveMode : UnitMode
	{
		DefensiveTuning tuning = DefensiveTuning.Default;
		GuardPostTuning guardTuning = GuardPostTuning.Default;
		CounterBatteryTuning counterBatteryTuning = CounterBatteryTuning.Default;
		WeaponRole role = WeaponRole.Unknown;
		bool recovering;
		bool consolidating;

		// How much of this unit's lifetime counter-battery allowance has been spent. Not cleared
		// by OnEnter, and that is the point: this mode is entered again on every doctrine change,
		// and a budget a doctrine switch refills is not a budget. See
		// CounterBatteryTuning.MaxEvaluations.
		int counterBatteryEvaluations;

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

			// What this unit's warhead is for. Sensed once here rather than every evaluation:
			// an actor's armament does not change, and the pure logic that reads it must not be
			// handed an engine type to look it up from.
			role = WeaponMatchLogic.RoleOf(self.Info.Name);

			guardTuning = GuardPostTuning.Default;
			counterBatteryTuning = CounterBatteryTuning.Default;
			recovering = false;
			consolidating = false;

			// A doctrine switch can assign this mode while a unit is far from home. Anchor the
			// whole screen to the current base instead of turning that forward position into a
			// permanent guard post.
			ctx.Anchor = ctx.BaseCenter;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			// Where the base is actually being destroyed, before anything is measured against
			// the anchor: DistanceFromAnchorUnits is read below and decides both the tether and
			// what this unit is allowed to engage. See BaseGuardLogic.
			var guarding = Reanchor(self, ctx, out var post, out var guardingEarner);

			// A moved anchor is not on its own enough to move anybody. The tether is twice this
			// unit's reach — eight cells for a four-cell rifle — so a harvester being killed
			// six or seven cells from the base centre sits comfortably inside it, rule 3 never
			// fires, and the screen keeps returning "on post, no threats" while the fleet dies
			// in plain sight. Covering an earner therefore means standing within a shot of it:
			// at any greater distance the defender cannot hit the thing that is shooting it, so
			// there is nothing for a slacker tether to buy. The leash is left alone, so what the
			// unit may chase once it arrives is unchanged.
			var active = guardingEarner && ctx.WeaponRangeUnits > 0
				? tuning with { TetherRadiusUnits = ctx.WeaponRangeUnits }
				: tuning;

			var state = new DefensiveState(
				HealthPercent: ctx.HealthPercent,
				DistanceFromAnchorUnits: ctx.DistanceFromAnchorUnits,
				WeaponRangeUnits: ctx.WeaponRangeUnits,
				IsIdle: ctx.IsIdle,
				HasWeapon: ctx.HasWeapon,
				CanMove: ctx.CanMove,
				RepairAvailable: ctx.FindRepairBay() != null,
				Threats: ctx.SenseThreats(SenseRadius(ctx)));

			// What they are made of, for the barracks that cannot see any of it. See
			// EnemySightings: whatever raids the base is counted here.
			EnemySightings.Record(self.Owner, state.Threats);

			// --- Decide ------------------------------------------------------------
			var decision = DefensiveLogic.Decide(
				state,
				active,
				role,
				consolidating,
				out var keepConsolidating);

			// Hysteresis, so a unit that limps to the repair bay stays long enough to actually be
			// repaired instead of oscillating in and out of combat at the retreat threshold.
			if (decision.Action == UnitAction.Retreat)
			{
				recovering = true;
				consolidating = false;
			}
			else if (recovering && state.HealthPercent < active.ResumeAboveHealthPercent)
			{
				decision = UnitDecision.Retreat("still recovering");
				consolidating = false;
			}
			else
			{
				recovering = false;
				consolidating = keepConsolidating;
				if (consolidating && decision.Action == UnitAction.ReturnToAnchor)
					decision = UnitDecision.AttackMoveTo(
						ctx.Anchor.X,
						ctx.Anchor.Y,
						$"{decision.Reason}, fighting regroup to defensive anchor");
			}

			// Say so when the anchor being walked to is a building the enemy is currently
			// destroying, or an earner they are currently killing, rather than the middle of the
			// base. The movement is the same order either way; what changes is that the decision
			// trace can tell a screen that went to the fight from one that merely drifted back
			// on post.
			if (guarding
				&& (decision.Action == UnitAction.ReturnToAnchor || decision.Action == UnitAction.AttackMoveTo))
				decision = decision with
				{
					Reason = guardingEarner
						? $"{decision.Reason}: covering {post.ActorType} at {post.HealthPercent}%, which is what they are shooting"
						: $"{decision.Reason}: holding {post.ActorType} at {post.HealthPercent}%, which is what they are shooting",
					ReasonId = guardingEarner
						? "defence.guard-earner-under-fire"
						: "defence.guard-damaged-building"
				};

			// Answer the gun that is taking the base apart from beyond everybody's reach.
			//
			// Last, so it can never displace a shot: a unit that came out of the rules above
			// with an Attack already has something it can hurt, and one that came out with a
			// Retreat is going to be repaired. What is left is the case this bot has never had
			// an answer to — a defender standing on a building that is being destroyed, with
			// nothing inside its own four cells, holding position. That was 7,368 decisions on
			// 16:9 while msam killed 28 of this side's 82 losses and took no damage at all. The
			// arithmetic and every bound are in CounterBatteryLogic: the leash is twelve cells
			// from this unit, the memory twelve seconds, and the whole mechanism is capped for
			// this unit's entire life.
			if (decision.Action != UnitAction.Attack
				&& decision.Action != UnitAction.Retreat
				&& DefensiveLogic.SelectTarget(state, active, role) == null)
			{
				var counterBattery = CounterBattery(ctx);
				if (counterBattery.HasValue)
				{
					counterBatteryEvaluations++;
					return counterBattery.Value;
				}
			}

			return decision;
		}

		/// <summary>
		/// Close on whatever is shelling this side from beyond the reach of what it is hitting,
		/// or null if there is nothing to answer.
		/// </summary>
		/// <remarks>
		/// The arithmetic lives in <see cref="CounterBatteryLogic"/>; this only resolves the
		/// reported actor and measures it. A gun that cannot be given as a target — which is the
		/// ordinary case, because a piece parked eleven cells out sits in fog this side has no
		/// eyes on — is answered by walking at the cell it fired from instead, measured from
		/// there rather than from an actor nobody can see.
		/// </remarks>
		UnitDecision? CounterBattery(ModeContext ctx)
		{
			if (!ShellingReports.TryGet(ctx.Owner, ctx.WorldTick, counterBatteryTuning, out var report))
				return null;

			var shooter = ctx.ResolveActor(report.AttackerId);
			var canAttack = shooter != null
				&& !shooter.IsDead
				&& shooter.IsInWorld
				&& ModeContext.HasPosition(shooter)
				&& ctx.CanAttack(shooter);

			return CounterBatteryLogic.Decide(
				new CounterBatteryState(
					HasShellingTarget: true,
					ShellingActorId: report.AttackerId,
					TargetX: canAttack ? shooter.Location.X : report.X,
					TargetY: canAttack ? shooter.Location.Y : report.Y,
					CanAttackTarget: canAttack,
					DistanceUnits: canAttack
						? ctx.DistanceTo(shooter)
						: ctx.DistanceTo(new CPos(report.X, report.Y)),
					WeaponRangeUnits: ctx.WeaponRangeUnits,
					TicksSinceHit: ctx.WorldTick - report.Tick,
					SpentEvaluations: counterBatteryEvaluations,
					CanMove: ctx.CanMove,
					HasWeapon: ctx.HasWeapon),
				counterBatteryTuning,
				"defence.counter-battery");
		}

		/// <summary>
		/// Points this unit's anchor at the part of the base currently being destroyed, or at the
		/// earner currently being killed, and says whether it moved it there.
		/// </summary>
		/// <remarks>
		/// The screen's whole job is to be where the enemy is, and on 16:9 it was not: 84% of
		/// this mode's rifle evaluations were "on post, no threats" while 27 buildings were
		/// destroyed in clusters 8 to 17 cells from the yard, outside every defender's leash.
		/// Own buildings are known exactly, so a falling health bar is a legitimate and precise
		/// report of where to stand — see <see cref="BaseDamageWatch"/>.
		/// <para>
		/// <b>Buildings were only half of it.</b> The next 16:9 attributed twelve decisions to
		/// this branch in 1,145 seconds, and the mode returned <c>Hold("on post, no threats")</c>
		/// 20,180 times against <b>63</b> <c>ReturnToAnchor</c> in the whole match — a screen
		/// anchored on the base centre and tethered at twice its own reach never has to walk
		/// anywhere, so it is purely reactive. Meanwhile the thing being destroyed was the
		/// harvester fleet, and a harvester is not a building. So a shot earner is a post too,
		/// ranked strictly below a hit building and bounded in both time and distance: see
		/// <see cref="BaseGuardLogic.WorthGuarding"/> for the numbers and
		/// <see cref="EarnerUnderFire"/> for the report it reads.
		/// </para>
		/// <para>
		/// Immobile defences are left alone: a tower cannot walk to a fight, and rewriting its
		/// anchor would only change what it believes it is allowed to shoot. So are unarmed
		/// units, for the reason <see cref="DefensiveLogic"/> rule 0 already writes down — a
		/// harvester swept into this mode must not be given a post at all.
		/// </para>
		/// </remarks>
		bool Reanchor(Actor self, ModeContext ctx, out GuardPost post, out bool earner)
		{
			if (BaseDamageWatch.NeedsObservation(self.Owner, ctx.WorldTick))
				BaseDamageWatch.Observe(self.Owner, ctx.OwnedBuildingStates(), ctx.WorldTick, guardTuning);

			post = GuardPost.None;
			earner = false;
			if (!ctx.CanMove || !ctx.HasWeapon)
				return false;

			var home = ctx.BaseCenter;

			if (BaseDamageWatch.TryGetPost(self.Owner, ctx.WorldTick, guardTuning, out post))
			{
				ctx.Anchor = new CPos(post.X, post.Y);
				return true;
			}

			// Nothing of ours is being shelled, so the screen is free. An earner losing health is
			// the only other report this side gets for free, and it is the one that was being
			// ignored while the economy was hunted down inside the base's own ground.
			if (EarnerUnderFire.TryGetPost(
				self.Owner, ctx.WorldTick, home.X, home.Y, guardTuning, out post))
			{
				earner = true;
				ctx.Anchor = new CPos(post.X, post.Y);
				return true;
			}

			post = GuardPost.None;
			ctx.Anchor = home;
			return false;
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

			// Before the CanAttack gate below, and deliberately so. This mode is assigned ToAll,
			// so it is also what a refinery, a power plant and a construction yard run — and
			// those are what artillery actually shoots. None of them can attack anything, so a
			// report written after that gate would be written by nobody who is being shelled.
			Report(self, ctx, attacker);

			if (!ctx.CanAttack(attacker))
				return;

			// Bring forward the next evaluation by clearing our record of what we last did.
			ctx.RequestReevaluation();
		}

		/// <summary>
		/// Tell the side about a gun that hit this actor from further out than this actor could
		/// answer.
		/// </summary>
		/// <remarks>
		/// <see cref="ModeContext.HasPosition"/> first, because a superweapon credits its damage
		/// to the firing player's actor: alive, in the world, enemy-owned and standing on no
		/// cell. This side lost a refinery to exactly that on 16:9, and reading a location off
		/// such an actor inside a damage notification throws, which takes the whole match down
		/// rather than losing one reaction.
		/// <para>
		/// Aircraft are dropped for the reason <see cref="DefensiveLogic"/> already refuses
		/// them: every aircraft in the ruleset outruns every ground unit this side fields, so a
		/// step taken toward one ends with the aircraft somewhere else.
		/// </para>
		/// </remarks>
		static void Report(Actor self, ModeContext ctx, Actor attacker)
		{
			if (!ModeContext.HasPosition(attacker))
				return;

			if (ModeContext.Classify(attacker) == ThreatKind.Aircraft)
				return;

			var standoff = ctx.DistanceTo(attacker);
			if (!CounterBatteryLogic.ShouldReport(standoff, ctx.WeaponRangeUnits, CounterBatteryTuning.Default))
				return;

			ShellingReports.Record(
				self.Owner,
				attacker.ActorID,
				attacker.Location.X,
				attacker.Location.Y,
				standoff,
				ctx.WorldTick,
				CounterBatteryTuning.Default);
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
