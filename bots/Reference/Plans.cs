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
		/// How many riflemen to keep standing before anything is spent on rockets.
		/// </summary>
		/// <remarks>
		/// A floor rather than a ratio, and deliberately small. <c>e1</c> is the cheapest body in
		/// the game and the best thing a barracks builds against other infantry (M16 does 150%
		/// against no armour, where the rocket does 28%), so a core of them is worth having
		/// whatever the enemy turns out to be. Everything above the floor goes on rockets,
		/// because everything above the floor is what has to kill vehicles and aircraft.
		/// </remarks>
		const int RifleCore = 12;

		/// <summary>
		/// How many harvesters to keep working before anything is spent on the next tank.
		/// </summary>
		/// <remarks>
		/// A refinery carries a <c>FreeActor</c> harvester and hands out exactly one, ever. No
		/// plan used to name <c>harv</c> at all, so this bot's entire income was "one harvester
		/// per refinery" — two of them for the first seven minutes of badland-ridges, three
		/// until 870s, four thereafter — and its cash was 0 or 1 at 46 of the 55 assessments
		/// after 150s. Income, not judgement, was the binding constraint on nearly the whole
		/// game.
		/// <para>
		/// The floor is four because a Tiberian Dawn refinery has one docking bay and comfortably
		/// feeds two harvesters, so the two refineries this bot has by 117s want four.
		/// </para>
		/// <para>
		/// Naming <c>harv</c> was necessary and turned out not to be sufficient, because the step
		/// cannot fire until a <c>Vehicle</c> queue exists. On the next badland-ridges the
		/// factory did not stand until 414s, the queue was given four orders in the whole match,
		/// and the one <c>harv</c> it was asked for at 576s was still unpaid at 1,023s. So
		/// <see cref="RefineryCore"/> now delivers this floor from the construction yard instead,
		/// and this step is what re-buys a harvester that dies.
		/// </para>
		/// </remarks>
		const int HarvesterCore = 4;

		/// <summary>
		/// How many refineries the construction yard buys before it buys any tech.
		/// </summary>
		/// <remarks>
		/// Equal to <see cref="HarvesterCore"/>, and that equality is the whole point.
		/// <c>harv</c> needs a <c>weap</c>/<c>afld</c> that costs 2,000 credits; <c>proc</c>
		/// needs only <c>anypower</c>, costs 1,500, carries a free harvester and is buildable
		/// from the <c>Building</c> queue the yard owns at second zero. A refinery is therefore
		/// the only harvester a poor bot can buy, and the harvester floor has to be reachable
		/// from the refinery count alone or it is not reachable at all.
		/// <para>
		/// It was not. On badland-ridges the ladder bought two refineries (51s, 117s) and then
		/// spent 1,000 on <c>hq</c> (167s) and 2,000 on <c>afld</c> (414s) before its third
		/// refinery — which was ninth in the plan, was not ordered until 498s and did not stand
		/// until 860s of a 1,023-second match. The bot therefore had exactly **two harvesters
		/// for the entire game**: the only three <c>harv</c> that ever existed appeared at 51s,
		/// 117s and 860s, each in the same second as a <c>proc</c>, so every one was a refinery's
		/// free actor and the bot produced none. Its cash read 0 from 150s to the end.
		/// </para>
		/// <para>
		/// The arithmetic that fixes it is simply the opening bank. Power, refinery, power,
		/// barracks, refinery costs 4,500 of the 7,500 a side starts with, which leaves exactly
		/// two more refineries — so four of them, and four harvesters, are affordable before a
		/// single credit of income is needed. The 1,000 spent on <c>hq</c> at 167s was the third
		/// refinery.
		/// </para>
		/// </remarks>
		const int RefineryCore = HarvesterCore;

		/// <summary>
		/// Where surplus vehicle capacity goes before it goes on tank number nine.
		/// </summary>
		/// <remarks>
		/// Two per refinery for the <see cref="RefineryCore"/> every plan now builds. It doubles
		/// as the only replacement rule this bot has: <c>Until(n)</c> counts what is standing
		/// now, so a step that names <c>harv</c> above the endless combat step re-fires the
		/// moment a harvester dies. All four died between 1351s and 1365s on badland-ridges and
		/// the bot spent its last 285 seconds with three refineries, fourteen buildings, zero
		/// income and no way to ever build another harvester; it completed one unit in the last
		/// 470 seconds of the match.
		/// <para>
		/// Tied to the refinery count rather than fixed, because a saturation target below
		/// two-per-refinery quietly stops being saturation the moment the base grows. It sits
		/// above the endless combat step, so it is the last thing bought before the plan goes
		/// back to buying things that shoot.
		/// </para>
		/// </remarks>
		const int HarvesterSaturation = RefineryCore * 2;

		/// <summary>
		/// The economy every doctrine wants, whichever one is running.
		/// </summary>
		/// <remarks>
		/// Candidates are alternatives for one role, so "powr" or "nuke" both mean "a power
		/// plant" and this works as either faction.
		/// <para>
		/// The order is income, then tech. That is the correction badland-ridges paid for: the
		/// ladder used to read power, refinery, power, barracks, refinery, <c>hq</c>,
		/// <c>weap</c>/<c>afld</c>, power, refinery — so the third refinery sat behind 3,000
		/// credits of buildings that earn nothing. It was ordered at 498s and stood at 860s of a
		/// 1,023-second match, and the bot ran the whole game on the two free harvesters its
		/// first two refineries handed out. Cash read 0 from 150s to the end, and total income
		/// worked out at roughly 12 credits a second against the winner's 88.
		/// </para>
		/// <para>
		/// Nothing was overtaken that pays for itself. <c>hq</c> unlocks <c>mtnk</c>,
		/// <c>ltnk</c>, <c>e2</c> and <c>atwr</c>, and the bot fielded none of them in that
		/// match; <c>weap</c>/<c>afld</c> unlocks scouting and harvester production, and took
		/// 247 seconds to pay for at two-harvester income. Both arrive **sooner** in wall-clock
		/// behind four refineries than they did in front of two, because the four refineries pay
		/// for them. Only the <c>Building</c> queue reads this list, so moving rungs around here
		/// does not slow infantry production at all — that comes from the production plan and a
		/// separate queue.
		/// </para>
		/// <para>
		/// The power rung between the two new refineries is not decoration. Two <c>nuke</c> ran
		/// a balance of -25 by 420s with two refineries and the shared defences up, and a
		/// brownout throttles every queue at once; <see cref="Modes.BuildBaseMode"/>'s low-power
		/// override is a rescue, not a plan.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> Economy { get; } =
		[
			new(["powr", "nuke"], 1),          // power
			new(["proc"], 1),                  // income before anything else
			new(["powr", "nuke"], 2),
			new(["pyle", "hand"], 1),          // barracks
			new(["proc"], 2),
			new(["proc"], 3),                  // ...and income again, while the opening bank lasts
			new(["powr", "nuke"], 3),
			new(["proc"], RefineryCore),       // four refineries is four harvesters, with no factory
			new(["hq"], 1),                    // unlocks tanks, grenadiers and the AA tower
			new(["weap", "afld"], 1),          // vehicle production
		];

		/// <summary>
		/// What every doctrine keeps standing at home, whatever else it happens to be doing.
		/// </summary>
		/// <remarks>
		/// These are <c>Support</c>-queue structures, not <c>Building</c> ones. They only build
		/// because <see cref="Modes.BuildBaseMode"/> drives every queue the yard owns; a yard
		/// driving only <c>Building</c> skips them in silence.
		/// <para>
		/// The anti-air tower is here — shared — rather than in the turtle's plan alone, because
		/// a base is undefended against aircraft for exactly as long as the bot is doing
		/// something other than turtling, which is most of a match. Aircraft accounted for 67 of
		/// 159 losses on badland-ridges, and the Defence doctrine that owned the only AA step was
		/// not entered until 770s of a 995-second game.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> HomeDefence { get; } =
		[
			new(["gtwr", "gun"], 2),           // a little static defence
			new(["atwr", "sam"], 1),           // ...that can also shoot upwards
		];

		/// <summary>The opening: an economy, and enough of an army not to die to a rush.</summary>
		public static IReadOnlyList<BuildStep> OpeningBuild { get; } =
		[
			.. Economy,
			.. HomeDefence,
			new(["powr", "nuke"], 4),
		];

		/// <summary>
		/// The opening army: bodies first, then the economy that pays for the rest, then rockets
		/// for everything bodies cannot hurt.
		/// </summary>
		/// <remarks>
		/// Each rocket step names <c>e3</c> and nothing else, and that is load-bearing.
		/// <c>Until(n)</c> counts every candidate the step lists, so a step written
		/// <c>["e3", "e1"]</c> is already satisfied by riflemen that exist for other reasons and
		/// never buys a single rocket — which is exactly how this bot finished badland-ridges
		/// having built 125 <c>e1</c> and zero <c>e3</c>. The harvester steps name <c>harv</c>
		/// alone for the same reason: listed beside a tank they would be satisfied by the tank.
		/// <para>
		/// The first harvester step sits above every combat vehicle because income compounds and
		/// a light tank does not. Two harvesters bought at around 250s, when the vehicle queue
		/// first exists, run for the remaining twenty minutes of a match this length; the same
		/// 2,200 credits spent on tanks buys three that die in the next engagement. This bot
		/// spent 3,300 credits on eleven scout buggies over badland-ridges and never once bought
		/// income.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> OpeningTrain { get; } =
		[
			new("Infantry", ["e1"], 4),        // bodies now; a first barracks has nothing else
			new("Vehicle", ["jeep", "bggy"], 1),
			new("Infantry", ["e3"], 4),        // ...and rockets, which that same barracks can build
			new("Vehicle", ["harv"], HarvesterCore),   // then income, before anything that shoots
			new("Infantry", ["e1"], RifleCore),
			new("Infantry", ["e2"], 4),
			new("Vehicle", ["mtnk", "ltnk"], 4),
			new("Infantry", ["e3"], 12),
			new("Vehicle", ["harv"], HarvesterSaturation),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
			new("Infantry", ["e3"], int.MaxValue),
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
		/// <remarks>
		/// Anti-air is not optional for this bot, and it is no longer only this doctrine's
		/// problem — see <see cref="HomeDefence"/>, which every plan now includes. What is left
		/// here is depth: a turtle wants more towers than a bot that happens to be at home.
		/// <para>
		/// Nothing this bot fields from the <c>Building</c> or <c>Vehicle</c> queues can shoot
		/// back at aircraft — the minigunner's rifle, the grenadier's grenade, the tank's cannon
		/// and the guard tower's gun are all ground-only. Only <c>e3</c>'s rockets and the
		/// <c>atwr</c>/<c>sam</c> pair can, which is why both now appear in every plan. On
		/// badland-ridges aircraft accounted for 67 of the 159 units lost, every one of them
		/// unanswered, and 21 of the last 23.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> DefenceBuild { get; } =
		[
			.. Economy,
			.. HomeDefence,
			new(["gtwr", "gun"], 4),
			new(["atwr", "sam"], 2),           // depth on the only thing that can hit aircraft
			new(["powr", "nuke"], 4),
			new(["gtwr", "gun"], 6),
		];

		/// <summary>Structures this bot builds from the yard's Support queue rather than Building.</summary>
		/// <remarks>Used by tests to prove every defence step is reachable from some driven queue.</remarks>
		public static IReadOnlyList<string> SupportQueueStructures { get; } =
			["gtwr", "gun", "atwr", "sam"];

		/// <summary>Structures whose armament can target aircraft.</summary>
		/// <remarks>
		/// Used by tests to prove no doctrine ships a build plan with nothing that shoots
		/// upwards. Everything else this bot builds is ground-only.
		/// </remarks>
		public static IReadOnlyList<string> AntiAirStructures { get; } = ["atwr", "sam"];

		/// <summary>Units whose armament can target aircraft.</summary>
		/// <remarks>
		/// One entry, and that is the point: <c>e3</c> is the only anti-air unit this bot can
		/// reach, it needs nothing but a barracks, and both factions build it.
		/// </remarks>
		public static IReadOnlyList<string> AntiAirUnits { get; } = ["e3"];

		/// <summary>Units that earn credits rather than spend them.</summary>
		/// <remarks>
		/// One entry, and that is also the point: <c>harv</c> is the only income this bot has,
		/// it comes from the same vehicle queue as the tanks and needs only a refinery, and both
		/// factions build it. Used by tests to prove no plan leaves income to the free harvester
		/// a refinery hands out.
		/// </remarks>
		public static IReadOnlyList<string> HarvesterUnits { get; } = ["harv"];

		/// <summary>Structures that produce and store harvested credits.</summary>
		public static IReadOnlyList<string> Refineries { get; } = ["proc"];

		/// <summary>Structures that own a <c>Vehicle</c> queue, and so gate <c>harv</c>.</summary>
		/// <remarks>
		/// Both cost 2,000 and both need <c>proc</c>, which is why a refinery is the cheaper
		/// harvester until one of these is standing: 1,500 for a refinery and its free actor
		/// against 2,000 plus 1,100 for the first bought one. Used by tests to prove the income
		/// rungs of every build plan come before the factory rather than behind it.
		/// </remarks>
		public static IReadOnlyList<string> VehicleFactories { get; } = ["weap", "afld"];

		/// <summary>Structures bought for what they unlock rather than for what they do.</summary>
		/// <remarks>
		/// <c>hq</c> earns nothing. It is worth having — <c>mtnk</c>, <c>ltnk</c>, <c>e2</c> and
		/// <c>atwr</c> all need <c>anyhq</c> — but not at the price of the refinery it displaced
		/// at 167s on badland-ridges. Used by tests to prove income leads tech in every plan.
		/// </remarks>
		public static IReadOnlyList<string> TechStructures { get; } = ["hq", "eye", "tmpl"];

		public static IReadOnlyList<ProductionStep> DefenceTrain { get; } =
		[
			new("Infantry", ["e1"], RifleCore),
			new("Infantry", ["e3"], 8),        // rockets, for whatever is chewing the base
			new("Vehicle", ["harv"], HarvesterCore),   // a siege that kills the economy wins by itself
			new("Infantry", ["e2"], 4),
			new("Vehicle", ["harv"], HarvesterSaturation),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
			new("Infantry", ["e3"], int.MaxValue),
		];

		/// <summary>Pushing: more production, better units, and the tech to make them worth having.</summary>
		public static IReadOnlyList<BuildStep> AttackBuild { get; } =
		[
			.. Economy,
			.. HomeDefence,
			new(["hq", "eye", "tmpl"], 1),     // tech
			new(["weap", "afld"], 2),
			new(["pyle", "hand"], 2),
			new(["powr", "nuke"], 5),
			new(["proc"], RefineryCore + 1),
		];

		/// <summary>
		/// The push: the economy that pays for it, then tanks ahead of infantry, and rockets
		/// rather than rifles behind them.
		/// </summary>
		/// <remarks>
		/// The vehicle steps sit above the endless infantry step on purpose. Both barracks and
		/// war factory ask the same plan what to build next and only the answer's owner acts on
		/// it, so an endless infantry step above an endless vehicle one hands every evaluation
		/// to whichever queue is idle most — and a barracks turning out a 100-credit rifleman
		/// every three seconds is idle far more often than a war factory. That ordering cost
		/// badland-ridges 132 infantry against 10 vehicles from two war factories.
		/// <para>
		/// The harvester floor leads even the tanks, and is a no-op whenever the economy is
		/// intact — it only fires when a harvester has died. A push is what a working economy is
		/// for, not a substitute for one: this bot entered Attack five separate times on
		/// badland-ridges with cash pinned at zero, razed ten enemy buildings, and lost anyway
		/// because the other side rebuilt from thirteen buildings to thirty-eight and grew its
		/// army from 7,250 at 900s to 70,200 while this one never once exceeded 7,800.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> AttackTrain { get; } =
		[
			new("Vehicle", ["harv"], HarvesterCore),   // replace what the last push cost us
			new("Vehicle", ["mtnk", "ltnk"], 8),
			new("Infantry", ["e3"], 8),
			new("Infantry", ["e1", "e2"], RifleCore),
			new("Vehicle", ["harv"], HarvesterSaturation),
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),
			new("Infantry", ["e3"], int.MaxValue),
		];

		/// <summary>Actor names treated as power plants, for the low-power override.</summary>
		public static IReadOnlyList<string> PowerPlants { get; } = ["powr", "nuke"];

		/// <summary>Every build plan any doctrine declares, for tests that check them all.</summary>
		public static IReadOnlyList<IReadOnlyList<BuildStep>> AllBuildPlans { get; } =
			[OpeningBuild, ScoutBuild, DefenceBuild, AttackBuild];

		/// <summary>Every production plan any doctrine declares, for tests that check them all.</summary>
		public static IReadOnlyList<IReadOnlyList<ProductionStep>> AllProductionPlans { get; } =
			[OpeningTrain, ScoutTrain, DefenceTrain, AttackTrain];

		/// <summary>Every build step any doctrine declares, for tests that check them all.</summary>
		public static IEnumerable<BuildStep> AllBuildSteps =>
			OpeningBuild.Concat(ScoutBuild).Concat(DefenceBuild).Concat(AttackBuild);
	}
}
