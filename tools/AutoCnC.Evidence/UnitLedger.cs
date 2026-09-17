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

namespace AutoCnC.Evidence
{
	/// <summary>One unit's whole life, as the evidence saw it.</summary>
	public sealed class UnitRecord
	{
		public uint ActorId { get; set; }
		public string Type { get; set; }
		public int Cost { get; set; }
		public string Faction { get; set; }
		public string Owner { get; set; }
		public int? BornSeconds { get; set; }
		public int? DiedSeconds { get; set; }
		public int? LifetimeSeconds { get; set; }
		public string KillerActor { get; set; }
		public string KillerPlayer { get; set; }
		public int? DeathX { get; set; }
		public int? DeathY { get; set; }
		public int Kills { get; set; }
		public int CreditsKilled { get; set; }
		public int DamageDealt { get; set; }
		public int DamageTaken { get; set; }
		public int CellsTravelled { get; set; }
		public int SecondsIdle { get; set; }
		public string ModesUsed { get; set; } = "";
		public int DecisionCount { get; set; }
		public bool Free { get; set; }

		internal List<(int Seconds, int X, int Y)> Positions { get; } = [];
		internal SortedSet<string> Modes { get; } = new(StringComparer.Ordinal);
		internal List<int> DecisionSeconds { get; } = [];
	}

	/// <summary>
	/// Builds <c>units.csv</c>: one row per actor, covering its whole lifecycle.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This exists because counts lie. <c>killed</c> and <c>lost</c> weight a 100-credit rifleman
	/// and an 1,800-credit heavy tank identically, so a match can be lost on exchange value while
	/// every headline number says the trade was even. A row per unit, carrying what it cost and
	/// what it destroyed, turns "which unit type never survived anything" and "what did each type
	/// kill per credit" into one grouping each.
	/// </para>
	/// <para>
	/// The joins are the fiddly part and are done once here so no analysis has to do them again.
	/// Birth comes from <c>built</c>, death and killer from <c>lost</c>, damage taken from
	/// <c>attacked</c>, damage dealt from <c>dealt</c>, and modes and decision counts from the
	/// trace — which carries no position at all, so every coordinate in this file comes from the
	/// battle log.
	/// </para>
	/// <para>
	/// Enemy units appear too, but only where they were observed: a row exists for an enemy this
	/// side killed, because that kill is in the log, and its birth is unknown because watching an
	/// enemy factory is not something this side could do. Those rows have an empty
	/// <c>bornSeconds</c> rather than a guessed one.
	/// </para>
	/// </remarks>
	public static class UnitLedger
	{
		/// <summary>Schema of the emitted CSV. Bumped whenever a column is appended.</summary>
		public const int SchemaVersion = 1;

		/// <summary>
		/// A gap between one actor's consecutive decisions longer than this counts as idle.
		/// </summary>
		/// <remarks>
		/// A unit re-evaluates roughly every 1.4 game seconds and only emits a trace line when its
		/// decision changes, so short gaps mean "still doing the same thing" rather than "doing
		/// nothing". Fifteen seconds is far longer than any plausible run of unchanged intent and
		/// well short of the time it takes to cross a map, which makes it a reasonable line
		/// between busy and parked.
		/// </remarks>
		public const int IdleGapSeconds = 15;

		public const string Header =
			"actorId,type,cost,faction,owner,bornSeconds,diedSeconds,lifetimeSeconds,killerActor," +
			"killerPlayer,deathX,deathY,kills,creditsKilled,damageDealt,damageTaken,cellsTravelled," +
			"secondsIdle,modesUsed,decisionCount,free";

