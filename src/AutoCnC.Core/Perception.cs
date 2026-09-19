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

namespace AutoCnC.Core
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
		bool CanHitUs)
	{
		/// <summary>Engine actor type, e.g. <c>e1</c> or <c>mtnk</c>.</summary>
		public string ActorType { get; init; }

		/// <summary>Current map cell X coordinate.</summary>
		public int CellX { get; init; }

		/// <summary>Current map cell Y coordinate.</summary>
		public int CellY { get; init; }

		/// <summary>Build cost/value, or zero when the actor has no value.</summary>
		public int Value { get; init; }

		/// <summary>Longest enabled weapon range in world units, or zero when unarmed.</summary>
		public int WeaponRangeUnits { get; init; }
	}

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
	/// What one cell holds, as a mode sees it. <see cref="Empty"/> when there is nothing to cut.
	/// </summary>
	/// <remarks>
	/// Cell coordinates rather than world units, because every order a harvester can be given
	/// names a cell.
	/// </remarks>
	public readonly record struct ResourceCell(int X, int Y, string ResourceType, int Density)
	{
		public static ResourceCell Empty { get; } = default;

		public bool HasResource => ResourceType != null && Density > 0;
	}

	/// <summary>
	/// A contiguous patch of harvestable resource — one tiberium field — flattened into
	/// engine-free primitives.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <see cref="NearestX"/>/<see cref="NearestY"/> is the cell of this field closest to whoever
	/// asked for it, and is the cell to send a harvester to: aiming at
	/// <see cref="CenterX"/>/<see cref="CenterY"/> drives it through the field to the far side.
	/// </para>
	/// <para>
	/// <see cref="TotalDensity"/> is the sum of every cell's density, so it says how much is
	/// actually left rather than how wide the patch once was. A field that has been mined out to
	/// a thin rind has a large <see cref="CellCount"/> and a small <see cref="TotalDensity"/>.
	/// </para>
	/// </remarks>
	public readonly record struct ResourceField(
		int CenterX,
		int CenterY,
		int NearestX,
		int NearestY,
		int DistanceUnits,
		int CellCount,
		int TotalDensity,
		string ResourceType);

	/// <summary>An owned live building, flattened into engine-free repair state.</summary>
	public readonly record struct OwnedBuildingState(
		uint ActorId,
		string ActorType,
		int CellX,
		int CellY,
		int HealthPercent,
		bool IsRepairable,
		bool RepairRequested,
		bool RepairActive);

	/// <summary>An owned support power registered with the player's support-power manager.</summary>
	public readonly record struct SupportPowerState(
		string Key,
		string OrderName,
		bool Active,
		bool Ready,
		bool Disabled,
		int RemainingTicks,
		int TotalTicks);

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
