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
	/// <summary>What one actor type costs and is, read from the resolved ruleset.</summary>
	public sealed class ActorRule
	{
		public string Id { get; init; }
		public string Kind { get; init; }
		public int Cost { get; init; }
		public int HitPoints { get; init; }
		public bool IsHarvester { get; init; }
		public bool IsRefinery { get; init; }
		public double SpeedCellsPerGameSecond { get; init; }

		/// <summary>Actors this one is handed for nothing when it finishes.</summary>
		public string[] FreeActors { get; init; } = [];

		/// <summary>Production queues that build this actor, such as <c>Infantry.GDI</c>.</summary>
		public string[] Queues { get; init; } = [];

		/// <summary>Queues this actor provides once it stands, such as <c>Vehicle.GDI</c> for a war factory.</summary>
		public string[] Produces { get; init; } = [];

		/// <summary>True when the actor carries at least one armament.</summary>
		public bool Armed { get; init; }
	}

	/// <summary>
	/// The subset of <c>game-rules.json</c> that the summary needs.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The file is around half a megabyte of resolved weapon and warhead detail, and the summary
	/// wants six fields per actor from it. Reading it into this rather than into a general model
	/// keeps the working set small and, more usefully, makes the dependency explicit: if the
	/// summary starts needing armour multipliers, that shows up here as a new field rather than as
	/// a silent new coupling to the whole ruleset.
	/// </para>
	/// <para>
	/// <see cref="FreeActorTypes"/> is the reason this is loaded at all. Credits-per-kill and
	/// share-of-spend are wrong by a harvester and a construction yard's worth unless the actors
	/// the game hands out free are excluded, and the previous way of finding them — matching a
	/// harvester's build second against a refinery's — guessed, sometimes wrongly, and could not
	/// be checked. The ruleset states it outright.
	/// </para>
	/// </remarks>
	public sealed class GameRules
	{
		public int SchemaVersion { get; private set; }
		public int NominalTickMilliseconds { get; private set; } = 40;

		public Dictionary<string, ActorRule> Actors { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Actor types the ruleset gives away, so they are never charged to a build order.
		/// </summary>
		/// <remarks>
		/// Populated from every <c>FreeActor</c> trait in the ruleset. Empty when the rules export
		/// predates schema 2, in which case spend figures carry
		/// <c>freeActorExclusion: "unavailable"</c> rather than quietly guessing.
		/// </remarks>
		public HashSet<string> FreeActorTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

		public bool KnowsFreeActors { get; private set; }

		public int CostOf(string actorType) =>
			actorType != null && Actors.TryGetValue(actorType, out var rule) ? rule.Cost : 0;

		public bool IsFree(string actorType) =>
			actorType != null && FreeActorTypes.Contains(actorType);

		public ActorRule Rule(string actorType) =>
			actorType != null && Actors.TryGetValue(actorType, out var rule) ? rule : null;

		/// <summary>
		/// Whether a player of <paramref name="faction"/> can build this actor from any of its queues.
		/// </summary>
		/// <remarks>
		/// Queues are named per faction, <c>Infantry.GDI</c> or <c>Vehicle.Nod</c>, so the faction
		/// is the suffix. False when the rules export carries no build data, rather than a guess
		/// that would recommend the other faction's units.
		/// </remarks>
		public bool BuildableBy(string actorType, string faction)
		{
			var rule = Rule(actorType);
			if (rule == null || string.IsNullOrEmpty(faction))
				return false;

			foreach (var queue in rule.Queues)
				if (queue.EndsWith("." + faction, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		/// <summary>True for a structure that trains infantry, vehicles or aircraft.</summary>
		public bool IsUnitFactory(string actorType)
		{
			var rule = Rule(actorType);
			if (rule == null)
				return false;

			foreach (var queue in rule.Produces)
				if (queue.StartsWith("Infantry", StringComparison.OrdinalIgnoreCase) ||
					queue.StartsWith("Vehicle", StringComparison.OrdinalIgnoreCase) ||
					queue.StartsWith("Aircraft", StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		public static GameRules Read(string path)
		{
			var rules = new GameRules();
			if (!File.Exists(path))
				return rules;

			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;

			if (root.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var parsed))
				rules.SchemaVersion = parsed;

			if (root.TryGetProperty("nominalTickMilliseconds", out var tick) && tick.TryGetInt32(out var ms) && ms > 0)
				rules.NominalTickMilliseconds = ms;

			if (!root.TryGetProperty("actors", out var actors) || actors.ValueKind != JsonValueKind.Array)
				return rules;

			foreach (var actor in actors.EnumerateArray())
			{
				var id = String(actor, "id");
				if (string.IsNullOrEmpty(id))
					continue;

				var free = FreeActors(actor);
				if (free.Length > 0)
				{
					rules.KnowsFreeActors = true;
					foreach (var spawned in free)
						rules.FreeActorTypes.Add(spawned);
				}

				rules.Actors[id] = new ActorRule
				{
					Id = id,
					Kind = String(actor, "kind"),
					Cost = Integer(actor, "cost"),
					HitPoints = Integer(actor, "hitPoints"),
					IsHarvester = Flag(actor, "isHarvester"),
					IsRefinery = Flag(actor, "isRefinery"),
					SpeedCellsPerGameSecond = Real(actor, "speedCellsPerGameSecond"),
					FreeActors = free,
					Queues = actor.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object
						? Strings(build, "queues")
						: [],
					Produces = Strings(actor, "produces"),
					Armed = actor.TryGetProperty("armaments", out var armaments) &&
						armaments.ValueKind == JsonValueKind.Array && armaments.GetArrayLength() > 0
				};
			}

			return rules;
		}

		static string[] Strings(JsonElement element, string name)
		{
			if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
				return [];

			var values = new List<string>();
			foreach (var item in array.EnumerateArray())
				if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
					values.Add(item.GetString());

			return values.ToArray();
		}

		static string[] FreeActors(JsonElement actor)
		{
			if (!actor.TryGetProperty("freeActors", out var free) || free.ValueKind != JsonValueKind.Array)
				return [];

			var names = new List<string>();
			foreach (var item in free.EnumerateArray())
			{
				// Tolerates both shapes the exporter could reasonably choose: a bare actor name,
				// or an object naming the actor alongside the condition it arrives under.
				var name = item.ValueKind == JsonValueKind.String ? item.GetString() : String(item, "actor");
				if (!string.IsNullOrEmpty(name))
					names.Add(name);
			}

			return names.ToArray();
		}

		static string String(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static int Integer(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetInt32(out var parsed)
				? parsed
				: 0;

		static double Real(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) &&
			value.ValueKind == JsonValueKind.Number &&
			value.TryGetDouble(out var parsed)
				? parsed
				: 0d;

		static bool Flag(JsonElement element, string name) =>
			element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
	}
}
