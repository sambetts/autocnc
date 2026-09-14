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
	/// Keeps one harvester earning: runs from a fight that is actually killing it, and restarts it
	/// whenever it has provably stopped.
	/// </summary>
	/// <remarks>
	/// Sensing and acting only — the judgement is in <see cref="HarvesterLogic"/>, which has no
	/// engine dependency. The watchdog it returns is per-unit memory, so it lives in an instance
	/// field: one mode instance per unit, never a static.
	/// </remarks>
	public sealed class HarvesterMode : UnitMode
	{
		readonly HarvesterTuning tuning = HarvesterTuning.Default;
		HarvesterWatchdog watchdog = HarvesterWatchdog.Start;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			watchdog = HarvesterWatchdog.Start;
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

			// Scanning the map is a scan, not a lookup, so only ask once the harvester has
			// actually run out of work. A harvester that is still cutting never gets here.
			var field = watchdog.StillEvaluations >= tuning.StallEvaluations
				? ctx.FindNearestResourceField(tuning.MinFieldCells)
				: null;

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
				MapMaxY: bounds.Top + bounds.Height - 1,
				HasKnownField: field != null,
				FieldX: field?.NearestX ?? 0,
				FieldY: field?.NearestY ?? 0,
				FieldDistanceUnits: field?.DistanceUnits ?? 0);

			// --- Decide ------------------------------------------------------------
			var outcome = HarvesterLogic.Decide(state, watchdog, tuning);
			watchdog = outcome.Watchdog;
			return outcome.Decision;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Being shot is the one thing worth reacting to sooner than the next scheduled
			// evaluation, because the flee rule is the only decision here that is time-critical.
			ctx.RequestReevaluation();
		}
	}
}
