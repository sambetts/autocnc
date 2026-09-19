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

namespace AutoCnC.Core
{
	/// <summary>Engine-free accumulator for the visible-enemy half of a battle assessment.</summary>
	internal sealed class BattleValueAccumulator
	{
		readonly Dictionary<ThreatKind, (int Count, int Value)> mix = [];

		public int EnemiesInSight { get; private set; }
		public int EnemiesNearBase { get; private set; }
		public int VisibleEnemyValue { get; private set; }
		public int EnemyValueNearBase { get; private set; }

		public void ObserveEnemy(bool visible, bool nearBase, ThreatKind kind, int value)
		{
			if (!visible)
				return;

			value = Math.Max(0, value);
			EnemiesInSight++;
			VisibleEnemyValue += value;

			if (nearBase)
			{
				EnemiesNearBase++;
				EnemyValueNearBase += value;
			}

			var current = mix.GetValueOrDefault(kind);
			mix[kind] = (current.Count + 1, current.Value + value);
		}

		public IReadOnlyList<ThreatValueSummary> VisibleEnemyMix()
		{
			var result = new List<ThreatValueSummary>(mix.Count);
			foreach (var pair in mix)
				result.Add(new ThreatValueSummary(pair.Key, pair.Value.Count, pair.Value.Value));

			result.Sort(static (a, b) => a.Kind.CompareTo(b.Kind));
			return result;
		}
	}

	internal readonly record struct BattleCumulativeTotals(
		int Seconds,
		int UnitsLost,
		int BuildingsLost,
		int UnitsKilled,
		int CreditsLost,
		int CreditsKilled,
		int IncomeEarned);

	internal readonly record struct BattleWindowTotals(
		int UnitsLost,
		int BuildingsLost,
		int UnitsKilled,
		int CreditsLost,
		int CreditsKilled,
		int IncomeEarned);

	internal static class BattleWindowCalculator
	{
		public static BattleWindowTotals Difference(
			in BattleCumulativeTotals current, in BattleCumulativeTotals previous) =>
			new(
				Math.Max(0, current.UnitsLost - previous.UnitsLost),
				Math.Max(0, current.BuildingsLost - previous.BuildingsLost),
				Math.Max(0, current.UnitsKilled - previous.UnitsKilled),
				Math.Max(0, current.CreditsLost - previous.CreditsLost),
				Math.Max(0, current.CreditsKilled - previous.CreditsKilled),
				Math.Max(0, current.IncomeEarned - previous.IncomeEarned));
	}
}
