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
using System.Drawing;
using System.IO;
using System.Linq;

namespace AutoCnC.Launcher
{
	/// <summary>One thing that happened, from the point of view of the side it happened to.</summary>
	/// <remarks>
	/// Both ends are named: <see cref="Player"/> owns <see cref="Actor"/> and
	/// <see cref="OtherPlayer"/> owns <see cref="OtherActor"/>. In a three-way match that is the
	/// difference between a readable log and a list of unit names.
	/// </remarks>
	public readonly record struct BattleEvent(
		int Seconds,
		string Kind,
		string Player,
		string Actor,
		string ActorId,
		string OtherPlayer,
		string OtherActor,
		string OtherActorId,
		string Where,
		string Detail)
	{
		/// <summary>A unit named so two of the same type can be told apart.</summary>
		public static string Name(string actor, string id) =>
			actor.Length == 0 ? "" : id.Length == 0 ? actor : $"{actor} #{id}";
	}

	/// <summary>One side of the match, as the battle log introduced it.</summary>
	public sealed class BattleSide
	{
		public string Name { get; init; }
		public string Faction { get; init; }
		public bool IsBot { get; init; }
		public Color Colour { get; init; }

		/// <summary>How this player stands to the one whose log this is: you, enemy or ally.</summary>
		public string Side { get; init; }

		public bool IsYou => string.Equals(Side, "you", StringComparison.OrdinalIgnoreCase);

		public string Label
		{
			get
			{
				var faction = string.IsNullOrEmpty(Faction) || Faction == "?" ? null : Faction.ToUpperInvariant();
				var parts = new List<string>();

				if (faction != null)
					parts.Add(faction);

				if (IsBot)
					parts.Add("bot");

				if (IsYou)
					parts.Add("you");

				return parts.Count == 0 ? Name : $"{Name} — {string.Join(", ", parts)}";
			}
		}
	}

	/// <summary>
	/// Reads the battle log the running match is writing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The file is the local player's point of view and nothing more: enemies coming into view,
	/// hits taken, units lost and killed. It is deliberately not a record of the match — the
	/// telemetry graph is that — because the useful question about a doctrine is not what was
	/// true, it is what the code knew when it decided. See the BattleLog trait.
	/// </para>
	/// <para>
	/// Re-read whole whenever the file grows, for the same reason <see cref="MatchLog"/> is: a
	/// long match is a few hundred kilobytes, which is far cheaper than the partial-line and
	/// rotation bugs that tailing invites. The events list only ever grows, so a view can render
	/// the new tail and leave what it has already drawn alone.
	/// </para>
	/// </remarks>
	public sealed class BattleEventLog
	{
		readonly List<BattleEvent> events = [];
		readonly List<BattleSide> sides = [];
		readonly Dictionary<string, Color> colours = new(StringComparer.Ordinal);

		long lastLength = -1;

		public string Path { get; private set; }

		public IReadOnlyList<BattleEvent> Events => events;

		public IReadOnlyList<BattleSide> Sides => sides;

		/// <summary>True when the last refresh replaced what was there rather than adding to it.</summary>
		public bool Restarted { get; private set; }

		public void Watch(string path)
		{
			Path = path;
			events.Clear();
			sides.Clear();
			colours.Clear();
			lastLength = -1;
			Restarted = true;
		}

		/// <summary>The colour the game gave a player, for drawing their events in.</summary>
		public Color ColourOf(string player) =>
			player != null && colours.TryGetValue(player, out var colour) ? colour : Color.Gainsboro;

		/// <summary>Re-reads the file if it has grown. True when there is something new to show.</summary>
		public bool Refresh()
		{
			Restarted = false;

			if (string.IsNullOrEmpty(Path))
				return false;

			try
			{
				var file = new FileInfo(Path);
				if (!file.Exists)
				{
					if (events.Count == 0 && sides.Count == 0)
						return false;

					Watch(Path);
					return true;
				}

				if (file.Length == lastLength)
					return false;

				// A shorter file is a new match in the same place, so whatever a view has already
				// drawn is about a battle that is over. Say so, rather than appending to it.
				Restarted = file.Length < lastLength;
				lastLength = file.Length;

				Parse(Csv.ReadShared(Path));
				return true;
			}
			catch (IOException)
			{
				// The game is mid-write, or rotating the file. Try again on the next tick.
				return false;
			}
		}

		void Parse(string text)
		{
			var parsed = new List<BattleEvent>();
			var roster = new List<BattleSide>();
			var palette = new Dictionary<string, Color>(StringComparer.Ordinal);

			var lines = text.Split('\n');
			if (lines.Length == 0)
				return;

			var columns = Csv.Columns(lines[0].TrimEnd('\r'));
			if (!columns.ContainsKey("seconds") || !columns.ContainsKey("event"))
				return;

			foreach (var line in lines.Skip(1))
			{
				var fields = Csv.Split(line.TrimEnd('\r'));
				if (fields.Length < 3)
					continue;

				string Field(string column) =>
					columns.TryGetValue(column, out var index) && index < fields.Length ? fields[index] : "";

				if (!int.TryParse(Field("seconds"), out var seconds))
					continue;

				var kind = Field("event");
				var player = Field("player");

				// The roster rows the log opens with, so a log read a week later still says who
				// was fighting whom rather than leaving it to be guessed from the unit names.
				if (kind == "player")
				{
					var facts = Facts(Field("detail"));
					var colour = Csv.Rgb(facts.GetValueOrDefault("colour"));

					roster.Add(new BattleSide
					{
						Name = player,
						Faction = facts.GetValueOrDefault("faction"),
						IsBot = facts.GetValueOrDefault("bot") == "1",
						Side = facts.GetValueOrDefault("side"),
						Colour = colour
					});

					palette[player] = colour;
					continue;
				}

				var x = Field("x");
				var y = Field("y");

				parsed.Add(new BattleEvent(
					seconds,
					kind,
					player,
					Field("actor"),
					Field("actorid"),
					Field("otherplayer"),
					Field("otheractor"),
					Field("otheractorid"),

					// Bracketed, or a map cell reads as a number somebody wrote with a comma.
					x.Length > 0 && y.Length > 0 ? $"({x},{y})" : "",
					Field("detail")));
			}

			// A view renders the tail, so the list must only ever grow between refreshes. It does:
			// the file is append-only, and a file that shrank has already set Restarted.
			if (Restarted || parsed.Count < events.Count)
			{
				Restarted = true;
				events.Clear();
			}

			events.AddRange(parsed.Skip(events.Count));

			sides.Clear();
			sides.AddRange(roster);

			colours.Clear();
			foreach (var pair in palette)
				colours[pair.Key] = pair.Value;
		}

		/// <summary>Splits a detail field's <c>key=value</c> pairs.</summary>
		static Dictionary<string, string> Facts(string detail)
		{
			var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach (var pair in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var split = pair.IndexOf('=', StringComparison.Ordinal);
				if (split > 0)
					facts[pair[..split]] = pair[(split + 1)..];
			}

			return facts;
		}
	}
}
