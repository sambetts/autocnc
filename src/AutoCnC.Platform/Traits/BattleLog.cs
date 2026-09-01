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
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Records the battle as your doctrine experienced it: who is playing, what your units",
		"saw, what hit them, what they lost and what they killed. Attach this to the world actor.",
		"Needs ReportsToBattleLog on the player actor for the damage and death events.")]
	public class BattleLogInfo : TraitInfo
	{
		[Desc("Where to write. A bare name lands in the support directory's Logs folder, next to",
			"debug.log; an absolute path is taken as given; 'none' records nothing.",
			"Launch.BattleLog on the command line overrides this.")]
		public readonly string File = "autocnc-battle.csv";

		[Desc("Seconds of game time between visibility scans. This is the resolution of a sighting,",
			"so it wants to sit near a unit's own evaluation interval — logging finer than your",
			"units think would claim a reaction time they do not have.")]
		public readonly int SightingInterval = 1;

		[Desc("Seconds an enemy must stay out of sight before seeing it again counts as a new",
			"sighting. Without it a unit sitting on a fog edge writes a line a second.")]
		public readonly int SightingCooldown = 30;

		[Desc("Seconds before the same actor being hit again is worth another line. Hits in",
			"between are counted and reported on the next one, so nothing is lost.")]
		public readonly int DamageCooldown = 5;

		public override object Create(ActorInitializer init) { return new BattleLog(init.World, this); }
	}

	/// <summary>
	/// Writes what happened to your side, in the order your code could have reacted to it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is deliberately <em>not</em> a record of the match. <see cref="MatchTelemetry"/> is
	/// that, and it is omniscient: a client simulates the whole world, so it can plot an
	/// opponent's army value whether or not you can see a single one of their units. Reading a
	/// doctrine's decisions against a feed like that teaches you nothing, because the decisions
	/// were made without it.
	/// </para>
	/// <para>
	/// So every event here is one the local player's code was in a position to act on, and the
	/// test for that is not a second opinion: sightings go through
	/// <see cref="ModeContext.IsVisibleEnemy"/>, the same predicate <c>ctx.SenseThreats</c> filters
	/// with, and damage arrives on the same notification that reaches
	/// <c>IUnitMode.OnDamaged</c>. If a line is in this file, a mode could have responded to it;
	/// if a mode could have responded to it, it is in this file.
	/// </para>
	/// <para>
	/// The file opens with a <c>player</c> row per side — name, faction, colour, whether it is a
	/// bot and which one is you — so a log read a week later still says who was fighting whom.
	/// Every event row then names the player it happened to and the player on the other end of
	/// it, which is what makes a three-way match readable at all.
	/// </para>
	/// </remarks>
	public class BattleLog : IWorldLoaded, ITick, IGameOver
	{
		/// <summary>Milliseconds per tick at <c>default</c> speed, which fixes the time axis.</summary>
		const int NominalTimestep = TurboSpeed.NominalTimestep;

		/// <summary>
		/// One row per event. <c>player</c> owns <c>actor</c> and <c>otherplayer</c> owns
		/// <c>otheractor</c>, on every row, so either end of an event can be filtered on. The two
		/// IDs are the engine's actor IDs, which is what makes "spotted, then hit by, then lost to"
		/// one story about one enemy rather than three rows that happen to name the same unit type.
		/// </summary>
		const string Header = "seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail";

		readonly World world;
		readonly BattleLogInfo info;

		/// <summary>Enemy actor ID to the tick we last saw it on, so a re-sighting is detectable.</summary>
		readonly Dictionary<uint, int> sighted = [];

		/// <summary>Our actor ID to when it last logged a hit, and the hits suppressed since.</summary>
		readonly Dictionary<uint, (int Tick, int Hits, int Damage)> hits = [];

		StreamWriter writer;
		Player self;
		int sightingTicks;
		int cooldownTicks;
		int damageTicks;
		int nextScanTick;

		public BattleLog(World world, BattleLogInfo info)
		{
			this.world = world;
			this.info = info;
		}

		/// <summary>True while there is somewhere to write to. Checked before doing any work.</summary>
		public bool IsRecording => writer != null;

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// The menu shellmap is a world too, and nobody wants a log of the attract mode.
			if (w.Type != WorldType.Regular)
				return;

			// A point of view is the whole premise, so a spectator has nothing to record.
			self = w.LocalPlayer;
			if (self == null || self.NonCombatant)
				return;

			var path = ResolvePath(LaunchOptions.BattleLog ?? info.File);
			if (path == null)
				return;

			writer = Open(path);
			if (writer == null)
				return;

			sightingTicks = Ticks(info.SightingInterval, 1);
			cooldownTicks = Ticks(info.SightingCooldown, 0);
			damageTicks = Ticks(info.DamageCooldown, 0);

			RecordRoster();

			// Subscribed after the map's own actors are in place, so the roster is not followed by
			// a page of 'built' rows for scenery. Starting units arrive later and are worth having.
			w.ActorAdded += Gained;

			Log.Write("debug", $"Battle log: recording {self.ResolvedPlayerName?.Trim()}'s battle to {path}.");
		}

		/// <summary>
		/// Names everybody in the match before the first event.
		/// </summary>
		/// <remarks>
		/// Lobby facts rather than anything observed, so recording all of them gives your code
		/// nothing it did not already have — the lobby is public. It is the events below that are
		/// filtered by what you can see.
		/// </remarks>
		void RecordRoster()
		{
			foreach (var player in world.Players.Where(p => p.Playable && !p.NonCombatant))
			{
				var relationship = player == self ? "you"
					: self.RelationshipWith(player).ToString().ToLowerInvariant();

				// The resolved faction for yourself and for bots, which have nothing to keep from
				// you; what the lobby showed for anybody else. A human who picked Random did not
				// publish what they got, and this file is the one thing here a person reads.
				var faction = player == self || player.IsBot ? player.Faction : player.DisplayFaction;

				Record("player", player, null, null, null,
					$"faction={faction?.InternalName ?? "?"}",
					$"bot={(player.IsBot ? 1 : 0)}",
					$"colour={player.Color.R:X2}{player.Color.G:X2}{player.Color.B:X2}",
					$"side={relationship}");
			}
		}

		void ITick.Tick(Actor actor)
		{
			if (writer == null || world.WorldTick < nextScanTick)
				return;

			Scan();
		}

		/// <summary>
		/// Logs every enemy that has come into view since the last scan.
		/// </summary>
		/// <remarks>
		/// Polled rather than driven off a reveal notification because "visible" is the answer to
		/// a shroud query, not an event the engine raises — and polling is what a mode does too,
		/// so a sighting lands on the log at the same granularity a unit would have noticed it.
		/// </remarks>
		void Scan()
		{
			nextScanTick = world.WorldTick + sightingTicks;

			var home = BaseCentre();

			foreach (var actor in world.Actors)
			{
				// Position first: an actor with no place on the map is not something a unit could
				// have seen, and asking it where it is throws.
				if (actor.OccupiesSpace == null || !ModeContext.IsVisibleEnemy(self, actor))
					continue;

				// Still in view since last time: keep the timestamp fresh, say nothing.
				if (sighted.TryGetValue(actor.ActorID, out var last) && world.WorldTick - last <= cooldownTicks)
				{
					sighted[actor.ActorID] = world.WorldTick;
					continue;
				}

				sighted[actor.ActorID] = world.WorldTick;

				Record("spotted", actor.Owner, actor, self, null,
					$"kind={ModeContext.Classify(actor)}",
					home.HasValue ? $"frombase={(actor.Location - home.Value).Length}" : null,
					last != 0 ? "again=1" : null);
			}

			// Anything not seen within the cooldown would be logged again on sight anyway, so the
			// entry has no further use. Dropping it here is what stops this growing all match.
			if (sighted.Count > 0)
				foreach (var id in sighted.Where(p => world.WorldTick - p.Value > cooldownTicks).Select(p => p.Key).ToArray())
					sighted.Remove(id);
		}

		/// <summary>An actor of ours entered the world: produced, placed, or unloaded.</summary>
		void Gained(Actor actor)
		{
			if (writer == null || actor.Owner != self)
				return;

			// Only things a plan can ask for. Crates, projectiles-as-actors and the like are not
			// something a doctrine builds, and would drown the rows that matter.
			if (!actor.Info.HasTraitInfo<ValuedInfo>())
				return;

			Record("built", self, actor, null, null,
				actor.Info.HasTraitInfo<BuildingInfo>() ? "kind=Building" : $"kind={ModeContext.Classify(actor)}");
		}

		/// <summary>
		/// One of our actors took damage — the same notification <c>IUnitMode.OnDamaged</c> gets.
		/// </summary>
		/// <remarks>
		/// Throttled per actor, because a machine gun raises this several times a second and a
		/// tank fight would otherwise be a thousand identical lines. The first hit is logged as it
		/// lands, since that is the moment a mode reacts; the ones suppressed behind it are
		/// counted into the next line rather than dropped, so the totals still add up.
		/// </remarks>
		internal void Damaged(Actor actor, AttackInfo attack)
		{
			// Negative damage is a repair. Nothing was done to us, so nothing happened.
			if (writer == null || actor.Owner != self || attack.Damage == null || attack.Damage.Value <= 0)
				return;

			var pending = (Tick: 0, Hits: 0, Damage: 0);
			if (hits.TryGetValue(actor.ActorID, out var previous))
			{
				if (world.WorldTick - previous.Tick < damageTicks)
				{
					hits[actor.ActorID] = (previous.Tick, previous.Hits + 1, previous.Damage + attack.Damage.Value);
					return;
				}

				pending = previous;
			}

			hits[actor.ActorID] = (world.WorldTick, 0, 0);

			var attacker = attack.Attacker;
			var health = actor.TraitOrDefault<IHealth>();

			Record("attacked", self, actor, attacker?.Owner, attacker,
				$"damage={pending.Damage + attack.Damage.Value}",
				pending.Hits > 0 ? $"hits={pending.Hits + 1}" : null,
				health != null && health.MaxHP > 0 ? $"health={health.HP * 100 / health.MaxHP}" : null,

				// Artillery and snipers hit from inside the fog. Your code still gets the attacker
				// on OnDamaged, so this is not a secret — but "who shot me and could I see them"
				// is the difference between a doctrine that can answer and one that cannot.
				attacker != null && !ModeContext.IsVisibleEnemy(self, attacker) ? "seen=0" : null);
		}

		/// <summary>An actor died. Ours is a loss; anything we could see and killed is a kill.</summary>
		internal void Killed(Actor actor, AttackInfo attack)
		{
			if (writer == null)
				return;

			var killer = attack.Attacker;

			if (actor.Owner == self)
			{
				hits.Remove(actor.ActorID);
				Record("lost", self, actor, killer?.Owner, killer);
				return;
			}

			// Somebody else's actor: only interesting if we are the ones who did it.
			if (killer == null || killer.Owner != self)
				return;

			// And only if we could see what we hit. This is the one event with no notification
			// behind it — there is no OnKilled for a mode to receive — so without a check it would
			// be the single row in the file reporting something the doctrine had no way to know.
			// Splash damage kills things in the fog, and it would name them, and say where.
			//
			// The sighting table is the check: Scan refreshes it every second for everything
			// currently in view, so an entry means we were looking at this actor a moment ago.
			// IsVisibleEnemy cannot answer here — the engine zeroes HP before raising Killed, so
			// by now the actor reads as dead and every visibility test says no.
			if (!sighted.Remove(actor.ActorID))
				return;

			Record("killed", actor.Owner, actor, self, killer);
		}

		/// <summary>
		/// The bot changed doctrine.
		/// </summary>
		/// <remarks>
		/// The most useful row in the file. Every other line says what the battle did to you; this
		/// one says what you decided about it, so the sightings and losses above and below a
		/// switch are the evidence for and the verdict on it.
		/// </remarks>
		internal void DoctrineChanged(string from, string to, string reason)
		{
			if (writer == null)
				return;

			Record("doctrine", self, null, null, null,
				$"from={Word(from)}",
				$"to={Word(to)}",
				string.IsNullOrWhiteSpace(reason) ? null : $"why={Word(reason)}");
		}

		/// <summary>
		/// A detail value that cannot break the <c>key=value</c> reading of the field it sits in.
		/// </summary>
		/// <remarks>
		/// Author-written reasons are prose, and prose has spaces in it. Underscores keep the
		/// whole reason attached to its key, which is what anything parsing this file expects.
		/// </remarks>
		static string Word(string value) =>
			string.IsNullOrEmpty(value) ? "" : value.Replace(' ', '_');

		void IGameOver.GameOver(World w)
		{
			if (writer == null)
				return;

			Record("over", self, null, null, null, $"result={self.WinState}");
			Close();
		}

		/// <summary>Our construction yard, for saying how deep into our territory a sighting was.</summary>
		CPos? BaseCentre()
		{
			foreach (var actor in world.ActorsHavingTrait<BaseProvider>())
				if (actor.Owner == self && !actor.IsDead && actor.IsInWorld)
					return actor.Location;

			return null;
		}

		/// <summary>
		/// Writes one row. <paramref name="details"/> are <c>key=value</c> pairs; nulls are
		/// dropped, so a caller can pass a fact it does not always have without a branch.
		/// </summary>
		/// <remarks>
		/// <para>
		/// The cell is taken from the subject rather than passed in: every event that happens
		/// somewhere happens to something, and deriving it here is one fewer thing a call site can
		/// get subtly wrong. An actor with no position on the map — which is a thing the engine
		/// has — simply has no cell.
		/// </para>
		/// <para>
		/// Nothing thrown here may escape. Two of the three callers are engine notifications
		/// raised mid-damage and mid-spawn, so an exception would not lose a line: it would take
		/// the match down. A log is a convenience, and it stops rather than costing anyone a game.
		/// </para>
		/// </remarks>
		void Record(string kind, Player player, Actor actor, Player otherPlayer, Actor otherActor,
			params string[] details)
		{
			try
			{
				var seconds = (long)world.WorldTick * NominalTimestep / 1000;
				var cell = actor?.OccupiesSpace != null ? actor.Location : (CPos?)null;

				writer.WriteLine(string.Join(',',
					seconds.ToString(CultureInfo.InvariantCulture),
					kind,

					// Trimmed because the engine pads enumerated bot names ("HAL 9001 2"), which
					// leaves a trailing space when there is only one of them.
					Escape(player?.ResolvedPlayerName?.Trim()),
					Escape(actor?.Info.Name),
					Id(actor),
					Escape(otherPlayer?.ResolvedPlayerName?.Trim()),
					Escape(otherActor?.Info.Name),
					Id(otherActor),
					cell?.X.ToString(CultureInfo.InvariantCulture) ?? "",
					cell?.Y.ToString(CultureInfo.InvariantCulture) ?? "",
					Escape(string.Join(' ', details.Where(d => d != null)))));
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Battle log: stopped recording after {ex.Message}");
				Close();
			}
		}

		/// <summary>Stops recording, for good. Safe to call more than once.</summary>
		void Close()
		{
			world.ActorAdded -= Gained;

			try
			{
				writer?.Dispose();
			}
			catch (IOException)
			{
			}

			writer = null;
		}

		static string Id(Actor actor) =>
			actor == null ? "" : actor.ActorID.ToString(CultureInfo.InvariantCulture);

		int Ticks(int seconds, int minimum) =>
			Math.Max(minimum, 1000 * Math.Max(0, seconds) / NominalTimestep);

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
				// A log is a convenience. Losing it must never cost anyone their match.
				Log.Write("debug", $"Battle log: could not write {path}: {ex.Message}");
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

	[TraitLocation(SystemActors.Player)]
	[Desc("Forwards this player's damage and death notifications to BattleLog on the world actor.",
		"Attach this to the player actor; it does nothing without BattleLog.")]
	public class ReportsToBattleLogInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new ReportsToBattleLog(init.World); }
	}

	/// <summary>
	/// The player-actor half of <see cref="BattleLog"/>.
	/// </summary>
	/// <remarks>
	/// A separate trait because of where the engine delivers the notifications: <c>Health</c>
	/// raises <see cref="INotifyDamage"/> and <see cref="INotifyKilled"/> on the damaged actor and
	/// on <em>its owner's player actor</em>, and never on the world actor. Sitting here means
	/// every actor a player owns is covered — including the buildings that have no
	/// <see cref="ProgrammableController"/> of their own — with nothing to add per actor type.
	/// </remarks>
	public class ReportsToBattleLog : INotifyDamage, INotifyKilled
	{
		readonly World world;
		BattleLog log;

		public ReportsToBattleLog(World world)
		{
			this.world = world;
		}

		/// <summary>
		/// Resolved on first use rather than on creation: player actors are constructed before the
		/// world actor's traits have finished loading, and a match with no log at all is the norm.
		/// </summary>
		BattleLog Log => log ??= world.WorldActor.TraitOrDefault<BattleLog>();

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			var battleLog = Log;
			if (battleLog != null && battleLog.IsRecording)
				battleLog.Damaged(self, e);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			var battleLog = Log;
			if (battleLog != null && battleLog.IsRecording)
				battleLog.Killed(self, e);
		}
	}
}
