// ============================================================================
//  ReferencePlans — what this doctrine builds and trains.
//
//  Deliberately a plain static class with no interfaces and no engine types, so
//  the plans can be read (and unit-tested) without loading anything from OpenRA.
//  ReferenceDoctrine.Configure is implemented in terms of these lists, so the
//  tests and the shipped strategy cannot drift apart.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference
{
	public static class ReferencePlans
	{
		/// <summary>
		/// Ordered base construction. Candidates are alternatives for one role, so "powr" or
		/// "nuke" both mean "a power plant" and this works as either faction.
		/// </summary>
		public static IReadOnlyList<BuildStep> Build { get; } =
		[
			new(["powr", "nuke"], 1),          // power
			new(["proc"], 1),                  // income before anything else
			new(["powr", "nuke"], 2),
			new(["pyle", "hand"], 1),          // barracks
			new(["proc"], 2),
			new(["weap", "afld"], 1),          // vehicle production
			new(["powr", "nuke"], 3),
			new(["gtwr", "gun"], 2),           // a little static defence
			new(["proc"], 3),
			new(["powr", "nuke"], 5),
			new(["hq", "eye", "tmpl"], 1),     // tech
			new(["weap", "afld"], 2),
		];

		/// <summary>
		/// Ordered unit production. The last entries never complete, so once the army is up to
		/// strength the factories keep replacing losses instead of going idle.
		/// </summary>
		public static IReadOnlyList<ProductionStep> Train { get; } =
		[
			new("Infantry", ["e1"], 6),
			new("Vehicle", ["jeep", "bggy"], 2),      // early scouting
			new("Infantry", ["e2"], 4),
			new("Vehicle", ["mtnk", "ltnk"], 6),
			new("Infantry", ["e1", "e2"], int.MaxValue),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
		];

		/// <summary>Actor names treated as power plants, for the low-power override.</summary>
		public static IReadOnlyList<string> PowerPlants { get; } = ["powr", "nuke"];
	}
}
