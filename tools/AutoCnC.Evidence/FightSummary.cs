#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;

namespace AutoCnC.Evidence
{
	/// <summary>
	/// Everything a triage question needs, precomputed, in one file small enough to read whole.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The raw records stay exactly as they were; this is derived from them and replaces none of
	/// them. It exists because the analysis budget was going on arithmetic rather than judgement:
	/// credits-per-kill by unit type, the telemetry crossover, income rate, doctrine episodes and
	/// loss clusters were recomputed from raw events every round by bespoke scripts, each of which
	/// could be — and was — subtly wrong in a different way.
	/// </para>
	/// <para>
	/// It is held under <see cref="FightSummaryBuilder.MaxBytes"/> deliberately. File reads
	/// truncate, and an artifact whose point is to be read whole must fit in one read; when a very
	/// long match would overflow, the builder trims its longest lists and records what it trimmed
	/// in <see cref="Truncated"/> rather than emitting a file that silently stops halfway.
	/// </para>
	/// </remarks>
	public sealed class FightSummary
	{
		/// <summary>
		/// Bumped when a field is added. Fields are only ever added, never renamed or removed.
		/// </summary>
		public int SchemaVersion { get; set; } = 1;

		public string RunId { get; set; }
		public string Bot { get; set; }
		public string SourceRevision { get; set; }
		public DateTime GeneratedUtc { get; set; }

		/// <summary>Which raw artifacts were present, and what schema each was written at.</summary>
		public SummaryProvenance Provenance { get; set; }

		public SummaryFight Fight { get; set; }
		public SummaryHeadline Headline { get; set; }
		public FitnessScore Fitness { get; set; }
		public SummaryCrossover Crossover { get; set; }
		public SummaryEconomy Economy { get; set; }
		public SummaryMap Map { get; set; }

		/// <summary>
		/// Per actor type: built, lost, kills, spend, share of spend, damage both ways,
		/// credits per kill and mean lifetime, as a <c>columns</c>/<c>rows</c> table.
		/// </summary>
		public Table UnitTypes { get; set; }

		/// <summary>Per production queue, as a <c>columns</c>/<c>rows</c> table.</summary>
		public Table Production { get; set; }

		/// <summary>Every doctrine episode, as a <c>columns</c>/<c>rows</c> table.</summary>
		public Table DoctrineEpisodes { get; set; }

		/// <summary>
		/// Own actor type against enemy actor type, both directions, as a
		/// <c>columns</c>/<c>rows</c> table.
		/// </summary>
		public Table Engagements { get; set; }

		/// <summary>Where this side kept dying, as a <c>columns</c>/<c>rows</c> table.</summary>
		public Table LossClusters { get; set; }

		/// <summary>
		/// What this side saw of the enemy and when, against when it was hit, and what the duel lab
		/// measured as the answer to each enemy unit type.
		/// </summary>
		public SummaryIntel Intel { get; set; }

		/// <summary>This side's income, spend, army and production beside the opponent's.</summary>
		public SummaryScale Scale { get; set; }

		public List<string> Notes { get; set; } = [];
		public Dictionary<string, string> Truncated { get; set; } = [];
	}

	/// <summary>Where each number came from, so a gap reads as a gap rather than a zero.</summary>
	public sealed class SummaryProvenance
	{
		public bool HasBattleLog { get; set; }
		public bool HasTelemetry { get; set; }
		public bool HasDecisionTrace { get; set; }
		public bool HasGameRules { get; set; }
		public bool HasMapFacts { get; set; }

		/// <summary>True when <c>battle.csv</c> carried <c>dealt</c> rows.</summary>
		public bool HasDamageDealt { get; set; }

		/// <summary>True when <c>telemetry.csv</c> carried the <c>earned</c>/<c>spent</c> flows.</summary>
		public bool HasEconomyFlows { get; set; }

		/// <summary>
		/// <c>ruleset</c>, or <c>unavailable</c> on a run whose rules export predates the
		/// <c>FreeActor</c> field. Never <c>heuristic</c>: guessing which actors were free is what
		/// this field exists to stop.
		/// </summary>
		public string FreeActorExclusion { get; set; }

