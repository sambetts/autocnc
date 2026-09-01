// ============================================================================
//  OpeningDoctrine — get an economy up and do not die.
//
//  The doctrine every other one here is measured against, and the one the bot
//  falls back to whenever the answer is "carry on building".
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Modes;
using AutoCnC.Sdk;

namespace AutoCnC.Reference.Doctrines
{
	public sealed class OpeningDoctrine : ReferenceDoctrineBase
	{
		public override string Name => ReferenceDoctrines.Opening;

		public override string Description => "Economy first; the army holds the base.";

		protected override IEnumerable<BuildStep> BuildSteps => ReferencePlans.OpeningBuild;

		protected override IEnumerable<ProductionStep> TrainSteps => ReferencePlans.OpeningTrain;

		protected override void Behaviour(IDoctrineBuilder b)
		{
			// Hold ground. An opening army that wanders off is an opening army that is not at
			// home when the first attack lands.
			b.Assign<DefensiveMode>().ToAll();
			b.Register<ScoutMode>();
		}
	}
}
