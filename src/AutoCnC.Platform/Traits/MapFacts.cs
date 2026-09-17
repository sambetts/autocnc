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
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Writes public map facts to JSON once the world has loaded. Attach this to the world actor.")]
	public class MapFactsInfo : TraitInfo
	{
		[Desc("Where to write. A bare name lands in the support directory's Logs folder, next to",
			"debug.log; an absolute path is taken as given; 'none' records nothing.",
			"Launch.MapFacts on the command line overrides this.")]
		public readonly string File = "autocnc-map.json";

		public override object Create(ActorInitializer init) { return new MapFacts(init.World, this); }
	}

	/// <summary>
	/// Writes the lobby-public facts about the map a match is being played on.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is deliberately smaller and earlier than <see cref="MatchTelemetry"/>: it records the
	/// title, UID, cell dimensions, playable bounds, spawn assignments and starting resources once
	/// the map has resolved. Those are map facts rather than scouting facts, so they are safe for a
	/// human report while still remaining write-only from the game's point of view.
	/// </para>
	/// <para>
	/// Nothing here describes what an opponent has built or where it moved. The enemy distances
	/// are only written when every enemy chose an explicit spawn in the lobby. A random opponent's
	/// resolved start is not public until it is scouted, so it is left as <c>null</c> here too.
	/// </para>
	/// </remarks>
	public class MapFacts : IWorldLoaded
	{
		sealed class CellFact
		{
			public int X { get; set; }
			public int Y { get; set; }
		}

		sealed class SpawnFact
		{
			public string Player { get; set; }
			public int X { get; set; }
			public int Y { get; set; }
		}

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true
		};

		readonly World world;
		readonly MapFactsInfo info;

		public MapFacts(World world, MapFactsInfo info)
		{
			this.world = world;
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// The menu shellmap is a world too, and nobody wants a stale map.json from it.
			if (w.Type != WorldType.Regular)
				return;

			var path = LaunchOptions.ResolvePath(LaunchOptions.MapFacts ?? info.File);
			if (path == null)
				return;

			Write(path);
		}

		void Write(string path)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));

				if (File.Exists(path))
					File.Move(path, path + ".1", true);

				var local = world.LocalPlayer;
				var playable = world.Players.Where(p => p.Playable && !p.NonCombatant).ToArray();
				var spawns = SpawnLocations().ToArray();
				var localSpawn = SpawnFor(local, spawns);
				var enemySpawns = playable
					.Where(p => p != local && local != null && local.RelationshipWith(p).HasRelationship(PlayerRelationship.Enemy))
					.Select(p => MakeSpawnFact(p, PublicSpawnFor(p, spawns)))
					.ToArray();
				var resources = ResourceCounts();
				var haveAllEnemySpawns = enemySpawns.All(s => s != null);

				File.WriteAllText(path, JsonSerializer.Serialize(new
				{
					SchemaVersion = 3,
					Map = world.Map.Title,
					MapUid = world.Map.Uid,

					// The seed this match actually ran on, whether Launch.Seed pinned it or the
					// engine chose it. Recorded so an interesting unpinned run can be replayed
					// exactly by passing this value back in, which is the difference between
					// "that was a strange match" and a reproduction.
					RandomSeed = world.LobbyInfo.GlobalSettings.RandomSeed,
					SeedWasPinned = LaunchOptions.Seed != 0,
					WidthCells = world.Map.MapSize.Width,
					HeightCells = world.Map.MapSize.Height,
					PlayableWidthCells = world.Map.Bounds.Width,
					PlayableHeightCells = world.Map.Bounds.Height,
					NominalTickMilliseconds = TurboSpeed.NominalTimestep,
					SpawnPoints = spawns.Select((s, i) => new SpawnFact
					{
						Player = OccupantAt(i + 1, playable, local),
						X = s.X,
						Y = s.Y
					}).ToArray(),
					LocalPlayer = Name(local),
					LocalSpawn = localSpawn.HasValue ? Cell(localSpawn.Value) : null,
					EnemySpawns = enemySpawns,
					HomeToNearestEnemyCells = haveAllEnemySpawns ? Distance(localSpawn, enemySpawns, nearest: true) : null,
					HomeToFurthestEnemyCells = haveAllEnemySpawns ? Distance(localSpawn, enemySpawns, nearest: false) : null,
					ResourceCells = resources.Total,
					ResourceCellsByType = resources.ByType
				}, JsonOptions));

				Log.Write("debug", $"Map facts: wrote {path}.");
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Map facts: could not write {path}: {ex.Message}");
			}
		}

		IEnumerable<CPos> SpawnLocations()
		{
			foreach (var node in world.Map.ActorDefinitions)
				if (node.Value.Value == "mpspawn")
					yield return new ActorReference(node.Key, node.Value).GetValue<LocationInit, CPos>();
		}

		(int Total, Dictionary<string, int> ByType) ResourceCounts()
		{
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			if (layer == null)
				return (0, byType);

			var total = 0;
			foreach (var cell in world.Map.AllCells)
			{
				var resource = layer.GetResource(cell);
				if (resource.Type == null)
					continue;

				total++;
				byType.TryGetValue(resource.Type, out var count);
				byType[resource.Type] = count + 1;
			}

			return (total, byType);
		}

		static CPos? SpawnFor(Player player, CPos[] spawns)
		{
			if (player == null)
				return null;

			if (player.SpawnPoint > 0 && player.SpawnPoint <= spawns.Length)
				return spawns[player.SpawnPoint - 1];

			return player.HomeLocation;
		}

		static CPos? PublicSpawnFor(Player player, CPos[] spawns)
		{
			if (player == null || player.DisplaySpawnPoint <= 0 || player.DisplaySpawnPoint > spawns.Length)
				return null;

			return spawns[player.DisplaySpawnPoint - 1];
		}

		static string OccupantAt(int spawnPoint, Player[] players, Player local)
		{
			foreach (var player in players)
			{
				if (player == local)
				{
					if (player.SpawnPoint == spawnPoint)
						return Name(player);
				}
				else if (player.DisplaySpawnPoint == spawnPoint)
					return Name(player);
			}

			return null;
		}

		static SpawnFact MakeSpawnFact(Player player, CPos? spawn) => spawn.HasValue ? new SpawnFact
		{
			Player = Name(player),
			X = spawn.Value.X,
			Y = spawn.Value.Y
		} : null;

		static CellFact Cell(CPos cell) => new()
		{
			X = cell.X,
			Y = cell.Y
		};

		static int? Distance(CPos? localSpawn, SpawnFact[] enemySpawns, bool nearest)
		{
			if (!localSpawn.HasValue || enemySpawns.Length == 0)
				return null;

			var distances = enemySpawns.Select(s => (new CPos(s.X, s.Y) - localSpawn.Value).Length);

			return nearest ? distances.Min() : distances.Max();
		}

		static string Name(Player player) => player?.ResolvedPlayerName?.Trim();
	}
}