		public int GameRulesSchemaVersion { get; set; }
		public int DecisionTraceSchemaVersion { get; set; }

		/// <summary>
		/// True when the duel lab's <c>matchups.json</c> was found, so <c>intel</c> carries counters.
		/// </summary>
		public bool HasMatchups { get; set; }
	}

	public sealed class SummaryFight
	{
		public string Map { get; set; }
		public string Difficulty { get; set; }
		public string Faction { get; set; }
		public string OpponentFaction { get; set; }
		public int Opponents { get; set; }
		public string Outcome { get; set; }
		public int DurationSeconds { get; set; }
		public string LocalPlayer { get; set; }
		public string OpponentPlayer { get; set; }
		public int? Seed { get; set; }
		public string Benchmark { get; set; }

		/// <summary>
		/// The single invocation of a benchmark this run belonged to.
		/// </summary>
		/// <remarks>
		/// Without it, a comparison selects every historical run sharing a benchmark name, so
		/// re-running <c>standard</c> against a new revision folds the previous revision's
		/// candidates and a stale control into the same win count. The name says which set was
		/// played; this says which sitting.
		/// </remarks>
		public string Batch { get; set; }

		public string Arm { get; set; }
	}

	/// <summary>
	/// The dozen numbers a round is judged on, kept in one flat block.
	/// </summary>
	/// <remarks>
	/// Flat and stable on purpose: this is what the cross-run index stores per run and what the
	/// trend artifact diffs, so a metric keeps being tracked whether or not a given round thought
	/// to mention it. Hand-carried prose is how a 46.6 to 25.4 credits-per-second regression went
	/// unnoticed for four rounds.
	/// </remarks>
	public sealed class SummaryHeadline
	{
		public string Outcome { get; set; }
		public int DurationSeconds { get; set; }

		/// <summary>Credits earned per second of match, the economy's actual rate.</summary>
		public double CreditsEarnedPerSecond { get; set; }

		/// <summary>Credits spent per second of match — the 46.6-to-25.4 regression metric.</summary>
		public double CreditsSpentPerSecond { get; set; }

		public int CreditsEarned { get; set; }
		public int CreditsSpent { get; set; }

		/// <summary>Value destroyed divided by value lost. Above 1 is a profitable war.</summary>
		public double ValueExchangeRatio { get; set; }

		public int CreditsKilled { get; set; }
		public int CreditsLost { get; set; }
		public int UnitsKilled { get; set; }
		public int UnitsLost { get; set; }

		/// <summary>Enemy buildings destroyed. Zero across a whole match is a headline fact.</summary>
		public int BuildingsKilled { get; set; }
		public int BuildingsLost { get; set; }

		public int PeakArmyValue { get; set; }
		public double MeanArmyValue { get; set; }

		/// <summary>Army value integrated over the match, in credit-seconds.</summary>
		public double ArmyValueIntegral { get; set; }

		public int PeakHarvesters { get; set; }
		public int Refineries { get; set; }
		public int CellsExplored { get; set; }
		public int DistinctEnemyActorsSeen { get; set; }
		public int DoctrineChanges { get; set; }
		public int ProductionOrders { get; set; }
		public int IdleUnitSeconds { get; set; }
	}

	/// <summary>
	/// When this side stopped being level on the curves that decide a match.
	/// </summary>
	/// <remarks>
	/// Recomputed from raw telemetry every round until now, with a different definition each time.
	/// The definition here is fixed: the last second this side was level or ahead, and the first
	/// second after it that it was not and never recovered.
	/// </remarks>
	public sealed class SummaryCrossover
	{
		public CrossoverPoint Units { get; set; }
		public CrossoverPoint Army { get; set; }
		public CrossoverPoint Assets { get; set; }
	}

	public sealed class CrossoverPoint
	{
		public int? LastLevelOrAheadSeconds { get; set; }
		public int? FirstBehindSeconds { get; set; }
		public int OwnValueAtCrossover { get; set; }
		public int EnemyValueAtCrossover { get; set; }
		public bool EverAhead { get; set; }
		public bool BehindAtEnd { get; set; }
	}

