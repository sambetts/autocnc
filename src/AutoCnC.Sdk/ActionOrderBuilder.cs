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
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Sdk
{
	internal static class ActionOrderBuilder
	{
		public static Order RepairBuilding(Actor playerActor, in Target target) =>
			new("RepairBuilding", playerActor, target, false);

		public static Order CancelProduction(Actor queueActor, string item, int count) =>
			Order.CancelProduction(queueActor, item, count);

		public static Order ActivateSupportPower(Actor playerActor, string key, in Target target) =>
			new(key, playerActor, target, false)
			{
				ExtraData = uint.MaxValue,
				SuppressVisualFeedback = true
			};

		public static bool CanStartRepair(in OwnedBuildingState building) =>
			building.IsRepairable && building.HealthPercent < 100 && !building.RepairRequested;

		public static string FindQueuedItem(
			IEnumerable<string> queuedItems, string requestedItem, int requestedCount)
		{
			if (queuedItems == null || string.IsNullOrWhiteSpace(requestedItem) || requestedCount <= 0)
				return null;

			string canonicalItem = null;
			var matches = 0;
			foreach (var item in queuedItems)
			{
				if (!string.Equals(item, requestedItem, StringComparison.OrdinalIgnoreCase))
					continue;

				canonicalItem ??= item;
				if (++matches >= requestedCount)
					return canonicalItem;
			}

			return null;
		}

		public static string FindReadySupportPower(
			IEnumerable<SupportPowerState> powers, string requestedPower)
		{
			if (powers == null || string.IsNullOrWhiteSpace(requestedPower))
				return null;

			SupportPowerState? exactKey = null;
			string orderNameMatch = null;

			foreach (var power in powers)
			{
				if (string.Equals(power.Key, requestedPower, StringComparison.OrdinalIgnoreCase))
				{
					exactKey = power;
					break;
				}

				if (!power.Active || !power.Ready || power.Disabled ||
					!string.Equals(power.OrderName, requestedPower, StringComparison.OrdinalIgnoreCase))
					continue;

				if (orderNameMatch == null ||
					string.Compare(power.Key, orderNameMatch, StringComparison.OrdinalIgnoreCase) < 0)
					orderNameMatch = power.Key;
			}

			if (exactKey.HasValue)
			{
				var power = exactKey.Value;
				return power.Active && power.Ready && !power.Disabled ? power.Key : null;
			}

			return orderNameMatch;
		}

		public static int ProgressPercent(int totalTime, int remainingTime, bool done)
		{
			if (done)
				return 100;

			if (totalTime <= 0 || remainingTime >= totalTime)
				return 0;

			var progress = (int)((long)(totalTime - remainingTime) * 100 / totalTime);
			return Math.Clamp(progress, 0, 100);
		}
	}
}
