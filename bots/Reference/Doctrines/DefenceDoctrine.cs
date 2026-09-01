// ============================================================================
//  DefenceDoctrine — something is taking the base apart.
//
//  Static defence goes up, production switches to cheap bodies, and everything
//  that was off doing something else comes home. DefensiveMode falls back to
//  each unit's anchor, which is where it was built, which is the base.
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

			// The scouts stop scouting and come back to guard the harvesters, which is where a
			// fast unarmoured thing is worth something during a siege.
			b.Assign<HarvesterEscortMode>().ToUnitType("jeep", "bggy");
		}
	}
}
