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
	/// last place this side saw an enemy structure — or, when nobody has ever seen one, on the
	/// cell the map's own symmetry says their base should be. See <see cref="Probe"/>.
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
		MusterTuning musterTuning = MusterTuning.Default;
		CounterBatteryTuning counterBatteryTuning = CounterBatteryTuning.Default;
		AssaultSweepTuning sweepTuning = AssaultSweepTuning.Default;
		MusterWatchdog musterWatchdog = MusterWatchdog.Start;
		WeaponRole role = WeaponRole.Unknown;
		uint objectiveId;

		// Whether this unit has already arrived somewhere and found nothing. Sticky, because the
		// alternative oscillates: a unit that steps one cell off the remembered base to sweep is
		// no longer "arrived", so the very next evaluation would order it back again.
		bool sweeping;

		// What last hit this unit from beyond its own reach, when, and how much of this unit's
		// lifetime counter-battery allowance has been spent. The budget is deliberately not
		// cleared by OnEnter — see CounterBatteryTuning.MaxEvaluations.
		uint shellingActorId;
		int shellingTick;
		int counterBatteryEvaluations;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			tuning = AssaultTuning.Default;
			musterTuning = MusterTuning.Default;
			counterBatteryTuning = CounterBatteryTuning.Default;
			sweepTuning = AssaultSweepTuning.Default;

			// A new push is a new situation, so whatever was shelling the last one is not
			// evidence about this one. The spent budget above survives on purpose.
			shellingActorId = 0;

			// What this unit's warhead is for. See DefensiveMode.OnEnter.
			role = WeaponMatchLogic.RoleOf(self.Info.Name);

			// Each push forms up once, and each unit forms up a bounded number of times in its
			// whole life. Clearing the per-leg clocks here is what makes the first true: a unit
			// that came home, rebuilt a force and set off again is starting a new assault, not
			// the second true, and it is the correction this round is about — the doctrine left
			// Attack 27 times on badland-ridges, so this method ran 27 times, and a full reset
			// each time is what let one staging machine consume 78% of the assault's decisions.
			musterWatchdog = musterWatchdog.ForNewPush();
			objectiveId = 0;

			// A fresh push starts by marching at what the side remembers, not by resuming the
			// hunt the last one ended on. The side's rung is untouched: it is the side's.
			sweeping = false;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			var objective = ctx.ResolveActor(objectiveId);

			// Objective destroyed or never chosen: pick the next one. Deliberately sticky, so
			// the force commits instead of re-evaluating every tick and drifting between
			// buildings.
			//
			// Stickiness is suspended for exactly one case: an objective this unit has not yet
			// put a mark on and is not yet close enough to. Nothing is invested in it, so there
			// is nothing to lose by joining whatever the rest of the push has already damaged —
			// and everything to gain, since on 16:9 this army spread 378,000 damage across an
			// enemy base and destroyed one building. A unit already in range of an untouched
			// objective is about to damage it, so it commits; and once its objective is damaged
			// the scan stops entirely. Those two bounds are what keep the search off the hot
			// path for a unit that is actually fighting.
			var joiningDamaged = false;
			if (objective == null || Uncommitted(ctx, objective))
			{
				var visible = ctx.SenseStructures(ObjectiveSearchRadius);
				var chosen = AttackBaseLogic.SelectObjective(visible, objectiveId) ?? 0;
				if (chosen != 0 && chosen != objectiveId)
				{
					var swappedFrom = objective;
					objectiveId = chosen;
					objective = ctx.ResolveActor(objectiveId);
					joiningDamaged = swappedFrom != null
						&& objective != null
						&& ctx.Snapshot(objective).HealthPercent < 100;
				}
			}

			// Anything we can see is worth remembering for the units that cannot. Recording every
			// evaluation is also what corroborates the sighting against a unit that cannot see it
			// — see EnemyBaseSightings.Forget.
			if (objective != null)
			{
				EnemyBaseSightings.Record(self.Owner, objective.Location, ctx.WorldTick);

				// There is something in front of this unit again, so whatever it was hunting for
				// is answered. The side's rung stays where it got to.
				sweeping = false;
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

			// What they are made of, for the barracks that cannot see any of it. See
			// EnemySightings: the push is where most of the enemy army is ever looked at.
			EnemySightings.Record(self.Owner, state.Threats);

			// --- Decide ------------------------------------------------------------
			// Answer the gun we cannot reach, but only while there is nothing at all inside our
			// own reach to shoot instead. That gate is what keeps this from becoming the chase
			// the mode refuses: a unit with a target in range is already fighting and the
			// scorers below rank it properly, while a unit with nothing in range is advancing,
			// walking to a staging cell, or standing still — and in all three it is taking
			// artillery fire entirely for free. On badland-ridges that was 113 e1 killed by
			// arty for zero damage dealt back. See CounterBatteryLogic.
			//
			// The same answer decides whether this unit may go hunting below: walking away from
			// something already inside our own weapon range is never the better trade.
			var inReach = AttackBaseLogic.SelectLastStandTarget(state, role) != null;
			if (!inReach)
			{
				var counterBattery = CounterBattery(ctx, weaponRange);
				if (counterBattery.HasValue)
				{
					counterBatteryEvaluations++;
					return counterBattery.Value;
				}
			}

			// Form up first. Staging is judged against the side's remembered sighting rather
			// than against whatever this unit can currently see, because it is the one target
			// every unit in the push agrees on — and a rule that gathered each unit around its
			// own nearest visible building would gather nobody.
			var outcome = AssaultStagingLogic.Decide(state, Muster(self, ctx, state), musterWatchdog, musterTuning, role);
			musterWatchdog = outcome.Watchdog;
			if (outcome.Decision.HasValue)
				return outcome.Decision.Value;

			var decision = AttackBaseLogic.Decide(
				state, tuning, objective != null ? ApproachOrders.None : Approach(self, ctx, !inReach), role);

			// Say so when this evaluation abandoned an untouched building to join one the rest
			// of the push has already hurt. The order is the same either way; what changes is
			// that the trace can prove the force converged rather than merely arrived.
			if (joiningDamaged
				&& decision.TargetActorId == objectiveId
				&& (decision.Action == UnitAction.Attack || decision.Action == UnitAction.AdvanceToObjective))
				decision = decision with
				{
					Reason = $"{decision.Reason}: joining the structure this side has already damaged",
					ReasonId = "assault.join-damaged-objective"
				};

			return decision;
		}
		/// <summary>
		/// Whether this unit has nothing invested in the objective it holds, so swapping to a
		/// better one costs it nothing.
		/// </summary>
		/// <remarks>
		/// Two conditions and they are both about sunk cost. A structure nobody has scratched is
		/// worth no more than any other, and a unit still walking towards one has not spent a
		/// shot on it. Either being false makes the objective sticky again, which is what stops
		/// a force in contact from drifting between buildings.
		/// </remarks>
		static bool Uncommitted(ModeContext ctx, Actor objective) =>
			ctx.Snapshot(objective).HealthPercent >= 100
			&& ctx.DistanceTo(objective) > ctx.WeaponRangeUnits;

		/// <summary>
		/// What this unit can see about the muster: how far their base is, which leg it should
		/// gather on, how far that cell is, and how much of the army is already standing on it.
		/// </summary>
		/// <remarks>
		/// Allies are counted around this unit rather than around the staging cell, because
		/// sensing is centred on the sensing unit and that is the only count available. It is
		/// also the more useful one: "am I going in alone" is a question about the company this
		/// unit will actually keep, not about a map coordinate.
		/// <para>
		/// The arithmetic all lives in <see cref="AssaultStagingLogic"/>; this method only turns
		/// its answer into a cell so the engine can measure the distance to it.
		/// </para>
		/// </remarks>
		MusterState Muster(Actor self, ModeContext ctx, in AssaultState assault)
		{
			if (!EnemyBaseSightings.TryGetLastKnown(self.Owner, out var theirBase))
				return default;

			var home = ctx.BaseCenter;
			var homeToTarget = DistanceBetween(home, theirBase);
			var toTarget = ctx.DistanceTo(theirBase);

			var advanceCells = AssaultStagingLogic.StagingAdvanceCells(
				homeToTarget / 1024,
				toTarget / 1024,
				musterTuning.LegUnits / 1024,
				musterTuning.StandoffUnits / 1024);

			var (x, y) = AssaultStagingLogic.StagingCell(home.X, home.Y, theirBase.X, theirBase.Y, advanceCells);
			var musterCell = new CPos(x, y);

			var allies = 0;
			foreach (var ally in ctx.SenseAllies(new WDist(musterTuning.MusterRadiusUnits)))
			{
				if (ally != self)
					allies++;
			}

			return new MusterState(
				HasTarget: true,
				CanMove: ctx.CanMove,
				ObjectiveInRange: assault.HasObjective && assault.DistanceToObjectiveUnits <= assault.WeaponRangeUnits,
				HomeToTargetUnits: homeToTarget,
				StagingAdvanceUnits: advanceCells * 1024,
				MusterX: x,
				MusterY: y,
				DistanceToMusterUnits: ctx.DistanceTo(musterCell),
				AlliesNearbyCount: allies);
		}

		/// <summary>
		/// How far apart two cells are, in world units, without reference to where this unit is.
		/// </summary>
		/// <remarks>
		/// <c>ModeContext.DistanceTo</c> always measures from the asking unit, and the length of
		/// the whole march is a fact about the side rather than about one of its soldiers — a
		/// unit that has walked most of the way must still know it belongs to a 68-cell push, or
		/// it will decide the approach is short enough not to need staging at all. Integer-only
		/// for the same lockstep reason as the rest of the staging arithmetic.
		/// </remarks>
		static int DistanceBetween(CPos a, CPos b)
			=> AssaultStagingLogic.IntSqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) * 1024;

		/// <summary>
		/// Where to march with nothing in sight, and the housekeeping that keeps that honest.
		/// </summary>
		/// <remarks>
		/// Two different situations share this method and only one of them is about a sighting.
		/// With nothing ever seen, the march is a deduction rather than a memory and
		/// <see cref="Probe"/> supplies it. With a sighting to march on, everything below
		/// applies.
		/// <para>
		/// The bot's own assessment still wins where it has an opinion — this carries in the
		/// window where it has none, and <c>ReferenceBotLogic</c> rule 4 reaches the same
		/// conclusion from <c>SecondsSinceContact</c> shortly afterwards either way.
		/// </para>
		/// <para>
		/// <b>Arriving and seeing nothing is a statement about this unit, not about their base.</b>
		/// Sensing is per-unit and shroud-filtered, so one unit inside their base behind a ridge
		/// reads empty while the rest of the push is shooting the refinery beside it — and this
		/// method used to believe the blind one and cancel the assault for everybody. It now asks
		/// <see cref="EnemyBaseSightings.Forget"/>, which only agrees once nobody on this side has
		/// seen an enemy structure for fifteen seconds; the doctrine is only ended when the
		/// sighting was genuinely discarded.
		/// </para>
		/// <para>
		/// What it no longer does is <em>stop</em>. Returning no orders here is what put 62,649
		/// <c>Hold("no objective assigned")</c> evaluations into one match and left 180 actors
		/// standing in the other side's half of the map being shelled; a unit that has arrived and
		/// found nothing now hunts instead. See <see cref="AssaultSweepLogic"/>.
		/// </para>
		/// </remarks>
		ApproachOrders Approach(Actor self, ModeContext ctx, bool nothingInReach)
		{
			// Nothing this method offers is any use to something that cannot walk, and a turret
			// reporting that it is standing on the hunt's current cell would retire a rung the
			// army has not been to. Static defences fall straight through to the last-stand
			// scorer, exactly as they did before there was a hunt.
			if (!ctx.CanMove)
				return ApproachOrders.None;

			// Already hunting. Sticky on purpose: the sweep cell is somewhere else by definition,
			// so a unit that has taken one step towards it is no longer standing on the arrival
			// radius below, and re-asking this question would march it straight back.
			if (sweeping)
				return nothingInReach ? Sweep(self, ctx) : ApproachOrders.None;

			if (!EnemyBaseSightings.TryGetLastKnown(self.Owner, out var cell))
				return Probe(self, ctx, nothingInReach);

			// Arrived, and there is nothing here after all: if nobody else can see their base
			// either, the sighting is stale, so drop it rather than hold the whole push in front
			// of an empty crater.
			var distance = ctx.DistanceTo(cell);
			if (distance <= ArrivedRadius.Length)
			{
				if (EnemyBaseSightings.Forget(self.Owner, ctx.WorldTick, SightingMemoryTuning.Default))
					ctx.SwitchDoctrine(ReferenceDoctrines.Scout, "their base is not there any more, going looking");

				// Something in our own reach outranks anywhere we could walk, so the last-stand
				// scorer below gets this evaluation and the hunt waits for the next one.
				return nothingInReach ? Sweep(self, ctx) : ApproachOrders.None;
			}

			return new ApproachOrders(true, cell.X, cell.Y, distance, null, null);
		}

		/// <summary>
		/// Where to march when nobody has ever seen one of their structures.
		/// </summary>
		/// <remarks>
		/// This branch used to give up and ask for the Scout doctrine, and that is what made this
		/// bot incapable of attacking. The push is gated on <c>EnemyBaseFound</c>, which only a
		/// unit standing in front of an enemy structure ever sets — so the army waited on the
		/// scouts, and on badland-ridges the scouts never came: <b>the Vehicle queue issued two
		/// orders in 1,187 seconds and both were harvesters</b>, so not one scout vehicle was
		/// built all match. <c>enemyBaseFound</c> first read true at <b>900s</b>, and only
		/// because <em>their</em> army had reached <em>our</em> base. Army value cleared the push
		/// threshold between 360s and 570s, so the two halves of the condition <b>never once
		/// overlapped</b>: across 238 assessments the Attack doctrine ran for zero seconds, and
		/// <c>buildingsDestroyed</c> has now scored a structural zero two fights running.
		/// <para>
		/// Waiting for certainty was the mistake. A human does not wait: skirmish maps place
		/// their spawns symmetrically, so the opposite corner is where the other side lives, and
		/// walking at it is both the attack and the search. <see cref="ScoutSearchLogic.Objective"/>
		/// rung 0 is exactly that deduction — our own base reflected through the centre of the
		/// map — and it is arithmetic on <c>Map.Bounds</c> and <c>ctx.BaseCenter</c>, both runtime
		/// facts about this match. Nothing here knows which map is being played, and the guess is
		/// wrong on an asymmetric map, which is what the arrival branch below is for.
		/// </para>
		/// <para>
		/// The army is also the better scout, now that it goes. It marches as
		/// <c>AttackMoveTo</c>, so it fights through what it meets instead of dying to it, it
		/// records every structure it sees for the whole side, and exploration is permanent —
		/// 146 cells were explored last fight against a fitness reference of 300, and a
		/// shroud-filtered resource layer means those unexplored cells were unusable income too.
		/// </para>
		/// <para>
		/// Standing on the guess and seeing nothing is the one honest way to learn the deduction
		/// was wrong, and it hands over to the search ladder that has more rungs — the same
		/// escape the stale-sighting branch above takes, for the same reason. The army does not
		/// wait for the jeep to do it, though: it keeps walking the rest of that ladder itself.
		/// </para>
		/// </remarks>
		ApproachOrders Probe(Actor self, ModeContext ctx, bool nothingInReach)
		{
			var field = Field(ctx);
			var (x, y) = ScoutSearchLogic.Objective(0, field, ScoutTuning.Default);
			var distance = ctx.DistanceTo(new CPos(x, y));

			if (distance <= ArrivedRadius.Length)
			{
				ctx.SwitchDoctrine(ReferenceDoctrines.Scout, "probed where their base should be, going looking");
				return nothingInReach ? Sweep(self, ctx) : ApproachOrders.None;
			}

			return new ApproachOrders(true, x, y, distance, "probing their likely base, mirrored from ours",
				"assault.probe-mirrored-base");
		}

		/// <summary>
		/// Where to walk when this unit has arrived somewhere and found nothing there.
		/// </summary>
		/// <remarks>
		/// The rung is read, the arrival tested against it, and the rung read again, because a
		/// unit standing on the current rung is the evidence that retires it — and the cell it is
		/// then given has to be the one the side has just moved on to rather than the one it has
		/// already answered. Both reads are integer arithmetic on the map's bounds and our own
		/// base cell; see <see cref="AssaultSweepLogic"/> for what the ladder costs and buys.
		/// </remarks>
		ApproachOrders Sweep(Actor self, ModeContext ctx)
		{
			sweeping = true;

			var field = Field(ctx);
			var standing = AssaultSweeps.Rung(self.Owner, ctx.WorldTick, false, sweepTuning);
			var at = ScoutSearchLogic.Objective(standing, field, ScoutTuning.Default);
			var arrived = ctx.DistanceTo(new CPos(at.X, at.Y)) <= ArrivedRadius.Length;

			var rung = AssaultSweeps.Rung(self.Owner, ctx.WorldTick, arrived, sweepTuning);
			if (rung != standing)
				at = ScoutSearchLogic.Objective(rung, field, ScoutTuning.Default);

			return new ApproachOrders(true, at.X, at.Y, ctx.DistanceTo(new CPos(at.X, at.Y)),
				$"nothing left where we stand, sweeping rung {rung} at {at.X},{at.Y}",
				"assault.sweep-for-targets");
		}

		/// <summary>The map and where on it we live, which is all the search ladder needs.</summary>
		static ScoutField Field(ModeContext ctx)
		{
			var bounds = ctx.World.Map.Bounds;
			var home = ctx.BaseCenter;
			return new ScoutField(
				MinX: bounds.Left,
				MinY: bounds.Top,
				MaxX: bounds.Left + bounds.Width - 1,
				MaxY: bounds.Top + bounds.Height - 1,
				BaseX: home.X,
				BaseY: home.Y);
		}

		/// <summary>
		/// Close on whatever last shelled this unit from beyond its reach, or null if there is
		/// nothing to answer.
		/// </summary>
		/// <remarks>
		/// The arithmetic lives in <see cref="CounterBatteryLogic"/>; this only resolves the
		/// remembered actor and measures it. A target that has died, or that this unit may no
		/// longer attack, is dropped here rather than being carried as a stale id.
		/// </remarks>
		UnitDecision? CounterBattery(ModeContext ctx, int weaponRange)
		{
			if (shellingActorId == 0)
				return null;

			var shooter = ctx.ResolveActor(shellingActorId);
			if (shooter == null || !ModeContext.HasPosition(shooter) || !ctx.CanAttack(shooter))
			{
				shellingActorId = 0;
				return null;
			}

			return CounterBatteryLogic.Decide(
				new CounterBatteryState(
					HasShellingTarget: true,
					ShellingActorId: shellingActorId,
					TargetX: shooter.Location.X,
					TargetY: shooter.Location.Y,
					CanAttackTarget: true,
					DistanceUnits: ctx.DistanceTo(shooter),
					WeaponRangeUnits: weaponRange,
					TicksSinceHit: ctx.WorldTick - shellingTick,
					SpentEvaluations: counterBatteryEvaluations,
					CanMove: ctx.CanMove,
					HasWeapon: ctx.HasWeapon),
				counterBatteryTuning,
				"assault.counter-battery");
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Taking fire from something already inside our reach is the expected cost of a base
			// assault, and reacting to it is exactly the distraction this mode exists to avoid.
			// Static defences are handled as blockers by AttackBaseLogic once they come into
			// range, and everything else in range is handled by the last-stand scorer.
			//
			// Being shelled from BEYOND our reach is not the same thing, and this method used to
			// treat it as though it were. Sensing is capped at this unit's own weapon range, so
			// an 11-cell artillery piece is not merely outranked in the candidate list, it never
			// enters it — the damage callback is the only way this side can learn the gun exists.
			// Doing nothing with it meant 139 of 264 units lost on badland-ridges died to arty,
			// which took 789,717 damage from this bot and returned 42,120.
			var attacker = e.Attacker;
			if (attacker == null || attacker.IsDead || !attacker.IsInWorld)
				return;

			if (self.Owner.RelationshipWith(attacker.Owner) != PlayerRelationship.Enemy)
				return;

			if (!ctx.CanAttack(attacker))
				return;

			if (!CounterBatteryLogic.ShouldRecord(ctx.DistanceTo(attacker), ctx.WeaponRangeUnits, counterBatteryTuning))
				return;

			shellingActorId = attacker.ActorID;
			shellingTick = ctx.WorldTick;

			// Bring the next evaluation forward rather than ordering from here, so this does not
			// fight the executor's order suppression. See DefensiveMode.OnDamaged.
			ctx.RequestReevaluation();
		}
	}
}