	public sealed class SummaryEconomy
	{
		public int CreditsEarned { get; set; }
		public int CreditsSpent { get; set; }
		public double CreditsEarnedPerMinute { get; set; }
		public double CreditsSpentPerMinute { get; set; }

		/// <summary>Credits banked and never spent, averaged over the match.</summary>
		public double MeanIdleCash { get; set; }

		public int PeakCash { get; set; }
		public int PeakHarvesters { get; set; }
		public int Refineries { get; set; }
		public int SecondsWithNoHarvester { get; set; }
		public int SecondsInLowPower { get; set; }
		public int MinPowerBalance { get; set; }

		/// <summary>One point per <see cref="IntervalSeconds"/>, so the series stays readable.</summary>
		public int IntervalSeconds { get; set; }

		/// <summary>
		/// The economy over time, as a <c>columns</c>/<c>rows</c> table.
		/// </summary>
		/// <remarks>
		/// Columns are <c>seconds, earned, spent, cash, harvesters, army, power, queued</c>.
		/// <c>earned</c> and <c>spent</c> are cumulative, so the rate between any two rows is the
		/// difference divided by the elapsed seconds.
		/// </remarks>
		public Table Series { get; set; }
	}

	public sealed class EconomySample
	{
		public int Seconds { get; set; }
		public int Earned { get; set; }
		public int Spent { get; set; }
		public int Cash { get; set; }
		public int Harvesters { get; set; }
		public int Army { get; set; }
		public int Power { get; set; }
		public int Queued { get; set; }
	}

	public sealed class SummaryMap
	{
		public string Name { get; set; }
		public int WidthCells { get; set; }
		public int HeightCells { get; set; }
		public string LocalSpawn { get; set; }
		public string[] EnemySpawns { get; set; } = [];
		public int? HomeToNearestEnemyCells { get; set; }
		public int ResourceCells { get; set; }
		public int ExploredCells { get; set; }
	}

	/// <summary>One actor type's whole contribution, in credits rather than counts.</summary>
	public sealed class UnitTypeLedger
	{
		public string Type { get; set; }
		public string Kind { get; set; }
		public int Cost { get; set; }
		public int Built { get; set; }
		public int Lost { get; set; }
		public int Kills { get; set; }
		public int CreditsSpent { get; set; }

		/// <summary>This type's share of everything spent on units this match, as a percentage.</summary>
		public double ShareOfSpendPercent { get; set; }

		public int CreditsKilled { get; set; }
		public int DamageDealt { get; set; }
		public int DamageTaken { get; set; }

		/// <summary>Credits spent on this type per kill it scored. Null when it killed nothing.</summary>
		public double? CreditsPerKill { get; set; }

		public double MeanLifetimeSeconds { get; set; }
		public int SurvivedToEnd { get; set; }

		/// <summary>True when any instance of this type was granted rather than bought.</summary>
		public bool Free { get; set; }

		/// <summary>
		/// How many instances were granted free.
		/// </summary>
		/// <remarks>
		/// Separate from <see cref="Free"/> because a type can be both. A refinery grants a
		/// harvester, and a harvester is also buildable for 1,100 credits, so "this type is free"
		/// would wrongly zero every one that was paid for.
		/// </remarks>
		public int FreeCount { get; set; }
	}

	public sealed class QueueSummary
	{
		public string Queue { get; set; }
		public int OrdersIssued { get; set; }
		public int DistinctItems { get; set; }

		/// <summary>
		/// How far down its plan this queue ever got, as an index into
		/// <see cref="ItemsInPlanOrder"/>.
		/// </summary>
		/// <remarks>
		/// The plan itself lives in the bot and is not in the evidence, but the planner returns
		/// the first unmet step every time it is asked, so the order in which distinct items were
		/// first requested is the order of the plan as it was actually walked. A queue that never
		/// gets past index 2 has a plan whose later rungs never ran, which is a different problem
		/// from one that walks the whole plan and loses anyway.
		/// </remarks>
		public int DeepestPlanStepIndex { get; set; }

		public string[] ItemsInPlanOrder { get; set; } = [];
		public string DeepestItem { get; set; }

