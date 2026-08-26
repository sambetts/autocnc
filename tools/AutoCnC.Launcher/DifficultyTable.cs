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

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCnC.Launcher
{
	/// <summary>One entry from <c>scripts/difficulties.json</c>.</summary>
	public sealed class DifficultyLevel
	{
		[JsonPropertyName("name")]
		public string Name { get; set; }

		[JsonPropertyName("botLabel")]
		public string BotLabel { get; set; }

		[JsonPropertyName("botHandicap")]
		public int BotHandicap { get; set; }

		[JsonPropertyName("playerHandicap")]
		public int PlayerHandicap { get; set; }

		[JsonPropertyName("summary")]
		public string Summary { get; set; }

		public override string ToString() => Name;
	}

	/// <summary>
	/// The difficulty levels, read from the same file the launch script reads.
	/// </summary>
	/// <remarks>
	/// The launcher only ever passes <c>-Difficulty &lt;name&gt;</c> back to the script, so the
	/// bot personality and handicap behind a level are never decided in two places. Everything
	/// read here is for display.
	/// </remarks>
	public sealed class DifficultyTable
	{
		[JsonPropertyName("default")]
		public string Default { get; set; }

		[JsonPropertyName("levels")]
		public List<DifficultyLevel> Levels { get; set; } = [];

		static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

		public static DifficultyTable Load(string path)
		{
			var table = JsonSerializer.Deserialize<DifficultyTable>(File.ReadAllText(path), Options);
			if (table == null || table.Levels.Count == 0)
				throw new InvalidDataException($"{path} defines no difficulty levels.");

			return table;
		}
	}
}
