// ============================================================================
//  WatchMode — the push's eyes: the light scout vehicles watch, not fight.
//
//  Sensing and acting only. Where to stand and when to look in is WatchLogic's
//  judgement; where to search when nothing is known is ScoutSearchLogic's.
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA. See LICENSE
//  and NOTICE.md.
// ============================================================================

using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// What a <c>jeep</c> or <c>bggy</c> does while the Attack doctrine runs: finish the home
	/// tiberium survey, then keep eyes on their base. See <see cref="WatchLogic"/>.
	/// </summary>
	/// <remarks>
	/// Unlike <see cref="ScoutMode"/> this never asks for a doctrine. The push is already the
	/// answer to "we know where they live", and a request from here would outrank nothing useful
	/// and could only interrupt it.
	/// <para>
	/// It records a structure for the side only when the side remembers none. Every assault unit
	/// that can see its objective records it on every evaluation, and the staging point is
	/// derived from that one remembered cell, so a second recorder watching a different part of
	/// their base would make the whole army's gathering point flicker between the two. Filling
	/// an empty memory is different: that is the army with nowhere to march, and the watcher's
	/// sighting is the target it lacks.
	/// </para>
	/// </remarks>
	public sealed class WatchMode : UnitMode
	{
		/// <summary>The same radius <see cref="ScoutMode"/> treats as "found their base".</summary>
		const int SightRadius = 10 * 1024;

		/// <summary>How close something that can shoot us has to be before we go around it.</summary>
		const int ThreatRadius = 6 * 1024;

		readonly WatchTuning tuning = WatchTuning.Default;
		readonly ScoutTuning searchTuning = ScoutTuning.Default;

		WatchMemory memory = WatchMemory.Start;
		ScoutWatchdog search = ScoutWatchdog.Start;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			memory = WatchMemory.Start;
			search = ScoutWatchdog.Start;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense -------------------------------------------------------------
			// Before the survey step, as in ScoutMode, so a surveyor that passes one of their
			// structures still reports it. Only the scalars survive past TryStep, which senses
			// again and may reuse the buffer.
			var structures = ctx.SenseStructures(new WDist(SightRadius));
			var structureInSight = structures.Count > 0;
			var found = false;
			var foundX = 0;
			var foundY = 0;
			if (structureInSight && !EnemyBaseSightings.TryGetLastKnown(self.Owner, out _))
			{
				foundX = structures[0].CellX;
				foundY = structures[0].CellY;
				EnemyBaseSightings.Record(self.Owner, new CPos(foundX, foundY), ctx.WorldTick);
				found = true;
			}

			// The home survey first. The push used to take the surveyor with it: on 16:9 it
			// stopped at point 3 of 17 at 225s and never resumed, and from 445s seven to nine
			// harvesters shared two worked-out patches. See TiberiumSurvey.
			if (TiberiumSurvey.TryStep(self, ctx, out var surveying))
				return found ? Found(surveying, foundX, foundY) : surveying;

			// What they field, for the barracks that cannot see it. Sensed out to the same radius
			// as their structures rather than only as far as the evasion radius, because watching
			// their production is this unit's whole job. Shroud-filtered like everything else.
			var threats = ctx.SenseThreats(new WDist(SightRadius));
			EnemySightings.Record(self.Owner, threats);

			var threatened = false;
			var threatStatic = false;
			var threatX = 0;
			var threatY = 0;
			var nearest = int.MaxValue;
			for (var i = 0; i < threats.Count; i++)
			{
				var t = threats[i];
				if (!t.CanHitUs || t.DistanceUnits > ThreatRadius || t.DistanceUnits >= nearest)
					continue;

				threatened = true;
				threatStatic = t.Kind == ThreatKind.Defence || t.Kind == ThreatKind.Structure;
				nearest = t.DistanceUnits;
				threatX = t.CellX;
				threatY = t.CellY;
			}

			var bounds = ctx.World.Map.Bounds;
			var home = ctx.BaseCenter;
			var here = self.Location;
			var field = new ScoutField(
				MinX: bounds.Left,
				MinY: bounds.Top,
				MaxX: bounds.Left + bounds.Width - 1,
				MaxY: bounds.Top + bounds.Height - 1,
				BaseX: home.X,
				BaseY: home.Y);

			// --- Decide ------------------------------------------------------------
			UnitDecision decision;
			if (EnemyBaseSightings.TryGetLastKnown(self.Owner, out var theirs))
			{
				var outcome = WatchLogic.Decide(
					new WatchState(
						CanMove: ctx.CanMove,
						X: here.X,
						Y: here.Y,
						Tick: ctx.WorldTick,
						SightingX: theirs.X,
						SightingY: theirs.Y,
						ThreatNearby: threatened,
						ThreatStatic: threatStatic,
						ThreatX: threatX,
						ThreatY: threatY,
						Field: field),
					memory,
					tuning);

				memory = outcome.Memory;
				decision = outcome.Decision;
			}
			else
			{
				// Nothing known: the push is probing, or has razed what it knew. Search the same
				// ladder ScoutMode does, without its doctrine request.
				var outcome = ScoutSearchLogic.Decide(
					new ScoutState(
						CanMove: ctx.CanMove,
						X: here.X,
						Y: here.Y,
						StructureInSight: structureInSight,
						ThreatNearby: threatened,
						ThreatX: threatX,
						ThreatY: threatY,
						Field: field),
					search,
					searchTuning);

				search = outcome.Watchdog;
				decision = outcome.Decision;
				if (string.IsNullOrEmpty(decision.ReasonId))
					decision = decision with { ReasonId = WatchLogic.SearchReasonId };
			}

			return found ? Found(decision, foundX, foundY) : decision;
		}

		static UnitDecision Found(in UnitDecision decision, int x, int y) =>
			decision with
			{
				Reason = $"recorded their structure at {x},{y} for a side that knew none; {decision.Reason}",
				ReasonId = WatchLogic.FoundReasonId
			};

		public override void OnDamaged(Actor self, ModeContext ctx, AttackInfo e)
		{
			// Being shot is the one thing a watcher has to answer before its next evaluation.
			ctx.RequestReevaluation();
		}
	}
}
