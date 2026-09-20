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

namespace AutoCnC.Reference.Logic
{
	/// <summary>
	/// Which standing building, if any, is worth spending a construction yard's evaluation on
	/// keeping rather than replacing.
	/// </summary>
	/// <remarks>
	/// This bot issued <b>zero</b> repair orders in a 913-second match on 16:9 and lost fourteen
	/// buildings — its whole base except one — worth about 14,900 of the 31,900 credits it lost
	/// altogether, while <c>valueExchange</c> scored 0.524 against a reference of 2. The SDK has
	/// exposed <see cref="UnitAction.RepairBuilding"/> the whole time and no mode had ever asked
	/// for one. That is a missing capability rather than a tuning question, which is why the rule
	/// here is deliberately small.
	/// <para>
	/// <b>It is bounded at one end only, because repair has a losing mode too.</b> A base under
	/// permanent attack can sink every credit it earns into structures that die anyway, and this
	/// bot's whole economy is built on income keeping absolute priority. So the caller still asks
	/// only on an evaluation where construction has nothing to order and no economy hold is
	/// running — the ordering is what protects income, and it is unchanged. What the band itself
	/// protects is nothing: see <see cref="RepairBelowHealthPercent"/> for the fight it was
	/// losing at 50%.
	/// </para>
	/// <para>
	/// One request per building is enough: the host refuses a repair for a building that already
	/// has one requested or active, so consecutive evaluations walk down the damaged list instead
	/// of arguing about the same structure. Ordering by health and then by actor id keeps that
	/// walk deterministic.
	/// </para>
	/// No OpenRA types, so the judgement stays readable without booting the engine.
	/// </remarks>
	public static class BaseRepairLogic
	{
		/// <summary>
		/// Health at or below which a standing structure is worth buying back rather than
		/// leaving to be replaced.
		/// </summary>
		/// <remarks>
		/// <b>This was 50, and at 50 the rule almost never fired.</b> On 16:9 the construction
		/// yard reached the branch that asks this question on roughly <b>900 evaluations</b> —
		/// 591 <c>Continue</c> and 330 <c>Hold</c> — and found a qualifying building on
		/// <b>24</b> of them, issuing <b>12</b> repair orders while <b>27 buildings were
		/// destroyed</b>. The opportunity was never the constraint; the band was. A building
		/// under fire crosses 50% and dies in the same handful of seconds, so a rule that only
		/// opens there is a rule that opens after the race is lost.
		/// <para>
		/// Widening it is nearly free, because OpenRA charges repair by the hit point rather
		/// than by the order: topping a 1,500-credit refinery up from three-quarters costs
		/// roughly a twentieth of replacing it. What the wider band buys is the case that
		/// actually decides a siege — chip damage between salvos and between air passes, where
		/// a fixed repair rate wins and a building that would have been ground down over four
		/// minutes is still standing at the end of them.
		/// </para>
		/// </remarks>
		public const int RepairBelowHealthPercent = 75;

		/// <summary>
		/// The building to request repair for, or 0 when none is worth it. Never returns a
		/// building that is already being repaired or already has a request pending.
		/// </summary>
		public static uint Choose(IReadOnlyCollection<OwnedBuildingState> buildings, int cash)
		{
			// Repair is paid for out of the same cash every queue draws on, so an empty treasury
			// buys an order that cannot progress.
			if (cash <= 0 || buildings == null)
				return 0;

			uint chosen = 0;
			var chosenHealth = int.MaxValue;

			foreach (var building in buildings)
			{
				if (!building.IsRepairable
					|| building.RepairRequested
					|| building.RepairActive
					|| building.HealthPercent <= 0
					|| building.HealthPercent > RepairBelowHealthPercent)
					continue;

				if (chosen != 0
					&& (building.HealthPercent > chosenHealth
						|| (building.HealthPercent == chosenHealth && building.ActorId >= chosen)))
					continue;

				chosen = building.ActorId;
				chosenHealth = building.HealthPercent;
			}

			return chosen;
		}

		/// <summary>The health of the building <see cref="Choose"/> picked, for the trace.</summary>
		public static int HealthOf(IReadOnlyCollection<OwnedBuildingState> buildings, uint actorId)
		{
			if (buildings == null || actorId == 0)
				return 0;

			foreach (var building in buildings)
				if (building.ActorId == actorId)
					return building.HealthPercent;

			return 0;
		}

		/// <summary>The type of the building <see cref="Choose"/> picked, for the trace.</summary>
		public static string TypeOf(IReadOnlyCollection<OwnedBuildingState> buildings, uint actorId)
		{
			if (buildings == null || actorId == 0)
				return null;

			foreach (var building in buildings)
				if (building.ActorId == actorId)
					return building.ActorType;

			return null;
		}
	}
}
