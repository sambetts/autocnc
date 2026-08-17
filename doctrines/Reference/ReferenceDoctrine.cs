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
			// --- Base construction ------------------------------------------------
			// Ordered. Candidates are alternatives for one role, so "powr" or "nuke"
			// both mean "a power plant" and this plan works as either faction.
			b.Build("powr", "nuke").Until(1);
			b.Build("proc").Until(1);              // income before anything else
			b.Build("powr", "nuke").Until(2);
			b.Build("pyle", "hand").Until(1);      // barracks
			b.Build("proc").Until(2);
			b.Build("weap", "afld").Until(1);      // vehicle production
			b.Build("powr", "nuke").Until(3);
			b.Build("gtwr", "gun").Until(2);       // a little static defence
			b.Build("proc").Until(3);
			b.Build("powr", "nuke").Until(5);
			b.Build("hq", "eye", "tmpl").Until(1); // tech
			b.Build("weap", "afld").Until(2);

			// --- Unit production --------------------------------------------------
			// Same idea. The last step never completes, so once the army is up to
			// strength the factory keeps replacing losses instead of going idle.
			b.Train("Infantry", "e1").Until(6);
			b.Train("Vehicle", "jeep", "bggy").Until(2);   // early scouting
			b.Train("Infantry", "e2").Until(4);
			b.Train("Vehicle", "mtnk", "ltnk").Until(6);
			b.Train("Infantry", "e1", "e2").Forever();
			b.Train("Vehicle", "mtnk", "ltnk").Forever();

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
