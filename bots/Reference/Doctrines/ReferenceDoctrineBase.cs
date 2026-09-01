// ============================================================================
//  ReferenceDoctrineBase — the parts of a doctrine that never vary.
//
//  A doctrine declares EVERYTHING about one way of fighting:
//
//    * what to build      (the base construction plan)
//    * what to train      (the unit production plan)
//    * how units behave   (which modes)
//    * who runs what      (the assignments)
//
//  It does not decide WHEN it is the right one. That is ReferenceBot's job, and
//  keeping the two apart is the whole reason a bot exists: an attack doctrine
//  that also has to worry about whether attacking is wise stops being an attack
//  doctrine and becomes a pile of special cases.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Modes;
using AutoCnC.Sdk;

namespace AutoCnC.Reference.Doctrines
{
	/// <summary>
	/// Common wiring: the parts of a doctrine that are the same whatever it is trying to do.
	/// </summary>
	/// <remarks>
	/// Somebody has to build the base, somebody has to run the factories, and harvesters should
	/// always run from trouble. Repeating that in four files would be four places to forget it —
	/// so a doctrine here is only the interesting difference, which is roughly what a doctrine
	/// ought to be.
	/// </remarks>
	public abstract class ReferenceDoctrineBase : IDoctrine
	{
		public abstract string Name { get; }

		public abstract string Description { get; }

		public void Configure(IDoctrineBuilder b)
		{
			foreach (var step in BuildSteps)
				b.Build(step.Candidates).Until(step.DesiredCount);

			foreach (var step in TrainSteps)
				b.Train(step.Queue, step.Candidates).Until(step.DesiredCount);

			// The infrastructure. Roles rather than strategy: the base has to go up and the
			// factories have to run whichever doctrine is deciding what they produce.
			b.Assign<BuildBaseMode>().ToUnitType("mcv", "fact");
			b.Assign<TrainUnitsMode>().ToUnitType("pyle", "hand", "weap", "afld");
			b.Assign<RunHomeMode>().ToUnitType("harv");

			// Control groups stay honoured, so a human watching can take a hand without having to
			// fight the bot for the whole army.
			b.Assign<AttackBaseMode>().ToGroup(1);
			b.Assign<HarvesterEscortMode>().ToGroup(2);

			Behaviour(b);
		}

		protected abstract IEnumerable<BuildStep> BuildSteps { get; }

		protected abstract IEnumerable<ProductionStep> TrainSteps { get; }

		/// <summary>What this doctrine does differently: which mode the army runs.</summary>
		protected abstract void Behaviour(IDoctrineBuilder b);
	}
}
