// ============================================================================
//  ReferenceDoctrine — the module AutoC&C ships as an opponent and example.
//
//  A doctrine is the unit of authorship in AutoC&C. It declares EVERYTHING
//  about how an army fights:
//
//    * what to build      (the base construction plan)
//    * what to train      (the unit production plan)
//    * how units behave   (the modes)
//    * who runs what      (the assignments)
//
//  The platform ships no strategy at all. Load a module and it plays; load none
//  and nothing deploys, builds or shoots.
//
//  TO WRITE YOUR OWN: copy this folder, rename the class and Name, change the
//  plans, and rebuild. Beating this module is the goal.
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA.
//  See LICENSE and NOTICE.md.
// ============================================================================

using AutoCnC.Reference.Modes;
using AutoCnC.Core;
using AutoCnC.Sdk;

namespace AutoCnC.Reference
{
	public sealed class ReferenceDoctrine : IDoctrine
	{
		public string Name => "Reference";

		public string Description => "Balanced economy opening, defends its base, pushes with group 1.";

		public void Configure(IDoctrineBuilder b)
		{
			// Plans live in ReferencePlans as plain data, so they can be unit-tested without
			// loading any engine types. Change them there, not here.
			foreach (var step in ReferencePlans.Build)
				b.Build(step.Candidates).Until(step.DesiredCount);

			foreach (var step in ReferencePlans.Train)
				b.Train(step.Queue, step.Candidates).Until(step.DesiredCount);

			// --- Behaviour --------------------------------------------------------
			// Most specific assignment wins: group > unit type > all.
			b.Assign<DefensiveMode>().ToAll();
			b.Assign<BuildBaseMode>().ToUnitType("mcv", "fact");
			b.Assign<TrainUnitsMode>().ToUnitType("pyle", "hand", "weap", "afld");
			b.Assign<RunHomeMode>().ToUnitType("harv");

			// Put units in control group 1 in game and they switch to attacking.
			b.Assign<AttackBaseMode>().ToGroup(1);
			b.Assign<HarvesterEscortMode>().ToGroup(2);

			// Available for the player to assign manually with /mode, but not
			// assigned to anything by default.
			b.Register<ScoutMode>();
		}
	}
}
