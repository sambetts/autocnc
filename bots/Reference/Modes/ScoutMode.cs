// ============================================================================
//  ScoutMode — TEMPLATE
//
//  Wanders the map looking for things, and runs from anything that shoots back.
//  Try it with:  /mode ScoutMode   (applies to your current selection)
//
//  Shows two things worth copying:
//    * per-unit state in instance fields (currentTarget)
//    * reacting immediately to damage via OnDamaged
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA. See LICENSE
//  and NOTICE.md. Modes you write and distribute inherit the same terms.
// ============================================================================

using System;
using System.Linq;
using AutoCnC.Sdk;
using AutoCnC.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	public sealed class ScoutMode : UnitMode
	{
		const int FleeRadius = 6 * 1024;

		/// <summary>How far out a structure counts as "found their base".</summary>
		const int SightRadius = 10 * 1024;

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
			// The question this scout was sent to answer. Saying so here rather than leaving it to
			// the bot is what lets a doctrine end itself: this works even for a bot with no rule
			// about scouting at all, because the request carries whenever the bot has no opinion.
			// A bot that does have one — like ReferenceBot — reaches the same conclusion, and its
			// reason is the one that ends up in the battle log.
			var structures = ctx.SenseStructures(new WDist(SightRadius));
			if (structures.Count > 0)
			{
				// Remember where, not just that. The push that this discovery unlocks starts at
				// home, where nothing enemy is visible, so a bare "found it" leaves the army with
				// nowhere to march.
				var found = ctx.ResolveActor(structures[0].ActorId);
				if (found != null)
					EnemyBaseSightings.Record(self.Owner, found.Location);

				ctx.SwitchDoctrine(ReferenceDoctrines.Opening, "scout found their base");
			}

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
