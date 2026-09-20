// ============================================================================
//  ReferencePlans — what each of ReferenceBot's doctrines builds and trains.
//
//  Deliberately a plain static class with no interfaces and no engine types, so
//  the plans can be read (and reasoned about) without loading anything from OpenRA.
//  Each doctrine's Configure is implemented in terms of these lists, so the rules
//  written down here and the shipped strategy cannot drift apart.
//
//  Build steps say "until N of these exist" and count what is already standing,
//  so they are cumulative rather than sequential: a doctrine whose plan extends
//  another's picks up where that one left off, and switching back and forth
//  never rebuilds anything.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System.Collections.Generic;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;

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
		/// How many riflemen to keep standing before most plans spend further on rockets.
		/// </summary>
		/// <remarks>
		/// A floor rather than a ratio, and deliberately small. <c>e1</c> is the cheapest body in
		/// the game and the best thing a barracks builds against other infantry (M16 does 150%
		/// against no armour, where the rocket does 28%), so a core of them is worth having
		/// whatever the enemy turns out to be.
		/// <para>
		/// <b>This floor used to be a ceiling as well, and that cost badland-ridges.</b> The rule
		/// was "everything above the floor goes on rockets", expressed as an endless <c>e3</c>
		/// rung at the bottom of every plan — so every spare barracks evaluation for the rest of
		/// the match bought a rocket soldier. It bought 38 of them: <b>11,400 credits, 29.3% of
		/// the 38,850 this bot spent all match</b>, 38 built and 38 lost, for <b>11 kills</b>.
		/// That is 1,036 credits a kill, against <b>96</b> for <c>e1</c> (25 built, 26 kills,
		/// 2,500 credits) and <b>107</b> for <c>bggy</c> (5 built, 14 kills, 1,500 credits) — the
		/// dearest unit per kill in the bot's reach sat at the bottom of the ladder where every
		/// leftover credit lands.
		/// </para>
		/// <para>
		/// The ruleset says why. <b>63% of the mobile army's engagement orders were against
		/// Infantry</b> (563 of 894, excluding the towers), and <c>e3</c> deals <b>318</b> damage
		/// a second to <c>None</c> armour where <c>e1</c> deals <b>1875</b>. The enemy infantry
		/// that did the killing — <c>e1</c> 33, <c>e3</c> 14, <c>e2</c> 9, 56 of 97 — deals that
		/// 1875 straight back, so a 300-credit unit was out-traded 5.9 to 1 by a 100-credit one.
		/// <c>e3</c> is also the slowest actor in the game at 0.952 cells a second, and the one
		/// assault of the match died strictly in speed order over 73.9 cells: <c>bggy</c> (4.15)
		/// at 491-527s inside their base, <c>e1</c> (1.318) at 566-574s on the wall, <c>e3</c> at
		/// 579-589s still eight cells short. 39 units in 50 seconds, for 9 kills.
		/// </para>
		/// <para>
		/// So the endless rung is now <c>e1</c> and <c>e3</c> keeps only its bounded rung above
		/// it. That rung is not decoration: <c>e3</c> is the only unit this bot can build that
		/// shoots upwards (see <see cref="AntiAirUnits"/>) and the best thing it can build per
		/// credit against <c>Heavy</c> armour (1592 against <c>None</c>'s 318), so it must be
		/// replaced when it dies — it simply must not be what every leftover credit buys. Capped
		/// at twelve it costs 3,600 rather than 11,400, and the 7,800-credit difference is 20% of
		/// everything the bot spent, moved to the rung that killed 10.8 times more per credit.
		/// </para>
		/// <para>
		/// <b>And then the endless <c>e1</c> rung lost the next match, for the mirror reason.</b>
		/// Everything above is an argument about <em>that</em> opponent, who was 63% infantry.
		/// Against an opponent who fielded none — badland-ridges at Hard, all <c>ltnk</c>,
		/// <c>bike</c>, <c>ftnk</c>, <c>arty</c>, <c>heli</c> and <c>a10</c> — the same rung
		/// bought 54 riflemen that absorbed 247,239 damage and killed 1,650 credits' worth, while
		/// the thirteen <c>e3</c> beside them dealt more damage for less money and took a fifth
		/// as much. So this constant is no longer the endless rung's answer at all:
		/// <see cref="Logic.ArmyMixLogic"/> chooses the body from what the side has actually
		/// watched the enemy field, and both matches come out right. What is left here is what
		/// the name always meant — the <em>floor</em> of cheap bodies held whatever the enemy
		/// turns out to be, which is a bounded rung and therefore not this rule's business.
		/// </para>
		/// </remarks>
		const int RifleCore = 12;

		/// <summary>
		/// The anti-air pair Defence targets while allowing rifle rebuilding behind one survivor.
		/// An empty floor temporarily yields to a large local infantry screen until that pressure
		/// clears or aircraft appear; see <see cref="Modes.TrainUnitsMode"/>.
		/// </summary>
		public const int DefenceAntiAirCore = 2;

		/// <summary>
		/// The defensive armour pair keeps one durable line unit standing without pinning the
		/// Vehicle queue ahead of recovery and long-range fire.
		/// </summary>
		/// <remarks>
		/// <see cref="Modes.TrainUnitsMode"/> releases this two-unit rung after one survivor, just
		/// as it does for the light screen. Zero armour therefore rebuilds one faction-equivalent
		/// tank, while one standing tank lets the queue continue to siege and income rungs.
		/// </remarks>
		public const int DefenceArmourCore = 2;

		// Scout targets the pair. Defence keeps one strict and lets that survivor release the
		// second slot so repeated screen losses cannot pin the Vehicle queue.
		const int ScreenVehicleCore = 2;

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
		/// <para>
		/// <b>And a floor equal to <see cref="RefineryCore"/> is a floor that can never fire.</b>
		/// A refinery hands out a free harvester, so four refineries satisfy a four-harvester
		/// rung four-out-of-four the moment they stand. On the next badland-ridges the four
		/// refineries stood at 51s, 117s, 174s and 263s, every <c>harv</c> the bot ever owned
		/// appeared in one of those same four seconds, and the <c>Vehicle</c> queue bought
		/// <b>zero</b> harvesters in 1,289 seconds. This number is now only the starting point:
		/// <see cref="Logic.ExpansionLogic.Reinforce"/> resizes this rung from the refineries
		/// actually standing, exactly as
		/// <see cref="Logic.ExpansionLogic.Saturate"/> resizes the saturation rung below, so the
		/// floor is a demand for docking places filled rather than for refineries counted twice.
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
		/// <para>
		/// <b>Tied to the refinery <em>constant</em> was still a constant.</b> Every plan reached
		/// eight harvesters at 634s on badland-ridges and then bought no income for the remaining
		/// 952 seconds, so a base that grew past four refineries ran one harvester each — the
		/// free actor a refinery hands out — and never two. <see cref="Modes.TrainUnitsMode"/>
		/// now resizes this rung, and only this rung, from the refineries actually standing; see
		/// <see cref="Logic.ExpansionLogic.Saturate"/>. The number here is the floor it starts
		/// from, and the resize can only ever raise it.
		/// </para>
		/// <para>
		/// <b>And this rung sits below something that dies, so it is not reachable either.</b>
		/// <see cref="SiegeCore"/> is directly above it and siege vehicles are killed — six
		/// <c>msam</c> built and six lost on the next badland-ridges — so the siege rung was
		/// permanently unmet, <c>UnitProductionLogic.ChooseNext</c> returns the first unmet step,
		/// and the <c>Vehicle</c> queue answered <c>msam</c> at 456s, 515s, 558s, 622s, 724s,
		/// 791s and 814s while the income underneath was never once reached. That is the third
		/// time rung order alone has failed to protect income, which is why the *floor* is now
		/// resized too and this rung is the backstop rather than the mechanism.
		/// </para>
		/// </remarks>
		const int HarvesterSaturation = RefineryCore * 2;

		/// <summary>
		/// How many long-reach siege vehicles to buy before the last of the income.
		/// </summary>
		/// <remarks>
		/// <b>The vehicle queue used to buy nothing that fights.</b> On badland-ridges this bot
		/// stood two airfields — 4,000 credits, 11.6% of everything it ever spent — and gave the
		/// <c>Vehicle</c> queue <b>eleven orders in 1,403 seconds</b>: five <c>bggy</c> and six
		/// <c>harv</c>. Not one combat vehicle, in a match where <c>hq</c> stood at 318s and the
		/// first <c>afld</c> at 385s, so <c>arty</c> was buildable for 1,018 seconds — 73% of the
		/// match.
		/// <para>
		/// The cause was rung order, and it is the exact mirror of the bug the tank rung used to
		/// have. <see cref="HarvesterSaturation"/> sat directly above the armour rung and
		/// <see cref="Logic.ExpansionLogic.Saturate"/> sizes it at two per standing refinery —
		/// eight, with four refineries up. Harvesters die: all eight were hunted down between
		/// 660s and 800s. So the rung was permanently unmet, <c>UnitProductionLogic.ChooseNext</c>
		/// does not gate on cash, and it returned <c>harv</c> to the vehicle queue for the rest of
		/// the game while everything below it stayed unreachable. The last vehicle order was at
		/// 777s; the next was at 1327s.
		/// </para>
		/// <para>
		/// What the army became instead was <b>72% <c>e3</c> by credits</b> — 10,500 of the 14,600
		/// ever spent on things that shoot — which is the worst anti-infantry warhead in the
		/// ruleset at 318 damage a second against <c>None</c> armour, while <b>62% of the army's
		/// engagement orders were against infantry</b> (601 against 366). It walked 62 cells to
		/// their base and <b>39 of its 90 losses happened in one five-by-four-cell patch at
		/// (52-56, 38-41) between 480s and 600s</b>, for 23 kills. At 480s this bot led on units
		/// (38 to 34), army (9,300 to 8,200) and was one building behind; by 600s it had seven
		/// units left and never recovered.
		/// </para>
		/// <para>
		/// <b>The arithmetic this number must not cross.</b> The rung displaces the same count of
		/// saturation harvesters at 1,100 each, so it has to cost less than the income it defers.
		/// Lifetime spend was 34,350 credits over the 782 seconds the economy was alive — 43.9 a
		/// second across a fleet averaging five live harvesters, so roughly 8.8 a second each and
		/// about 125 seconds for a harvester to repay itself. Four is the worst case at GDI prices:
		/// 4 x 900 = 3,600, which is 82 seconds of that income — <b>less than the payback period of
		/// the single harvester it defers</b>, so the rung can never cost more than it delays. At
		/// Nod prices it is 4 x 600 = 2,400, 55 seconds.
		/// </para>
		/// <para>
		/// What it buys for that: one <c>arty</c> does <b>5,390</b> damage a second to <c>None</c>
		/// armour where an <c>e3</c> does <b>318</b>, so a single 600-credit artillery piece is
		/// worth seventeen 300-credit rocket soldiers — 5,100 credits — against the infantry that
		/// was most of what this army met. It is also 1.758 cells a second against <c>e3</c>'s
		/// 0.952, so it arrives in half the time.
		/// </para>
		/// </remarks>
		const int SiegeCore = 4;

		/// <summary>
		/// The 11-cell answer, per faction.
		/// </summary>
		/// <remarks>
		/// <b>Nothing this bot fielded reached past 6 cells</b>, and the thing that killed most of
		/// it did: enemy <c>arty</c> was the joint-largest killer of this bot's units on
		/// badland-ridges at 13 of 90, level with <c>heli</c>. Both factions have the answer behind
		/// prerequisites this bot routinely meets — <c>anyhq</c> plus its own vehicle factory —
		/// and <c>game-rules.json</c> prices them: GDI <c>msam</c> at 900 with <c>227mm</c> at
		/// <c>rangeCells 11</c>, Nod <c>arty</c> at 600 with <c>ArtilleryShell</c> at
		/// <c>rangeCells 11</c>.
		/// <para>
		/// Candidates are alternatives for one role, so exactly one of these is buildable at a
		/// time and the step works as either faction. Neither short-ranged tank is listed here,
		/// and that is load-bearing: <c>Until(n)</c> counts <em>every</em> candidate a step lists,
		/// so a rung written <c>["arty", "ltnk"]</c> is satisfied by light tanks and buys no reach
		/// at all — the same trap that once made a step written <c>["e3", "e1"]</c> buy 125
		/// riflemen and zero rockets. The tanks keep their own rungs below.
		/// </para>
		/// <para>
		/// Declared above every plan on purpose. Static property initialisers in this file run in
		/// <b>textual order</b>, so a list referenced by a plan declared above it captures null.
		/// </para>
		/// <para>
		/// Both are already measured by <see cref="Logic.WeaponMatchLogic.RoleOf"/> — <c>arty</c>
		/// as AntiInfantry, <c>msam</c> as AntiArmour — so the target scorers divide their work
		/// correctly the moment they exist, and an actor id that table has never seen still falls
		/// through to a flat Unknown rather than a guess.
		/// </para>
		/// </remarks>
		public static string[] SiegeVehicles { get; } = ["arty", "msam"];

		/// <summary>
		/// Why every plan's last vehicle rung is reach rather than armour.
		/// </summary>
		/// <remarks>
		/// <c>UnitProductionLogic.ChooseNext</c> returns the first unmet step its queue can
		/// build, and an endless step is never met — so once a ladder is climbed, the endless
		/// rung is the queue's answer to every remaining evaluation of the match. The endless
		/// rung is therefore not a fallback, it is <b>what the bot spends its late game on</b>,
		/// and until now the <c>Vehicle</c> queue's was <c>["mtnk", "ltnk"]</c>.
		/// <para>
		/// <b>That rung was the worst buy in the match.</b> From the badland-ridges unit ledger,
		/// as credits killed per credit spent: <c>e3</c> <b>2.38</b>, <c>msam</c> <b>1.86</b>,
		/// <c>e1</c> <b>1.06</b>, <c>mtnk</c> <b>0.64</b>. The two endless rungs were the two
		/// worst units on the field and took 33% of all spend between them, while the two best
		/// were both capped — <c>e3</c> at twelve and the siege rung at
		/// <see cref="SiegeCore"/> four. Per credit <c>msam</c> dealt 123 damage and absorbed 7;
		/// <c>mtnk</c> dealt 40 and absorbed 37.
		/// </para>
		/// <para>
		/// <b>And the ruleset says the same thing about the specific fight this bot keeps
		/// losing.</b> Enemy <c>arty</c> killed 139 of this side's 264 losses. <c>msam</c> is its
		/// hard counter on every axis at once: identical reach (11 cells against 11), identical
		/// speed (1.758 cells a second), 12,000 hit points against 7,500, and 2,405 damage a
		/// second against <c>Light</c> armour — which is what <c>arty</c> wears — so one
		/// <c>msam</c> kills one <c>arty</c> in about three seconds. It was the only thing this
		/// bot fielded that ever hurt one: four of its eight artillery kills came from thirteen
		/// <c>msam</c>, against 113 <c>e1</c> that died to <c>arty</c> having dealt it nothing.
		/// <c>mtnk</c> meanwhile reaches 4.75 cells and cannot answer an 11-cell gun at all.
		/// </para>
		/// <para>
		/// The armour rung is kept directly underneath rather than deleted, and that is
		/// load-bearing. Both need <c>anyhq</c>, so an <c>hq</c> that falls — it was built once
		/// and lost once on badland-ridges — takes <em>both</em> with it; but a rung whose
		/// candidates cannot be built is skipped silently, so any future divergence in their
		/// prerequisites leaves armour as the fallback rather than leaving the queue with
		/// nothing below it to reach. Same 900-credit price either way, so this reorders spend
		/// without changing its rate, and every harvester rung still sits above both.
		/// </para>
		/// <para>
		/// Declared here rather than inlined so the three plans that use it cannot drift apart,
		/// and below <see cref="SiegeVehicles"/> because static initialisers in this file run in
		/// textual order — a list that referenced it from above would capture null.
		/// </para>
		/// </remarks>
		public static string[] EndlessReach { get; } = SiegeVehicles;

		/// <summary>The name of the queue a barracks owns.</summary>
		public const string InfantryQueue = "Infantry";

		/// <summary>The name of the queue every defensive emplacement comes from.</summary>
		/// <remarks>
		/// Named here rather than in the one mode that drives it, because a second rule now has
		/// to ask that queue what it can build: see <see cref="Modes.TrainUnitsMode"/> and the
		/// 1,112 seconds of 16:9 this side spent with no gun standing at home.
		/// </remarks>
		public const string SupportQueue = "Support";

		/// <summary>The cheap body, whose rifle is built for other infantry.</summary>
		/// <remarks>
		/// 1,875 damage a second against <c>None</c> armour, 500 against <c>Light</c> and 125
		/// against <c>Heavy</c>, and it cannot shoot upwards at all. Both factions build it from
		/// a bare barracks, so it is the only thing a side with nothing else can buy.
		/// </remarks>
		public static string[] RifleBodies { get; } = ["e1"];

		/// <summary>The plated-target body, and the only unit this bot fields that shoots upwards.</summary>
		/// <remarks>
		/// The mirror image: 319 against <c>None</c>, 1,593 against <c>Light</c> and
		/// <c>Heavy</c>. Three times the price of <see cref="RifleBodies"/> and worth it against
		/// exactly the half of the game the rifle cannot touch.
		/// </remarks>
		public static string[] RocketBodies { get; } = ["e3"];

		/// <summary>The cheap faction-portable vehicles that scout and screen the economy.</summary>
		public static string[] ScreenVehicles { get; } = ["jeep", "bggy"];

		/// <summary>The faction-portable tanks that anchor a defensive line.</summary>
		public static string[] DefenceArmourVehicles { get; } = ["mtnk", "ltnk"];

		/// <summary>Faction alternatives for the tech infantry that clears infantry off harvesters.</summary>
		public static string[] HarvesterGuardInfantry { get; } = ["e2", "e4"];

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
		/// match; it is now last, because it is the only rung here that earns nothing at all.
		/// Only the <c>Building</c> queue reads this list, so moving rungs around here does not
		/// slow infantry production — that comes from the production plan and a separate queue.
		/// </para>
		/// <para>
		/// <b>The factory is not tech, and putting it last cost the next match.</b> It used to
		/// sit behind four refineries and the <c>hq</c> on the argument that four refineries pay
		/// for it sooner. They do not, because a refinery is a <em>one-shot</em> harvester and
		/// this bot's harvesters die. On badland-ridges the ladder reached <c>afld</c> at
		/// <b>474s</b>; two of the four free harvesters were already dead, at 271s and 275s,
		/// and until 474s there was no way in the game to replace either. The fleet averaged
		/// <b>1.06 live harvesters across a 1,187-second match</b> with four refineries standing
		/// from 260s, 550 seconds of the match had none at all, and income finished at 17.2
		/// credits a second against a reference of 50. Five refineries were bought for 7,500
		/// credits — 20.7% of everything ever spent — to man them with one harvester.
		/// </para>
		/// <para>
		/// <b>The queue must also open before the second refinery spends the remaining bank.</b>
		/// The latest fight built that refinery first, then opened the Vehicle queue after cash
		/// was exhausted. Its replacement harvester competed with the next refinery order and
		/// neither delivered before both free harvesters died. Reordering the harvester inside
		/// the Vehicle plan could not help a queue that opened too late to fund it.
		/// </para>
		/// <para>
		/// The arithmetic is the opening bank again, and it fits. <c>weap</c>/<c>afld</c> costs
		/// 2,000 and needs only <c>proc</c>, so power, refinery, power, barracks, factory is
		/// 5,000 of the 7,500 a side starts with. The replacement queue now opens while the bank
		/// can still fund it, then the plan returns immediately to the second refinery. A bought
		/// harvester is 1,100 against a refinery's 1,500, needs no site or additional defence,
		/// and can be bought again the next time one dies — which is the whole difference
		/// between an economy and a countdown.
		/// </para>
		/// <para>
		/// <b>Four is a floor, not a ceiling.</b> A plan is a finite ladder and a map is not: on
		/// badland-ridges every rung of the Attack plan was met when the fifth refinery landed at
		/// 744s, and the construction yard asked for nothing at all for the remaining 842 seconds
		/// of a 1,586-second match while the winner grew from 21 buildings to 52.
		/// <see cref="Logic.ExpansionLogic"/> takes over from there and sizes the refinery count
		/// from the tiberium the side has actually explored. It is consulted only once this list
		/// is satisfied, so nothing here is displaced, delayed or outbid, and it can only ever
		/// raise the number these rungs already asked for.
		/// </para>
		/// <para>
		/// The power rung between the two new refineries is not decoration. Two <c>nuke</c> ran
		/// a balance of -25 by 420s with two refineries and the shared defences up, and a
		/// brownout throttles every queue at once; <see cref="Modes.BuildBaseMode"/>'s low-power
		/// override is a rescue, not a plan.
		/// </para>
		/// <para>
		/// <b>Tech is last of the earners, not last of the ladder, and that distinction cost
		/// 16:9.</b> "Income leads tech" is still right and still why <c>hq</c> sits below three
		/// refineries, the barracks and the factory — but it was reading as "tech leads nothing",
		/// and a rung below the <em>fourth</em> refinery lands very late. On 16:9 the refineries
		/// stood at 51s, 345s, 423s and 606s and <c>hq</c> only at <b>638s of a 1,024-second
		/// match</b>. Until it stood, the Vehicle queue's entire catalogue was a 300-credit scout
		/// buggy and a harvester, so the airfield spent 386 of its first 500 seconds buying the
		/// worst unit on the field — <c>bggy</c>, 20 built, 20 lost, 667 credits a kill. Four
		/// seconds after <c>hq</c> stood it ordered its first <c>ltnk</c>, which lived 183s and
		/// killed 3,100 credits' worth for 750; the <c>arty</c> that followed killed 1,900 for
		/// 600. Those are the only two units in this bot's reach that traded above 3:1, they are
		/// both gated here, and mean army value finished at 586 against a reference of 6,000.
		/// So <c>hq</c> moves one rung up, above the fourth refinery and below the third. The
		/// economy keeps its lead — three refineries, a barracks and a factory all still precede
		/// it — and the fourth refinery is displaced by 1,000 credits rather than by a doctrine.
		/// </para>
		/// <para>
		/// <b>The second refinery now leads the barracks and the factory, because "the bank can
		/// still fund it" stopped being true.</b> The argument above for opening the Vehicle
		/// queue early is sound and is kept — but it assumed the 2,500 remaining after power,
		/// refinery, power, barracks, factory was the second refinery's to spend. It is not:
		/// <c>Infantry</c> and <c>Support</c> draw on the same cash in parallel, and on the
		/// latest 16:9 they emptied the bank before the yard ever reached the rung. The whole
		/// opening, in order: <c>nuke</c> 13s, <c>proc</c> 51s, <c>nuke</c> 65s, <c>hand</c>
		/// 79s, four <c>e1</c> to 91s, <c>gtwr</c> 105s, four <c>e3</c> to 123s, <c>afld</c>
		/// 129s, <c>sam</c> 134s, <c>bggy</c> 148s — <b>8,150 credits and one refinery</b>. The
		/// second refinery could not be started until income alone had rebuilt its price and
		/// stood at <b>249s</b>. The side therefore ran <b>one harvester from 51s to 249s</b>,
		/// earned 3,955 credits in the whole match at <b>5.9 a second</b> against a prior median
		/// of 28.6, and the <c>Vehicle</c> queue the factory had opened spent <b>470 seconds
		/// waiting</b> for cash that was never going to exist.
		/// </para>
		/// <para>
		/// So <c>proc</c> two moves above both. Power, refinery, power, refinery is 4,000 of the
		/// 7,500 bank and needs nothing that is not already standing, and the second free
		/// harvester roughly doubles income from about 90s — which buys the 2,000 factory back
		/// inside a minute rather than deferring it. The barracks moves with the factory rather
		/// than ahead of it, and that is deliberate: <c>gtwr</c> and <c>sam</c> both require a
		/// barracks, so the <c>Support</c> queue cannot open a second front on the opening bank
		/// until the economy has had its first two rungs.
		/// <see cref="Logic.IncomeFirstLogic.ReserveOpeningBank"/> is what stops the other queues
		/// taking the refinery's money once the barracks does stand.
		/// </para>
		/// <para>
		/// <b>And the third refinery now leads the factory, because a reservation owned by a
		/// queue does not choose what that queue buys.</b> The move above worked exactly as
		/// written — the second refinery stood at <b>103s</b> rather than 249s — and the bank
		/// still died, because the rung immediately underneath it was the 2,000-credit factory.
		/// The yard ordered <c>afld</c> at <b>118s</b> and the reservation could not stop it:
		/// <c>Building</c> <em>is</em> the reserving queue, so the 1,500 held for refinery three
		/// was spent on the factory instead. <c>afld</c> landed at 167s and took cash to zero;
		/// refinery three was ordered at 168s and was never paid for, the <c>sam</c> ordered at
		/// 155s was never paid for, and the side finished on <b>two refineries, two harvesters
		/// and 2,100 credits earned in 977 seconds</b> — 2.1 a second against a reference of 50.
		/// Both free harvesters died to infantry at 197s and 208s, <b>822 seconds of the match
		/// had no live harvester</b>, and between 176s and 944s the bot completed nothing at
		/// all in any queue.
		/// </para>
		/// <para>
		/// What the 2,000 bought: the <c>Vehicle</c> queue it opened issued <b>two</b> orders in
		/// the 810 seconds that followed and delivered <b>one 300-credit buggy, at 966s</b>. A
		/// <c>proc</c> is 1,500, needs only <c>anypower</c>, and ships a harvester with it, so
		/// on the same bank it is both cheaper and the only rung here that raises income. The
		/// factory keeps its place above <c>hq</c> and above refinery four — the failure that
		/// argument was written for was a factory at 474s, behind four refineries and the tech,
		/// and one rung of 1,500 does not reproduce it.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> Economy { get; } =
		[
			new(["powr", "nuke"], 1),          // power
			new(["proc"], 1),                  // income before anything else
			new(["powr", "nuke"], 2),
			new(["proc"], 2),                  // the second free harvester, before anything that earns nothing
			new(["pyle", "hand"], 1),          // barracks
			new(["proc"], 3),                  // the third free harvester, while the opening bank lasts
			new(["weap", "afld"], 1),          // ...and the means to replace a harvester that dies
			new(["powr", "nuke"], 3),
			new(["hq"], 1),                    // tech behind three earners: it unlocks ltnk and arty
			new(["proc"], RefineryCore),       // four refineries is four harvesters, with no factory
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
		/// <para>
		/// <b>The anti-air rung is interleaved rather than stacked behind the ground pair</b>, for
		/// the reason <see cref="DefenceBuild"/> already writes down one rung lower: a two-tower
		/// standing floor pins the only step that shoots upwards behind a step that is unmet for
		/// as long as ground towers keep dying. On 16:9 the base finished with one <c>gtwr</c>
		/// and no anti-air at all, and enemy <c>heli</c> killed <b>all four</b> refineries — the
		/// single largest source of damage taken in the match at 232,227 against <c>proc</c>
		/// alone, plus both airfields and the construction yard. The totals here are unchanged;
		/// only the order in which a queue is offered them is, so one of each stands before the
		/// second of either.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> HomeDefence { get; } =
		[
			new(["gtwr", "gun"], 1),           // a little static defence
			new(["atwr", "sam"], 1),           // ...that can also shoot upwards, before tower two
			new(["gtwr", "gun"], 2),
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
		/// <para>
		/// <b>Saturation leads the tanks for a reason that cost the next match.</b> It used to sit
		/// below <c>new("Vehicle", ["mtnk", "ltnk"], 4)</c>, and a floor of four tanks is never
		/// permanently met because tanks die — so the vehicle queue stuck on that rung and the
		/// saturation step underneath it was unreachable for the whole game. The war factory
		/// stood at 362s and produced five <c>mtnk</c> and three <c>jeep</c> in the 816 seconds
		/// that followed: 5,700 credits of armour and <b>zero</b> harvesters. Every harvester the
		/// bot ever owned was a refinery's free actor, the fleet sat at four from 229s to 828s,
		/// and when three of them died between 828s and 844s nothing could replace them — income
		/// went from 37.1 credits a second in the window to 780s to 1.6, then to zero for the
		/// last 338 seconds. A harvester also outranks a tank on the numbers: 1,100 against 900,
		/// and the four <c>mtnk</c> this displaces killed so little that the bot finished 28
		/// kills to 75 losses.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> OpeningTrain { get; } =
		[
			new("Infantry", ["e1"], 4),        // bodies now; a first barracks has nothing else
			new("Vehicle", ScreenVehicles, 1),
			new("Infantry", ["e3"], 4),        // ...and rockets, which that same barracks can build
			new("Vehicle", ["harv"], HarvesterCore),   // then income, before anything that shoots
			new("Infantry", ["e1"], RifleCore),
			new(InfantryQueue, HarvesterGuardInfantry, 4),
			new("Vehicle", SiegeVehicles, SiegeCore),       // reach, ahead of the last of the income
			new("Vehicle", ["harv"], HarvesterSaturation),  // ...and all of the income, before any of the armour
			new("Vehicle", ["mtnk", "ltnk"], 4),
			new("Infantry", ["e3"], 12),
			new("Vehicle", EndlessReach, int.MaxValue),      // reach forever: see EndlessReach
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),  // ...and armour when reach is unbuildable
			new(InfantryQueue, RifleBodies, int.MaxValue),   // ...and the body that beats what we have seen: see ArmyMixLogic
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
			new("Vehicle", ScreenVehicles, ScreenVehicleCore),
			.. OpeningTrain,
		];

		/// <summary>
		/// Turtling: alternate the first ground and air emplacements, then add depth and bodies.
		/// Cheap infantry rather than tanks, because what is needed is guns in the base now
		/// rather than better guns in a minute.
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
		/// <para>
		/// The leading <c>gtwr</c>/AA pair this list used to carry itself is gone, because
		/// <see cref="HomeDefence"/> now interleaves exactly that pair for every doctrine. A
		/// cumulative "until N" rung that is already met is skipped silently, so the duplicate
		/// was a no-op — but two places writing the same opening is how the two drift apart.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<BuildStep> DefenceBuild { get; } =
		[
			.. Economy,
			.. HomeDefence,                    // one tower, then AA, then tower two
			new(["gtwr", "gun"], 4),
			new(["atwr", "sam"], 2),           // depth on the only thing that can hit aircraft
			new(["powr", "nuke"], 4),
			new(["gtwr", "gun"], 6),
		];

		/// <summary>Structures this bot builds from the yard's Support queue rather than Building.</summary>
		/// <remarks>Every defence step must be reachable from one of the queues the bot drives.</remarks>
		public static IReadOnlyList<string> SupportQueueStructures { get; } =
			["gtwr", "gun", "atwr", "sam"];

		/// <summary>Structures whose armament can target aircraft.</summary>
		/// <remarks>
		/// No doctrine should ship a build plan with nothing that shoots upwards. Everything else
		/// this bot builds is ground-only.
		/// </remarks>
		public static IReadOnlyList<string> AntiAirStructures { get; } = ["atwr", "sam"];

		/// <summary>The cheap ground tower, per faction.</summary>
		/// <remarks>
		/// Both reach exactly 6 cells, which is the number that matters: see
		/// <see cref="CoveringRoles"/>.
		/// </remarks>
		public static IReadOnlyList<string> GuardTowers { get; } = ["gtwr", "gun"];

		/// <summary>Units whose armament can target aircraft.</summary>
		/// <remarks>
		/// One entry, and that is the point: <c>e3</c> is the only anti-air unit this bot can
		/// reach, it needs nothing but a barracks, and both factions build it. Aliased to
		/// <see cref="RocketBodies"/> rather than written out again, because the two lists have
		/// to name the same actor and a duplicate is a drift waiting to happen.
		/// </remarks>
		public static IReadOnlyList<string> AntiAirUnits { get; } = RocketBodies;

		/// <summary>Units that earn credits rather than spend them.</summary>
		/// <remarks>
		/// One entry, and that is also the point: <c>harv</c> is the only income this bot has,
		/// it comes from the same vehicle queue as the tanks and needs only a refinery, and both
		/// factions build it. No plan should leave income to the free harvester a refinery hands
		/// out.
		/// </remarks>
		public static IReadOnlyList<string> HarvesterUnits { get; } = ["harv"];

		/// <summary>Structures that produce and store harvested credits.</summary>
		public static IReadOnlyList<string> Refineries { get; } = ["proc"];

		/// <summary>Structures that own a <c>Vehicle</c> queue, and so gate <c>harv</c>.</summary>
		/// <remarks>
		/// Both cost 2,000 and both need only <c>proc</c>. A refinery looks like the cheaper
		/// harvester — 1,500 with its free actor against 2,000 plus 1,100 for the first bought
		/// one — and that comparison is what put the factory last in <see cref="Economy"/> and
		/// lost badland-ridges. It only holds for the <em>first</em> harvester. A refinery buys
		/// one and can never buy another; a factory buys every replacement for the rest of the
		/// match, and this bot's harvesters are hunted. See <see cref="Economy"/> for the
		/// numbers. Standing one of these is also the first of the three conditions
		/// <see cref="Logic.IncomeFirstLogic"/> requires before it will hold the barracks back:
		/// with no factory up there is no harvester to protect the credits for.
		/// </remarks>
		public static IReadOnlyList<string> VehicleFactories { get; } = ["weap", "afld"];

		/// <summary>Structures bought for what they unlock rather than for what they do.</summary>
		/// <remarks>
		/// <c>hq</c> earns nothing. It is worth having — <c>mtnk</c>, <c>ltnk</c>, <c>e2</c> and
		/// <c>atwr</c> all need <c>anyhq</c> — but not at the price of the refinery it displaced
		/// at 167s on badland-ridges. Income still leads tech in every plan: three refineries, a
		/// barracks and a vehicle factory all precede it in <see cref="Economy"/>. What changed
		/// after 16:9 is that it no longer trails the <em>fourth</em> refinery as well, because
		/// the two units it unlocks were the only ones this bot built that traded above 3:1 and
		/// they were unbuildable for the first 638 seconds of a 1,024-second match.
		/// </remarks>
		public static IReadOnlyList<string> TechStructures { get; } = ["hq", "eye", "tmpl"];

		public static IReadOnlyList<ProductionStep> DefenceTrain { get; } =
		[
			new(InfantryQueue, RocketBodies, DefenceAntiAirCore), // restore AA; current infantry pressure may redirect an empty floor
			new(InfantryQueue, RifleBodies, RifleCore),
			new(InfantryQueue, RocketBodies, 8), // then anti-armour and anti-air depth
			new("Vehicle", ScreenVehicles, ScreenVehicleCore), // keep one escort strict; TrainUnitsMode releases the second
			new("Vehicle", ["harv"], HarvesterCore),   // a siege that kills the economy wins by itself
			new("Infantry", ["e2"], 4),
			new("Vehicle", DefenceArmourVehicles, DefenceArmourCore), // one tank holds the line; TrainUnitsMode releases the second
			new("Vehicle", SiegeVehicles, SiegeCore),  // 11 cells of reach, sited at home
			new("Vehicle", ["harv"], HarvesterSaturation),
			new("Vehicle", EndlessReach, int.MaxValue),      // reach forever: see EndlessReach
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),  // ...and armour when reach is unbuildable
			new(InfantryQueue, RifleBodies, int.MaxValue),   // ...and the body that beats what we have seen: see ArmyMixLogic
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
		/// <para>
		/// Saturation sits directly under the floor rather than under the tank rung, for the same
		/// reason it does in <see cref="OpeningTrain"/>: a floor of eight tanks is never
		/// permanently met, so anything below it never fires. A push that has already been fought
		/// once needs the income to pay for the next one more than it needs tank number five.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ProductionStep> AttackTrain { get; } =
		[
			new("Vehicle", ["harv"], HarvesterCore),   // replace what the last push cost us
			new("Vehicle", SiegeVehicles, SiegeCore),  // then the reach the last push did not have
			new("Vehicle", ["harv"], HarvesterSaturation),
			new("Vehicle", ["mtnk", "ltnk"], 8),
			new("Infantry", ["e3"], 8),
			new("Infantry", ["e1", "e2"], RifleCore),
			new("Vehicle", EndlessReach, int.MaxValue),      // reach forever: see EndlessReach
			new("Vehicle", ["mtnk", "ltnk"], int.MaxValue),  // ...and armour when reach is unbuildable
			new(InfantryQueue, RifleBodies, int.MaxValue),   // ...and the body that beats what we have seen: see ArmyMixLogic
		];

		/// <summary>Actor names treated as power plants, for the low-power override.</summary>
		public static IReadOnlyList<string> PowerPlants { get; } = ["powr", "nuke"];

		/// <summary>
		/// How many power plants are built inside the base before the rest lead the frontier.
		/// </summary>
		/// <remarks>
		/// Two, because the first two go up before there is anywhere to expand to — the first at
		/// 13s and the second at 65s on badland-ridges, when the only other structures were the
		/// yard and one refinery — and because a base whose every power plant is out on the
		/// frontier browns out the moment the frontier is raided.
		/// </remarks>
		const int PowerPlantsAtHome = 2;

		/// <summary>
		/// Structures that push outward as they multiply, and how many of each stay home first.
		/// </summary>
		/// <remarks>
		/// Refineries expand because a harvester works the closest tiberium to the refinery it
		/// docks with, so refineries stacked in one ring share one patch and starve together.
		/// <para>
		/// Power plants expand because they are the only cheap thing that <em>can</em> move the
		/// frontier. A Tiberian Dawn cell is buildable when it is close enough to a structure
		/// carrying <c>GivesBuildableArea</c>, and of everything this bot builds only
		/// <c>fact</c>, <c>proc</c>, <c>nuke</c>, <c>hand</c>/<c>pyle</c>, <c>hq</c> and
		/// <c>weap</c>/<c>afld</c> carry it — <c>silo</c>, <c>gtwr</c>, <c>gun</c>, <c>atwr</c>
		/// and <c>sam</c> all require buildable area without giving any, so no quantity of cheap
		/// towers ever extends the base by one cell. At 500 credits the power plant is the
		/// cheapest of the ones that do, against 1,500 for a refinery and 2,000 for a factory,
		/// and every plan here builds four or five anyway.
		/// </para>
		/// <para>
		/// Production is deliberately absent: a barracks wants its rally point inside the base.
		/// Defence used to be absent too, on the grounds that a tower on the frontier is a tower
		/// defending nothing. That was wrong, and <see cref="CoveringRoles"/> is the correction.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<ExpandingRole> ExpandingRoles { get; } =
		[
			new(Refineries, 1),
			new(PowerPlants, PowerPlantsAtHome),
		];

		/// <summary>
		/// Structures that follow the economy out, because they are only defence where their
		/// weapon reaches, and how far each one reaches.
		/// </summary>
		/// <remarks>
		/// <see cref="ExpandingRoles"/> pushes the economy outward and does it well: on
		/// badland-ridges the four refineries stood 7.07, 11.05, 13.42 and 13.42 cells from the
		/// construction yard. Defence took the default 2–14 ring, and <c>FindBuildLocation</c>
		/// answers that with the nearest legal cell, so all three defences the bot ever built
		/// went up on top of the yard — <c>gtwr</c> at 1.41 cells (105s), <c>gtwr</c> at 2.24
		/// (130s), <c>sam</c> at 2.83 (250s). That is 1,850 credits, 7.9% of the 23,450 the bot
		/// spent all match, buying cover for the one part of the base nothing attacked until
		/// 1,320s.
		/// <para>
		/// A <c>gtwr</c> reaches 6 cells. From the nearest tower the three outer refineries were
		/// 10.20, 12.08 and 12.08 cells away, so no tower covered any of them. Enemy <c>e3</c>
		/// killed all four harvesters at 670s, 686s, 696s and 699s, 8.54 to 10.05 cells from that
		/// tower, and a rocket soldier reaches 6 cells too — it stood where nothing could answer
		/// and shot the economy to pieces. The bot then held four refineries and no harvester for
		/// 733 of 1,453 seconds; income fell from 23.2 credits a second to 0.76.
		/// </para>
		/// <para>
		/// Reach is the pessimistic member of each pair so the rule is safe either way round:
		/// <c>gtwr</c> and <c>gun</c> are both 6, while <c>atwr</c> is 7 on the ground against
		/// <c>sam</c>'s 10. One of each stays home, because the yard, the barracks and the
		/// vehicle factory still need something over them and nothing else provides it.
		/// </para>
		/// </remarks>
		public static IReadOnlyList<CoveringRole> CoveringRoles { get; } =
		[
			new(GuardTowers, 6, 1),
			new(AntiAirStructures, 7, 1),
		];
	}
}
