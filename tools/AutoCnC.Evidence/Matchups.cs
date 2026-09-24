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
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoCnC.Evidence
{
	/// <summary>
	/// The duel lab's measured unit-against-unit margins, read from <c>matchups.json</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Written by <c>scripts/duel-lab-report.ps1</c> from fights the engine actually played, so a
	/// counter named in the summary is a measurement rather than arithmetic on the rules. Only the
	/// blind equal-cost margins are read: that is the fight a real army has, with no spotter.
	/// </para>
	/// <para>
	/// Static rules knowledge, the same a player reads from the encyclopedia. It says nothing about
	/// any match, so quoting it next to a fight's evidence leaks nothing about that fight.
	/// </para>
	/// </remarks>
	public sealed class Matchups
	{
		/// <summary>The file this was read from, for provenance.</summary>
		public string Source { get; private set; }

		/// <summary>Every unit type the lab fought, in the lab's order.</summary>
		public List<string> Units { get; } = [];

		readonly Dictionary<string, Dictionary<string, double>> margins = new(StringComparer.OrdinalIgnoreCase);

		public bool Knows(string actorType) => actorType != null && margins.ContainsKey(actorType);

		/// <summary>
		/// Share of the opponent's value destroyed minus share of one's own, from equal-cost groups,
		/// -1 to +1. Null when either side was never measured.
		/// </summary>
		public double? Margin(string unit, string opponent)
		{
			if (unit == null || opponent == null || !margins.TryGetValue(unit, out var row))
				return null;

			return row.TryGetValue(opponent, out var margin) ? margin : null;
		}

		/// <summary>
		/// Finds the matchups to use: an explicit path, then a copy beside the evidence, then the
		/// repository's own results found by walking up from this tool.
		/// </summary>
		/// <param name="evidenceDirectory">The fight's evidence folder.</param>
		/// <param name="explicitPath">A file to use, or <c>none</c> to use no matchups at all.</param>
		/// <returns>Null when nothing is found or the file cannot be read.</returns>
		public static Matchups Locate(string evidenceDirectory, string explicitPath = null)
		{
			if (string.Equals(explicitPath, "none", StringComparison.OrdinalIgnoreCase))
				return null;

			if (!string.IsNullOrEmpty(explicitPath))
				return Read(explicitPath);

			if (!string.IsNullOrEmpty(evidenceDirectory))
			{
				var beside = Path.Combine(evidenceDirectory, "matchups.json");
				if (File.Exists(beside))
					return Read(beside);
			}

			var directory = new DirectoryInfo(AppContext.BaseDirectory);
			for (var depth = 0; directory != null && depth < 10; depth++, directory = directory.Parent)
			{
				var candidate = Path.Combine(directory.FullName, "tools", "DuelLab", "results", "matchups.json");
				if (File.Exists(candidate))
					return Read(candidate);
			}

			return null;
		}

		public static Matchups Read(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;

			try
			{
				using var stream = File.OpenRead(path);
				using var document = JsonDocument.Parse(stream);
				var root = document.RootElement;
				if (!root.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
					return null;

				var matchups = new Matchups { Source = Path.GetFullPath(path) };
				if (root.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array)
					foreach (var unit in units.EnumerateArray())
						if (unit.ValueKind == JsonValueKind.String)
							matchups.Units.Add(unit.GetString());

				foreach (var row in cost.EnumerateObject())
				{
					if (row.Value.ValueKind != JsonValueKind.Object)
						continue;

					var entries = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
					foreach (var cell in row.Value.EnumerateObject())
						if (cell.Value.ValueKind == JsonValueKind.Number && cell.Value.TryGetDouble(out var margin))
							entries[cell.Name] = margin;

					matchups.margins[row.Name] = entries;
					if (!matchups.Units.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
						matchups.Units.Add(row.Name);
				}

				return matchups.margins.Count > 0 ? matchups : null;
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
	}
}
