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
using System.IO.Compression;
using System.Linq;

namespace AutoCnC.Launcher
{
	public sealed class MapInfo
	{
		/// <summary>What <c>Launch.Map</c> wants: the engine matches a UID or a file name.</summary>
		public string Id { get; init; }

		public string Title { get; init; }
		public int PlayerCount { get; init; }

		public override string ToString() => $"{Title} — {PlayerCount} players";
	}

	/// <summary>
	/// The maps you can start a battle on.
	/// </summary>
	/// <remarks>
	/// A <c>.oramap</c> is a zip with a <c>map.yaml</c> in it, and everything we need — the title,
	/// whether the map is a skirmish map at all, and how many players it seats — is in the first
	/// few lines of that file. Reading it directly avoids booting the engine just to fill in a
	/// combo box, which would put a multi-second pause in front of the window.
	/// </remarks>
	public static class MapCatalog
	{
		public static IReadOnlyList<MapInfo> Scan(RepoLayout repo)
		{
			var maps = new List<MapInfo>();

			foreach (var directory in MapDirectories(repo))
			{
				if (!Directory.Exists(directory))
					continue;

				foreach (var file in Directory.EnumerateFiles(directory, "*.oramap"))
					Add(maps, file, () => ReadFromPackage(file));

				// Maps can also ship unpacked, as a folder with the same contents.
				foreach (var folder in Directory.EnumerateDirectories(directory))
				{
					var yaml = Path.Combine(folder, "map.yaml");
					if (File.Exists(yaml))
						Add(maps, folder, () => File.ReadAllText(yaml));
				}
			}

			return maps
				.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
				.Select(g => g.First())
				.OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
				.ToList();
		}

		static IEnumerable<string> MapDirectories(RepoLayout repo)
		{
			// The mod inherits Tiberian Dawn's maps, and adds a per-version folder of its own for
			// anything the player downloaded or made.
			yield return Path.Combine(repo.EngineDir, "mods", "cnc", "maps");

			var userMaps = Path.Combine(SupportDir(repo), "maps", "autocnc");
			if (Directory.Exists(userMaps))
				foreach (var versioned in Directory.EnumerateDirectories(userMaps))
					yield return versioned;
		}

		/// <summary>
		/// OpenRA keeps player data beside the engine when a <c>Support</c> folder exists there,
		/// which is how a portable install works, and in the user profile otherwise.
		/// </summary>
		static string SupportDir(RepoLayout repo)
		{
			var portable = Path.Combine(repo.EngineDir, "Support");
			if (Directory.Exists(portable))
				return portable;

			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA");
		}

		static void Add(List<MapInfo> maps, string path, Func<string> readYaml)
		{
			try
			{
				var map = Parse(readYaml(), Path.GetFileName(path));
				if (map != null)
					maps.Add(map);
			}
			catch (Exception)
			{
				// A map we cannot read is a map we cannot offer. Nothing else here cares.
			}
		}

		static string ReadFromPackage(string file)
		{
			using var archive = ZipFile.OpenRead(file);
			var entry = archive.GetEntry("map.yaml")
				?? throw new InvalidDataException($"{file} has no map.yaml.");

			using var reader = new StreamReader(entry.Open());
			return reader.ReadToEnd();
		}

		/// <summary>Returns null for maps you cannot start a skirmish on.</summary>
		static MapInfo Parse(string yaml, string id)
		{
			string title = null;

			// Absent means Lobby, which is what makes a map show up in the skirmish map browser.
			// Missions, shellmaps and map-editor scratch maps declare something else.
			var visibility = "Lobby";
			var players = 0;

			foreach (var line in yaml.Split('\n'))
			{
				var trimmed = line.Trim();

				// Only top-level keys, so an actor called "Title" cannot confuse us.
				var topLevel = line.Length > 0 && line[0] != ' ' && line[0] != '\t';

				if (topLevel && trimmed.StartsWith("Title:", StringComparison.Ordinal))
					title = trimmed["Title:".Length..].Trim();
				else if (topLevel && trimmed.StartsWith("Visibility:", StringComparison.Ordinal))
					visibility = trimmed["Visibility:".Length..].Trim();
				else if (trimmed.Equals("Playable: True", StringComparison.OrdinalIgnoreCase))
					players++;
			}

			if (players < 2 || !visibility.Contains("Lobby", StringComparison.OrdinalIgnoreCase))
				return null;

			return new MapInfo
			{
				Id = id,
				Title = string.IsNullOrWhiteSpace(title) ? id : title,
				PlayerCount = players
			};
		}
	}
}
