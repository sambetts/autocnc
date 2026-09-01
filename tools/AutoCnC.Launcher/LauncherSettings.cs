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
using System.IO;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// What the launcher remembers between runs, so the second battle is one click.
	/// </summary>
	/// <remarks>
	/// Stored in the user profile rather than the repository: it is a personal working state,
	/// and a checkout shared by two people should not have one of them overwriting the other's
	/// battle bot path.
	/// </remarks>
	public sealed class LauncherSettings
	{
		public string RepositoryRoot { get; set; }
		public string BattleBotPath { get; set; }
		public string Map { get; set; }
		public string Difficulty { get; set; }
		public string GameSpeed { get; set; }
		public int Opponents { get; set; } = 1;
		public string Faction { get; set; } = "Random";
		public string BotFaction { get; set; } = "Random";
		public bool RunTests { get; set; }

		static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

		static string FilePath => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"AutoCnC", "launcher.json");

		public static LauncherSettings Load()
		{
			try
			{
				if (File.Exists(FilePath))
					return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new LauncherSettings();
			}
			catch (Exception)
			{
				// Corrupt or unreadable settings must never stop the launcher opening.
			}

			return new LauncherSettings();
		}

		public void Save()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
				File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
			}
			catch (Exception)
			{
			}
		}
	}
}
