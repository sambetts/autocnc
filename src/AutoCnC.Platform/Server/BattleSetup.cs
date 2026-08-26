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
using System.Linq;
using OpenRA;
using OpenRA.Network;
using OpenRA.Server;
using OpenRA.Traits;
using S = OpenRA.Server.Server;
namespace AutoCnC.Platform.Server
{
	/// <summary>
	/// Turns <c>Launch.Bot</c> and friends into a lobby: seats the requested test opponents,
	/// applies their handicap and faction, and leaves the game ready to start.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Launch.Map</c> on its own gets you a map with nobody to fight — the engine's
	/// <c>SkirmishLogic</c> only seats a bot for <see cref="ServerType.Skirmish"/>, and a launched
	/// map is a <see cref="ServerType.Local"/> game. This trait fills that gap, so the launcher
	/// can go from "press play" to "in a battle against a named AI" with no menus in between.
	/// </para>
	/// <para>
	/// It is a server trait rather than client-side UI automation because the lobby is server
	/// state: seating a bot is a host command, and doing it here means it has already happened by
	/// the time the first lobby sync reaches the client.
	/// </para>
	/// <para>
	/// It touches exactly one game — the one the command line asked for. Anything the player
	/// starts afterwards from the menus is theirs.
	/// </para>
	/// <para>
	/// C&amp;C's bots are personalities rather than difficulty tiers, so difficulty here is a
	/// personality plus a handicap — the same 0-95% knob the lobby exposes, which scales a
	/// player's firepower, durability and build speed. Handicapping the human instead is what
	/// makes a level above "the hardest bot, unhandicapped" possible.
	/// </para>
	/// </remarks>
	public class BattleSetup : ServerTrait, IClientJoined, IInterpretCommand
	{
		const string GameSpeedOption = "gamespeed";

		/// <summary>
		/// Static, and never reset: the launched battle is the first server the process creates,
		/// and every server after it is one the player asked for from the menus.
		/// </summary>
		/// <remarks>
		/// A server trait is constructed per server, so an instance field would let the next game
		/// be configured too — and <see cref="ServerType.Local"/> covers missions, loading a save
		/// and the in-game Restart, not just a launched map. Consuming the opportunity once is
		/// what keeps this feature from reaching into games it was never asked about.
		/// </remarks>
		static bool consumed;

		/// <summary>Set once this server turns out to be the launched battle.</summary>
		bool ours;

		/// <summary>The requested game speed, once validated against the map. Null to leave it be.</summary>
		string speed;

		void IClientJoined.ClientJoined(S server, Connection conn)
		{
			if (consumed || !LaunchOptions.HasBattle)
				return;

			// Only the game the command line asked for. A skirmish set up by hand from the menu
			// is ServerType.Skirmish and gets its opponents from the engine's own SkirmishLogic.
			if (server.Type != ServerType.Local)
				return;

			var client = server.GetClient(conn);
			if (client == null || !client.IsAdmin || client.Slot == null)
				return;

			// Whatever happens below, this was the launched battle and the chance is now spent.
			consumed = true;
			ours = true;

			speed = ResolveGameSpeed(server, LaunchOptions.GameSpeed);
			if (speed != null)
				server.InterpretCommand($"option {GameSpeedOption} {speed}", conn);

			// A speed-only launch is a legitimate thing to ask for: -Opponents 0 gives you the
			// map to yourself, which is how you watch an opening build order without pressure.
			if (string.IsNullOrEmpty(LaunchOptions.Bot))
				return;

			var bot = ResolveBot(server, LaunchOptions.Bot);
			if (bot == null)
				return;

			var seated = SeatBots(server, conn, client, bot);
			if (seated == 0)
			{
				Log.Write("server", "AutoC&C: no free bot slots on this map; launching without an opponent.");
				return;
			}

			ApplyHandicapsAndFactions(server, conn, client);

			Log.Write("server", $"AutoC&C: seated {seated}x {bot.Type} on handicap {LaunchOptions.BotHandicap}%, " +
				$"player on handicap {LaunchOptions.PlayerHandicap}%, speed {speed ?? "as the map likes it"}.");
		}

		/// <summary>
		/// Keeps the launched battle at the speed that was asked for.
		/// </summary>
		/// <remarks>
		/// <para>
		/// The engine starts a launched map by issuing a hardcoded <c>option gamespeed default</c>
		/// from the client, and it arrives after <see cref="IClientJoined"/> has already run — so
		/// setting the speed above is not enough on its own, it would simply be overwritten a
		/// moment later.
		/// </para>
		/// <para>
		/// Rather than race it, we swallow that one command and re-issue our own in its place,
		/// which leaves the normal lobby machinery to apply and broadcast the change. This is why
		/// the trait is listed ahead of <c>LobbyCommands</c> in mod.yaml: the server stops at the
		/// first trait that claims a command.
		/// </para>
		/// </remarks>
		bool IInterpretCommand.InterpretCommand(S server, Connection conn, Session.Client client, string cmd)
		{
			if (!ours || speed == null || cmd == null)
				return false;

			if (!cmd.StartsWith($"option {GameSpeedOption} ", StringComparison.Ordinal))
				return false;

			// Our own re-issue, and anything that already agrees with us, goes through untouched.
			if (cmd == $"option {GameSpeedOption} {speed}")
				return false;

			server.InterpretCommand($"option {GameSpeedOption} {speed}", conn);
			return true;
		}

