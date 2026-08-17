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
	/// The loaded battle module's plans, as seen by a mode.
	/// </summary>
	/// <remarks>
	/// Plans come from the module rather than being baked into a mode, so a generic
	/// <c>BuildBaseMode</c> can be reused across modules with different build orders.
	/// </remarks>
	public interface IModeHost
	{
		IReadOnlyList<BuildStep> BuildPlan { get; }

		IReadOnlyList<ProductionStep> ProductionPlan { get; }
	}
}
