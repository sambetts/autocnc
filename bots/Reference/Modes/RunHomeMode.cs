// ============================================================================
//  RunHomeMode — TEMPLATE
//
//  Flees to safety when enemies appear, and otherwise stays out of the way.
//  Written for harvesters:  /mode type harv RunHomeMode
//
//  Copy this file, rename the class, and it appears in-game after a rebuild.
//  Check your work fast with:  dotnet test src/AutoCnC.Core.Tests
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
	public sealed class RunHomeMode : UnitMode
	{
		// Tweak these. Distances are in world units: 1024 units == 1 cell.
		const int PanicRadius = 7 * 1024;
		const int SafeDistance = 4 * 1024;

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			// Anything hostile nearby that could actually hurt us?
			var threats = ctx.SenseThreats(new WDist(PanicRadius));
			var danger = threats.Any(t => t.CanHitUs);

			if (!danger)
			{
				// All clear. Returning Continue means "leave the unit alone", so a harvester
				// gets on with harvesting instead of us fighting its normal behaviour.
				return UnitDecision.Continue;
			}

			// Run for the nearest refinery, falling back to wherever we started.
			var refinery = ctx.FindRefinery();
			var home = refinery?.Location ?? ctx.Anchor;

			// Already home and still being shot at? Nothing more to do than sit tight.
			if (ctx.DistanceTo(home) < SafeDistance)
				return UnitDecision.Continue;

			return UnitDecision.MoveTo(home.X, home.Y, "enemy nearby, running home");
		}
	}
}
