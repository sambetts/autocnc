// ============================================================================
//  HarvesterEscortMode — TEMPLATE
//
//  Sticks close to the nearest harvester and shoots whatever threatens it.
//  Try it with:  /mode group 2 HarvesterEscortMode
//
//  Shows the sense -> decide -> act split. The judgement below is simple enough
//  to inline, but for anything meatier put it in AutoCnC.Core as a pure
//  function so you can unit-test it without launching the game.
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA. See LICENSE
//  and NOTICE.md. Modes you write and distribute inherit the same terms.
// ============================================================================

using System.Linq;
using AutoCnC.Sdk;
using AutoCnC.Core;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	public sealed class HarvesterEscortMode : UnitMode
	{
		const string WardType = "harv";
		const int GuardRadius = 5 * 1024;
		const int SearchRadius = 30 * 1024;

		uint wardId;

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// --- Sense: who are we protecting? ---------------------------------
			var ward = ctx.ResolveActor(wardId);
			if (ward == null)
			{
				ward = ctx.SenseAllies(new WDist(SearchRadius), WardType)
					.OrderBy(a => ctx.DistanceTo(a))
					.FirstOrDefault();

				wardId = ward?.ActorID ?? 0;
			}

			if (ward == null)
				return UnitDecision.Hold("no harvester to escort");

			// --- Decide: shoot anything threatening the ward, else stay close ---
			var threat = ctx.SenseThreats(new WDist(GuardRadius))
				.Where(t => t.IsAttackable)
				.OrderByDescending(t => t.CanHitUs)
				.ThenBy(t => t.DistanceUnits)
				.FirstOrDefault();

			if (threat.ActorId != 0)
				return UnitDecision.Attack(threat.ActorId, "defending harvester");

			if (ctx.DistanceTo(ward) > GuardRadius)
				return UnitDecision.MoveTo(ward.Location.X, ward.Location.Y, "closing on harvester");

			return UnitDecision.Continue;
		}
	}
}
