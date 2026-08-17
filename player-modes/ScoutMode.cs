// ============================================================================
//  ScoutMode — TEMPLATE
//
//  Wanders the map looking for things, and runs from anything that shoots back.
//  Try it with:  /mode ScoutMode   (applies to your current selection)
//
//  Shows two things worth copying:
//    * per-unit state in instance fields (currentTarget)
//    * reacting immediately to damage via OnDamaged
// ============================================================================

using System;
using System.Linq;
using AutoCnC.Mod.Modes;
using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Traits;

namespace PlayerModes
{
	public sealed class ScoutMode : UnitMode
	{
		const int FleeRadius = 6 * 1024;

		// One mode instance per unit, so instance fields are safe per-unit memory.
		// (A static field would be shared by every unit — don't do that.)
		CPos currentTarget;
		bool hasTarget;

		// Mode code runs outside the simulation, so ordinary System.Random is fine here.
		readonly Random random = new();

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			hasTarget = false;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// Scouts are fragile: break off from anything that can shoot us.
			var threat = ctx.SenseThreats(new WDist(FleeRadius)).FirstOrDefault(t => t.CanHitUs);
			if (threat.CanHitUs)
			{
				hasTarget = false;
				return UnitDecision.MoveTo(ctx.Anchor.X, ctx.Anchor.Y, "spotted, falling back");
			}

			// Pick a new destination once we arrive, or if we never had one.
			if (!hasTarget || ctx.DistanceTo(currentTarget) < 2 * 1024)
			{
				currentTarget = RandomCell(ctx);
				hasTarget = true;
				return UnitDecision.MoveTo(currentTarget.X, currentTarget.Y, "scouting");
			}

			return UnitDecision.Continue;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Don't wait for the next scheduled evaluation; react now.
			hasTarget = false;
			ctx.RequestReevaluation();
		}

		CPos RandomCell(ModeContext ctx)
		{
			var bounds = ctx.World.Map.Bounds;
			return new CPos(
				bounds.Left + random.Next(bounds.Width),
				bounds.Top + random.Next(bounds.Height));
		}
	}
}
