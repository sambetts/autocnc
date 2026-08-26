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
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Records each player's unit count, army value and economy to a CSV while the match runs,",
		"for the launcher's match graph and for reading afterwards. Attach this to the world actor.")]
	public class MatchTelemetryInfo : TraitInfo
	{
		[Desc("Seconds of game time between samples. Game time rather than wall-clock, so a match",
			"watched at 20x plots against the same axis as one watched at 1x.")]
		public readonly int SampleInterval = 1;

		[Desc("Where to write. A bare name lands in the support directory's Logs folder, next to",
			"debug.log; an absolute path is taken as given; empty records nothing.",
			"Launch.Telemetry on the command line overrides this.")]
		public readonly string File = "autocnc-telemetry.csv";

		public override object Create(ActorInitializer init) { return new MatchTelemetry(init.World, this); }
	}

	/// <summary>
	/// Writes a running record of how every player's army and economy is doing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A doctrine wins or loses over twenty minutes, in ways no single moment on screen shows you:
	/// an army count that plateaus at eight, a bank balance that climbs because nothing is
	/// spending it, a value curve that crosses the opponent's and then falls back. This is that
	/// record, sampled on game time so runs at different speeds stay comparable, and left on disk
	/// so a match can be read after it has been won.
	/// </para>
	/// <para>
	/// Everything here is exact rather than estimated, because a client simulates the whole world:
	/// fog hides actors from the <em>player</em>, not from the process. That is precisely why this
	/// is a file for a human to look at and not something a doctrine can reach. Mode code that
	/// wants to know about the enemy goes through <c>ModeContext</c>, which filters by visibility —
	/// see docs/determinism.md.
	/// </para>
	/// <para>
	/// The same reasoning caps what gets recorded: against a human opponent, a live feed of their
	/// army value would be a maphack with a graph on it, so only the local player is recorded
	/// unless every opponent is a bot, or we are watching a replay.
	/// </para>
	/// </remarks>
	public class MatchTelemetry : IWorldLoaded, ITickRender, IGameOver
	{
		/// <summary>Milliseconds per tick at <c>default</c> speed, which fixes the time axis.</summary>
		const int NominalTimestep = TurboSpeed.NominalTimestep;

		const string Header = "seconds,player,faction,bot,colour,units,army,buildings,basevalue," +
			"assets,cash,killed,lost,buildingskilled,buildingslost,state";

		readonly World world;
		readonly MatchTelemetryInfo info;

		/// <summary>Buildings owned, tallied once per sample and read back per player.</summary>
		readonly Dictionary<Player, (int Count, int Value)> bases = [];

		/// <summary>Build cost per actor type, because it never changes and the lookup is not free.</summary>
		readonly Dictionary<ActorInfo, int> costs = [];

		StreamWriter writer;
		Player[] recorded = [];
		int ticksPerSample;
		int nextSampleTick;

		public MatchTelemetry(World world, MatchTelemetryInfo info)
		{
			this.world = world;
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// The menu shellmap is a world too, and nobody wants a graph of the attract mode.
			if (w.Type != WorldType.Regular)
				return;

			var path = ResolvePath(LaunchOptions.Telemetry ?? info.File);
			if (path == null)
				return;

			var playable = w.Players.Where(p => p.Playable && !p.NonCombatant).ToArray();

			// See the class remarks: a live read-out of a human opponent's army is a maphack.
			var humanOpponents = playable.Any(p => !p.IsBot && p != w.LocalPlayer);
			recorded = !humanOpponents || w.IsReplay
				? playable
				: playable.Where(p => p == w.LocalPlayer).ToArray();

			if (recorded.Length == 0)
				return;

			writer = Open(path);
			if (writer == null)
				return;

			if (humanOpponents && !w.IsReplay)
				Log.Write("debug", "Telemetry: human opponent present, so only your own side is recorded.");

			Log.Write("debug", $"Telemetry: {recorded.Length} player(s) to {path}, every {info.SampleInterval}s of game time.");

			ticksPerSample = Math.Max(1, 1000 * Math.Max(1, info.SampleInterval) / NominalTimestep);
			Sample();
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			// WorldTick stands still while the game is paused, so this samples game time without
			// having to ask whether the game is paused, and without the wall clock coming into it.
			if (writer == null || world.WorldTick < nextSampleTick)
				return;

			Sample();
		}

		void IGameOver.GameOver(World w)
		{
			// One last row, so the file ends on the result rather than a second or so before it.
			if (writer == null)
				return;

			Sample();
			writer.Dispose();
			writer = null;
		}

		void Sample()
		{
			nextSampleTick = world.WorldTick + ticksPerSample;

			var seconds = (long)world.WorldTick * NominalTimestep / 1000;
			TallyBases();

			foreach (var player in recorded)
			{
				var stats = player.PlayerActor.TraitOrDefault<PlayerStatistics>();
				var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (stats == null)
					continue;

				// Units[..].Count follows the same AddToArmyValue rule the army value does, so the
				// count and the value are always talking about the same set of things.
				var units = stats.Units.Values.Sum(u => u.Count);
				bases.TryGetValue(player, out var owned);

				writer.WriteLine(string.Join(',',
					seconds.ToString(CultureInfo.InvariantCulture),

					// Trimmed because the engine pads enumerated bot names ("HAL 9001 2"), which
					// leaves a trailing space when there is only one of them.
					Escape(player.ResolvedPlayerName?.Trim()),
					Escape(player.Faction?.InternalName ?? ""),
					player.IsBot ? 1 : 0,
					$"{player.Color.R:X2}{player.Color.G:X2}{player.Color.B:X2}",
					units.ToString(CultureInfo.InvariantCulture),
					stats.ArmyValue.ToString(CultureInfo.InvariantCulture),
					owned.Count.ToString(CultureInfo.InvariantCulture),
					owned.Value.ToString(CultureInfo.InvariantCulture),
					stats.AssetsValue.ToString(CultureInfo.InvariantCulture),
					(resources?.Cash + resources?.Resources ?? 0).ToString(CultureInfo.InvariantCulture),
					stats.UnitsKilled.ToString(CultureInfo.InvariantCulture),
					stats.UnitsDead.ToString(CultureInfo.InvariantCulture),
					stats.BuildingsKilled.ToString(CultureInfo.InvariantCulture),
					stats.BuildingsDead.ToString(CultureInfo.InvariantCulture),
					player.WinState.ToString()));
			}
		}

		/// <summary>
		/// Counts and values every standing building, per player.
		/// </summary>
		/// <remarks>
		/// Counted from the world rather than read off <see cref="PlayerStatistics"/>, which has no
		/// equivalent of its army figures for structures: its assets value lumps buildings in with
		/// harvesters and everything else a player owns. Walked once for all players rather than
		/// once each, since the interesting matches are the ones with several of them.
		/// </remarks>
		void TallyBases()
		{
			bases.Clear();

			foreach (var actor in world.ActorsHavingTrait<Building>())
			{
				if (actor.IsDead || !actor.IsInWorld || actor.Owner == null)
					continue;

				bases.TryGetValue(actor.Owner, out var tally);
				bases[actor.Owner] = (tally.Count + 1, tally.Value + Cost(actor.Info));
			}
		}

		int Cost(ActorInfo actorInfo)
		{
			if (costs.TryGetValue(actorInfo, out var cost))
				return cost;

			cost = actorInfo.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			costs.Add(actorInfo, cost);
			return cost;
		}

		/// <summary>Opens the file for writing, rotating any previous match out of the way first.</summary>
		static StreamWriter Open(string path)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));

				// Same one-deep rotation the engine's own logs use: the last match survives a
				// relaunch, which is exactly long enough to notice you wanted to keep it.
				if (System.IO.File.Exists(path))
					System.IO.File.Move(path, path + ".1", true);

				// Shared so the launcher can read the file while this process is still writing it,
				// and flushed per line so what it reads is never a second behind.
				var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
				var writer = new StreamWriter(stream) { AutoFlush = true };
				writer.WriteLine(Header);
				return writer;
			}
			catch (Exception ex)
			{
				// Telemetry is a convenience. Losing it must never cost anyone their match.
				Log.Write("debug", $"Telemetry: could not write {path}: {ex.Message}");
				return null;
			}
		}

		static string ResolvePath(string file)
		{
			// "none" is how a command line says "off": there is no way to pass an empty value.
			if (string.IsNullOrWhiteSpace(file) || string.Equals(file, "none", StringComparison.OrdinalIgnoreCase))
				return null;

			// Fully qualified: this assembly's own namespace is AutoCnC.Platform, which otherwise
			// shadows the engine's Platform class.
			return Path.IsPathRooted(file) ? file : Path.Combine(OpenRA.Platform.SupportDir, "Logs", file);
		}

		/// <summary>Player names are whatever somebody typed, so they can contain commas.</summary>
		static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "";

			if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
				return value;

			return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
		}
	}
}
