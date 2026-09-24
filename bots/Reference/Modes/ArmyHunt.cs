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

using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Mods.Common.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// One unit's part in hunting the last of the enemy across the map. See <see cref="HuntLogic"/>.
	/// </summary>
	/// <remarks>
	/// A part of <see cref="DefensiveMode"/> rather than a mode of its own, and that is
	/// load-bearing. The platform keeps a unit's mode instance only while its mode type stays the
	/// same, so a separate hunt mode assigned by the Scout doctrine reset every defender's state at
	/// each switch between Scout, Opening and Defence. That changed the first five minutes of games
	/// it never hunted in, and two of them flipped from wins to losses. Folded in here, nothing
	/// changes until the hunt actually starts.
	/// </remarks>
	public sealed class ArmyHunt
	{
		readonly HuntTuning tuning = HuntTuning.Default;

		int sector = -1;
		int lastX = int.MinValue;
		int lastY = int.MinValue;
		int still;

		public void Reset()
		{
			sector = -1;
			still = 0;
		}

		/// <summary>
		/// Only in the Scout doctrine, and only once the army is big. Below that the Scout doctrine
		/// is the opening's search for their base and the army belongs at home.
		/// </summary>
		public bool Active(Actor self, ModeContext ctx)
		{
			if (!ctx.HasWeapon || !ctx.CanMove ||
				!string.Equals(ctx.Doctrine, ReferenceDoctrines.Scout, System.StringComparison.Ordinal))
				return false;

			// This side's own statistic, the same number BattleState carries as ArmyValue.
			var army = self.Owner.PlayerActor.TraitOrDefault<PlayerStatistics>()?.ArmyValue ?? 0;
			return army >= tuning.ArmyValue;
		}

		public UnitDecision Tick(Actor self, ModeContext ctx)
		{
			// Something of theirs in view: remember where for the whole side, ask for the push, and
			// go and hit it. The push outranks the hunt, because AttackBaseMode knows how to take
			// a base apart and this does not.
			var structures = ctx.SenseStructures(WDist.FromCells(tuning.SightCells));
			ThreatSnapshot? nearest = null;
			for (var i = 0; i < structures.Count; i++)
				if (structures[i].IsAttackable && (nearest == null || structures[i].DistanceUnits < nearest.Value.DistanceUnits))
					nearest = structures[i];

			if (nearest != null)
			{
				var found = ctx.ResolveActor(nearest.Value.ActorId);
				if (found != null)
				{
					EnemyBaseSightings.Record(self.Owner, found.Location, ctx.WorldTick);
					ctx.SwitchDoctrine(ReferenceDoctrines.Attack, "hunt found one of their structures", "hunt.found");
					if (ctx.CanAttack(found))
						return UnitDecision.Attack(found.ActorID,
							$"hunting: attacking their {nearest.Value.ActorType}", "hunt.attack");
				}
			}

			var bounds = ctx.World.Map.Bounds;
			var minX = bounds.Left;
			var minY = bounds.Top;
			var maxX = bounds.Left + bounds.Width - 1;
			var maxY = bounds.Top + bounds.Height - 1;
			var sectors = HuntLogic.Sectors(minX, minY, maxX, maxY, tuning);
			if (sector < 0)
				sector = HuntLogic.Start(self.ActorID, sectors);

			var here = self.Location;
			if (here.X == lastX && here.Y == lastY)
				still++;
			else
			{
				still = 0;
				lastX = here.X;
				lastY = here.Y;
			}

			var (x, y) = HuntLogic.Center(sector, minX, minY, maxX, maxY, tuning);
			if (HuntLogic.Within(here.X, here.Y, x, y, tuning.ArrivedCells) || still >= tuning.StallEvaluations)
			{
				sector = HuntLogic.Next(sector, sectors);
				still = 0;
				(x, y) = HuntLogic.Center(sector, minX, minY, maxX, maxY, tuning);
			}

			return UnitDecision.AttackMoveTo(x, y,
				$"hunting their last structures, sector {sector} of {sectors} at {x},{y}", "hunt.sweep");
		}
	}
}
