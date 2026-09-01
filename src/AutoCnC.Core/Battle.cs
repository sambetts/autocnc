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

namespace AutoCnC.Core
{
	/// <summary>
	/// How the battle is going, as far as your side can tell.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is what a battle bot decides on, and "as far as your side can tell" is the whole point
	/// of it. Everything about your own economy and army is exact, because it is yours. Everything
	/// about the enemy is only what you can currently <em>see</em>: the platform fills these
	/// fields in through the same visibility rule <c>ctx.SenseThreats</c> filters with, so a bot
	/// cannot switch to an attack doctrine on the strength of an army value no unit of yours has
	/// laid eyes on.
	/// </para>
	/// <para>
	/// Plain values with no engine types, so the interesting half of a bot — deciding — is a pure
	/// function you can unit-test in milliseconds without launching a game.
	/// </para>
	/// </remarks>
	public readonly record struct BattleState(
		// When
		int Seconds,                 // game time since the match started
		string Doctrine,             // the doctrine running right now
		int DoctrineSeconds,         // how long it has been running: your hysteresis lives here

		// Your economy
		int Cash,                    // cash plus banked resources
		int PowerBalance,            // spare power; negative is a brownout
		int Harvesters,
		int Refineries,

		// Your forces
		int Units,                   // mobile units, counted the way army value is
		int ArmyValue,               // what they cost to build
		int Buildings,
		int BaseValue,               // what your standing structures cost to build

		// What has happened lately
		int WindowSeconds,           // how far back the next three reach
		int UnitsLost,
		int BuildingsLost,           // the "am I being dismantled" number
		int UnitsKilled,

		// What you can see of the enemy, and nothing you cannot
		int EnemiesInSight,          // visible enemies anywhere on the map
		int EnemiesNearBase,         // of those, how many are at your door
		int NearestEnemyCells,       // cells from your base to the nearest, or -1 for none
		int SecondsSinceContact,     // since you last saw any enemy, or -1 if you never have
		bool EnemyBaseFound)         // true once you have seen an enemy structure
	{
		/// <summary>Nothing known yet: no doctrine, no contact, nothing built.</summary>
		public static BattleState Empty { get; } = new()
		{
			NearestEnemyCells = -1,
			SecondsSinceContact = -1
		};

		/// <summary>Something is being done to you at home, rather than to you in the field.</summary>
		public bool BaseUnderAttack => BuildingsLost > 0 || EnemiesNearBase > 0;

		/// <summary>You do not know where the enemy is, and cannot see one to ask.</summary>
		public bool BlindToEnemy => !EnemyBaseFound && EnemiesInSight == 0;

		/// <summary>You are trading well and nothing of yours has fallen over.</summary>
		public bool Winning => UnitsKilled > UnitsLost && BuildingsLost == 0;
	}

	/// <summary>
	/// What a battle bot decided to do about the doctrine it is running.
	/// </summary>
	/// <remarks>
	/// A decision rather than an action, for the same reason <see cref="UnitDecision"/> is one: it
	/// can be asserted in a test, written to the battle log, and compared against the last one, so
	/// a bot that keeps deciding the same thing costs nothing and says nothing.
	/// </remarks>
	public readonly record struct DoctrineDecision(string Doctrine, string Reason)
	{
		/// <summary>Stay on the current doctrine. The common answer, and the cheap one.</summary>
		public static DoctrineDecision Continue => default;

		/// <summary>Change to a named doctrine, and say why. The reason reaches the battle log.</summary>
		public static DoctrineDecision SwitchTo(string doctrine, string reason) => new(doctrine, reason);

		public bool WantsChange => !string.IsNullOrEmpty(Doctrine);
	}
}
