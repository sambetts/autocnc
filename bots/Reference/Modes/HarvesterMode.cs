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
	/// Keeps one harvester earning: runs from a fight that is actually killing it, and keeps it on
	/// ground that still has tiberium in it.
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

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			watchdog = HarvesterWatchdog.Start;
			fields.Clear();
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			var danger = false;
			var threats = ctx.SenseThreats(new WDist(tuning.PanicRadiusUnits));
			for (var i = 0; i < threats.Count; i++)
			{
				if (threats[i].CanHitUs)
				{
					danger = true;
					break;
				}
			}

			var refinery = ctx.FindRefinery();
			var home = refinery?.Location ?? ctx.BaseCenter;
			var bounds = ctx.World.Map.Bounds;
			var here = self.Location;

			var state = new HarvesterState(
				HealthPercent: ctx.HealthPercent,
				CanMove: ctx.CanMove,
				IsIdle: ctx.IsIdle,
				DangerNearby: danger,
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
					var f = found[i];
					fields.Add(new FieldOption(f.NearestX, f.NearestY, f.CenterX, f.CenterY, f.CellCount, f.TotalDensity, f.DistanceUnits));
				}
			}

			// --- Decide ------------------------------------------------------------
			var outcome = HarvesterLogic.Decide(state, watchdog, tuning, fields);
			watchdog = outcome.Watchdog;
			return outcome.Decision;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			var attacker = e.Attacker;
			if (attacker != null
				&& attacker.IsInWorld
				&& !attacker.IsDead
				&& self.Owner.RelationshipWith(attacker.Owner) == PlayerRelationship.Enemy)
				HarvesterThreats.Record(
					self.Owner,
					self.ActorID,
					attacker.ActorID,
					attacker.Location.X,
					attacker.Location.Y,
					ModeContext.Classify(attacker),
					ctx.WorldTick);

			// Being shot is the one thing worth reacting to sooner than the next scheduled
			// evaluation, both for this harvester's flee rule and for its escorts.
			ctx.RequestReevaluation();
		}
	}
}
