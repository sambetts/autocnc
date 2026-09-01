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
using AutoCnC.Core;
using OpenRA;

namespace AutoCnC.Sdk
{
	/// <summary>
	/// Per-unit state the platform holds on a mode's behalf.
	/// </summary>
	/// <remarks>
	/// Exists so the SDK does not have to reference the platform assembly: the platform
	/// implements this, the SDK only consumes it. Modules never implement it themselves.
	/// </remarks>
	public interface IUnitState
	{
		/// <summary>The position this unit treats as home.</summary>
		CPos Anchor { get; set; }

		/// <summary>Control group (1-9), or 0 when unassigned.</summary>
		int GroupId { get; }

		/// <summary>Name of the mode currently running.</summary>
		string ActiveModeName { get; }

		/// <summary>
		/// Ask the platform to evaluate this unit next tick and re-send its order even if the
		/// decision is unchanged.
		/// </summary>
		void RequestReevaluation();
	}

	/// <summary>
	/// The loaded doctrine's plans, and the bot it belongs to, as seen by a mode.
	/// </summary>
	/// <remarks>
	/// Plans come from the doctrine rather than being baked into a mode, so a generic
	/// <c>BuildBaseMode</c> can be reused across doctrines with different build orders.
	/// </remarks>
	public interface IModeHost
	{
		IReadOnlyList<BuildStep> BuildPlan { get; }

		IReadOnlyList<ProductionStep> ProductionPlan { get; }

		/// <summary>The doctrine running right now, or null if none is loaded.</summary>
		string ActiveDoctrine { get; }

		/// <summary>Every doctrine the loaded bot owns, in the order it declared them.</summary>
		IReadOnlyList<string> DoctrineNames { get; }

		/// <summary>
		/// Ask for a different doctrine. Honoured at the next assessment, or refused.
		/// </summary>
		/// <remarks>
		/// A request rather than a command: the platform still applies the bot's minimum dwell
		/// time, so a mode cannot make the army thrash by asking every tick, and a name that is
		/// not in the bot is ignored rather than fatal.
		/// </remarks>
		void RequestDoctrine(string doctrine, string reason);
	}
}
