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
using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Platform
{
	/// <summary>
	/// Works out how the battle is going, from the point of view of the side fighting it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Everything about your own side is read exactly, because it is yours to read. Everything
	/// about the enemy goes through <see cref="ModeContext.IsVisibleEnemy"/> — the same predicate
	/// <c>ctx.SenseThreats</c> filters with — so a bot decides on what its units can see and not
	/// on what the process happens to know. A client simulates the whole world; that is precisely
	/// why the filtering has to be deliberate.
	/// </para>
	/// <para>
	/// Losses and kills are differences in <see cref="PlayerStatistics"/> across a rolling window
	/// rather than counted from notifications. The engine already keeps those totals, and a
	/// difference of two totals cannot drift the way a parallel tally can — the question a bot
	/// asks is "how much have I lost lately", and lately is exactly what a window is.
	/// </para>
	/// </remarks>
	public sealed class BattleAssessor
	{
		/// <summary>Milliseconds per tick at <c>default</c> speed, which fixes the time axis.</summary>
		const int NominalTimestep = Traits.TurboSpeed.NominalTimestep;

		/// <summary>One reading of the engine's running totals, and when it was taken.</summary>
		readonly record struct Totals(int Seconds, int UnitsDead, int BuildingsDead, int Killed);

		readonly World world;
		readonly Player self;
		readonly int windowSeconds;
		readonly int baseRadiusCells;

		/// <summary>Readings inside the window, oldest first. A handful of entries at most.</summary>
		readonly Queue<Totals> history = new();

		bool enemyBaseFound;
		int lastContactSeconds = -1;

		public BattleAssessor(World world, Player self, int windowSeconds, int baseRadiusCells)
		{
			this.world = world;
			this.self = self;
			this.windowSeconds = Math.Max(1, windowSeconds);
			this.baseRadiusCells = Math.Max(1, baseRadiusCells);
		}

		/// <summary>
		/// Reads the world and returns what the bot is allowed to know about it.
		/// </summary>
		/// <param name="doctrine">The doctrine running now.</param>
		/// <param name="doctrineSeconds">How long it has been running.</param>
		public BattleState Assess(string doctrine, int doctrineSeconds)
		{
			var seconds = (int)((long)world.WorldTick * NominalTimestep / 1000);

			var stats = self.PlayerActor.TraitOrDefault<PlayerStatistics>();
			var resources = self.PlayerActor.TraitOrDefault<PlayerResources>();
			var power = self.PlayerActor.TraitOrDefault<PowerManager>();

			var mine = Own();
			var seen = Contact(seconds, mine.Home);
			var lately = Recent(seconds, stats);

			return new BattleState(
				Seconds: seconds,
				Doctrine: doctrine,
				DoctrineSeconds: doctrineSeconds,

				Cash: resources?.GetCashAndResources() ?? 0,
				PowerBalance: power?.ExcessPower ?? 0,
				Harvesters: mine.Harvesters,
				Refineries: mine.Refineries,

				Units: mine.Units,
				ArmyValue: stats?.ArmyValue ?? 0,
				Buildings: mine.Buildings,
				BaseValue: mine.BaseValue,

				WindowSeconds: windowSeconds,
				UnitsLost: lately.UnitsLost,
				BuildingsLost: lately.BuildingsLost,
				UnitsKilled: lately.Killed,

				EnemiesInSight: seen.InSight,
				EnemiesNearBase: seen.NearBase,
				NearestEnemyCells: seen.NearestCells,
				SecondsSinceContact: lastContactSeconds < 0 ? -1 : seconds - lastContactSeconds,
				EnemyBaseFound: enemyBaseFound);
		}

		/// <summary>Your own side, counted from the world because all of it is yours to count.</summary>
		(int Units, int Buildings, int BaseValue, int Harvesters, int Refineries, CPos? Home) Own()
		{
			var units = 0;
			var buildings = 0;
			var baseValue = 0;
			var harvesters = 0;
			var refineries = 0;
			CPos? home = null;

			foreach (var actor in world.Actors)
			{
				if (actor.Owner != self || actor.IsDead || !actor.IsInWorld || actor.OccupiesSpace == null)
					continue;

				if (actor.Info.HasTraitInfo<BuildingInfo>())
				{
					buildings++;
					baseValue += actor.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

					if (actor.Info.HasTraitInfo<RefineryInfo>())
						refineries++;

					// The construction yard is home. Falling back to any building keeps
					// "near my base" meaningful for a player who has lost theirs.
					if (home == null || actor.Info.HasTraitInfo<BaseProviderInfo>())
						home = actor.Location;

					continue;
				}

				if (actor.Info.HasTraitInfo<HarvesterInfo>())
				{
					harvesters++;
					continue;
				}

				if (actor.Info.HasTraitInfo<MobileInfo>() || actor.Info.HasTraitInfo<AircraftInfo>())
					units++;
			}

			return (units, buildings, baseValue, harvesters, refineries, home);
		}

		/// <summary>
		/// The enemy, as far as you can see them — and no further.
		/// </summary>
		/// <remarks>
		/// <see cref="enemyBaseFound"/> and the contact clock are remembered rather than
		/// recomputed, because knowing where the enemy lives is not something you stop knowing
		/// when the shroud closes over it again. That is the one enemy fact a bot keeps, and it
		/// is one it genuinely learned.
		/// </remarks>
		(int InSight, int NearBase, int NearestCells) Contact(int seconds, CPos? home)
		{
			var inSight = 0;
			var nearBase = 0;
			var nearest = -1;

			foreach (var actor in world.Actors)
			{
				if (actor.OccupiesSpace == null || !ModeContext.IsVisibleEnemy(self, actor))
					continue;

				inSight++;

				if (actor.Info.HasTraitInfo<BuildingInfo>())
					enemyBaseFound = true;

				if (home == null)
					continue;

				var cells = (actor.Location - home.Value).Length;
				if (nearest < 0 || cells < nearest)
					nearest = cells;

				if (cells <= baseRadiusCells)
					nearBase++;
			}

			if (inSight > 0)
				lastContactSeconds = seconds;

			return (inSight, nearBase, nearest);
		}

		/// <summary>
		/// What the window cost you, as the difference between now and the oldest reading in it.
		/// </summary>
		/// <remarks>
		/// Early in a match there is nothing older than the window, and the answer is correctly
		/// the whole match so far.
		/// </remarks>
		(int UnitsLost, int BuildingsLost, int Killed) Recent(int seconds, PlayerStatistics stats)
		{
			var now = new Totals(
				seconds,
				stats?.UnitsDead ?? 0,
				stats?.BuildingsDead ?? 0,
				(stats?.UnitsKilled ?? 0) + (stats?.BuildingsKilled ?? 0));

			history.Enqueue(now);

			// Drop readings that have fallen out of the window, but never the last one: with an
			// empty history there is nothing to measure against.
			while (history.Count > 1 && seconds - history.Peek().Seconds > windowSeconds)
				history.Dequeue();

			var then = history.Peek();

			return (
				Math.Max(0, now.UnitsDead - then.UnitsDead),
				Math.Max(0, now.BuildingsDead - then.BuildingsDead),
				Math.Max(0, now.Killed - then.Killed));
		}
	}
}
