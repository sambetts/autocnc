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
		/// <summary>Do nothing; leave any running activity alone.</summary>
		Continue = 0,

		/// <summary>Stop and stand still.</summary>
		Hold = 1,

		/// <summary>Attack <see cref="UnitDecision.TargetActorId"/>.</summary>
		Attack = 2,

		/// <summary>Move back to the anchor position without seeking combat.</summary>
		ReturnToAnchor = 3,

		/// <summary>Break off and head for repair.</summary>
		Retreat = 4,

		/// <summary>Advance on the objective, engaging only what blocks the path.</summary>
		AdvanceToObjective = 5,
	}

	/// <summary>
	/// The complete output of a mode's decision step.
	/// </summary>
	/// <remarks>
	/// A decision is *data*, not an action. Keeping it inert is what lets us assert on it in
	/// unit tests, log it into replays, and render it as a debug overlay without ever running
	/// the engine. The mode's Act phase is the only thing that turns it into an activity.
	/// </remarks>
	public readonly record struct UnitDecision(UnitAction Action, uint TargetActorId, string Reason)
	{
		public static readonly UnitDecision Continue = new(UnitAction.Continue, 0, "no change");

		public static UnitDecision Hold(string reason) => new(UnitAction.Hold, 0, reason);
		public static UnitDecision Attack(uint targetActorId, string reason) => new(UnitAction.Attack, targetActorId, reason);
		public static UnitDecision ReturnToAnchor(string reason) => new(UnitAction.ReturnToAnchor, 0, reason);
		public static UnitDecision Retreat(string reason) => new(UnitAction.Retreat, 0, reason);
		public static UnitDecision AdvanceToObjective(uint objectiveActorId, string reason) => new(UnitAction.AdvanceToObjective, objectiveActorId, reason);
	}
}
