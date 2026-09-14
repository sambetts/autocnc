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
using System.Globalization;
using System.IO;
using System.Linq;

namespace AutoCnC.Launcher
{
	/// <summary>One player's state at one moment of game time.</summary>
	public readonly record struct MatchSample(
		int Seconds,
		int Units,
		int Army,
		int Buildings,
		int BaseValue,
		int Assets,
		int Cash,
		int Killed,
		int Lost,
		string State);

	/// <summary>The high-water mark of everything a player can hold but also lose.</summary>
	public readonly record struct MatchPeak(int Units, int Army, int Buildings, int BaseValue);

	/// <summary>One side of the match, and how it went.</summary>
	public sealed class MatchPlayer
	{
		readonly List<MatchSample> samples = [];

		public string Name { get; init; }
		public bool IsBot { get; init; }
		public Color Colour { get; init; }

		public IReadOnlyList<MatchSample> Samples => samples;

		/// <summary>The engine's win state as of the last sample: Won, Lost or Undefined.</summary>
		public string Outcome => samples.Count == 0 ? null : samples[^1].State;

		/// <summary>
		/// The last sample taken while this player was still in the fight.
		/// </summary>
		/// <remarks>
		/// Defeat is not a quiet bookkeeping event: the engine destroys every actor a beaten
		/// player owns, and the match keeps running — and keeps being sampled — for as long as
		/// the survivors need to finish. Read the final row of a lost match and it says nothing
		/// but zero: no units, no army, no buildings, no base, whether the player was overrun in
		/// four minutes or held out for sixteen. This is the row before that happened, which is
		/// the one worth comparing, and for a winner it is the same picture as the last row.
		/// </remarks>
		public MatchSample LastContested { get; private set; }

		/// <summary>The most this player ever held, which losing it all afterwards cannot erase.</summary>
		public MatchPeak Peak { get; private set; }

		public bool HasResult => Decided(Outcome);

		public string Label => IsBot ? $"{Name} (bot)" : Name;

		internal void Add(MatchSample sample)
		{
			samples.Add(sample);
			Peak = new MatchPeak(
				Math.Max(Peak.Units, sample.Units),
				Math.Max(Peak.Army, sample.Army),
				Math.Max(Peak.Buildings, sample.Buildings),
				Math.Max(Peak.BaseValue, sample.BaseValue));

			// The first sample is kept regardless, so a player who is somehow already decided when
			// recording starts reports the figures it does have rather than a row of zeros.
			if (samples.Count == 1 || !Decided(sample.State))
				LastContested = sample;
		}

		/// <summary>True once the engine has settled this player's fate one way or the other.</summary>
		static bool Decided(string state) =>
			!string.IsNullOrEmpty(state) && !string.Equals(state, "Undefined", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Reads the CSV the running match is writing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A file rather than a socket because it is the only channel that costs the game nothing,
	/// survives the game exiting, and works just as well for a match somebody launched from the
	/// command line an hour ago. The writer flushes every line, so this is live enough to watch.
	/// </para>
	/// <para>
	/// The whole file is re-read whenever its length changes rather than tailed from an offset:
	/// a long match is a few hundred kilobytes, which is far cheaper than the partial-line and
	/// rotation bugs that tailing invites.
	/// </para>
	/// </remarks>
	public sealed class MatchLog
	{
		readonly List<MatchPlayer> players = [];

		long lastLength = -1;

		public string Path { get; private set; }

		public IReadOnlyList<MatchPlayer> Players => players;

		public bool IsEmpty => players.Count == 0;

		/// <summary>The last moment any player has a sample for, in seconds of game time.</summary>
		public int Duration => players.Count == 0 ? 0 : players.Max(p => p.Samples.Count > 0 ? p.Samples[^1].Seconds : 0);

		/// <summary>True once the engine has decided the match, so a result is worth showing.</summary>
		public bool IsDecided => players.Any(p => p.HasResult);

		public void Watch(string path)
		{
			Path = path;
			players.Clear();
			lastLength = -1;
		}

		/// <summary>Re-reads the file if it has grown. True when there is something new to draw.</summary>
		public bool Refresh()
		{
			if (string.IsNullOrEmpty(Path))
				return false;

			try
			{
				var file = new FileInfo(Path);
				if (!file.Exists)
				{
					if (players.Count == 0)
						return false;

					players.Clear();
					lastLength = -1;
					return true;
				}

				if (file.Length == lastLength)
					return false;

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
			players.Clear();

			var byName = new Dictionary<string, MatchPlayer>(StringComparer.Ordinal);
			var lines = text.Split('\n');
			if (lines.Length == 0)
				return;

			// Columns are looked up by name from the header rather than by position, so the game
			// can record something new without this window having to be rebuilt to match — and an
			// older file still reads correctly instead of silently plotting the wrong column.
			var columns = Csv.Columns(lines[0].TrimEnd('\r'));
			if (!columns.ContainsKey("seconds") || !columns.ContainsKey("player"))
				return;

			// Skip the header, and any final line the writer has not finished yet.
			foreach (var line in lines.Skip(1))
			{
				var fields = Csv.Split(line.TrimEnd('\r'));
				if (fields.Length < 3)
					continue;

				string Field(string column) =>
					columns.TryGetValue(column, out var index) && index < fields.Length ? fields[index] : "";

				if (!int.TryParse(Field("seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
					continue;

				var name = Field("player");
				if (!byName.TryGetValue(name, out var player))
				{
					player = new MatchPlayer
					{
						Name = name,
						IsBot = Field("bot") == "1",
						Colour = Csv.Rgb(Field("colour"))
					};

					byName.Add(name, player);
					players.Add(player);
				}

				player.Add(new MatchSample(
					seconds,
					Csv.Number(Field("units")),
					Csv.Number(Field("army")),
					Csv.Number(Field("buildings")),
					Csv.Number(Field("basevalue")),
					Csv.Number(Field("assets")),
					Csv.Number(Field("cash")),
					Csv.Number(Field("killed")),
					Csv.Number(Field("lost")),
					Field("state")));
			}
		}
	}
}
