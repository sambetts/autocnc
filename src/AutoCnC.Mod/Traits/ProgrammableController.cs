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

using AutoCnC.Mod.Modes;
using AutoCnC.Modes.Core;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace AutoCnC.Mod.Traits
{
	[Desc("Marks this actor as programmable, so a C# mode can drive it.",
		"Actors with this trait are picked up by ModeExecutor on their owner's client.")]
	public class ProgrammableControllerInfo : ConditionalTraitInfo
	{
		[Desc("How often this unit re-evaluates its mode, in game ticks.",
			"Higher is cheaper and calmer; lower is more reactive.")]
		public readonly int TickInterval = 8;

		[Desc("Default sense radius for this unit, used by modes that do not specify one.")]
		public readonly WDist ScanRadius = WDist.FromCells(8);

		[Desc("The mode this actor type naturally runs, e.g. BuildBaseMode for an MCV.",
			"Sits above the executor's global default in the precedence chain, so '/mode all X'",
			"will not stop a construction yard building, but below the player's own type and",
			"group assignments so it can still be overridden deliberately.")]
		public readonly string DefaultMode = null;

		public override object Create(ActorInitializer init) { return new ProgrammableController(init.Self, this); }
	}

	/// <summary>
	/// Per-unit state for the mode system: which mode is running, its instance, and the unit's
	/// anchor.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This trait deliberately does <b>not</b> tick modes. Mode execution lives in
	/// <see cref="ModeExecutor"/>, which runs on the owning player's client only and turns
	/// decisions into orders.
	/// </para>
	/// <para>
	/// Everything here is therefore client-local policy, not simulation state — which is why
	/// nothing is marked <c>[VerifySync]</c>. Two clients holding different values changes only
	/// which orders each player's own client chooses to emit, and orders are synced.
	/// </para>
	/// </remarks>
	public class ProgrammableController : ConditionalTrait<ProgrammableControllerInfo>, INotifyDamage, INotifyOwnerChanged
	{
		public Actor Self { get; }
		public Player Owner => Self.Owner;

		/// <summary>Control group (1-9) this unit belongs to, or 0 when unassigned.</summary>
		public int GroupId { get; internal set; }

		/// <summary>The position this unit treats as home.</summary>
		public CPos Anchor { get; set; }

		public string ActiveModeName { get; private set; }

		/// <summary>An explicit per-unit mode, set by the player. Overrides group/type/global.</summary>
		public string ModeOverride { get; internal set; }

		internal IUnitMode ActiveMode { get; private set; }
		internal ModeContext Context { get; private set; }

		/// <summary>Last decision issued as an order, used to suppress duplicate orders.</summary>
		internal UnitDecision LastIssued { get; set; } = UnitDecision.Continue;

		/// <summary>Tick on which this unit should next evaluate.</summary>
		internal int NextEvaluationTick { get; set; }

		public ProgrammableController(Actor self, ProgrammableControllerInfo info)
			: base(info)
		{
			Self = self;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			Anchor = self.Location;
			Context = new ModeContext(this, self);
		}

		/// <summary>
		/// Switches the running mode. Called by <see cref="ModeExecutor"/> when the resolved
		/// assignment for this unit changes.
		/// </summary>
		internal void ApplyMode(string modeName)
		{
			if (modeName == ActiveModeName)
				return;

			ActiveMode?.OnExit(Self, Context);

			ActiveModeName = modeName;
			ActiveMode = string.IsNullOrEmpty(modeName) ? null : ModeRegistry.CreateOrNull(modeName);
			LastIssued = UnitDecision.Continue;

			ActiveMode?.OnEnter(Self, Context);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			// Only the owning client runs mode logic, so ignore the notification everywhere else.
			if (IsTraitDisabled || ActiveMode == null || self.Owner != self.World.LocalPlayer)
				return;

			ActiveMode.OnDamaged(self, Context, e);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// A captured unit must not keep executing its previous owner's plan.
			ActiveMode?.OnExit(self, Context);
			ActiveMode = null;
			ActiveModeName = null;
			ModeOverride = null;
			GroupId = 0;
			Anchor = self.Location;
			LastIssued = UnitDecision.Continue;
		}
	}
}
