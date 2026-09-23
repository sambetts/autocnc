// ============================================================================
//  DefenceDoctrine — something is taking the base apart.
//
//  Static defence goes up, production switches to cheap bodies, and everything
//  that was off doing something else comes home. DefensiveMode falls back to
//  the runtime base centre, so a doctrine switch cannot strand defenders forward.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Modes;
using AutoCnC.Sdk;

namespace AutoCnC.Reference.Doctrines
{
	public sealed class DefenceDoctrine : ReferenceDoctrineBase
	{
		public override string Name => ReferenceDoctrines.Defence;

		public override string Description => "Turtle: static defence, cheap bodies, everything holds the base.";

		protected override IEnumerable<BuildStep> BuildSteps => ReferencePlans.DefenceBuild;

		protected override IEnumerable<ProductionStep> TrainSteps => ReferencePlans.DefenceTrain;

		protected override void Behaviour(IDoctrineBuilder b)
		{
			b.Assign<DefensiveMode>().ToAll();

			// Scouts and faction-equivalent anti-infantry specialists stay with the economy,
			// rockets answer vehicles, and siege units can return fire beyond a harvester's
			// sight. Claims bound the response to each reported attacker. One rifleman keeps
			// watching the approach instead: a turtle that cannot see what is coming builds the
			// wrong answer to it. See IntelWatch.
			b.Assign<HarvesterEscortMode>().ToUnitType("jeep", "bggy", "e2", "e4", "e3", "arty", "msam");
			b.Assign<DefendOrWatchMode>().ToUnitType("e1");
		}
	}
}