		/// <summary>Seconds between an order and the item appearing, summed over the match.</summary>
		public int SecondsWaiting { get; set; }

		/// <summary>Seconds after this queue's first order during which it had none outstanding.</summary>
		public int SecondsIdle { get; set; }

		/// <summary>
		/// Orders re-issued for an item already outstanding — the queue refusing work.
		/// </summary>
		public int RepeatedRequests { get; set; }

		public int Delivered { get; set; }
		public int CreditsOrdered { get; set; }
	}

	public sealed class DoctrineEpisode
	{
		public string From { get; set; }
		public string To { get; set; }
		public string Reason { get; set; }
		public int EntrySeconds { get; set; }
		public int? ExitSeconds { get; set; }
		public int DurationSeconds { get; set; }
		public int ArmyValueAtEntry { get; set; }
		public int ArmyValueAtExit { get; set; }
		public int CreditsKilledDuring { get; set; }
		public int CreditsLostDuring { get; set; }
	}

	/// <summary>One own-actor-type against one enemy-actor-type, both directions.</summary>
	public sealed class Engagement
	{
		public string Own { get; set; }
		public string Enemy { get; set; }
		public int DamageDealt { get; set; }
		public int DamageTaken { get; set; }
		public int Kills { get; set; }
		public int Deaths { get; set; }
	}

	/// <summary>Where this side kept dying, and when.</summary>
	public sealed class LossCluster
	{
		public int X { get; set; }
		public int Y { get; set; }
		public int RadiusCells { get; set; }
		public int Count { get; set; }
		public int CreditsLost { get; set; }
		public int FromSeconds { get; set; }
		public int ToSeconds { get; set; }
		public string[] Types { get; set; } = [];
	}

	/// <summary>
	/// Whether this side saw the enemy's army coming, and whether it built what beats it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Built only from what this side's own battle log observed — sightings, hits taken and units
	/// lost — plus the duel lab's static matchups. In sixteen fights at d1a695b, 77% of the value the
	/// bot lost went to enemy unit types first seen less than 30 seconds before they started
	/// shooting, and nothing in the summary said so; answering that took a bespoke script.
	/// </para>
	/// <para>
	/// A counter can only be bought for something that has been seen, so the sighting timings and
	/// the counters belong together.
	/// </para>
	/// </remarks>
	public sealed class SummaryIntel
	{
		/// <summary>When an enemy structure more than <see cref="FightSummaryBuilder.NearBaseCells"/> from home was first seen.</summary>
		public int? EnemyBaseFirstSeenSeconds { get; set; }

		/// <summary>When the enemy first damaged anything of this side's.</summary>
		public int? FirstEnemyHitSeconds { get; set; }

		/// <summary>
		/// Share of the value lost to enemy combat units that went to types first seen less than
		/// <see cref="FightSummaryBuilder.LateSightingSeconds"/> before they first hit this side, or
		/// never seen at all. Null when nothing was lost to an enemy unit.
		/// </summary>
		public double? LateSightingLossPercent { get; set; }

		/// <summary>
		/// Share of the same losses that went to types first seen within
		/// <see cref="FightSummaryBuilder.NearBaseCells"/> of this side's base.
		/// </summary>
		public double? NearBaseSightingLossPercent { get; set; }

		/// <summary>
		/// How well the credits this side spent on combat units answer the enemy army it saw,
		/// by the duel lab's equal-cost margins: 50 is an even trade, 100 a clean sweep, 0 swept.
		/// Null without matchups.
		/// </summary>
		public int? CounterMatchPercent { get; set; }

		/// <summary>
		/// The three units this side's faction can build that best answer the enemy army it saw,
		/// weighted by the value of each enemy type seen, as <c>type +margin</c> joined by <c>|</c>.
		/// </summary>
		public string MixCounters { get; set; }

		/// <summary>Each enemy structure type, and the second it was first seen, in order.</summary>
		public Dictionary<string, int> EnemyStructuresFirstSeen { get; set; } = [];

