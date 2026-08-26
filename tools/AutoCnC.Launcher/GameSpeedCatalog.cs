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

namespace AutoCnC.Launcher
{
	public sealed class GameSpeedInfo
	{
		/// <summary>The key the lobby option takes, e.g. <c>fastest</c>.</summary>
		public string Id { get; init; }

		/// <summary>Milliseconds per tick. Lower is faster.</summary>
		public int Timestep { get; init; }

		public bool IsDefault { get; init; }

		public override string ToString()
		{
			var label = char.ToUpper(Id[0], CultureInfo.CurrentCulture) + Id[1..];
			if (IsDefault)
				label += " (normal)";

			// A multiplier says more than a timestep does: the point of the control is "how long
			// do I have to sit here", not "what is the tick rate".
			return $"{label} — {40.0 / Timestep:0.##}x";
		}
	}

	/// <summary>
	/// The game speeds the mod defines, read from its manifest.
	/// </summary>
	/// <remarks>
	/// Parsed out of <c>mods/autocnc/mod.yaml</c> rather than listed here, so the window offers
	/// exactly what the mod supports and cannot drift from it. The block is shaped like:
	/// <code>
	/// GameSpeeds:
	///     DefaultSpeed: default
	///     Speeds:
	///         fastest:
	///             Timestep: 20
	/// </code>
	/// </remarks>
	public static class GameSpeedCatalog
	{
		public static IReadOnlyList<GameSpeedInfo> Read(RepoLayout repo)
		{
			var manifest = Path.Combine(repo.Root, "mods", "autocnc", "mod.yaml");
			if (!File.Exists(manifest))
				return [];

			try
			{
				return Parse(File.ReadAllLines(manifest));
			}
			catch (Exception)
			{
				return [];
			}
		}

		static IReadOnlyList<GameSpeedInfo> Parse(string[] lines)
		{
			var speeds = new List<GameSpeedInfo>();
			var defaultSpeed = "default";

			var inBlock = false;
			var inSpeeds = false;
			string current = null;

			foreach (var line in lines)
			{
				if (line.Trim().Length == 0)
					continue;

				var depth = Depth(line);
				var trimmed = line.Trim();

				if (depth == 0)
				{
					// Any other top-level key ends the block we care about.
					inBlock = trimmed.StartsWith("GameSpeeds:", StringComparison.Ordinal);
					inSpeeds = false;
					current = null;
					continue;
				}

				if (!inBlock)
					continue;

				if (depth == 1)
				{
					inSpeeds = trimmed.StartsWith("Speeds:", StringComparison.Ordinal);
					current = null;

					if (trimmed.StartsWith("DefaultSpeed:", StringComparison.Ordinal))
						defaultSpeed = trimmed["DefaultSpeed:".Length..].Trim();

					continue;
				}

				if (!inSpeeds)
					continue;

				if (depth == 2 && trimmed.EndsWith(':'))
					current = trimmed[..^1].Trim();
				else if (depth == 3 && current != null && trimmed.StartsWith("Timestep:", StringComparison.Ordinal)
					&& int.TryParse(trimmed["Timestep:".Length..].Trim(), out var timestep) && timestep > 0)
				{
					speeds.Add(new GameSpeedInfo { Id = current, Timestep = timestep });
					current = null;
				}
			}

			// Slowest first, which is how every game presents this.
			return speeds
				.Select(s => new GameSpeedInfo { Id = s.Id, Timestep = s.Timestep, IsDefault = s.Id == defaultSpeed })
				.OrderByDescending(s => s.Timestep)
				.ToList();
		}

		/// <summary>Indentation depth in tabs, which is what OpenRA's YAML uses.</summary>
		static int Depth(string line)
		{
			var depth = 0;
			while (depth < line.Length && line[depth] == '\t')
				depth++;

			return depth;
		}
	}
}