		public static List<UnitRecord> Build(BattleEvents battle, DecisionTrace trace, GameRules rules,
			int matchSeconds)
		{
			var units = new Dictionary<uint, UnitRecord>();
			var local = battle.LocalPlayer;

			UnitRecord Get(uint id, string type, string owner)
			{
				if (!units.TryGetValue(id, out var unit))
				{
					unit = new UnitRecord { ActorId = id };
					units[id] = unit;
				}

				if (!string.IsNullOrEmpty(type))
					unit.Type = type;

				if (!string.IsNullOrEmpty(owner))
					unit.Owner = owner;

				return unit;
			}

			foreach (var e in battle.Events)
			{
				switch (e.Kind)
				{
					case BattleEvents.Built:
					{
						if (e.ActorId == 0)
							break;

						var unit = Get(e.ActorId, e.Actor, e.Player);
						unit.BornSeconds = e.Seconds;
						Position(unit, e);
						break;
					}

					case BattleEvents.Lost:
					{
						if (e.ActorId == 0)
							break;

						var unit = Get(e.ActorId, e.Actor, e.Player);
						Death(unit, e);
						break;
					}

					case BattleEvents.Killed:
					{
						// Written from the victim's point of view: `player` owns the victim and
						// `otherplayer` owns the killer. The victim gets a row of its own, and the
						// killer — one of ours — gets the credit.
						if (e.ActorId != 0)
						{
							var victim = Get(e.ActorId, e.Actor, e.Player);
							Death(victim, e);
						}

						if (e.OtherActorId != 0)
						{
							var killer = Get(e.OtherActorId, e.OtherActor, e.OtherPlayer);
							killer.Kills++;
							killer.CreditsKilled += Value(e, rules);
						}

						break;
					}

					case BattleEvents.Attacked:
					{
						if (e.ActorId == 0)
							break;

						var victim = Get(e.ActorId, e.Actor, e.Player);
						victim.DamageTaken += e.DetailInt("damage");
						Position(victim, e);
						break;
					}

					case BattleEvents.Dealt:
					{
						// `player` owns the attacker on this row, which is the opposite of the
						// `killed` orientation above. Both are documented in BattleEvents.
						if (e.ActorId == 0)
							break;

						var attacker = Get(e.ActorId, e.Actor, e.Player);
						attacker.DamageDealt += e.DetailInt("damage");
						Position(attacker, e);
						break;
					}

					case BattleEvents.Spotted:
					{
						if (e.ActorId == 0)
							break;

						var seen = Get(e.ActorId, e.Actor, e.Player);
						Position(seen, e);
						break;
					}
				}
			}

			foreach (var decision in trace.UnitDecisions)
			{
				if (decision.ActorId == 0)
					continue;

				// A decision only ever concerns one of our own units, so an id the battle log
				// never mentioned is still ours — it simply never did anything loggable.
				var unit = Get(decision.ActorId, decision.Actor, local);
				unit.DecisionCount++;
				unit.DecisionSeconds.Add(decision.Seconds);

				if (!string.IsNullOrEmpty(decision.Mode))
					unit.Modes.Add(decision.Mode);
			}

			foreach (var unit in units.Values)
				Finish(unit, battle, rules, matchSeconds);

			MarkFreeActors(units.Values, trace, rules, local);

			return units.Values
				.OrderBy(u => u.BornSeconds ?? int.MaxValue)
				.ThenBy(u => u.ActorId)
				.ToList();
		}

		/// <summary>
		/// Decides which individual actors the game gave away, rather than which types it can.
		/// </summary>
		/// <remarks>
		/// <para>
		/// The ruleset is authoritative about which types arrive free — a refinery grants a
		/// harvester — but it cannot say which harvesters those were, and in the C&amp;C rules
		/// <c>harv</c> is also buildable for 1,100 credits. Marking the type free therefore zeroed
		/// every purchased harvester too, which is a large and silent error in exactly the figures
		/// this ledger exists to get right: credits per kill and share of lifetime spend.
		/// </para>
		/// <para>
		/// The decision trace settles it without guessing. A harvester this side paid for has a
		/// <c>Produce</c> order naming it; one the game handed over has none. So the count that
		/// was ordered is charged and the remainder is not — no time-window matching, no pairing a
		/// harvester's build second against a refinery's, and both halves of the judgement come
		/// from a record rather than an inference.
		/// </para>
		/// <para>
		/// The earliest instances are the ones treated as free, because a granted actor arrives
		/// with the structure that granted it and therefore precedes anything bought later.
		/// </para>
		/// </remarks>
		static void MarkFreeActors(IEnumerable<UnitRecord> units, DecisionTrace trace, GameRules rules,
			string local)
		{
			if (!rules.KnowsFreeActors)
				return;

			var ordered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (var decision in trace.UnitDecisions)
			{
				if (string.IsNullOrEmpty(decision.ItemName) ||
					!string.Equals(decision.Action, "Produce", StringComparison.OrdinalIgnoreCase))
					continue;

				ordered[decision.ItemName] = ordered.GetValueOrDefault(decision.ItemName) + 1;
			}

			var ours = units
				.Where(u => string.Equals(u.Owner, local, StringComparison.Ordinal))
				.Where(u => u.BornSeconds != null && rules.IsFree(u.Type));

			foreach (var group in ours.GroupBy(u => u.Type, StringComparer.OrdinalIgnoreCase))
			{
				var built = group.OrderBy(u => u.BornSeconds).ToList();
				var paid = ordered.GetValueOrDefault(group.Key);
				var free = Math.Max(0, built.Count - paid);

				for (var i = 0; i < free; i++)
					built[i].Free = true;
			}
		}

