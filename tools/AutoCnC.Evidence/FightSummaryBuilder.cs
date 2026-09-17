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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Evidence
{
	/// <summary>Derives <see cref="FightSummary"/> from one fight's raw evidence.</summary>
	public static class FightSummaryBuilder
	{
		/// <summary>
		/// The ceiling the emitted file is held under.
		/// </summary>
		/// <remarks>
		/// File reads truncate at 20 KB. An artifact whose whole purpose is to be read in one go
		/// and trusted must fit, so this is a hard constraint rather than a target: the builder
		/// trims until it holds and says what it trimmed.
		/// </remarks>
		public const int MaxBytes = 20 * 1024;

		/// <summary>Sampling interval for the economy series, in game seconds.</summary>
		public const int EconomyIntervalSeconds = 60;

		/// <summary>Cells within this of each other count as the same place a unit died.</summary>
		public const int LossClusterRadiusCells = 6;

		/// <summary>A power balance below this is a brownout, which slows production.</summary>
		public const int LowPowerThreshold = 0;

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

			// The battle log carries player-chosen names and author-written reasons. Relaxed
			// escaping keeps those readable instead of turning every apostrophe into \u0027 in a
			// file whose entire point is being read.
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		public static FightSummary Build(EvidenceSet evidence, IReadOnlyList<UnitRecord> units)
		{
			var battle = evidence.Battle;
			var telemetry = evidence.Telemetry;
			var trace = evidence.Trace;
			var rules = evidence.Rules;
			var manifest = evidence.Manifest;

			var local = battle.LocalPlayer ?? manifest.LocalPlayer;
			var opponent = battle.Roster.Values
				.FirstOrDefault(r => !string.Equals(r.Name, local, StringComparison.Ordinal))?.Name;

			var own = local == null ? [] : telemetry.For(local);
			var theirs = opponent == null ? [] : telemetry.For(opponent);
			var seconds = MatchSeconds(battle, telemetry, manifest);

			var summary = new FightSummary
			{
				RunId = manifest.Id,
				Bot = trace.Bot ?? manifest.BotProject,
				SourceRevision = manifest.SourceRevision,
				GeneratedUtc = DateTime.UtcNow,
				Provenance = new SummaryProvenance
				{
					HasBattleLog = battle.Events.Count > 0,
					HasTelemetry = telemetry.Samples.Count > 0,
					HasDecisionTrace = trace.UnitDecisions.Count > 0 || trace.Assessments.Count > 0,
					HasGameRules = rules.Actors.Count > 0,
					HasMapFacts = evidence.Map != null,
					HasDamageDealt = battle.Of(BattleEvents.Dealt).Any(),
					HasEconomyFlows = telemetry.HasEconomyFlows,
					FreeActorExclusion = rules.KnowsFreeActors ? "ruleset" : "unavailable",
					GameRulesSchemaVersion = rules.SchemaVersion,
					DecisionTraceSchemaVersion = trace.SchemaVersion
				},
				Fight = new SummaryFight
				{
					Map = evidence.Map?.Map ?? manifest.Map,
					Difficulty = manifest.Difficulty,
					Faction = battle.Roster.TryGetValue(local ?? "", out var me) ? me.Faction : manifest.Faction,
					OpponentFaction = battle.Roster.TryGetValue(opponent ?? "", out var them)
						? them.Faction
						: manifest.BotFaction,
					Opponents = Math.Max(manifest.Opponents, battle.Roster.Count - 1),
					Outcome = Outcome(battle, manifest, trace),
					DurationSeconds = seconds,
					LocalPlayer = local,
					OpponentPlayer = opponent,
					Seed = manifest.Seed,
					Benchmark = manifest.Benchmark,
					Batch = manifest.Batch,
					Arm = manifest.Arm
				}
			};

			var ours = units.Where(u => string.Equals(u.Owner, local, StringComparison.Ordinal)).ToList();

			// Built as typed records, because the notes and the headline reason over them, then
			// emitted as tables, because indented JSON spends more on field names than on facts
			// and the summary has a hard size ceiling to live under.
			var unitTypes = UnitTypes(ours, rules);
			var queues = Queues(trace, battle, rules);
			var episodes = Doctrines(trace, battle, own, seconds, rules);
			var clusters = LossClusters(battle, local, rules);

			summary.Economy = Economy(own, seconds);
			summary.Crossover = Crossover(own, theirs);
			summary.Engagements = Engagements(battle, local);
			summary.Map = MapSummary(evidence, battle);

			summary.UnitTypes = Table.Of(unitTypes,
				[
					"type", "kind", "cost", "built", "lost", "kills", "creditsSpent",
					"shareOfSpendPercent", "creditsKilled", "damageDealt", "damageTaken",
					"creditsPerKill", "meanLifetimeSeconds", "survivedToEnd", "freeCount"
				],
				u =>
				[
					u.Type, u.Kind, u.Cost, u.Built, u.Lost, u.Kills, u.CreditsSpent,
					u.ShareOfSpendPercent, u.CreditsKilled, u.DamageDealt, u.DamageTaken,
					u.CreditsPerKill, u.MeanLifetimeSeconds, u.SurvivedToEnd, u.FreeCount
				]);

			summary.Production = Table.Of(queues,
				[
					"queue", "ordersIssued", "distinctItems", "deepestPlanStepIndex", "deepestItem",
					"itemsInPlanOrder", "secondsWaiting", "secondsIdle", "repeatedRequests",
					"delivered", "creditsOrdered"
				],
				q =>
				[
					q.Queue, q.OrdersIssued, q.DistinctItems, q.DeepestPlanStepIndex, q.DeepestItem,
					string.Join('>', q.ItemsInPlanOrder), q.SecondsWaiting, q.SecondsIdle,
					q.RepeatedRequests, q.Delivered, q.CreditsOrdered
				]);

			summary.DoctrineEpisodes = Table.Of(episodes,
				[
					"to", "from", "entrySeconds", "exitSeconds", "durationSeconds",
					"armyValueAtEntry", "armyValueAtExit", "creditsKilledDuring",
					"creditsLostDuring", "reason"
				],
				d =>
				[
					d.To, d.From, d.EntrySeconds, d.ExitSeconds, d.DurationSeconds,
					d.ArmyValueAtEntry, d.ArmyValueAtExit, d.CreditsKilledDuring,
					d.CreditsLostDuring, d.Reason
				]);

			summary.LossClusters = Table.Of(clusters,
				["x", "y", "radiusCells", "count", "creditsLost", "fromSeconds", "toSeconds", "types"],
				c =>
				[
					c.X, c.Y, c.RadiusCells, c.Count, c.CreditsLost, c.FromSeconds, c.ToSeconds,
					string.Join('|', c.Types)
				]);

			summary.Headline = Headline(summary, own, theirs, ours, queues, trace, seconds, rules);
			summary.Fitness = FitnessScore.From(summary.Headline, summary.Map, summary.Fight.Outcome,
				summary.Provenance);

			Note(summary, unitTypes, trace, rules);
			return summary;
		}

		static string Outcome(BattleEvents battle, FightManifest manifest, DecisionTrace trace)
		{
			var over = battle.Of(BattleEvents.Over).LastOrDefault()?.DetailText("result");
			return !string.IsNullOrEmpty(over) ? over
				: !string.IsNullOrEmpty(manifest.Outcome) ? manifest.Outcome
				: trace.Result ?? "Unknown";
		}

		static int MatchSeconds(BattleEvents battle, Telemetry telemetry, FightManifest manifest)
		{
			var candidates = new List<int> { manifest.DurationSeconds };
			if (battle.Events.Count > 0)
				candidates.Add(battle.Events[^1].Seconds);

			if (telemetry.Samples.Count > 0)
				candidates.Add(telemetry.Samples[^1].Seconds);

			return Math.Max(1, candidates.Max());
		}

		/// <summary>
		/// One row per actor type this side built, valued in credits rather than counted.
		/// </summary>
		/// <remarks>
		/// Free actors are excluded from spend, and from the share-of-spend denominator, on the
		/// authority of the ruleset's own <c>FreeActor</c> trait. They keep their kills and losses,
		/// because a free harvester dying still costs the match something — it just never cost
		/// credits to make.
		/// </remarks>
		static List<UnitTypeLedger> UnitTypes(List<UnitRecord> ours, GameRules rules)
		{
			var ledger = new List<UnitTypeLedger>();

			foreach (var group in ours.Where(u => !string.IsNullOrEmpty(u.Type))
				.GroupBy(u => u.Type, StringComparer.OrdinalIgnoreCase))
			{
				var all = group.ToList();
				var born = all.Where(u => u.BornSeconds != null).ToList();
				var built = born.Count;
				var cost = all[0].Cost > 0 ? all[0].Cost : rules.CostOf(group.Key);
				var freeCount = born.Count(u => u.Free);
				var lifetimes = all.Where(u => u.LifetimeSeconds != null)
					.Select(u => (double)u.LifetimeSeconds.Value).ToList();

				ledger.Add(new UnitTypeLedger
				{
					Type = group.Key,
					Kind = rules.Rule(group.Key)?.Kind,
					Cost = cost,
					Built = built,

					// Charged per instance, not per type: a type can be both granted free by a
					// structure and bought outright, and charging neither is as wrong as charging
					// both. UnitLedger.MarkFreeActors decides which individual actors were given.
					CreditsSpent = (built - freeCount) * cost,

					Lost = all.Count(u => u.DiedSeconds != null),
					Kills = all.Sum(u => u.Kills),
					CreditsKilled = all.Sum(u => u.CreditsKilled),
					DamageDealt = all.Sum(u => u.DamageDealt),
					DamageTaken = all.Sum(u => u.DamageTaken),
					MeanLifetimeSeconds = lifetimes.Count > 0 ? Math.Round(lifetimes.Average(), 1) : 0,
					SurvivedToEnd = all.Count(u => u.BornSeconds != null && u.DiedSeconds == null),
					Free = freeCount > 0,
					FreeCount = freeCount
				});
			}

			var totalSpend = (double)ledger.Sum(l => l.CreditsSpent);
			foreach (var entry in ledger)
			{
				entry.ShareOfSpendPercent = totalSpend > 0
					? Math.Round(100d * entry.CreditsSpent / totalSpend, 1)
					: 0;

				// Null rather than infinity: "this type killed nothing" is a fact worth reading,
				// and a number divided by zero dressed up as a metric is not.
				entry.CreditsPerKill = entry.Kills > 0 && entry.CreditsSpent > 0
					? Math.Round((double)entry.CreditsSpent / entry.Kills, 1)
					: null;
			}

			return ledger.OrderByDescending(l => l.CreditsSpent).ThenBy(l => l.Type, StringComparer.Ordinal).ToList();
		}

		static SummaryEconomy Economy(List<TelemetrySample> own, int seconds)
		{
			var economy = new SummaryEconomy { IntervalSeconds = EconomyIntervalSeconds };
			if (own.Count == 0)
				return economy;

			var last = own[^1];
			var peakEarned = own.Max(s => s.Earned ?? 0);
			var peakSpent = own.Max(s => s.Spent ?? 0);

			economy.CreditsEarned = Math.Max(last.Earned ?? 0, peakEarned);
			economy.CreditsSpent = Math.Max(last.Spent ?? 0, peakSpent);
			economy.CreditsEarnedPerMinute = Rate(economy.CreditsEarned, seconds) * 60d;
			economy.CreditsSpentPerMinute = Rate(economy.CreditsSpent, seconds) * 60d;
			economy.MeanIdleCash = Math.Round(own.Average(s => (double)s.Cash), 1);
			economy.PeakCash = own.Max(s => s.Cash);
			economy.PeakHarvesters = own.Max(s => s.Harvesters ?? 0);
			economy.Refineries = 0;
			economy.SecondsWithNoHarvester = own.Count(s => (s.Harvesters ?? 1) == 0);
			economy.SecondsInLowPower = own.Count(s => (s.Power ?? 1) < LowPowerThreshold);
			economy.MinPowerBalance = own.Min(s => s.Power ?? 0);

			var samples = new List<EconomySample>();
			var next = 0;
			foreach (var sample in own)
			{
				if (sample.Seconds < next)
					continue;

				next = sample.Seconds + EconomyIntervalSeconds;
				samples.Add(new EconomySample
				{
					Seconds = sample.Seconds,
					Earned = sample.Earned ?? 0,
					Spent = sample.Spent ?? 0,
					Cash = sample.Cash,
					Harvesters = sample.Harvesters ?? 0,
					Army = sample.Army,
					Power = sample.Power ?? 0,
					Queued = sample.Queued ?? 0
				});
			}

			economy.Series = Table.Of(samples,
				["seconds", "earned", "spent", "cash", "harvesters", "army", "power", "queued"],
				s => [s.Seconds, s.Earned, s.Spent, s.Cash, s.Harvesters, s.Army, s.Power, s.Queued]);

			return economy;
		}

		/// <summary>
		/// The last second this side was level or ahead on each curve, and the first after it that
		/// it was behind and stayed behind.
		/// </summary>
		static SummaryCrossover Crossover(List<TelemetrySample> own, List<TelemetrySample> theirs)
		{
			var enemy = new Dictionary<int, TelemetrySample>();
			foreach (var sample in theirs)
				enemy[sample.Seconds] = sample;

			return new SummaryCrossover
			{
				Units = Cross(own, enemy, s => s.Units),
				Army = Cross(own, enemy, s => s.Army),
				Assets = Cross(own, enemy, s => s.Assets)
			};
		}

		static CrossoverPoint Cross(List<TelemetrySample> own, Dictionary<int, TelemetrySample> enemy,
			Func<TelemetrySample, int> value)
		{
			var point = new CrossoverPoint();
			if (own.Count == 0 || enemy.Count == 0)
				return point;

			int? lastLevel = null;
			int? firstBehind = null;

			foreach (var sample in own)
			{
				if (!enemy.TryGetValue(sample.Seconds, out var other))
					continue;

				var mine = value(sample);
				var hers = value(other);

				if (mine >= hers)
				{
					lastLevel = sample.Seconds;
					point.EverAhead |= mine > hers;

					// Level again, so any earlier crossing was recovered from and is not the one
					// that decided the match.
					firstBehind = null;
					continue;
				}

				if (firstBehind == null)
				{
					firstBehind = sample.Seconds;
					point.OwnValueAtCrossover = mine;
					point.EnemyValueAtCrossover = hers;
				}
			}

			point.LastLevelOrAheadSeconds = lastLevel;
			point.FirstBehindSeconds = firstBehind;
			point.BehindAtEnd = firstBehind != null;
			return point;
		}

		/// <summary>
		/// Per production queue: how much was asked of it, how far down its plan it got, and how
		/// much of the match it spent waiting or doing nothing.
		/// </summary>
		static List<QueueSummary> Queues(DecisionTrace trace, BattleEvents battle, GameRules rules)
		{
			var produced = battle.Of(BattleEvents.Built)
				.GroupBy(e => e.Actor, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Select(e => e.Seconds).OrderBy(s => s).ToList(),
					StringComparer.OrdinalIgnoreCase);

			// One cursor for the whole match, walked in request order across every queue.
			//
			// The cursor has to be shared, because two orders must never claim the same completed
			// actor. But sharing it while processing one whole queue before the next starves the
			// later queue of any delivery that happened during the earlier queue's requests —
			// which is reachable whenever two queues can build the same actor type. Walking every
			// order chronologically keeps the no-double-claim guarantee and removes the ordering
			// artefact with it.
			var claimed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var states = new Dictionary<string, QueueState>(StringComparer.OrdinalIgnoreCase);

			var orders = trace.UnitDecisions
				.Where(d => !string.IsNullOrEmpty(d.Queue) &&
					string.Equals(d.Action, "Produce", StringComparison.OrdinalIgnoreCase))
				.OrderBy(d => d.Seconds)
				.ToList();

			foreach (var request in orders)
			{
				if (!states.TryGetValue(request.Queue, out var state))
				{
					state = new QueueState
					{
						Summary = new QueueSummary { Queue = request.Queue },
						Settled = request.Seconds
					};

					states[request.Queue] = state;
				}

				state.Summary.OrdersIssued++;

				// Idle means the queue had nothing outstanding and was asked for nothing. An
				// order that was issued and never delivered leaves the queue occupied, not idle:
				// counting that gap as idle would report a blocked queue as a lazy one, which is
				// the opposite diagnosis.
				if (state.Outstanding.Count == 0 && request.Seconds > state.Settled)
					state.Summary.SecondsIdle += request.Seconds - state.Settled;

				if (!string.IsNullOrEmpty(request.ItemName))
				{
					if (!state.Order.Contains(request.ItemName, StringComparer.OrdinalIgnoreCase))
						state.Order.Add(request.ItemName);

					state.Summary.CreditsOrdered += rules.CostOf(request.ItemName);

					if (!state.Outstanding.Add(request.ItemName))
						state.Summary.RepeatedRequests++;
				}

				// The first completion of this item at or after the request is the one this order
				// produced, and it is consumed so nothing else can claim it.
				var wait = Deliver(produced, claimed, request.ItemName, request.Seconds);
				if (wait == null)
					continue;

				state.Summary.Delivered++;
				state.Summary.SecondsWaiting += wait.Value;
				state.Outstanding.Remove(request.ItemName);
				state.Settled = Math.Max(state.Settled, request.Seconds + wait.Value);
			}

			foreach (var state in states.Values)
			{
				state.Summary.DistinctItems = state.Order.Count;
				state.Summary.ItemsInPlanOrder = state.Order.ToArray();
				state.Summary.DeepestPlanStepIndex = state.Order.Count - 1;
				state.Summary.DeepestItem = state.Order.Count > 0 ? state.Order[^1] : null;
			}

			return states.Values.Select(s => s.Summary)
				.OrderByDescending(s => s.OrdersIssued)
				.ThenBy(s => s.Queue, StringComparer.Ordinal)
				.ToList();
		}

		/// <summary>One queue's running state while the match's orders are walked in time order.</summary>
		sealed class QueueState
		{
			public QueueSummary Summary { get; init; }
			public List<string> Order { get; } = [];
			public HashSet<string> Outstanding { get; } = new(StringComparer.OrdinalIgnoreCase);

			/// <summary>When this queue last had nothing left to deliver.</summary>
			public int Settled { get; set; }
		}

		static int? Deliver(Dictionary<string, List<int>> produced, Dictionary<string, int> cursor,
			string item, int requestedAt)
		{
			if (string.IsNullOrEmpty(item) || !produced.TryGetValue(item, out var times))
				return null;

			var start = cursor.GetValueOrDefault(item);
			for (var i = start; i < times.Count; i++)
			{
				if (times[i] < requestedAt)
					continue;

				cursor[item] = i + 1;
				return times[i] - requestedAt;
			}

			return null;
		}

		/// <summary>
		/// Every doctrine the bot ran, with what its army was worth going in and coming out.
		/// </summary>
		static List<DoctrineEpisode> Doctrines(DecisionTrace trace, BattleEvents battle,
			List<TelemetrySample> own, int seconds, GameRules rules)
		{
			var changes = trace.DoctrineChanges.OrderBy(c => c.Seconds).ToList();
			if (changes.Count == 0)
				foreach (var e in battle.Of(BattleEvents.Doctrine).OrderBy(e => e.Seconds))
					changes.Add(new DoctrineChangeRecord
					{
						Seconds = e.Seconds,
						From = Unword(e.DetailText("from")),
						To = Unword(e.DetailText("to")),
						Reason = Unword(e.DetailText("why"))
					});

			var episodes = new List<DoctrineEpisode>();
			for (var i = 0; i < changes.Count; i++)
			{
				var entry = changes[i].Seconds;
				var exit = i + 1 < changes.Count ? changes[i + 1].Seconds : (int?)null;
				var end = exit ?? seconds;

				episodes.Add(new DoctrineEpisode
				{
					From = changes[i].From,
					To = changes[i].To,
					Reason = changes[i].Reason,
					EntrySeconds = entry,
					ExitSeconds = exit,
					DurationSeconds = Math.Max(0, end - entry),
					ArmyValueAtEntry = ArmyAt(own, entry),
					ArmyValueAtExit = ArmyAt(own, end),
					CreditsKilledDuring = Between(battle.KillsBy(battle.LocalPlayer), entry, end, rules),
					CreditsLostDuring = Between(battle.LossesOf(battle.LocalPlayer), entry, end, rules)
				});
			}

			return episodes;
		}

		static string Unword(string value) => value?.Replace('_', ' ');

		static int Between(IEnumerable<BattleEvent> events, int from, int to, GameRules rules) =>
			events.Where(e => e.Seconds >= from && e.Seconds < to)
				.Sum(e => e.DetailInt("value") > 0 ? e.DetailInt("value") : rules.CostOf(e.Actor));

		static int ArmyAt(List<TelemetrySample> own, int seconds)
		{
			var best = 0;
			foreach (var sample in own)
			{
				if (sample.Seconds > seconds)
					break;

				best = sample.Army;
			}

			return best;
		}

		/// <summary>Own actor type against enemy actor type, both directions, in one matrix.</summary>
		static List<Engagement> EngagementList(BattleEvents battle, string local)
		{
			var matrix = new Dictionary<(string Own, string Enemy), Engagement>();

			Engagement Pair(string own, string enemy)
			{
				if (string.IsNullOrEmpty(own) || string.IsNullOrEmpty(enemy))
					return null;

				var key = (own, enemy);
				if (!matrix.TryGetValue(key, out var engagement))
				{
					engagement = new Engagement { Own = own, Enemy = enemy };
					matrix[key] = engagement;
				}

				return engagement;
			}

			foreach (var e in battle.Events)
			{
				switch (e.Kind)
				{
					case BattleEvents.Attacked when string.Equals(e.Player, local, StringComparison.Ordinal):
					{
						var pair = Pair(e.Actor, e.OtherActor);
						if (pair != null)
							pair.DamageTaken += e.DetailInt("damage");

						break;
					}

					case BattleEvents.Dealt when string.Equals(e.Player, local, StringComparison.Ordinal):
					{
						var pair = Pair(e.Actor, e.OtherActor);
						if (pair != null)
							pair.DamageDealt += e.DetailInt("damage");

						break;
					}

					// Victim's point of view: the killer is ours and sits in otheractor.
					case BattleEvents.Killed when string.Equals(e.OtherPlayer, local, StringComparison.Ordinal):
					{
						var pair = Pair(e.OtherActor, e.Actor);
						if (pair != null)
							pair.Kills++;

						break;
					}

					case BattleEvents.Lost when string.Equals(e.Player, local, StringComparison.Ordinal):
					{
						var pair = Pair(e.Actor, e.OtherActor);
						if (pair != null)
							pair.Deaths++;

						break;
					}
				}
			}

			return matrix.Values
				.OrderByDescending(e => e.DamageDealt + e.DamageTaken + 1000 * (e.Kills + e.Deaths))
				.ThenBy(e => e.Own, StringComparer.Ordinal)
				.ToList();
		}

		static Table Engagements(BattleEvents battle, string local) =>
			Table.Of(EngagementList(battle, local),
				["own", "enemy", "damageDealt", "damageTaken", "kills", "deaths"],
				e => [e.Own, e.Enemy, e.DamageDealt, e.DamageTaken, e.Kills, e.Deaths]);

		/// <summary>
		/// Where this side kept dying, found by greedy agglomeration around the busiest cell.
		/// </summary>
		/// <remarks>
		/// Greedy rather than k-means because the question is "where did a stream of units walk
		/// into something", which has no fixed number of answers and wants the densest place
		/// first. Ordering by credits lost rather than by body count keeps a cluster of four
		/// harvesters ahead of a cluster of five riflemen.
		/// </remarks>
		static List<LossCluster> LossClusters(BattleEvents battle, string local, GameRules rules)
		{
			var losses = battle.LossesOf(local)
				.Where(e => e.X != null && e.Y != null)
				.Select(e => (e.Seconds, X: e.X.Value, Y: e.Y.Value, e.Actor,
					Value: e.DetailInt("value") > 0 ? e.DetailInt("value") : rules.CostOf(e.Actor)))
				.ToList();

			var clusters = new List<LossCluster>();

			while (losses.Count > 0)
			{
				var seed = losses
					.OrderByDescending(a => losses.Count(b => Near(a.X, a.Y, b.X, b.Y)))
					.First();

				var members = losses.Where(b => Near(seed.X, seed.Y, b.X, b.Y)).ToList();
				losses.RemoveAll(b => Near(seed.X, seed.Y, b.X, b.Y));

				clusters.Add(new LossCluster
				{
					X = (int)Math.Round(members.Average(m => (double)m.X)),
					Y = (int)Math.Round(members.Average(m => (double)m.Y)),
					RadiusCells = (int)Math.Ceiling(members.Max(m =>
						Math.Sqrt(Math.Pow(m.X - seed.X, 2) + Math.Pow(m.Y - seed.Y, 2)))),
					Count = members.Count,
					CreditsLost = members.Sum(m => m.Value),
					FromSeconds = members.Min(m => m.Seconds),
					ToSeconds = members.Max(m => m.Seconds),
					Types = members.Select(m => m.Actor).Distinct(StringComparer.OrdinalIgnoreCase)
						.OrderBy(t => t, StringComparer.Ordinal).ToArray()
				});
			}

			return clusters.OrderByDescending(c => c.CreditsLost).ToList();
		}

		static bool Near(int ax, int ay, int bx, int by)
		{
			var dx = ax - bx;
			var dy = ay - by;
			return dx * dx + dy * dy <= LossClusterRadiusCells * LossClusterRadiusCells;
		}

		static SummaryMap MapSummary(EvidenceSet evidence, BattleEvents battle)
		{
			var facts = evidence.Map;

			// A coarse proxy, and named as one: the distinct cells this side's own log ever
			// reported an event at. It cannot see where a unit merely walked, but it separates a
			// bot that operated across the map from one that never left its own corner.
			var explored = new HashSet<(int, int)>();
			foreach (var e in battle.Events)
				if (e.X != null && e.Y != null)
					explored.Add((e.X.Value, e.Y.Value));

			return new SummaryMap
			{
				Name = facts?.Map ?? evidence.Manifest.Map,
				WidthCells = facts?.WidthCells ?? 0,
				HeightCells = facts?.HeightCells ?? 0,
				LocalSpawn = facts?.LocalSpawn,
				EnemySpawns = facts?.EnemySpawns?.ToArray() ?? [],
				HomeToNearestEnemyCells = facts?.HomeToNearestEnemyCells,
				ResourceCells = facts?.ResourceCells ?? 0,
				ExploredCells = explored.Count
			};
		}

		static SummaryHeadline Headline(FightSummary summary, List<TelemetrySample> own,
			List<TelemetrySample> theirs, List<UnitRecord> ours, List<QueueSummary> queues,
			DecisionTrace trace, int seconds, GameRules rules)
		{
			var last = own.Count > 0 ? own[^1] : null;
			var creditsKilled = ours.Sum(u => u.CreditsKilled);
			var creditsLost = ours.Where(u => u.DiedSeconds != null).Sum(u => u.Cost);

			var meanArmy = own.Count > 0 ? own.Average(s => (double)s.Army) : 0d;

			return new SummaryHeadline
			{
				Outcome = summary.Fight.Outcome,
				DurationSeconds = seconds,
				CreditsEarned = summary.Economy.CreditsEarned,
				CreditsSpent = summary.Economy.CreditsSpent,
				CreditsEarnedPerSecond = Rate(summary.Economy.CreditsEarned, seconds),
				CreditsSpentPerSecond = Rate(summary.Economy.CreditsSpent, seconds),

				// Null would be truthful for "lost nothing", but a ratio that reads as an enormous
				// number when a bot loses nothing is more misleading than one that reads as the
				// credits it destroyed, so an unopposed match scores its kills outright.
				ValueExchangeRatio = creditsLost > 0
					? Math.Round((double)creditsKilled / creditsLost, 3)
					: creditsKilled,

				CreditsKilled = creditsKilled,
				CreditsLost = creditsLost,
				UnitsKilled = Peak(own, s => s.Killed),
				UnitsLost = Peak(own, s => s.Lost),
				BuildingsKilled = Peak(own, s => s.BuildingsKilled),
				BuildingsLost = Peak(own, s => s.BuildingsLost),
				PeakArmyValue = own.Count > 0 ? own.Max(s => s.Army) : 0,
				MeanArmyValue = Math.Round(meanArmy, 1),
				ArmyValueIntegral = Math.Round(meanArmy * seconds, 1),
				PeakHarvesters = summary.Economy.PeakHarvesters,
				Refineries = trace.Assessments.Count > 0 ? trace.Assessments.Max(a => a.Refineries) : 0,
				CellsExplored = summary.Map?.ExploredCells ?? 0,
				DistinctEnemyActorsSeen = summary.Engagements.Rows
					.Select(r => Csv.SplitLine(r)).Where(c => c.Length > 1).Select(c => c[1])
					.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
				DoctrineChanges = summary.DoctrineEpisodes.Count,
				ProductionOrders = queues.Sum(p => p.OrdersIssued),
				IdleUnitSeconds = ours.Sum(u => u.SecondsIdle)
			};
		}

		static int Peak(List<TelemetrySample> own, Func<TelemetrySample, int> value) =>
			own.Count > 0 ? own.Max(value) : 0;

		static double Rate(int total, int seconds) =>
			seconds > 0 ? Math.Round((double)total / seconds, 3) : 0d;

		/// <summary>
		/// Says out loud the things a reader would otherwise have to notice, including the ones
		/// that mean a number below is missing rather than zero.
		/// </summary>
		static void Note(FightSummary summary, List<UnitTypeLedger> unitTypes, DecisionTrace trace,
			GameRules rules)
		{
			if (!summary.Provenance.HasEconomyFlows)
				summary.Notes.Add(
					"telemetry.csv predates the earned/spent columns, so every credits-per-second " +
					"figure here is zero because it is unknown, not because nothing was earned.");

			if (!summary.Provenance.HasDamageDealt)
				summary.Notes.Add(
					"battle.csv predates the 'dealt' event, so damageDealt is only known for units " +
					"that died and reported a total.");

			if (!rules.KnowsFreeActors)
				summary.Notes.Add(
					"game-rules.json predates the freeActors field, so free actors are NOT excluded " +
					"from spend. Do not compare creditsPerKill against a run that had it.");

			if (summary.Headline.BuildingsKilled == 0)
				summary.Notes.Add(
					"This side destroyed no enemy buildings all match: the army never finished a " +
					"structure, whatever else the attack did.");

			if (summary.Economy.SecondsWithNoHarvester > 60)
				summary.Notes.Add(
					$"{summary.Economy.SecondsWithNoHarvester}s of the match had no live harvester.");

			if (trace.Errors.Count > 0)
				summary.Notes.Add($"The decision trace recorded {trace.Errors.Count} error(s).");

			if (summary.Provenance.FreeActorExclusion == "ruleset" && unitTypes.Any(u => u.FreeCount > 0))
				summary.Notes.Add(
					"Granted free and excluded from creditsSpent: " +
					string.Join(", ", unitTypes.Where(u => u.FreeCount > 0)
						.Select(u => $"{u.FreeCount}x {u.Type}")) +
					". Instances of the same type that were ordered are still charged.");
		}

		/// <summary>
		/// Serialises, trimming the longest lists until the result fits in one file read.
		/// </summary>
		/// <remarks>
		/// Order of sacrifice is deliberate: the engagement matrix and the loss clusters are long
		/// tails whose first entries carry nearly all the meaning, the economy series thins without
		/// losing its shape, and the per-unit-type ledger is trimmed last because it is the thing
		/// most rounds actually came for. Whatever goes is recorded in <c>truncated</c>, so a
		/// reader is never left guessing whether a short list is short or cut.
		/// </remarks>
		/// <summary>
		/// Serialises, trimming the longest tables until the result fits in one file read.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Order of sacrifice is deliberate: the engagement matrix and the loss clusters are long
		/// tails whose first rows carry nearly all the meaning, the economy series thins without
		/// losing its shape, and the per-unit-type ledger is trimmed last because it is the thing
		/// most rounds actually came for. Whatever goes is recorded in <c>truncated</c>, so a
		/// reader is never left guessing whether a short table is short or cut.
		/// </para>
		/// <para>
		/// The final clamp is not decoration. A ladder of fixed steps can run out while the
		/// document is still too large — a match with hundreds of distinct actor types would do
		/// it — and returning an oversized file at that point would quietly defeat the one
		/// guarantee this artifact makes. So the last stage halves every table repeatedly until
		/// it fits, and gives up only when there is nothing left to halve.
		/// </para>
		/// </remarks>
		public static string Serialise(FightSummary summary)
		{
			// The writer appends a trailing newline, which counts against the same ceiling.
			var budget = MaxBytes - 1;

			var text = JsonSerializer.Serialize(summary, JsonOptions);
			if (Encoding.UTF8.GetByteCount(text) <= budget)
				return text;

			foreach (var (table, keep, name) in new[]
			{
				(summary.Engagements, 24, "engagements"),
				(summary.LossClusters, 8, "lossClusters"),
				(summary.Economy?.Series, 20, "economy.series"),
				(summary.DoctrineEpisodes, 12, "doctrineEpisodes"),
				(summary.UnitTypes, 20, "unitTypes"),
				(summary.Engagements, 8, "engagements"),
				(summary.LossClusters, 3, "lossClusters"),
				(summary.Economy?.Series, 10, "economy.series"),
				(summary.UnitTypes, 12, "unitTypes")
			})
			{
				if (table?.Trim(keep, summary.Truncated, name) != true)
					continue;

				text = JsonSerializer.Serialize(summary, JsonOptions);
				if (Encoding.UTF8.GetByteCount(text) <= budget)
					return text;
			}

			var tables = new (Table Table, string Name)[]
			{
				(summary.Engagements, "engagements"),
				(summary.LossClusters, "lossClusters"),
				(summary.Economy?.Series, "economy.series"),
				(summary.DoctrineEpisodes, "doctrineEpisodes"),
				(summary.Production, "production"),
				(summary.UnitTypes, "unitTypes")
			};

			while (Encoding.UTF8.GetByteCount(text) > budget)
			{
				var trimmed = false;
				foreach (var (table, name) in tables)
				{
					if (table == null || table.Count == 0)
						continue;

					trimmed |= table.Trim(table.Count / 2, summary.Truncated, name);
				}

				if (!trimmed)
					break;

				text = JsonSerializer.Serialize(summary, JsonOptions);
			}

			return text;
		}

		public static void Write(string path, FightSummary summary)
		{
			var full = Path.GetFullPath(path);
			System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllText(full, Serialise(summary) + "\n", new UTF8Encoding(false));
		}

		public static string Describe(FightSummary summary) =>
			string.Create(CultureInfo.InvariantCulture,
				$"{summary.Fight.Outcome} in {summary.Fight.DurationSeconds}s on {summary.Fight.Map}; " +
				$"fitness {summary.Fitness.Total:0.###}; " +
				$"{summary.Headline.CreditsEarnedPerSecond:0.#} cr/s earned, " +
				$"{summary.Headline.CreditsSpentPerSecond:0.#} cr/s spent; " +
				$"exchange {summary.Headline.ValueExchangeRatio:0.##}; " +
				$"buildings killed {summary.Headline.BuildingsKilled}");
	}
}
