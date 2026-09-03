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
	/// <para>
	/// <c>Launch.Doctrine</c> and <c>Launch.DoctrinePath</c> are still accepted for
	/// <see cref="BattleBot"/> and <see cref="BattleBotPath"/>. A battle used to be played with a
	/// doctrine rather than a bot, and a command line somebody has in their shell history should
	/// not stop working over a rename.
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

		/// <summary>Name of the battle bot to load once the world is up. Optional.</summary>
		public static string BattleBot => Value("Launch.BattleBot") ?? Value("Launch.Doctrine");

		/// <summary>
		/// A battle bot assembly, or a folder of them, to load in addition to the usual search
		/// paths. Lets the launcher play a build straight out of its own output folder instead of
		/// copying it into the engine.
		/// </summary>
		public static string BattleBotPath => Value("Launch.BattleBotPath") ?? Value("Launch.DoctrinePath");

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
		/// Where to write the battle log — what your side saw, took and did. Blank leaves the
		/// trait's own setting alone; the launcher names a file per run so the window it opens is
		/// showing the battle it just started.
		/// </summary>
		public static string BattleLog => Value("Launch.BattleLog");

		/// <summary>
		/// Where to write the newline-delimited JSON trace connecting assessments and mode
		/// decisions to the orders they produced. Blank disables the trace.
		/// </summary>
		public static string DecisionTrace => Value("Launch.DecisionTrace");

		/// <summary>Run the world from the CPU-speed client loop instead of the rendered scheduler.</summary>
		public static bool Headless => Boolean("Launch.Headless");

		/// <summary>Where the headless runner writes measured simulation throughput.</summary>
		public static string HeadlessReport => Value("Launch.HeadlessReport");

		/// <summary>
		/// A sentinel file checked by the headless loop. Creating it requests a clean game end.
		/// </summary>
		public static string CancellationFile => Value("Launch.CancellationFile");

		/// <summary>
		/// Maximum nominal game seconds for a headless match. Zero allows an unlimited match.
		/// </summary>
		public static int HeadlessMaxGameSeconds => Math.Max(0, Integer("Launch.HeadlessMaxGameSeconds", 90 * 60));

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

		static bool Boolean(string key)
		{
			var value = Value(key);
			return value == "1" || value != null && bool.TryParse(value, out var parsed) && parsed;
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
