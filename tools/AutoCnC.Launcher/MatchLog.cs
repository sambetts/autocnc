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
		int Lost);

	/// <summary>One side of the match, and how it went.</summary>
	public sealed class MatchPlayer
	{
		public string Name { get; init; }
		public bool IsBot { get; init; }
		public Color Colour { get; init; }
		public List<MatchSample> Samples { get; } = [];

		public string Label => IsBot ? $"{Name} (bot)" : Name;
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
				Parse(ReadShared(Path));
				return true;
			}
			catch (IOException)
			{
				// The game is mid-write, or rotating the file. Try again on the next tick.
				return false;
			}
		}

		/// <summary>Opens a file another process is actively writing to.</summary>
		static string ReadShared(string path)
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
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
			var columns = Columns(lines[0].TrimEnd('\r'));
			if (!columns.ContainsKey("seconds") || !columns.ContainsKey("player"))
				return;

			// Skip the header, and any final line the writer has not finished yet.
			foreach (var line in lines.Skip(1))
			{
				var fields = SplitCsv(line.TrimEnd('\r'));
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
						Colour = ParseColour(Field("colour"))
					};

					byName.Add(name, player);
					players.Add(player);
				}

				player.Samples.Add(new MatchSample(
					seconds,
					Number(Field("units")),
					Number(Field("army")),
					Number(Field("buildings")),
					Number(Field("basevalue")),
					Number(Field("assets")),
					Number(Field("cash")),
					Number(Field("killed")),
					Number(Field("lost"))));
			}
		}

		static Dictionary<string, int> Columns(string header)
		{
			var fields = SplitCsv(header);
			var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			for (var i = 0; i < fields.Length; i++)
				columns[fields[i].Trim()] = i;

			return columns;
		}

		static int Number(string value) =>
			int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

		static Color ParseColour(string value)
		{
			if (value.Length == 6 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
				return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

			return Color.Gray;
		}

		/// <summary>Player names are whatever somebody typed, so a field can be quoted.</summary>
		static string[] SplitCsv(string line)
		{
			if (line.Length == 0)
				return [];

			if (line.IndexOf('"', StringComparison.Ordinal) < 0)
				return line.Split(',');

			var fields = new List<string>();
			var field = new System.Text.StringBuilder();
			var quoted = false;

			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];

				if (quoted)
				{
					if (c != '"')
						field.Append(c);
					else if (i + 1 < line.Length && line[i + 1] == '"')
						field.Append(line[++i]);
					else
						quoted = false;
				}
				else if (c == '"')
					quoted = true;
				else if (c == ',')
				{
					fields.Add(field.ToString());
					field.Clear();
				}
				else
					field.Append(c);
			}

			fields.Add(field.ToString());
			return [.. fields];
		}
	}
}
