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
using System.Linq;

namespace AutoCnC.Evidence
{
	/// <summary>One sample of one player's army and economy from <c>telemetry.csv</c>.</summary>
	/// <remarks>
	/// The flow columns — <see cref="Earned"/> and <see cref="Spent"/> — are cumulative, and are
	/// the point of the file for economic analysis. <see cref="Cash"/> is a stock, so a zero there
	/// cannot tell "earning nothing" apart from "spending it the instant it arrives"; the
	/// difference between two <see cref="Earned"/> samples can.
	/// </remarks>
	public sealed class TelemetrySample
	{
		public int Seconds { get; init; }
		public string Player { get; init; }
		public string Faction { get; init; }
		public bool IsBot { get; init; }
		public int Units { get; init; }
		public int Army { get; init; }
		public int Buildings { get; init; }
		public int BaseValue { get; init; }
		public int Assets { get; init; }
		public int Cash { get; init; }
		public int Killed { get; init; }
		public int Lost { get; init; }
		public int BuildingsKilled { get; init; }
		public int BuildingsLost { get; init; }
		public string State { get; init; }

		/// <summary>Cumulative credits harvested, or null on a run predating the column.</summary>
		public int? Earned { get; init; }
		public int? Spent { get; init; }
		public int? Power { get; init; }
		public int? PowerProvided { get; init; }
		public int? PowerDrained { get; init; }
		public int? Harvesters { get; init; }
		public int? Queued { get; init; }

		/// <summary>
		/// Live lifecycle: <c>Playing</c>, <c>Eliminated</c>, or the resolved result.
		/// </summary>
		/// <remarks>
		/// Appended alongside <see cref="State"/> rather than replacing it. <see cref="State"/> is
		/// the engine's win state and reads <c>Undefined</c> until the match is decided, which the
		/// launcher's own match log depends on to know a result is not yet final.
		/// </remarks>
		public string Status { get; init; }
	}

	/// <summary>A whole match's telemetry, indexed by player.</summary>
	public sealed class Telemetry
	{
		public List<TelemetrySample> Samples { get; } = [];

		public bool HasEconomyFlows { get; private set; }

		public IEnumerable<string> Players =>
			Samples.Select(s => s.Player).Distinct(StringComparer.Ordinal);

		public List<TelemetrySample> For(string player) =>
			Samples.Where(s => string.Equals(s.Player, player, StringComparison.Ordinal)).ToList();

		public static Telemetry Read(string path)
		{
			var telemetry = new Telemetry();
			var rows = Csv.ReadRows(path, out var header);
			if (header.Length == 0)
				return telemetry;

			var index = Csv.Index(header);
			telemetry.HasEconomyFlows = index.ContainsKey("earned");

			foreach (var row in rows)
				telemetry.Samples.Add(new TelemetrySample
				{
					Seconds = Csv.Int(row, index, "seconds"),
					Player = Csv.Field(row, index, "player"),
					Faction = Csv.Field(row, index, "faction"),
					IsBot = Csv.Int(row, index, "bot") == 1,
					Units = Csv.Int(row, index, "units"),
					Army = Csv.Int(row, index, "army"),
					Buildings = Csv.Int(row, index, "buildings"),
					BaseValue = Csv.Int(row, index, "basevalue"),
					Assets = Csv.Int(row, index, "assets"),
					Cash = Csv.Int(row, index, "cash"),
					Killed = Csv.Int(row, index, "killed"),
					Lost = Csv.Int(row, index, "lost"),
					BuildingsKilled = Csv.Int(row, index, "buildingskilled"),
					BuildingsLost = Csv.Int(row, index, "buildingslost"),
					State = Csv.Field(row, index, "state"),
					Earned = Csv.OptionalInt(row, index, "earned"),
					Spent = Csv.OptionalInt(row, index, "spent"),
					Power = Csv.OptionalInt(row, index, "power"),
					PowerProvided = Csv.OptionalInt(row, index, "powerprovided"),
					PowerDrained = Csv.OptionalInt(row, index, "powerdrained"),
					Harvesters = Csv.OptionalInt(row, index, "harvesters"),
					Queued = Csv.OptionalInt(row, index, "queued"),
					Status = Csv.Field(row, index, "status")
				});

			return telemetry;
		}
	}
}
