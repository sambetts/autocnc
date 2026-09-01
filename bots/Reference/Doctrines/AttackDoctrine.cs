// ============================================================================
//  AttackDoctrine — spend the army on their base.
//
//  AttackBaseMode is assigned ToAll rather than to a control group, because
//  there is nobody to press the button: a bot cannot put units in a control
//  group, so a push that waited for one would never happen. The group
//  assignment in the base class still works for a human who wants to help.
//
//  Builders, factories and harvesters keep their own modes — a unit-type
//  assignment is more specific than ToAll, so they are not swept into the push.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Modes;
using AutoCnC.Sdk;

namespace AutoCnC.Reference.Doctrines
{
	public sealed class AttackDoctrine : ReferenceDoctrineBase
	{
		public override string Name => ReferenceDoctrines.Attack;

		public override string Description => "Push: tech, more production, and the whole army goes to their base.";

		protected override IEnumerable<BuildStep> BuildSteps => ReferencePlans.AttackBuild;

		protected override IEnumerable<ProductionStep> TrainSteps => ReferencePlans.AttackTrain;

		protected override void Behaviour(IDoctrineBuilder b)
		{
			b.Assign<AttackBaseMode>().ToAll();
			b.Register<DefensiveMode>();
		}
	}
}
