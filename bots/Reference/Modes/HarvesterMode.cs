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

using System.Collections.Generic;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using AutoCnC.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// Keeps one harvester earning: leaves immediate threats before they can pin it down, and keeps
	/// it on ground that still has tiberium in it.
	/// </summary>
	/// <remarks>
	/// Sensing and acting only — the judgement is in <see cref="HarvesterLogic"/>, which has no
	/// engine dependency. The watchdog it returns is per-unit memory, so it lives in an instance
	/// field: one mode instance per unit, never a static.
	/// </remarks>
	public sealed class HarvesterMode : UnitMode
	{
		readonly HarvesterTuning tuning = HarvesterTuning.Default;
		readonly List<FieldOption> fields = [];
		HarvesterWatchdog watchdog = HarvesterWatchdog.Start;
		int publishedContestedTick = int.MinValue;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			watchdog = HarvesterWatchdog.Start;
			publishedContestedTick = int.MinValue;
			fields.Clear();
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			var nearbyThreatCount = 0;
			long threatXTotal = 0;
			long threatYTotal = 0;
			var threats = ctx.SenseThreats(new WDist(tuning.PanicRadiusUnits));
			for (var i = 0; i < threats.Count; i++)
			{
				var threat = threats[i];
				if (!threat.CanHitUs)
					continue;

				var actor = ctx.ResolveActor(threat.ActorId);
				if (actor == null || !actor.IsInWorld || actor.IsDead)
					continue;

				nearbyThreatCount++;
				threatXTotal += actor.Location.X;
				threatYTotal += actor.Location.Y;
			}

			var refinery = ctx.FindRefinery();
			var home = refinery?.Location ?? ctx.BaseCenter;
			var bounds = ctx.World.Map.Bounds;
			var here = self.Location;

			var state = new HarvesterState(
				HealthPercent: ctx.HealthPercent,
				CanMove: ctx.CanMove,
				IsIdle: ctx.IsIdle,
				DangerNearby: nearbyThreatCount > 0,
				NearbyThreatCount: nearbyThreatCount,
				WorldTick: ctx.WorldTick,
				HasThreatCenter: nearbyThreatCount > 0,
				ThreatCenterX: nearbyThreatCount > 0 ? (int)(threatXTotal / nearbyThreatCount) : 0,
				ThreatCenterY: nearbyThreatCount > 0 ? (int)(threatYTotal / nearbyThreatCount) : 0,
				HasRefinery: refinery != null,
				RefineryX: home.X,
				RefineryY: home.Y,
				DistanceToRefineryUnits: refinery != null ? ctx.DistanceTo(refinery) : int.MaxValue,
				X: here.X,
				Y: here.Y,
				BaseX: ctx.BaseCenter.X,
				BaseY: ctx.BaseCenter.Y,
				MapMinX: bounds.Left,
				MapMinY: bounds.Top,
				MapMaxX: bounds.Left + bounds.Width - 1,
				MapMaxY: bounds.Top + bounds.Height - 1);

			// Scanning the map is a scan, not a lookup, so the rule decides when it is worth
			// paying for: on a stall, or once a review window has gone by. The scan is centred on
			// the refinery rather than on the harvester, because what a field costs to work is the
			// round trip to the refinery, not how close the harvester happens to be right now.
			//
			// The state has to be built first. Whether this evaluation is a stall is a fact about
			// where the harvester is standing *now* against where it was last time, so the test
			// cannot be answered from the watchdog alone — and answering it from the watchdog
			// alone is what made the stall case unreachable for an entire match.
			fields.Clear();
			if (HarvesterLogic.ShouldScan(watchdog, state, tuning))
			{
				var found = ctx.FindResourceFields(tuning.MinFieldCells, tuning.MaxFieldsConsidered, home);
				for (var i = 0; i < found.Count; i++)
				{
					// Copied, not retained: sensing methods reuse their buffers.
					//
					// Ranked on what the patch pays rather than how much of it there is: blue
					// tiberium pays about 1.7 times green per unit, so density alone drives past
					// the better field. Every use of this number in HarvesterLogic is a ratio or
					// a "> 0" test, so swapping credits for density changes the ranking and
					// nothing else. A mod that declares no value reports 0; fall back to density.
					var f = found[i];
					var worth = f.TotalValue > 0 ? f.TotalValue : f.TotalDensity;
					fields.Add(new FieldOption(f.NearestX, f.NearestY, f.CenterX, f.CenterY, f.CellCount, worth, f.DistanceUnits));
				}
			}

			// --- Decide ------------------------------------------------------------

			// Adopt the side's report of where it was last driven off, unless this harvester has
			// a more recent one of its own. Seeding before the rule runs is what lets a harvester
			// avoid an ambush it has not personally been shot by — the whole point of
			// ContestedGround, which explains why a per-unit memory of this was worthless.
			var hasReport = ContestedGround.TryGet(
				self.Owner,
				ctx.WorldTick,
				tuning.ContestedMemoryTicks,
				out var fleetX,
				out var fleetY,
				out var fleetTick);

			if (hasReport && fleetTick > watchdog.ContestedTick)
				watchdog = watchdog with
				{
					ContestedX = fleetX,
					ContestedY = fleetY,
					ContestedFromFleet = true,
					ContestedTick = fleetTick
				};
			else if (!hasReport && watchdog.ContestedFromFleet)
				// An inherited exclusion expires with the report that created it. A harvester
				// that was never shot on that ground has no reason of its own to go on avoiding
				// it, and ground that is never offered back is ground given away.
				watchdog = watchdog with
				{
					ContestedX = int.MinValue,
					ContestedY = int.MinValue,
					ContestedFromFleet = false,
					ContestedTick = int.MinValue
				};

			var outcome = HarvesterLogic.Decide(state, watchdog, tuning, fields);
			watchdog = outcome.Watchdog;

			// Publish only first-hand reports. A seeded value already carries the fleet's tick
			// and is flagged as inherited, so it can never be echoed back and refresh itself
			// into a permanent exclusion.
			if (watchdog.HasContested
				&& !watchdog.ContestedFromFleet
				&& watchdog.ContestedTick > publishedContestedTick)
			{
				publishedContestedTick = watchdog.ContestedTick;
				ContestedGround.Record(
					self.Owner, watchdog.ContestedX, watchdog.ContestedY, watchdog.ContestedTick);
			}

			return outcome.Decision;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			EnemySightings.RecordAttacker(self.Owner, e.Attacker);

			var attacker = e.Attacker;
			if (attacker != null
				&& attacker.IsInWorld
				&& !attacker.IsDead
				// A superweapon credits its damage to the firing player's actor: alive, in the
				// world, enemy-owned and occupying no cell. Reading Location off one throws, and
				// an exception raised inside a damage notification takes the whole match down
				// rather than losing one reaction. This side lost a refinery to exactly such a
				// hit on 16:9, so the case is not hypothetical here.
				&& ModeContext.HasPosition(attacker)
				&& self.Owner.RelationshipWith(attacker.Owner) == PlayerRelationship.Enemy)
			{
				HarvesterThreats.Record(
					self.Owner,
					self.ActorID,
					attacker.ActorID,
					attacker.Location.X,
					attacker.Location.Y,
					ModeContext.Classify(attacker),
					ctx.WorldTick);

				// ...and tell the rest of the side, not only this harvester's own escort. The
				// four bodies the escort rung buys are not the screen; the screen is every idle
				// rifleman standing on the base centre with nothing to shoot. See
				// BaseGuardLogic.WorthGuarding.
				EarnerUnderFire.Record(
					self.Owner,
					self.ActorID,
					self.Info.Name,
					self.Location.X,
					self.Location.Y,
					ctx.HealthPercent,
					ctx.WorldTick,
					GuardPostTuning.Default);

				// A harvester has no reach at all, so everything that shoots it outranges it and
				// every hit is a candidate report. That matters more than it sounds: msam killed
				// six of the nine harvesters on 16:9 while the screen it was standing next to
				// held position, because nothing told the screen the gun existed. See
				// ShellingReports, which keeps whichever gun is furthest out.
				if (ModeContext.Classify(attacker) != ThreatKind.Aircraft)
				{
					var standoff = ctx.DistanceTo(attacker);
					if (CounterBatteryLogic.ShouldReport(
						standoff, ctx.WeaponRangeUnits, CounterBatteryTuning.Default))
						ShellingReports.Record(
							self.Owner,
							attacker.ActorID,
							attacker.Location.X,
							attacker.Location.Y,
							standoff,
							ctx.WorldTick,
							CounterBatteryTuning.Default);
				}
			}

			// Being shot is the one thing worth reacting to sooner than the next scheduled
			// evaluation, both for this harvester's flee rule and for its escorts.
			ctx.RequestReevaluation();
		}
	}
}