		/// <summary>
		/// One row per enemy combat unit type, most damaging first, as a <c>columns</c>/<c>rows</c>
		/// table.
		/// </summary>
		/// <remarks>
		/// Columns are <c>type, kind, firstSeenSeconds, firstSeenCellsFromBase, firstHitSeconds,
		/// leadSeconds, actorsSeen, valueSeen, creditsKilled, counters, creditsSpentOnCounters</c>.
		/// <c>leadSeconds</c> is first hit minus first sighting: negative when the type hit this side
		/// before it was ever seen, as long-range artillery does, and empty when either is missing.
		/// <c>counters</c> are the units this side's faction can build that beat the type at equal
		/// cost, best first, and <c>creditsSpentOnCounters</c> is what this side spent on them.
		/// </remarks>
		public Table Threats { get; set; }
	}

	/// <summary>
	/// Whether this side out-produced the opponent: flows and peaks for both, side by side.
	/// </summary>
	/// <remarks>
	/// The opponent's figures come from telemetry, which is an omniscient after-match record. They
	/// are here to diagnose the gap between the two economies, never as something a bot could
	/// have read during the match.
	/// </remarks>
	public sealed class SummaryScale
	{
		public double OwnEarnedPerSecond { get; set; }
		public double OpponentEarnedPerSecond { get; set; }
		public double OwnSpentPerSecond { get; set; }
		public double OpponentSpentPerSecond { get; set; }

		/// <summary>This side's spend as a percentage of the opponent's. Null when either is unknown.</summary>
		public double? SpendVsOpponentPercent { get; set; }

		public int OwnPeakArmyValue { get; set; }
		public int OpponentPeakArmyValue { get; set; }
		public double OwnMeanArmyValue { get; set; }
		public double OpponentMeanArmyValue { get; set; }
		public int OwnPeakHarvesters { get; set; }
		public int OpponentPeakHarvesters { get; set; }
		public double OwnMeanHarvesters { get; set; }
		public double OpponentMeanHarvesters { get; set; }

		/// <summary>
		/// Credits earned per second for each harvester on the field on average. Separates "too
		/// few harvesters" from "harvesters that are not working". Null when there were none.
		/// </summary>
		public double? OwnEarnedPerHarvesterSecond { get; set; }

		public double? OpponentEarnedPerHarvesterSecond { get; set; }
		public int OwnPeakBuildings { get; set; }
		public int OpponentPeakBuildings { get; set; }

		/// <summary>Most barracks, war factories, airfields and helipads this side had standing at once.</summary>
		public int OwnUnitFactoriesPeak { get; set; }

		/// <summary>When this side first had two unit factories standing, or null if it never did.</summary>
		public int? OwnSecondUnitFactorySeconds { get; set; }

		/// <summary>
		/// When the opponent's income pulled away: the first minute, from the fourth, at which its
		/// income over the previous two minutes was at least 1.5 times this side's and stayed so for
		/// the two minutes after. Null if it never did.
		/// </summary>
		/// <remarks>
		/// The end-of-match totals cannot say whether the opponent ramped faster or tore this side's
		/// economy down. In 31 losses at the start of this measurement, the split came at a median
		/// of minute 9 with no harvester of ours lost yet. Each of ours was earning barely half what
		/// each of theirs did, and the harvesters died after the split, not before it.
		/// </remarks>
		public int? IncomeSplitSeconds { get; set; }

		/// <summary>This side's harvesters lost by <see cref="IncomeSplitSeconds"/>.</summary>
		public int? OwnHarvestersLostBeforeSplit { get; set; }

		/// <summary>
		/// Both economies minute by minute, as a <c>columns</c>/<c>rows</c> table.
		/// </summary>
		/// <remarks>
		/// Columns are <c>seconds, ownEarned, oppEarned, ownHarvesters, oppHarvesters, ownArmy,
		/// oppArmy, ownHarvestersLost, enemyNearBase</c>. Earned and harvesters lost are cumulative.
		/// <c>enemyNearBase</c> counts enemy combat units this side saw within
		/// <see cref="FightSummaryBuilder.NearBaseCells"/> of home during that minute.
		/// </remarks>
		public Table Series { get; set; }
	}
}
