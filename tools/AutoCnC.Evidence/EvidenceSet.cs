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
using System.Text.Json;

namespace AutoCnC.Evidence
{
	/// <summary>The map facts a finished match cannot be asked about afterwards.</summary>
	/// <remarks>
	/// Map dimensions, spawn cells and resource cells are lobby-public facts that no amount of
	/// reading the event stream can recover, so the engine writes them out at world load. Nothing
	/// here says anything about what the opponent did with the map — only what the map is.
	/// </remarks>
	public sealed class MapFacts
	{
		public string Map { get; init; }
		public int WidthCells { get; init; }
		public int HeightCells { get; init; }
		public string LocalSpawn { get; init; }
		public List<string> EnemySpawns { get; init; } = [];
		public int? HomeToNearestEnemyCells { get; init; }
		public int ResourceCells { get; init; }

		public static MapFacts Read(string path)
		{
			if (!File.Exists(path))
				return null;

			try
			{
				using var stream = File.OpenRead(path);
				using var document = JsonDocument.Parse(stream);
				var root = document.RootElement;

				return new MapFacts
				{
					Map = Text(root, "map"),
					WidthCells = Integer(root, "widthCells"),
					HeightCells = Integer(root, "heightCells"),
					LocalSpawn = Cell(root, "localSpawn"),
					EnemySpawns = Cells(root, "enemySpawns"),
					HomeToNearestEnemyCells = root.TryGetProperty("homeToNearestEnemyCells", out var d) &&
						d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var parsed)
							? parsed
							: null,
					ResourceCells = Integer(root, "resourceCells")
				};
			}
			catch (JsonException)
			{
				return null;
			}
		}

		static string Cell(JsonElement root, string name)
		{
			if (!root.TryGetProperty(name, out var cell) || cell.ValueKind != JsonValueKind.Object)
				return null;

			return $"{Integer(cell, "x")},{Integer(cell, "y")}";
		}

		static List<string> Cells(JsonElement root, string name)
		{
			var cells = new List<string>();
			if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
				return cells;

			foreach (var item in array.EnumerateArray())
				if (item.ValueKind == JsonValueKind.Object)
					cells.Add($"{Integer(item, "x")},{Integer(item, "y")}");

			return cells;
		}

		static string Text(JsonElement root, string name) =>
			root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static int Integer(JsonElement root, string name) =>
			root.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetInt32(out var parsed)
				? parsed
				: 0;
	}

	/// <summary>The handful of <c>fight.json</c> fields the summary quotes back.</summary>
	public sealed class FightManifest
	{
		public string Id { get; init; }
		public string SourceRevision { get; init; }
		public string BotProject { get; init; }
		public string Map { get; init; }
		public string Difficulty { get; init; }
		public string Faction { get; init; }
		public string BotFaction { get; init; }
		public int Opponents { get; init; }
		public string Outcome { get; init; }
		public int DurationSeconds { get; init; }
		public string LocalPlayer { get; init; }
		public int? Seed { get; init; }
		public string Benchmark { get; init; }
		public string Batch { get; init; }
		public string Arm { get; init; }

		public static FightManifest Read(string path)
		{
			if (!File.Exists(path))
				return new FightManifest();

			try
			{
				using var stream = File.OpenRead(path);
				using var document = JsonDocument.Parse(stream);
				var root = document.RootElement;
				var battle = Child(root, "Battle");
				var result = Child(root, "Result");

				return new FightManifest
				{
					Id = Text(root, "Id"),
					SourceRevision = Text(root, "SourceRevision"),
					BotProject = Text(root, "BotProject"),
					Map = Text(battle, "Map"),
					Difficulty = Text(battle, "Difficulty"),
					Faction = Text(battle, "Faction"),
					BotFaction = Text(battle, "BotFaction"),
					Opponents = Integer(battle, "Opponents"),
					Seed = OptionalInteger(battle, "Seed"),
					Benchmark = Text(battle, "Benchmark"),
					Batch = Text(battle, "Batch"),
					Arm = Text(battle, "Arm"),
					Outcome = Text(result, "Outcome"),
					DurationSeconds = Integer(result, "DurationSeconds"),
					LocalPlayer = Text(result, "LocalPlayer")
				};
			}
			catch (JsonException)
			{
				return new FightManifest();
			}
		}

		static JsonElement? Child(JsonElement? parent, string name) =>
			parent.HasValue && parent.Value.ValueKind == JsonValueKind.Object &&
			parent.Value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
				? child
				: null;

		static string Text(JsonElement? parent, string name) =>
			parent.HasValue && parent.Value.ValueKind == JsonValueKind.Object &&
			parent.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static int Integer(JsonElement? parent, string name) => OptionalInteger(parent, name) ?? 0;

		static int? OptionalInteger(JsonElement? parent, string name) =>
			parent.HasValue && parent.Value.ValueKind == JsonValueKind.Object &&
			parent.Value.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
				? parsed
				: null;
	}

	/// <summary>
	/// One fight's evidence directory: where each artifact lives, and its contents once read.
	/// </summary>
	/// <remarks>
	/// Loading is done once and shared. The decision trace is the reason — it is by far the
	/// largest input, and re-reading it per question is the cost this whole pipeline exists to
	/// remove.
	/// </remarks>
	public sealed class EvidenceSet
	{
		public string Directory { get; }

		public string BattleLogPath => Path.Combine(Directory, "battle.csv");
		public string TelemetryPath => Path.Combine(Directory, "telemetry.csv");
		public string DecisionTracePath => Path.Combine(Directory, "decisions.jsonl");
		public string GameRulesPath => Path.Combine(Directory, "game-rules.json");
		public string FightManifestPath => Path.Combine(Directory, "fight.json");
		public string MapFactsPath => Path.Combine(Directory, "map.json");
		public string SummaryPath => Path.Combine(Directory, "summary.json");
		public string UnitsPath => Path.Combine(Directory, "units.csv");
		public string ChecksPath => Path.Combine(Directory, "checks.json");
		public string CheckResultsPath => Path.Combine(Directory, "check-results.json");
		public string TrendPath => Path.Combine(Directory, "trend.json");

		public BattleEvents Battle { get; private set; }
		public Telemetry Telemetry { get; private set; }
		public DecisionTrace Trace { get; private set; }
		public GameRules Rules { get; private set; }
		public MapFacts Map { get; private set; }
		public FightManifest Manifest { get; private set; }

		public EvidenceSet(string directory)
		{
			Directory = Path.GetFullPath(directory);
		}

		public EvidenceSet Load()
		{
			Battle = BattleEvents.Read(BattleLogPath);

			// Qualified because the property and the type share a name, and a static method
			// reached through an instance is a compile error rather than a subtle bug.
			Telemetry = AutoCnC.Evidence.Telemetry.Read(TelemetryPath);
			Trace = DecisionTrace.Read(DecisionTracePath);
			Rules = GameRules.Read(GameRulesPath);
			Map = MapFacts.Read(MapFactsPath);
			Manifest = FightManifest.Read(FightManifestPath);
			return this;
		}
	}
}
