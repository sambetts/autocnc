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

namespace AutoCnC.Modes.Core
{
	/// <summary>
	/// A single observed enemy, flattened into engine-free primitives.
	/// </summary>
	/// <remarks>
	/// Distances are in OpenRA world units (1024 units == 1 cell) so that no floating
	/// point is required anywhere in decision logic. See docs/determinism.md.
	/// </remarks>
	public readonly record struct ThreatSnapshot(
		uint ActorId,
		int DistanceUnits,
		int HealthPercent,
		ThreatKind Kind,
		bool IsAttackable,
		bool CanHitUs);

	public enum ThreatKind : byte
	{
		Unknown = 0,
		Infantry = 1,
		Vehicle = 2,
		Aircraft = 3,
		Structure = 4,

		/// <summary>Structures that shoot back: turrets, pillboxes, SAM sites.</summary>
		Defence = 5,

		/// <summary>Economy actors: harvesters, refineries, silos.</summary>
		Economy = 6,
	}

	/// <summary>
	/// Everything <see cref="DefensiveLogic"/> is allowed to know about the world.
	/// </summary>
	public readonly record struct DefensiveState(
		int HealthPercent,
		int DistanceFromAnchorUnits,
		int WeaponRangeUnits,
		bool IsIdle,
		bool HasWeapon,
		bool CanMove,
		bool RepairAvailable,
		IReadOnlyList<ThreatSnapshot> Threats)
	{
		public static DefensiveState Empty { get; } = new(100, 0, 0, true, true, true, false, []);
	}

	/// <summary>
	/// Everything <see cref="AttackBaseLogic"/> is allowed to know about the world.
	/// </summary>
	public readonly record struct AssaultState(
		int HealthPercent,
		bool IsIdle,
		bool HasWeapon,
		bool CanMove,
		bool HasObjective,
		uint ObjectiveActorId,
		int DistanceToObjectiveUnits,
		int WeaponRangeUnits,
		IReadOnlyList<ThreatSnapshot> Threats)
	{
		public static AssaultState Empty { get; } = new(100, true, true, true, false, 0, 0, 0, []);
	}
}
