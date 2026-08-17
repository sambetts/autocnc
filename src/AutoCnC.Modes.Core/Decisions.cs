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

namespace AutoCnC.Modes.Core
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
		string Reason)
	{
		public static readonly UnitDecision Continue = new(UnitAction.Continue, 0, 0, 0, null, "no change");

		public static UnitDecision Hold(string reason) => new(UnitAction.Hold, 0, 0, 0, null, reason);
		public static UnitDecision Attack(uint targetActorId, string reason) => new(UnitAction.Attack, targetActorId, 0, 0, null, reason);
		public static UnitDecision ReturnToAnchor(string reason) => new(UnitAction.ReturnToAnchor, 0, 0, 0, null, reason);
		public static UnitDecision Retreat(string reason) => new(UnitAction.Retreat, 0, 0, 0, null, reason);
		public static UnitDecision AdvanceToObjective(uint objectiveActorId, string reason) => new(UnitAction.AdvanceToObjective, objectiveActorId, 0, 0, null, reason);
		public static UnitDecision MoveTo(int x, int y, string reason) => new(UnitAction.MoveTo, 0, x, y, null, reason);
		public static UnitDecision AttackMoveTo(int x, int y, string reason) => new(UnitAction.AttackMoveTo, 0, x, y, null, reason);
		public static UnitDecision Deploy(string reason) => new(UnitAction.Deploy, 0, 0, 0, null, reason);
		public static UnitDecision Produce(string itemName, string reason) => new(UnitAction.Produce, 0, 0, 0, itemName, reason);
		public static UnitDecision PlaceBuilding(string itemName, int x, int y, string reason) => new(UnitAction.PlaceBuilding, 0, x, y, itemName, reason);

		/// <summary>
		/// True if this decision commands the same thing as <paramref name="other"/>, ignoring the
		/// human-readable reason. Used to suppress duplicate orders.
		/// </summary>
		public bool SameIntent(in UnitDecision other) =>
			Action == other.Action &&
			TargetActorId == other.TargetActorId &&
			TargetX == other.TargetX &&
			TargetY == other.TargetY &&
			ItemName == other.ItemName;
	}
}
