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
using System.Text;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// Reading the CSVs a running match writes.
	/// </summary>
	/// <remarks>
	/// Both the match telemetry and the battle log are the same kind of file — a header, a row
	/// per record, appended and flushed by a process that is still running — so they read the
	/// same way. Columns are always looked up by name from the header rather than by position, so
	/// the game can record something new without this window having to be rebuilt to match, and
	/// an older file still reads correctly instead of silently showing the wrong column.
	/// </remarks>
	public static class Csv
	{
		/// <summary>Opens a file another process is actively writing to.</summary>
		public static string ReadShared(string path)
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}

		public static Dictionary<string, int> Columns(string header)
		{
			var fields = Split(header);
			var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			for (var i = 0; i < fields.Length; i++)
				columns[fields[i].Trim()] = i;

			return columns;
		}

		public static int Number(string value) =>
			int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

		/// <summary>A player colour, written as six hex digits.</summary>
		public static Color Rgb(string value)
		{
			if (value != null && value.Length == 6
				&& int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
				return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

			return Color.Gray;
		}

		/// <summary>Player names are whatever somebody typed, so a field can be quoted.</summary>
		public static string[] Split(string line)
		{
			if (line.Length == 0)
				return [];

			if (line.IndexOf('"', StringComparison.Ordinal) < 0)
				return line.Split(',');

			var fields = new List<string>();
			var field = new StringBuilder();
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

		/// <summary>Game time as a clock, which is how every other part of this window shows it.</summary>
		public static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
	}
}
