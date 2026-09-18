// ============================================================================
//  ScoutMode — go and find out where the other side lives.
//
//  Sensing and acting only. Where to look, and what to do about something
//  shooting at us on the way, is ScoutSearchLogic's judgement — a pure function
//  of the map's shape and where our own base is.
//
//  Shows two things worth copying:
//    * per-unit state in instance fields (the watchdog)
//    * reacting immediately to damage via OnDamaged
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA. See LICENSE
//  and NOTICE.md. Modes you write and distribute inherit the same terms.
// ============================================================================

using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using AutoCnC.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	public sealed class ScoutMode : UnitMode
	{
		/// <summary>How far out a structure counts as "found their base".</summary>
		const int SightRadius = 10 * 1024;

		/// <summary>How close something that can shoot us has to be before we go around it.</summary>
		const int ThreatRadius = 6 * 1024;

		readonly ScoutTuning tuning = ScoutTuning.Default;

		// One mode instance per unit, so instance fields are safe per-unit memory.
		// (A static field would be shared by every unit — don't do that.)
		ScoutWatchdog watchdog = ScoutWatchdog.Start;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			watchdog = ScoutWatchdog.Start;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			// The question this scout was sent to answer, asked before anything else so a scout
			// that dies this evaluation has still reported. Saying so here rather than leaving it
			// to the bot is what lets a doctrine end itself: the request carries whenever the bot
			// has no opinion, and ReferenceBot reaches the same conclusion, so its reason is the
			// one that ends up in the battle log.
			var structures = ctx.SenseStructures(new WDist(SightRadius));
			var inSight = structures.Count > 0;
			if (inSight)
			{
				// Remember where, not just that. The push that this discovery unlocks starts at
				// home, where nothing enemy is visible, so a bare "found it" leaves the army with
				// nowhere to march.
				var found = ctx.ResolveActor(structures[0].ActorId);
				if (found != null)
					EnemyBaseSightings.Record(self.Owner, found.Location, ctx.WorldTick);

				ctx.SwitchDoctrine(ReferenceDoctrines.Opening, "scout found their base");
			}

			// Where the threat is, not merely that there is one: going around something needs to
			// know which side of us it stands on, and ThreatSnapshot carries a range but no cell.
			var threatened = false;
			var threatX = 0;
			var threatY = 0;
			var threats = ctx.SenseThreats(new WDist(ThreatRadius));

			// A scout's whole job is looking, so what it sees is worth keeping. See
			// EnemySightings: this is usually the first read of their composition anyone gets.
			EnemySightings.Record(self.Owner, threats);

			for (var i = 0; i < threats.Count; i++)
			{
				if (!threats[i].CanHitUs)
					continue;

				var actor = ctx.ResolveActor(threats[i].ActorId);
				if (actor == null)
					continue;

				threatened = true;
				threatX = actor.Location.X;
				threatY = actor.Location.Y;
				break;
			}

			var bounds = ctx.World.Map.Bounds;
			var here = self.Location;
			var home = ctx.BaseCenter;

			var state = new ScoutState(
				CanMove: ctx.CanMove,
				X: here.X,
				Y: here.Y,
				StructureInSight: inSight,
				ThreatNearby: threatened,
				ThreatX: threatX,
				ThreatY: threatY,
				Field: new ScoutField(
					MinX: bounds.Left,
					MinY: bounds.Top,
					MaxX: bounds.Left + bounds.Width - 1,
					MaxY: bounds.Top + bounds.Height - 1,
					BaseX: home.X,
					BaseY: home.Y));

			// --- Decide ------------------------------------------------------------
			var outcome = ScoutSearchLogic.Decide(state, watchdog, tuning);
			watchdog = outcome.Watchdog;
			return outcome.Decision;
		}

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Don't wait for the next scheduled evaluation; being shot is the one thing a scout
			// has to answer sooner than that.
			ctx.RequestReevaluation();
		}
	}
}
