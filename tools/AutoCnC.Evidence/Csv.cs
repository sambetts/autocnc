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
using System.Text;

namespace AutoCnC.Evidence
{
	/// <summary>
	/// The smallest CSV reader and writer that can round-trip what the exporters write.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Deliberately not a dependency. The exporters quote with the one rule in
	/// <c>BattleLog.Escape</c> — double the quotes, wrap the field — and a general-purpose parser
	/// would bring its own opinions about headers, type inference and blank lines, none of which
	/// this format has. Reading it here is thirty lines and cannot drift from the writer.
	/// </para>
	/// <para>
	/// Column lookup is by name rather than index, which is what lets an analysis keep working
	/// when a later schema appends columns: the whole evidence contract is append-only, so a name
	/// that was there before is still there, and one that is missing means an older run rather
	/// than a broken file.
	/// </para>
	/// </remarks>
	public static class Csv
	{
		/// <summary>Splits one CSV line, honouring doubled quotes inside a quoted field.</summary>
		public static string[] SplitLine(string line)
		{
			var fields = new List<string>();
			var field = new StringBuilder();
			var quoted = false;

			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];

				if (quoted)
				{
					if (c != '"')
					{
						field.Append(c);
						continue;
					}

					// A doubled quote inside a quoted field is one literal quote.
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						field.Append('"');
						i++;
						continue;
					}

					quoted = false;
					continue;
				}

				if (c == '"' && field.Length == 0)
				{
					quoted = true;
					continue;
				}

				if (c == ',')
				{
					fields.Add(field.ToString());
					field.Clear();
					continue;
				}

				field.Append(c);
			}

			fields.Add(field.ToString());
			return fields.ToArray();
		}

		/// <summary>Quotes a value only when it would otherwise break the row.</summary>
		public static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "";

			if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
				return value;

			return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
		}

		/// <summary>Reads a file as header plus rows, skipping any blank line.</summary>
		public static List<string[]> ReadRows(string path, out string[] header)
		{
			header = [];
			var rows = new List<string[]>();
			if (!File.Exists(path))
				return rows;

			var lines = File.ReadAllLines(path);
			if (lines.Length == 0)
				return rows;

			header = SplitLine(lines[0]);
			for (var i = 1; i < lines.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(lines[i]))
					continue;

				rows.Add(SplitLine(lines[i]));
			}

			return rows;
		}

		/// <summary>Maps header names to their column index, case-insensitively.</summary>
		public static Dictionary<string, int> Index(string[] header)
		{
			var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < header.Length; i++)
				index[header[i]] = i;

			return index;
		}

		/// <summary>
		/// Reads one field by name, or empty when this run predates the column.
		/// </summary>
		/// <remarks>
		/// The missing-column case is the normal one rather than an error: evidence is append-only
		/// across schema versions, so every artifact written before a column existed is still a
		/// valid input and simply has nothing to say about it.
		/// </remarks>
		public static string Field(string[] row, Dictionary<string, int> index, string name)
		{
			if (!index.TryGetValue(name, out var i) || i >= row.Length)
				return "";

			return row[i];
		}

		public static int Int(string[] row, Dictionary<string, int> index, string name) =>
			int.TryParse(Field(row, index, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
				? value
				: 0;

		public static int? OptionalInt(string[] row, Dictionary<string, int> index, string name) =>
			int.TryParse(Field(row, index, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
				? value
				: null;

		/// <summary>
		/// Parses a <c>detail</c> field: space-separated <c>key=value</c> pairs.
		/// </summary>
		/// <remarks>
		/// The writer replaces spaces inside a value with underscores precisely so this split is
		/// safe, so a value that comes back with underscores in it is prose that had spaces.
		/// </remarks>
		public static Dictionary<string, string> Details(string detail)
		{
			var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrWhiteSpace(detail))
				return details;

			foreach (var pair in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var split = pair.IndexOf('=');
				if (split <= 0)
					continue;

				details[pair[..split]] = pair[(split + 1)..];
			}

			return details;
		}

		public static int DetailInt(Dictionary<string, string> details, string key) =>
			details.TryGetValue(key, out var value) &&
			int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
				? parsed
				: 0;

		public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

		public static string Number(double value) =>
			Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
	}
}
