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
	public enum UnitAction : byte
	{
		/// <summary>Do nothing; leave the unit to carry on with whatever it is doing.</summary>
		Continue = 0,

		/// <summary>Stop and stand still.</summary>
		Hold = 1,

		/// <summary>Attack <see cref="UnitDecision.TargetActorId"/>.</summary>
		Attack = 2,

		/// <summary>Move back to the unit's anchor without seeking combat.</summary>
		ReturnToAnchor = 3,

		/// <summary>Break off and head for repair.</summary>
		Retreat = 4,

		/// <summary>Advance on <see cref="UnitDecision.TargetActorId"/>, engaging what blocks the path.</summary>
		AdvanceToObjective = 5,

		/// <summary>Move to the cell in <see cref="UnitDecision.TargetX"/>/<see cref="UnitDecision.TargetY"/>.</summary>
		MoveTo = 6,

		/// <summary>Attack-move to the cell in <see cref="UnitDecision.TargetX"/>/<see cref="UnitDecision.TargetY"/>.</summary>
		AttackMoveTo = 7,

		/// <summary>Deploy this unit, e.g. an MCV unfolding into a construction yard.</summary>
		Deploy = 8,

		/// <summary>Start producing <see cref="UnitDecision.ItemName"/> from this actor's queue.</summary>
		Produce = 9,

		/// <summary>
		/// Place the finished building <see cref="UnitDecision.ItemName"/> at
		/// <see cref="UnitDecision.TargetX"/>/<see cref="UnitDecision.TargetY"/>.
		/// </summary>
		PlaceBuilding = 10,

		/// <summary>
		/// Harvest the resource at <see cref="UnitDecision.TargetX"/>/<see cref="UnitDecision.TargetY"/>,
		/// then keep harvesting and delivering from there without further orders.
		/// </summary>
		/// <remarks>
		/// The action to send a harvester to a field, in preference to <see cref="MoveTo"/>. A move
		/// order puts the harvester on the cell and stops; this one re-centres the engine's own
		/// harvest-and-deliver loop on the named field, which is the only way to get a harvester to
		/// work ground further out than its search radius reaches.
		/// </remarks>
		Harvest = 11,

		/// <summary>
		/// Start repairing the owned building in <see cref="UnitDecision.TargetActorId"/>.
		/// </summary>
		RepairBuilding = 12,

		/// <summary>
		/// Cancel an exact number of <see cref="UnitDecision.ItemName"/> entries from
		/// <see cref="UnitDecision.Queue"/>.
		/// </summary>
		CancelProduction = 13,

		/// <summary>
		/// Activate the configured support power in <see cref="UnitDecision.ItemName"/> at
		/// <see cref="UnitDecision.TargetX"/>/<see cref="UnitDecision.TargetY"/>.
		/// </summary>
		ActivateSupportPower = 14,
	}

	/// <summary>
	/// The complete output of a mode's decision step.
	/// </summary>
	/// <remarks>
	/// A decision is <i>data</i>, not an action. Keeping it inert is what lets us assert on it in
	/// tests, log it, and render it as a debug overlay. It also lets the executor compare intent
	/// between ticks and only send an order when something actually changed.
	/// </remarks>
	public readonly record struct UnitDecision(
		UnitAction Action,
		uint TargetActorId,
		int TargetX,
		int TargetY,
		string ItemName,
		string Queue,
		string Reason)
	{
		public static readonly UnitDecision Continue = new(UnitAction.Continue, 0, 0, 0, null, null, "no change");

		/// <summary>
		/// Stable machine-readable explanation for this decision. Null on decisions created
		/// through the legacy factories or positional constructor.
		/// </summary>
		/// <remarks>
		/// Keep this stable when the human-readable <see cref="Reason"/> changes. Dotted or
		/// kebab-case identifiers such as <c>combat.focus-armour</c> work well in evidence checks.
		/// It is deliberately not positional, so the historical seven-argument constructor and
		/// deconstruction shape remain source-compatible.
		/// </remarks>
		public string ReasonId { get; init; }

		/// <summary>
		/// Number of matching queue entries requested by <see cref="CancelProduction"/>.
		/// Zero for every other action.
		/// </summary>
		public int Count => Action == UnitAction.CancelProduction ? TargetX : 0;

		/// <summary>
		/// Configured support-power key or order name requested by <see cref="ActivateSupportPower"/>.
		/// Null for every other action.
		/// </summary>
		public string Power => Action == UnitAction.ActivateSupportPower ? ItemName : null;

		public static UnitDecision Hold(string reason) => Hold(reason, null);
		public static UnitDecision Hold(string reason, string reasonId) =>
			new(UnitAction.Hold, 0, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision Attack(uint targetActorId, string reason) => Attack(targetActorId, reason, null);
		public static UnitDecision Attack(uint targetActorId, string reason, string reasonId) =>
			new(UnitAction.Attack, targetActorId, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision ReturnToAnchor(string reason) => ReturnToAnchor(reason, null);
		public static UnitDecision ReturnToAnchor(string reason, string reasonId) =>
			new(UnitAction.ReturnToAnchor, 0, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision Retreat(string reason) => Retreat(reason, null);
		public static UnitDecision Retreat(string reason, string reasonId) =>
			new(UnitAction.Retreat, 0, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision AdvanceToObjective(uint objectiveActorId, string reason) =>
			AdvanceToObjective(objectiveActorId, reason, null);

		public static UnitDecision AdvanceToObjective(uint objectiveActorId, string reason, string reasonId) =>
			new(UnitAction.AdvanceToObjective, objectiveActorId, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision MoveTo(int x, int y, string reason) => MoveTo(x, y, reason, null);
		public static UnitDecision MoveTo(int x, int y, string reason, string reasonId) =>
			new(UnitAction.MoveTo, 0, x, y, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision AttackMoveTo(int x, int y, string reason) => AttackMoveTo(x, y, reason, null);
		public static UnitDecision AttackMoveTo(int x, int y, string reason, string reasonId) =>
			new(UnitAction.AttackMoveTo, 0, x, y, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision Deploy(string reason) => Deploy(reason, null);
		public static UnitDecision Deploy(string reason, string reasonId) =>
			new(UnitAction.Deploy, 0, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision Produce(string queue, string itemName, string reason) =>
			Produce(queue, itemName, reason, null);

		public static UnitDecision Produce(string queue, string itemName, string reason, string reasonId) =>
			new(UnitAction.Produce, 0, 0, 0, itemName, queue, reason) { ReasonId = reasonId };

		public static UnitDecision PlaceBuilding(string queue, string itemName, int x, int y, string reason) =>
			PlaceBuilding(queue, itemName, x, y, reason, null);

		public static UnitDecision PlaceBuilding(string queue, string itemName, int x, int y,
			string reason, string reasonId) =>
			new(UnitAction.PlaceBuilding, 0, x, y, itemName, queue, reason) { ReasonId = reasonId };

		public static UnitDecision Harvest(int x, int y, string reason) => Harvest(x, y, reason, null);
		public static UnitDecision Harvest(int x, int y, string reason, string reasonId) =>
			new(UnitAction.Harvest, 0, x, y, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision RepairBuilding(uint targetActorId, string reason) =>
			RepairBuilding(targetActorId, reason, null);

		public static UnitDecision RepairBuilding(uint targetActorId, string reason, string reasonId) =>
			new(UnitAction.RepairBuilding, targetActorId, 0, 0, null, null, reason) { ReasonId = reasonId };

		public static UnitDecision CancelProduction(string queue, string itemName, int count, string reason) =>
			CancelProduction(queue, itemName, count, reason, null);

		public static UnitDecision CancelProduction(
			string queue, string itemName, int count, string reason, string reasonId) =>
			new(UnitAction.CancelProduction, 0, count, 0, itemName, queue, reason) { ReasonId = reasonId };

		public static UnitDecision ActivateSupportPower(string power, int x, int y, string reason) =>
			ActivateSupportPower(power, x, y, reason, null);

		public static UnitDecision ActivateSupportPower(
			string power, int x, int y, string reason, string reasonId) =>
			new(UnitAction.ActivateSupportPower, 0, x, y, power, null, reason) { ReasonId = reasonId };

		/// <summary>
		/// True if this decision commands the same thing as <paramref name="other"/>, ignoring the
		/// human-readable reason and machine-readable reason identifier. Used to suppress
		/// duplicate orders.
		/// </summary>
		public bool SameIntent(in UnitDecision other) =>
			Action == other.Action &&
			TargetActorId == other.TargetActorId &&
			TargetX == other.TargetX &&
			TargetY == other.TargetY &&
			ItemName == other.ItemName &&
			Queue == other.Queue;
	}
}