		static void Death(UnitRecord unit, BattleEvent e)
		{
			unit.DiedSeconds = e.Seconds;
			unit.KillerActor = e.OtherActor;
			unit.KillerPlayer = e.OtherPlayer;
			unit.DeathX = e.X;
			unit.DeathY = e.Y;
			Position(unit, e);

			// Schema 2 of the battle log fills these in. Older runs leave them empty, and the
			// ledger falls back to the ruleset for value and to the birth row for lifetime.
			var dealt = e.DetailInt("dealt");
			if (dealt > unit.DamageDealt)
				unit.DamageDealt = dealt;

			var life = e.DetailInt("life");
			if (life > 0 && unit.BornSeconds == null)
				unit.BornSeconds = Math.Max(0, e.Seconds - life);
		}

		static void Position(UnitRecord unit, BattleEvent e)
		{
			if (e.X == null || e.Y == null)
				return;

			var last = unit.Positions.Count > 0 ? unit.Positions[^1] : (Seconds: -1, X: int.MinValue, Y: int.MinValue);
			if (last.X == e.X.Value && last.Y == e.Y.Value)
				return;

			unit.Positions.Add((e.Seconds, e.X.Value, e.Y.Value));
		}

		static int Value(BattleEvent e, GameRules rules)
		{
			var stated = e.DetailInt("value");
			return stated > 0 ? stated : rules.CostOf(e.Actor);
		}

		static void Finish(UnitRecord unit, BattleEvents battle, GameRules rules, int matchSeconds)
		{
			unit.Cost = unit.Cost > 0 ? unit.Cost : rules.CostOf(unit.Type);

			if (battle.Roster.TryGetValue(unit.Owner ?? "", out var entry))
				unit.Faction = entry.Faction;

			if (unit.BornSeconds != null)
			{
				var end = unit.DiedSeconds ?? matchSeconds;
				unit.LifetimeSeconds = Math.Max(0, end - unit.BornSeconds.Value);
			}

			unit.ModesUsed = string.Join('|', unit.Modes);
			unit.CellsTravelled = Travelled(unit);
			unit.SecondsIdle = Idle(unit, matchSeconds);
		}

		/// <summary>
		/// Straight-line distance between the places this unit was actually observed.
		/// </summary>
		/// <remarks>
		/// A lower bound, and deliberately so. The decision trace carries no position, and the
		/// battle log only fixes a unit in place when something happens to it, so a unit that
		/// crossed the map untroubled and died at the far end reports one long leg rather than the
		/// path it walked. It is still the difference between an army that went somewhere and one
		/// that milled about outside its own base, which is the question being asked.
		/// </remarks>
		static int Travelled(UnitRecord unit)
		{
			var total = 0d;
			for (var i = 1; i < unit.Positions.Count; i++)
			{
				var dx = unit.Positions[i].X - unit.Positions[i - 1].X;
				var dy = unit.Positions[i].Y - unit.Positions[i - 1].Y;
				total += Math.Sqrt(dx * dx + dy * dy);
			}

			return (int)Math.Round(total);
		}

		/// <summary>
		/// Seconds this unit spent in gaps longer than <see cref="IdleGapSeconds"/> between its
		/// own consecutive decisions, including the gap from its last decision to its death.
		/// </summary>
		static int Idle(UnitRecord unit, int matchSeconds)
		{
			if (unit.DecisionCount == 0)
				return 0;

			var seconds = unit.DecisionSeconds;
			seconds.Sort();

			var idle = 0;
			for (var i = 1; i < seconds.Count; i++)
			{
				var gap = seconds[i] - seconds[i - 1];
				if (gap > IdleGapSeconds)
					idle += gap;
			}

			var end = unit.DiedSeconds ?? matchSeconds;
			var tail = end - seconds[^1];
			if (tail > IdleGapSeconds)
				idle += tail;

			return idle;
		}

		public static void Write(string path, IEnumerable<UnitRecord> units)
		{
			var text = new StringBuilder();
			text.Append(Header).Append('\n');

			foreach (var unit in units)
				text.Append(string.Join(',',
					Csv.Number((int)unit.ActorId),
					Csv.Escape(unit.Type),
					Csv.Number(unit.Cost),
					Csv.Escape(unit.Faction),
					Csv.Escape(unit.Owner),
					Optional(unit.BornSeconds),
					Optional(unit.DiedSeconds),
					Optional(unit.LifetimeSeconds),
					Csv.Escape(unit.KillerActor),
					Csv.Escape(unit.KillerPlayer),
					Optional(unit.DeathX),
					Optional(unit.DeathY),
					Csv.Number(unit.Kills),
					Csv.Number(unit.CreditsKilled),
					Csv.Number(unit.DamageDealt),
					Csv.Number(unit.DamageTaken),
					Csv.Number(unit.CellsTravelled),
					Csv.Number(unit.SecondsIdle),
					Csv.Escape(unit.ModesUsed),
					Csv.Number(unit.DecisionCount),
					unit.Free ? "1" : "0")).Append('\n');

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
			File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
		}

		static string Optional(int? value) =>
			value?.ToString(CultureInfo.InvariantCulture) ?? "";
	}
}
