// ============================================================================
//  ReferencePlans — what each of ReferenceBot's doctrines builds and trains.
//
//  Deliberately a plain static class with no interfaces and no engine types, so
//  the plans can be read (and unit-tested) without loading anything from OpenRA.
//  Each doctrine's Configure is implemented in terms of these lists, so the
//  tests and the shipped strategy cannot drift apart.
//
//  Build steps say "until N of these exist" and count what is already standing,
//  so they are cumulative rather than sequential: a doctrine whose plan extends
//  another's picks up where that one left off, and switching back and forth
//  never rebuilds anything.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using AutoCnC.Core;

namespace AutoCnC.Reference
{
	/// <summary>The names ReferenceBot's doctrines answer to, in one place so they cannot drift.</summary>
	public static class ReferenceDoctrines
	{
		public const string Opening = "Opening";
		public const string Scout = "Scout";
		public const string Attack = "Attack";
		public const string Defence = "Defence";
	}

	public static class ReferencePlans
	{
		/// <summary>
		/// The economy every doctrine wants, whichever one is running.
		/// </summary>
		/// <remarks>
		/// Candidates are alternatives for one role, so "powr" or "nuke" both mean "a power
		/// plant" and this works as either faction.
		/// </remarks>
		public static IReadOnlyList<BuildStep> Economy { get; } =
		[
			new(["powr", "nuke"], 1),          // power
			new(["proc"], 1),                  // income before anything else
			new(["powr", "nuke"], 2),
			new(["pyle", "hand"], 1),          // barracks
			new(["proc"], 2),
			new(["weap", "afld"], 1),          // vehicle production
			new(["powr", "nuke"], 3),
			new(["proc"], 3),
		];

		/// <summary>The opening: an economy, and enough of an army not to die to a rush.</summary>
		public static IReadOnlyList<BuildStep> OpeningBuild { get; } =
		[
			.. Economy,
			new(["gtwr", "gun"], 2),           // a little static defence
			new(["powr", "nuke"], 4),
		];

		public static IReadOnlyList<ProductionStep> OpeningTrain { get; } =
		[
			new("Infantry", ["e1"], 6),
			new("Vehicle", ["jeep", "bggy"], 1),
			new("Infantry", ["e2"], 4),
			new("Vehicle", ["mtnk", "ltnk"], 4),
			new("Infantry", ["e1", "e2"], int.MaxValue),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
		];

		/// <summary>
		/// Scouting: the economy carries on, and a couple of cheap fast vehicles go looking.
		/// </summary>
		/// <remarks>
		/// Not a pause in the game plan. A doctrine that stopped building to go and look would
		/// lose to one that did both, so the only thing this changes is that a jeep exists and
		/// has somewhere to be.
		/// </remarks>
		public static IReadOnlyList<BuildStep> ScoutBuild { get; } = OpeningBuild;

		public static IReadOnlyList<ProductionStep> ScoutTrain { get; } =
		[
			new("Vehicle", ["jeep", "bggy"], 2),
			.. OpeningTrain,
		];

		/// <summary>
		/// Turtling: static defence first, then bodies. Cheap infantry rather than tanks, because
		/// what is needed is guns in the base now rather than better guns in a minute.
		/// </summary>
		public static IReadOnlyList<BuildStep> DefenceBuild { get; } =
		[
			.. Economy,
			new(["gtwr", "gun"], 4),
			new(["powr", "nuke"], 4),
			new(["gtwr", "gun"], 6),
		];

		public static IReadOnlyList<ProductionStep> DefenceTrain { get; } =
		[
			new("Infantry", ["e1"], 10),
			new("Infantry", ["e3", "e1"], 6),  // rockets, for whatever is chewing the base
			new("Infantry", ["e1", "e2"], int.MaxValue),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
		];

		/// <summary>Pushing: more production, better units, and the tech to make them worth having.</summary>
		public static IReadOnlyList<BuildStep> AttackBuild { get; } =
		[
			.. Economy,
			new(["hq", "eye", "tmpl"], 1),     // tech
			new(["weap", "afld"], 2),
			new(["pyle", "hand"], 2),
			new(["powr", "nuke"], 5),
			new(["proc"], 4),
		];

		public static IReadOnlyList<ProductionStep> AttackTrain { get; } =
		[
			new("Vehicle", ["mtnk", "ltnk"], 8),
			new("Infantry", ["e1", "e2"], 12),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
			new("Infantry", ["e1", "e2"], int.MaxValue),
		];

		/// <summary>Actor names treated as power plants, for the low-power override.</summary>
		public static IReadOnlyList<string> PowerPlants { get; } = ["powr", "nuke"];

		/// <summary>Every build step any doctrine declares, for tests that check them all.</summary>
		public static IEnumerable<BuildStep> AllBuildSteps =>
			OpeningBuild.Concat(ScoutBuild).Concat(DefenceBuild).Concat(AttackBuild);
	}
}