		/// <summary>Validates a game speed against the map, returning null for "leave it alone".</summary>
		static string ResolveGameSpeed(S server, string requested)
		{
			if (string.IsNullOrEmpty(requested))
				return null;

			var option = server.Map.WorldActorInfo.TraitInfos<ILobbyOptions>()
				.SelectMany(t => t.LobbyOptions(server.Map))
				.FirstOrDefault(o => o.Id == GameSpeedOption);

			if (option == null)
			{
				Log.Write("server", $"AutoC&C: this map has no game speed option, ignoring Launch.GameSpeed={requested}.");
				return null;
			}

			if (option.IsLocked)
			{
				Log.Write("server", $"AutoC&C: this map locks the game speed, ignoring Launch.GameSpeed={requested}.");
				return null;
			}

			if (!option.Values.ContainsKey(requested))
			{
				Log.Write("server", $"AutoC&C: unknown game speed '{requested}', leaving it at the map default. " +
					$"Known speeds: {string.Join(", ", option.Values.Keys)}.");
				return null;
			}

			return requested;
		}

		/// <summary>The requested bot, or the mod's first bot if that name is not one of them.</summary>
		static IBotInfo ResolveBot(S server, string requested)
		{
			var bots = server.Map.PlayerActorInfo.TraitInfos<IBotInfo>().ToArray();
			var bot = bots.FirstOrDefault(b => b.Type == requested);
			if (bot != null)
				return bot;

			var fallback = bots.FirstOrDefault();
			Log.Write("server", fallback == null
				? $"AutoC&C: this mod defines no bots, ignoring Launch.Bot={requested}."
				: $"AutoC&C: unknown bot '{requested}', falling back to '{fallback.Type}'. " +
					$"Known bots: {string.Join(", ", bots.Select(b => b.Type))}.");

			return fallback;
		}

		static int SeatBots(S server, Connection conn, Session.Client host, IBotInfo bot)
		{
			var seated = 0;

			for (var i = 0; i < LaunchOptions.Opponents; i++)
			{
				var slot = server.LobbyInfo.FirstEmptyBotSlot();
				if (slot == null)
					break;

				// The host is the bot controller: its client is the one that issues their orders.
				if (!server.InterpretCommand($"slot_bot {slot} {host.Index} {bot.Type}", conn))
					break;

				seated++;
			}

			return seated;
		}

		static void ApplyHandicapsAndFactions(S server, Connection conn, Session.Client host)
		{
			var botFaction = ResolveFaction(server, LaunchOptions.BotFaction);
			var playerFaction = ResolveFaction(server, LaunchOptions.Faction);

			foreach (var bot in server.LobbyInfo.Clients.Where(c => c.IsBot && c.Slot != null).ToArray())
				Configure(server, conn, bot, LaunchOptions.BotHandicap, botFaction);

			Configure(server, conn, host, LaunchOptions.PlayerHandicap, playerFaction);
		}

		static void Configure(S server, Connection conn, Session.Client client, int handicap, string faction)
		{
			if (handicap > 0)
				server.InterpretCommand($"handicap {client.Index} {handicap}", conn);

			if (faction != null)
				server.InterpretCommand($"faction {client.Index} {faction}", conn);
		}

		/// <summary>
		/// Validates a faction name against the map, returning null for "let the engine pick".
		/// </summary>
		/// <remarks>
		/// The server's own <c>faction</c> handler validates the sending client's faction rather
		/// than the requested one, so an unknown name would be accepted and produce a player with
		/// no faction at all. Checking here keeps a typo from breaking the match.
		/// </remarks>
		static string ResolveFaction(S server, string requested)
		{
			if (string.IsNullOrEmpty(requested) || string.Equals(requested, "Random", StringComparison.OrdinalIgnoreCase))
				return null;

			var factions = server.Map.WorldActorInfo.TraitInfos<FactionInfo>()
				.Where(f => f.Selectable)
				.Select(f => f.InternalName)
				.ToArray();

			var match = factions.FirstOrDefault(f => string.Equals(f, requested, StringComparison.OrdinalIgnoreCase));
			if (match == null)
				Log.Write("server", $"AutoC&C: unknown faction '{requested}', leaving it random. " +
					$"Selectable factions: {string.Join(", ", factions)}.");

			return match;
		}
	}
}
