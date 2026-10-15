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

using System;
using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	public static class MechanizedForceLogic
	{
		public const string ArmourReasonId = "production.mechanized-line";
		public const string InfantryReasonId = "production.mechanized-infantry-limit";
		public const int InfantryCore = 12;

		public static int ArmourTarget(int siege) => (Math.Max(0, siege) + 1) / 2;

		public static int InfantryLimit(int armour, int siege) =>
			InfantryCore + 2 * (Math.Max(0, armour) + Math.Max(0, siege));

		public static IReadOnlyList<ProductionStep> ReinforceLine(
			IReadOnlyList<ProductionStep> plan, int armour, int siege, int losses, in AttritionTuning attrition)
		{
			var target = ArmourTarget(siege);
			// Respect the existing write-off when tanks are being destroyed faster than
			// they can form a line; a new ratio must not resurrect that queue deadlock.
			if (armour >= target || (attrition.Losses > 0 && losses >= attrition.Losses))
				return plan;

			var siegeRung = ExpansionLogic.FirstRungNaming(plan, ReferencePlans.SiegeVehicles);
			if (siegeRung < 0)
				return plan;

			// Preserve the first siege piece and all earlier economy/screen priorities.
			// Unlike a fixed tank floor, this target cannot grow while siege is being lost.
			var result = new List<ProductionStep>(plan);
			result.Insert(siegeRung, new ProductionStep(
				"Vehicle", ReferencePlans.DefenceArmourVehicles, target));
			return result;
		}

		public static IReadOnlyList<ProductionStep> LimitInfantry(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyDictionary<string, int> owned,
			bool protectAntiAir)
		{
			var result = new List<ProductionStep>(plan);
			for (var i = 0; i < result.Count; i++)
			{
				var step = result[i];
				if (!string.Equals(step.Queue, ReferencePlans.InfantryQueue, StringComparison.OrdinalIgnoreCase))
					continue;

				var antiAir = true;
				foreach (var candidate in step.Candidates)
					antiAir &= ArmyMixLogic.Names(ReferencePlans.AntiAirUnits, candidate);

				if (protectAntiAir && antiAir)
					continue;

				// Mark every affected rung met, including endless and mixed-candidate rungs.
				// Holding only the barracks would leave ChooseNext stuck above vehicle orders.
				result[i] = new ProductionStep(step.Queue, step.Candidates,
					ExpansionLogic.Standing(owned, step.Candidates));
			}

			return result;
		}
	}
}
