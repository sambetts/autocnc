#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCnC.Platform.Traits;
using OpenRA;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;

namespace AutoCnC.Platform.UtilityCommands
{
	/// <summary>Exports an agent-friendly snapshot from the ruleset OpenRA actually resolved.</summary>
	public sealed class ExportAgentRulesCommand : IUtilityCommand
	{
		sealed class Snapshot
		{
			public int SchemaVersion { get; set; } = 1;
			public string Source { get; set; }
			public DateTime GeneratedAtUtc { get; set; }
			public int NominalTickMilliseconds { get; set; }
			public List<ActorRule> Actors { get; set; } = [];
			public List<WeaponRule> Weapons { get; set; } = [];
		}

		sealed class ActorRule
		{
			public string Id { get; set; }
			public string Kind { get; set; }
			public string DisplayNameKey { get; set; }
			public int? Cost { get; set; }
			public int? HitPoints { get; set; }
			public string[] ArmorTypes { get; set; }
			public string[] TargetTypes { get; set; }
			public int? SpeedWorldUnitsPerTick { get; set; }
			public double? SpeedCellsPerGameSecond { get; set; }
			public int? SightRangeWorldUnits { get; set; }
			public double? SightRangeCells { get; set; }
			public int? Power { get; set; }
			public bool IsHarvester { get; set; }
			public bool IsRefinery { get; set; }
			public BuildRule Build { get; set; }
			public string[] Produces { get; set; }
			public List<ArmamentRule> Armaments { get; set; } = [];
			public string[] Traits { get; set; }
		}

		sealed class BuildRule
		{
			public string[] Queues { get; set; }
			public string[] Prerequisites { get; set; }
			public int BaseDurationTicks { get; set; }
			public int DurationModifierPercent { get; set; }
			public int BuildLimit { get; set; }
			public string DescriptionKey { get; set; }
		}

		sealed class ArmamentRule
		{
			public string Name { get; set; }
			public string Weapon { get; set; }
			public int FireDelayTicks { get; set; }
			public int RangeWorldUnits { get; set; }
			public double RangeCells { get; set; }
		}

		sealed class WeaponRule
		{
			public string Id { get; set; }
			public int RangeWorldUnits { get; set; }
			public double RangeCells { get; set; }
			public int MinimumRangeWorldUnits { get; set; }
			public int ReloadDelayTicks { get; set; }
			public double ReloadGameSeconds { get; set; }
			public int Burst { get; set; }
			public int[] BurstDelayTicks { get; set; }
			public int FireCycleTicks { get; set; }
			public double ShotsPerGameSecond { get; set; }
			public string[] ValidTargets { get; set; }
			public string[] InvalidTargets { get; set; }
			public string ProjectileType { get; set; }
			public Dictionary<string, object> ProjectileFields { get; set; }
			public List<WarheadRule> Warheads { get; set; } = [];
		}

		sealed class WarheadRule
		{
			public string Type { get; set; }
			public int? Damage { get; set; }
			public string[] DamageTypes { get; set; }
			public Dictionary<string, int> VersusArmorPercent { get; set; }
			public string[] ValidTargets { get; set; }
			public string[] InvalidTargets { get; set; }
			public Dictionary<string, object> Fields { get; set; }
		}

		string IUtilityCommand.Name => "--export-agent-rules";

		bool IUtilityCommand.ValidateArguments(string[] args) => args.Length == 2;

