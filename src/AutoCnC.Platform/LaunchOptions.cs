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
using OpenRA;

namespace AutoCnC.Platform
{
	/// <summary>
	/// AutoC&amp;C's own <c>Launch.*</c> command line arguments, which drop the player straight into
	/// a scripted test battle instead of the main menu.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The engine parses a fixed set of <c>Launch.*</c> arguments into
	/// <see cref="LaunchArguments"/> and silently ignores the rest, so a mod can add its own
	/// simply by reading the process command line again. That keeps this feature entirely inside
	/// the AutoC&amp;C assemblies — the pinned engine submodule stays untouched.
	/// </para>
	/// <para>
	/// These are read by two very different consumers — the mode executor on the client and
	/// <see cref="Server.BattleSetup"/> on the (in-process) server — hence a static that both can
	/// reach rather than a value passed down a call chain.
	/// </para>
	/// </remarks>
	public static class LaunchOptions
	{
		/// <summary>Handicaps are only accepted by the server in 5% steps, up to 95%.</summary>
		public const int MaxHandicap = 95;
		public const int HandicapStep = 5;

		static readonly Arguments Args = Parse();

		static Arguments Parse()
		{
			try
			{
				// Skip [0], which is the executable rather than an argument.
				var argv = Environment.GetCommandLineArgs();
				var launchArgs = new string[Math.Max(0, argv.Length - 1)];
				Array.Copy(argv, 1, launchArgs, 0, launchArgs.Length);
				return new Arguments(launchArgs);
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Could not read AutoC&C launch arguments: {ex}");
				return Arguments.Empty;
			}
		}

		/// <summary>Name of the doctrine to load once the world is up. Optional.</summary>
		public static string Doctrine => Value("Launch.Doctrine");

		/// <summary>
		/// A doctrine assembly, or a folder of them, to load in addition to the usual search
		/// paths. Lets the launcher play a build straight out of its own output folder instead of
		/// copying it into the engine.
		/// </summary>
		public static string DoctrinePath => Value("Launch.DoctrinePath");

		/// <summary>Bot type for the test opponent, e.g. <c>hal9001</c>. Blank means no auto battle.</summary>
		public static string Bot => Value("Launch.Bot");

		/// <summary>How many copies of <see cref="Bot"/> to add. Clamped to the free slots.</summary>
		public static int Opponents => Clamp(Integer("Launch.Opponents", 1), 1, 32);

		/// <summary>Handicap applied to every test opponent: higher is weaker, so lower difficulty.</summary>
		public static int BotHandicap => Handicap("Launch.BotHandicap");

		/// <summary>Handicap applied to the human player, for difficulties above the hardest bot.</summary>
		public static int PlayerHandicap => Handicap("Launch.Handicap");

		/// <summary>Faction for the human player. Blank or <c>Random</c> leaves the engine to pick.</summary>
		public static string Faction => Value("Launch.Faction");

		/// <summary>Faction for the test opponents. Blank or <c>Random</c> leaves the engine to pick.</summary>
		public static string BotFaction => Value("Launch.BotFaction");

		/// <summary>
		/// Game speed key, e.g. <c>fastest</c>. Blank leaves the map's own default alone.
		/// </summary>
		public static string GameSpeed => Value("Launch.GameSpeed");

		/// <summary>
		/// Where to write the match telemetry CSV. Blank leaves the trait's own setting alone.
		/// The launcher names a file per run so it never graphs a previous match by mistake.
		/// </summary>
		public static string Telemetry => Value("Launch.Telemetry");

		/// <summary>
		/// The engine's own map argument. We only read it to recognise the battle this process
		/// was launched into, which is the one — and the only one — we are entitled to set up.
		/// </summary>
		public static string Map => Value("Launch.Map");

		/// <summary>
		/// True when the command line asked us to set up the game it launched — an opponent, a
		/// speed, or both. All of it hangs off <c>Launch.Map</c>, since that is the game we mean.
		/// </summary>
		public static bool HasBattle =>
			!string.IsNullOrEmpty(Map) && (!string.IsNullOrEmpty(Bot) || !string.IsNullOrEmpty(GameSpeed));

		static string Value(string key)
		{
			var value = Args.GetValue(key, null);
			return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
		}

		static int Integer(string key, int fallback)
		{
			var value = Value(key);
			return value != null && int.TryParse(value, out var parsed) ? parsed : fallback;
		}

		/// <summary>Reads a handicap and snaps it to what the server will actually accept.</summary>
		static int Handicap(string key)
		{
			var requested = Clamp(Integer(key, 0), 0, MaxHandicap);
			return requested / HandicapStep * HandicapStep;
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
