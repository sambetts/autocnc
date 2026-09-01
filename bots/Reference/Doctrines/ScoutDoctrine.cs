// ============================================================================
//  ScoutDoctrine — find out where the enemy lives.
//
//  The only doctrine here whose job is a question rather than a fight, and the
//  only one that ends by answering it: ScoutMode calls ctx.SwitchDoctrine the
//  moment it sees an enemy structure, rather than waiting for the bot's next
//  assessment to notice.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Modes;
using AutoCnC.Sdk;

namespace AutoCnC.Reference.Doctrines
{
	public sealed class ScoutDoctrine : ReferenceDoctrineBase
	{
		public override string Name => ReferenceDoctrines.Scout;

		public override string Description => "Economy carries on; fast vehicles go looking for their base.";

		protected override IEnumerable<BuildStep> BuildSteps => ReferencePlans.ScoutBuild;

		protected override IEnumerable<ProductionStep> TrainSteps => ReferencePlans.ScoutTrain;

		protected override void Behaviour(IDoctrineBuilder b)
		{
			b.Assign<DefensiveMode>().ToAll();

			// Only the cheap fast things go. Sending the army to look would answer the question
			// and lose the match doing it.
			b.Assign<ScoutMode>().ToUnitType("jeep", "bggy");
		}
	}
}
