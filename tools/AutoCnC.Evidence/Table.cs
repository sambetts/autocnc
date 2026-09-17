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
using System.Linq;

namespace AutoCnC.Evidence
{
	/// <summary>
	/// A named-column table, for the parts of the summary that are a series rather than a record.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Rows are comma-separated strings rather than JSON arrays, and that is the whole point.
	/// Indented JSON puts every array element on its own line, so a six-number row costs about a
	/// hundred and twenty bytes of brackets and whitespace to carry twenty bytes of fact. At one
	/// row per line the same table is a third of the size, which is the difference between a
	/// summary that carries its full per-unit-type ledger and one that has to trim it to fit in a
	/// single file read.
	/// </para>
	/// <para>
	/// It stays self-describing and stays readable: <see cref="Columns"/> names every position, and
	/// what a reader sees is an ordinary CSV table that happens to live inside a JSON document.
	/// Values are escaped by the same rule the CSV exporters use, so a doctrine reason with a
	/// comma in it cannot shift every column after it.
	/// </para>
	/// </remarks>
	public sealed class Table
	{
		public string[] Columns { get; set; } = [];

		/// <summary>One row per entry, comma-separated in <see cref="Columns"/> order.</summary>
		public List<string> Rows { get; set; } = [];

		public int Count => Rows.Count;

		public static Table Of<T>(IEnumerable<T> items, string[] columns, Func<T, object[]> row)
		{
			var table = new Table { Columns = columns };
			foreach (var item in items)
				table.Rows.Add(string.Join(',', row(item).Select(Cell)));

			return table;
		}

		/// <summary>
		/// Formats one value: invariant, trimmed of noise, and escaped if it could break the row.
		/// </summary>
		/// <remarks>
		/// Null becomes an empty cell rather than the string "null", because empty is how the rest
		/// of the evidence says "not applicable" — a unit type that killed nothing has no credits
		/// per kill, and a zero there would be a different and wrong claim.
		/// </remarks>
		static string Cell(object value) => value switch
		{
			null => "",
			bool flag => flag ? "1" : "0",
			double number => Csv.Escape(number.ToString("0.###", CultureInfo.InvariantCulture)),
			float number => Csv.Escape(number.ToString("0.###", CultureInfo.InvariantCulture)),
			IFormattable formattable => Csv.Escape(formattable.ToString(null, CultureInfo.InvariantCulture)),
			_ => Csv.Escape(value.ToString())
		};

		/// <summary>
		/// One row as a column-name lookup, found by matching <paramref name="key"/> against the
		/// first column.
		/// </summary>
		/// <remarks>
		/// This is what keeps the check language working now that rows are text: every table in
		/// the summary puts its natural key in the first column, so
		/// <c>summary.unitTypes[e1].creditsPerKill</c> still resolves.
		/// </remarks>
		public Dictionary<string, object> Row(string key)
		{
			foreach (var row in Rows)
			{
				var cells = Csv.SplitLine(row);
				if (cells.Length == 0 ||
					!string.Equals(cells[0], key, StringComparison.OrdinalIgnoreCase))
					continue;

				var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
				for (var i = 0; i < Columns.Length && i < cells.Length; i++)
					lookup[Columns[i]] = cells[i];

				return lookup;
			}

			return null;
		}

		/// <summary>Keeps the first <paramref name="keep"/> rows, reporting what went.</summary>
		/// <remarks>
		/// A table can be trimmed more than once as the budget tightens, so the note records the
		/// count this table started at rather than the count it happened to hold at the last
		/// trim — "kept 3 of 6" after two passes over an original 24 rows would understate what
		/// was lost, which is the one thing this note exists to prevent.
		/// </remarks>
		public bool Trim(int keep, Dictionary<string, string> truncated, string name)
		{
			if (Rows.Count <= keep)
				return false;

			var original = Rows.Count;
			if (truncated.TryGetValue(name, out var existing))
			{
				var of = existing.LastIndexOf(" of ", StringComparison.Ordinal);
				if (of >= 0 && int.TryParse(existing[(of + 4)..], out var first))
					original = first;
			}

			truncated[name] = $"kept the {keep} most significant of {original}";
			Rows.RemoveRange(keep, Rows.Count - keep);
			return true;
		}
	}
}