		[Desc("PATH", "Export resolved actor and weapon rules as agent-friendly JSON.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			Game.ModData = utility.ModData;
			var rules = utility.ModData.DefaultRules;
			var snapshot = new Snapshot
			{
				Source = "OpenRA ModData.DefaultRules resolved from the AutoC&C manifest, inherited YAML, and overrides.",
				GeneratedAtUtc = DateTime.UtcNow,
				NominalTickMilliseconds = TurboSpeed.NominalTimestep
			};

			foreach (var actor in rules.Actors.Values.OrderBy(a => a.Name, StringComparer.Ordinal))
			{
				var exported = Actor(actor);
				if (exported != null)
					snapshot.Actors.Add(exported);
			}

			foreach (var weapon in rules.Weapons.OrderBy(w => w.Key, StringComparer.Ordinal))
				snapshot.Weapons.Add(Weapon(weapon.Key, weapon.Value));

			var path = Path.GetFullPath(args[1]);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var options = new JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			};
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, options));
			File.Move(temporary, path, true);
			Console.WriteLine($"Exported {snapshot.Actors.Count} actors and {snapshot.Weapons.Count} weapons to {path}");
		}

		static ActorRule Actor(ActorInfo actor)
		{
			var value = actor.TraitInfoOrDefault<ValuedInfo>();
			var buildable = actor.TraitInfoOrDefault<BuildableInfo>();
			var health = actor.TraitInfoOrDefault<HealthInfo>();
			var mobile = actor.TraitInfoOrDefault<MobileInfo>();
			var aircraft = actor.TraitInfoOrDefault<AircraftInfo>();
			var building = actor.TraitInfoOrDefault<BuildingInfo>();

			// The agent needs things a player can own, build, or fight. This excludes world,
			// player, effect, and decoration actors without maintaining a second list of units.
			if (value == null && buildable == null &&
				(health == null || mobile == null && aircraft == null && building == null))
				return null;

			var speed = mobile?.Speed ?? aircraft?.Speed;
			var sight = actor.TraitInfos<RevealsShroudInfo>().Select(r => r.Range.Length).DefaultIfEmpty().Max();
			var power = actor.TraitInfos<PowerInfo>().Select(p => p.Amount).DefaultIfEmpty().Sum();

			var result = new ActorRule
			{
				Id = actor.Name,
				Kind = building != null ? "building" : aircraft != null ? "aircraft" : mobile != null ? "mobile" : "other",
				DisplayNameKey = actor.TraitInfos<TooltipInfo>().Select(t => t.Name).FirstOrDefault(),
				Cost = value?.Cost,
				HitPoints = health?.HP,
				ArmorTypes = actor.TraitInfos<ArmorInfo>()
					.Select(a => a.Type).Where(t => t != null).Distinct().OrderBy(t => t).ToArray(),
				TargetTypes = actor.GetAllTargetTypes().OrderBy(t => t).ToArray(),
				SpeedWorldUnitsPerTick = speed,
				SpeedCellsPerGameSecond = speed.HasValue
					? Math.Round(speed.Value * (1000d / TurboSpeed.NominalTimestep) / 1024d, 3)
					: null,
				SightRangeWorldUnits = sight > 0 ? sight : null,
				SightRangeCells = sight > 0 ? Math.Round(sight / 1024d, 3) : null,
				Power = power != 0 ? power : null,
				IsHarvester = actor.HasTraitInfo<HarvesterInfo>(),
				IsRefinery = actor.HasTraitInfo<RefineryInfo>(),
				Produces = actor.TraitInfos<ProductionInfo>()
					.SelectMany(p => p.Produces).Distinct().OrderBy(p => p).ToArray(),
				Traits = actor.TraitsInConstructOrder()
					.Select(t => TrimInfo(t.GetType().Name)).Distinct().OrderBy(t => t).ToArray()
			};

			if (buildable != null)
				result.Build = new BuildRule
				{
					Queues = buildable.Queue.OrderBy(q => q).ToArray(),
					Prerequisites = buildable.Prerequisites.ToArray(),
					BaseDurationTicks = buildable.BuildDuration,
					DurationModifierPercent = buildable.BuildDurationModifier,
					BuildLimit = buildable.BuildLimit,
					DescriptionKey = buildable.Description
				};

			foreach (var armament in actor.TraitInfos<ArmamentInfo>().OrderBy(a => a.Name))
				result.Armaments.Add(new ArmamentRule
				{
					Name = armament.Name,
					Weapon = armament.Weapon,
					FireDelayTicks = armament.FireDelay,
					RangeWorldUnits = armament.ModifiedRange.Length,
					RangeCells = Math.Round(armament.ModifiedRange.Length / 1024d, 3)
				});

			return result;
		}

		static WeaponRule Weapon(string id, WeaponInfo weapon)
		{
			var burstDelay = weapon.Burst <= 1 ? 0
				: weapon.BurstDelays.Length == 1
					? weapon.BurstDelays[0] * (weapon.Burst - 1)
					: weapon.BurstDelays.Take(weapon.Burst - 1).Sum();
			var cycleTicks = weapon.ReloadDelay + burstDelay;
			var result = new WeaponRule
			{
				Id = id,
				RangeWorldUnits = weapon.Range.Length,
				RangeCells = Math.Round(weapon.Range.Length / 1024d, 3),
				MinimumRangeWorldUnits = weapon.MinRange.Length,
				ReloadDelayTicks = weapon.ReloadDelay,
				ReloadGameSeconds = Math.Round(weapon.ReloadDelay * TurboSpeed.NominalTimestep / 1000d, 3),
				Burst = weapon.Burst,
				BurstDelayTicks = weapon.BurstDelays.ToArray(),
				FireCycleTicks = cycleTicks,
				ShotsPerGameSecond = Math.Round(
					weapon.Burst * 1000d / (cycleTicks * TurboSpeed.NominalTimestep), 3),
				ValidTargets = weapon.ValidTargets.OrderBy(t => t).ToArray(),
				InvalidTargets = weapon.InvalidTargets.OrderBy(t => t).ToArray(),
				ProjectileType = TrimInfo(weapon.Projectile?.GetType().Name),
				ProjectileFields = PublicFields(weapon.Projectile)
			};

			foreach (var warhead in weapon.Warheads)
			{
				var common = warhead as Warhead;
				var damage = warhead as DamageWarhead;
				result.Warheads.Add(new WarheadRule
				{
					Type = TrimInfo(warhead.GetType().Name),
					Damage = damage?.Damage,
					DamageTypes = damage?.DamageTypes.OrderBy(t => t).ToArray(),
					VersusArmorPercent = damage?.Versus.ToDictionary(v => v.Key, v => v.Value),
					ValidTargets = common?.ValidTargets.OrderBy(t => t).ToArray(),
					InvalidTargets = common?.InvalidTargets.OrderBy(t => t).ToArray(),
					Fields = PublicFields(warhead,
						"Damage", "DamageTypes", "Versus", "ValidTargets", "InvalidTargets")
				});
			}

			return result;
		}

		static Dictionary<string, object> PublicFields(object subject, params string[] excluded)
		{
			if (subject == null)
				return null;

			var result = new Dictionary<string, object>(StringComparer.Ordinal);
			var omitted = excluded.ToHashSet(StringComparer.Ordinal);
			foreach (var field in Utility.GetFields(subject.GetType())
				.Where(f => !f.IsStatic)
				.Where(f => !omitted.Contains(f.Name))
				.OrderBy(f => f.Name, StringComparer.Ordinal))
				result[field.Name] = Normalise(field.GetValue(subject));

			return result;
		}

		static object Normalise(object value)
		{
			if (value == null)
				return null;

			if (value is string || value is bool ||
				value is byte || value is sbyte ||
				value is short || value is ushort ||
				value is int || value is uint ||
				value is long || value is ulong ||
				value is float || value is double || value is decimal)
				return value;

			if (value is Enum)
				return value.ToString();

			if (value is WDist distance)
				return new { WorldUnits = distance.Length, Cells = Math.Round(distance.Length / 1024d, 3) };

			if (value is WAngle angle)
				return new { Units = angle.Angle, Degrees = Math.Round(angle.Angle * 360d / 1024d, 3) };

			var valueType = value.GetType();
			if (valueType.IsGenericType &&
				valueType.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
				(bool)valueType.GetProperty("IsDefault").GetValue(value))
				return Array.Empty<object>();

			var dictionaryType = valueType.GetInterfaces().FirstOrDefault(i =>
				i.IsGenericType &&
				(i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
					i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
			if (dictionaryType != null)
			{
				var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
				foreach (var item in (IEnumerable)value)
				{
					var type = item.GetType();
					var key = type.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
					var entry = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
					dictionary[key?.ToString() ?? ""] = Normalise(entry);
				}

				return dictionary;
			}

			if (value is IEnumerable sequence)
				return sequence.Cast<object>().Select(Normalise).ToArray();

			return value.ToString();
		}

		static string TrimInfo(string name) =>
			name?.EndsWith("Info", StringComparison.Ordinal) == true ? name[..^4] : name;
	}
}
