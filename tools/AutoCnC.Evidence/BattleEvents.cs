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
using System.Linq;

namespace AutoCnC.Evidence
{
	/// <summary>One row of <c>battle.csv</c>, with its <c>detail</c> already split out.</summary>
	/// <remarks>
	/// <para>
	/// The two ends of an event are named rather than numbered — <see cref="Player"/> owns
	/// <see cref="Actor"/> and <see cref="OtherPlayer"/> owns <see cref="OtherActor"/> — because
	/// the orientation is not the same on every row and assuming it is has caused real analysis
	/// bugs. In particular a <c>killed</c> row carries the <em>victim's</em> owner in
	/// <see cref="Player"/>, so the local bot's kills are the rows where
	/// <see cref="OtherPlayer"/> is the local bot, not the ones where <see cref="Player"/> is.
	/// <see cref="BattleEvents"/> exposes that as a named query so no caller has to remember it.
	/// </para>
	/// </remarks>
	public sealed class BattleEvent
	{
		public int Seconds { get; init; }
		public string Kind { get; init; }
		public string Player { get; init; }
		public string Actor { get; init; }
		public uint ActorId { get; init; }
		public string OtherPlayer { get; init; }
		public string OtherActor { get; init; }
		public uint OtherActorId { get; init; }
		public int? X { get; init; }
		public int? Y { get; init; }
		public string Detail { get; init; }
		public Dictionary<string, string> Details { get; init; }

		public int DetailInt(string key) => Csv.DetailInt(Details, key);

		public string DetailText(string key) =>
			Details.TryGetValue(key, out var value) ? value : null;
	}

	/// <summary>Every event in one fight's <c>battle.csv</c>, plus who was playing.</summary>
	public sealed class BattleEvents
	{
		public const string Spotted = "spotted";
		public const string Attacked = "attacked";
		public const string Dealt = "dealt";
		public const string Built = "built";
		public const string Lost = "lost";
		public const string Killed = "killed";
		public const string Doctrine = "doctrine";
		public const string PlayerRoster = "player";
		public const string Over = "over";

		public List<BattleEvent> Events { get; } = [];

		/// <summary>The local bot, taken from the roster row that declares <c>side=you</c>.</summary>
		public string LocalPlayer { get; private set; }

		public Dictionary<string, RosterEntry> Roster { get; } = new(StringComparer.Ordinal);

		public sealed class RosterEntry
		{
			public string Name { get; init; }
			public string Faction { get; init; }
			public bool IsBot { get; init; }
			public string Side { get; init; }
			public string Colour { get; init; }
		}

		public IEnumerable<BattleEvent> Of(string kind) =>
			Events.Where(e => string.Equals(e.Kind, kind, StringComparison.Ordinal));

		/// <summary>
		/// The kills the local bot scored.
		/// </summary>
		/// <remarks>
		/// A <c>killed</c> row is written from the victim's point of view, so this filters on
		/// <see cref="BattleEvent.OtherPlayer"/>. Every analysis that filtered on
		/// <see cref="BattleEvent.Player"/> instead silently reported zero kills, which is why
		/// this exists as a method rather than as advice in a comment.
		/// </remarks>
		public IEnumerable<BattleEvent> KillsBy(string player) =>
			Of(Killed).Where(e => string.Equals(e.OtherPlayer, player, StringComparison.Ordinal));

		public IEnumerable<BattleEvent> LossesOf(string player) =>
			Of(Lost).Where(e => string.Equals(e.Player, player, StringComparison.Ordinal));

		public static BattleEvents Read(string path)
		{
			var events = new BattleEvents();
			var rows = Csv.ReadRows(path, out var header);
			if (header.Length == 0)
				return events;

			var index = Csv.Index(header);

			foreach (var row in rows)
			{
				var detail = Csv.Field(row, index, "detail");
				var kind = Csv.Field(row, index, "event");
				var record = new BattleEvent
				{
					Seconds = Csv.Int(row, index, "seconds"),
					Kind = kind,
					Player = Csv.Field(row, index, "player"),
					Actor = Csv.Field(row, index, "actor"),
					ActorId = Id(Csv.Field(row, index, "actorid")),
					OtherPlayer = Csv.Field(row, index, "otherplayer"),
					OtherActor = Csv.Field(row, index, "otheractor"),
					OtherActorId = Id(Csv.Field(row, index, "otheractorid")),
					X = Csv.OptionalInt(row, index, "x"),
					Y = Csv.OptionalInt(row, index, "y"),
					Detail = detail,
					Details = Csv.Details(detail)
				};

				events.Events.Add(record);

				if (!string.Equals(kind, PlayerRoster, StringComparison.Ordinal))
					continue;

				var side = record.DetailText("side");
				events.Roster[record.Player] = new RosterEntry
				{
					Name = record.Player,
					Faction = record.DetailText("faction"),
					IsBot = record.DetailInt("bot") == 1,
					Side = side,
					Colour = record.DetailText("colour")
				};

				if (string.Equals(side, "you", StringComparison.Ordinal))
					events.LocalPlayer = record.Player;
			}

			return events;
		}

		static uint Id(string value) => uint.TryParse(value, out var parsed) ? parsed : 0u;
	}
}
