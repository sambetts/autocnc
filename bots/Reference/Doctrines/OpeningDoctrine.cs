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

			// ...except the cheap fast things, which keep looking. Scouting used to happen only
			// while the Scout doctrine happened to be running — 320 seconds of the 1,136 on
			// badland-ridges — and in the rest of the match the jeeps stood in the base as
			// defenders. They are not defenders: four of them finished that match with 0 kills,
			// 11,064 damage dealt and 30,539 taken. Finding the other side is the only thing a
			// jeep does that this bot cannot do without, because the Attack doctrine is gated on
			// it, so a jeep should be doing it whenever it is alive.
			b.Assign<ScoutMode>().ToUnitType("jeep", "bggy");

			// Once their base is found, one rifleman becomes the side's watcher: see IntelWatch.
			b.Assign<DefendOrWatchMode>().ToUnitType("e1");
		}
	}
}
