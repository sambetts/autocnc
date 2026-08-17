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

using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// A programmable behaviour that a unit executes every evaluation tick.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Implementations are discovered by reflection (see <see cref="ModeRegistry"/>), so adding a
	/// mode requires no registration code — write the class, name it in YAML.
	/// </para>
	/// <para>
	/// <b>One instance per unit.</b> The controller constructs a fresh mode object when a unit
	/// enters the mode and discards it on exit, so instance fields are safe to use for per-unit
	/// memory. Do not share mutable state between instances.
	/// </para>
	/// <para>
	/// <b>Determinism is mandatory.</b> This method runs inside the lockstep simulation on every
	/// client. Use <see cref="ModeContext.Random"/> and integer maths only; never touch
	/// <c>World.LocalRandom</c>, <c>world.LocalPlayer</c>, <c>world.Selection</c>, wall-clock time,
	/// or floating point. Never issue an <see cref="Order"/> — call the <see cref="ModeContext"/>
	/// actuators, which queue activities directly (exactly as the engine's own AutoTarget does).
	/// See docs/determinism.md.
	/// </para>
	/// </remarks>
	public interface IUnitMode
	{
		/// <summary>Name used to reference this mode from YAML and from mode-switch orders.</summary>
		string Name { get; }

		/// <summary>Called once when the unit enters this mode.</summary>
		void OnEnter(Actor self, ModeContext ctx);

		/// <summary>Called every <c>TickInterval</c> ticks while this mode is active.</summary>
		void OnTick(Actor self, ModeContext ctx);

		/// <summary>Called whenever the unit takes damage, regardless of tick interval.</summary>
		void OnDamaged(Actor self, ModeContext ctx, AttackInfo e);

		/// <summary>Called once when the unit leaves this mode. Release any per-unit state here.</summary>
		void OnExit(Actor self, ModeContext ctx);
	}

	/// <summary>
	/// Convenience base class so a mode only has to override what it actually cares about.
	/// </summary>
	public abstract class UnitMode : IUnitMode
	{
		/// <summary>Defaults to the concrete type name, which is what YAML references.</summary>
		public virtual string Name => GetType().Name;

		public virtual void OnEnter(Actor self, ModeContext ctx) { }
		public abstract void OnTick(Actor self, ModeContext ctx);
		public virtual void OnDamaged(Actor self, ModeContext ctx, AttackInfo e) { }
		public virtual void OnExit(Actor self, ModeContext ctx) { }
	}
}
