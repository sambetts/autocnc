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
using System.IO;
using System.Text;
using AutoCnC.Core;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Sdk
{
	internal readonly record struct ProductionQueueEntry(string Item, bool Infinite);

	internal static class ActionOrderBuilder
	{
		const byte CancellationPayloadVersion = 1;
		const int MaximumCancellationPayloadLength = 1024 * 1024;

		public const string EnsureRepairOrder = "AutoCnCEnsureRepair";
		public const string ExactCancelProductionOrder = "AutoCnCExactCancelProduction";

		public static Order RequestRepairBuilding(Actor playerActor, in Target target) =>
			new(EnsureRepairOrder, playerActor, target, false) { SuppressVisualFeedback = true };

		public static Order RepairBuilding(Actor playerActor, in Target target) =>
			new("RepairBuilding", playerActor, target, false);

		public static Order RequestCancelProduction(
			Actor playerActor,
			in Target queueTarget,
			int queueIndex,
			string item,
			int count,
			ulong expectedQueueRevision) =>
			new(ExactCancelProductionOrder, playerActor, queueTarget, false)
			{
				TargetString = EncodeCancellationPayload(item, expectedQueueRevision),
				ExtraLocation = new CPos(queueIndex, 0),
				ExtraData = (uint)count,
				SuppressVisualFeedback = true
			};

		public static Order CancelProduction(Actor queueActor, string item, int count) =>
			Order.CancelProduction(queueActor, item, count);

		public static string EncodeCancellationPayload(
			string item, ulong expectedQueueRevision)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
			{
				writer.Write(CancellationPayloadVersion);
				writer.Write(item ?? "");
				writer.Write(expectedQueueRevision);
			}

			return Convert.ToBase64String(stream.ToArray());
		}

		public static bool TryDecodeCancellationPayload(
			string payload, out string item, out ulong expectedQueueRevision)
		{
			item = null;
			expectedQueueRevision = 0;
			if (string.IsNullOrEmpty(payload) || payload.Length > MaximumCancellationPayloadLength)
				return false;

			try
			{
				using var stream = new MemoryStream(Convert.FromBase64String(payload), writable: false);
				using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
				if (reader.ReadByte() != CancellationPayloadVersion)
					return false;

				item = reader.ReadString();
				expectedQueueRevision = reader.ReadUInt64();
				if (string.IsNullOrWhiteSpace(item) || expectedQueueRevision == 0)
					return false;

				return stream.Position == stream.Length;
			}
			catch (FormatException)
			{
				return false;
			}
			catch (EndOfStreamException)
			{
				return false;
			}
			catch (IOException)
			{
				return false;
			}
		}

		public static Order ActivateSupportPower(Actor playerActor, string key, in Target target) =>
			new(key, playerActor, target, false)
			{
				ExtraData = uint.MaxValue,
				SuppressVisualFeedback = true
			};

		public static bool CanStartRepair(in OwnedBuildingState building) =>
			building.IsRepairable &&
			building.HealthPercent < 100 &&
			!building.RepairRequested &&
			!building.RepairActive;

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

		public static UnitDecision? ResolveCancellation(
			in UnitDecision decision,
			uint queueActorId,
			string queueName,
			IEnumerable<string> queuedItems)
		{
			var item = FindQueuedItem(queuedItems, decision.ItemName, decision.Count);
			return item == null
				? null
				: decision with
				{
					TargetActorId = queueActorId,
					Queue = queueName,
					ItemName = item
				};
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

		public static UnitDecision? ResolveSupportPower(
			in UnitDecision decision, IEnumerable<SupportPowerState> powers)
		{
			var key = FindReadySupportPower(powers, decision.Power);
			return key == null ? null : decision with { ItemName = key };
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
