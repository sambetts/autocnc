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

using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// A programmable behaviour that decides what one unit should do.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Implementations are discovered by reflection from every loaded mod assembly, so writing a
	/// mode needs no registration code: add the class, rebuild, and refer to it by class name.
	/// </para>
	/// <para>
	/// <b>One instance per unit.</b> A fresh mode object is constructed when a unit enters the
	/// mode and discarded on exit, so instance fields are a safe place for per-unit memory. Avoid
	/// <c>static</c> mutable fields — those would be shared across every unit.
	/// </para>
	/// <para>
	/// <b>Your code runs outside the simulation</b>, on your client only, and its output is
	/// orders. So the usual multiplayer determinism rules do not apply: floating point, LINQ,
	/// <c>System.Random</c> and wall-clock time are all fine. An opponent never runs your code and
	/// you never run theirs.
	/// </para>
	/// <para>
	/// <see cref="OnTick"/> returns a <see cref="UnitDecision"/> rather than acting directly. The
	/// executor only emits an order when the decision actually changes, so returning the same
	/// decision every tick is cheap and correct.
	/// </para>
	/// </remarks>
	public interface IUnitMode
	{
		/// <summary>Called once when the unit enters this mode.</summary>
		void OnEnter(Actor self, ModeContext ctx);

		/// <summary>
		/// Called every <c>TickInterval</c> ticks. Return what the unit should do, or
		/// <see cref="UnitDecision.Continue"/> to leave it alone.
		/// </summary>
		UnitDecision OnTick(Actor self, ModeContext ctx);

		/// <summary>Called whenever the unit takes damage, regardless of tick interval.</summary>
		void OnDamaged(Actor self, ModeContext ctx, AttackInfo e);

		/// <summary>Called once when the unit leaves this mode.</summary>
		void OnExit(Actor self, ModeContext ctx);
	}

	/// <summary>
	/// Convenience base class so a mode only overrides what it actually cares about.
	/// Most modes only need <see cref="OnTick"/>.
	/// </summary>
	public abstract class UnitMode : IUnitMode
	{
		public virtual void OnEnter(Actor self, ModeContext ctx) { }

		public abstract UnitDecision OnTick(Actor self, ModeContext ctx);

		public virtual void OnDamaged(Actor self, ModeContext ctx, AttackInfo e) { }

		public virtual void OnExit(Actor self, ModeContext ctx) { }
	}
}
