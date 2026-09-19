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
using System.Collections;
using System.Collections.Generic;

namespace AutoCnC.Core
{
	internal readonly record struct ThreatValueBucket(int Count, int Value)
	{
		public bool Present => Count != 0 || Value != 0;

		public ThreatValueBucket Add(in ThreatValueSummary summary) =>
			new(Count + summary.Count, Value + summary.Value);
	}

	/// <summary>
	/// Fixed, value-comparable storage behind <see cref="BattleState.VisibleEnemyMix"/>.
	/// </summary>
	internal readonly record struct ThreatValueProfile(
		ThreatValueBucket Unknown,
		ThreatValueBucket Infantry,
		ThreatValueBucket Vehicle,
		ThreatValueBucket Aircraft,
		ThreatValueBucket Structure,
		ThreatValueBucket Defence,
		ThreatValueBucket Economy) : IReadOnlyList<ThreatValueSummary>
	{
		public int Count =>
			(Unknown.Present ? 1 : 0) +
			(Infantry.Present ? 1 : 0) +
			(Vehicle.Present ? 1 : 0) +
			(Aircraft.Present ? 1 : 0) +
			(Structure.Present ? 1 : 0) +
			(Defence.Present ? 1 : 0) +
			(Economy.Present ? 1 : 0);

		public ThreatValueSummary this[int index]
		{
			get
			{
				foreach (var summary in this)
				{
					if (index-- == 0)
						return summary;
				}

				throw new ArgumentOutOfRangeException(nameof(index));
			}
		}

		public static ThreatValueProfile From(IReadOnlyList<ThreatValueSummary> summaries)
		{
			var result = new ThreatValueProfile();
			if (summaries == null)
				return result;

			for (var i = 0; i < summaries.Count; i++)
			{
				var summary = summaries[i];
				result = summary.Kind switch
				{
					ThreatKind.Unknown => result with { Unknown = result.Unknown.Add(summary) },
					ThreatKind.Infantry => result with { Infantry = result.Infantry.Add(summary) },
					ThreatKind.Vehicle => result with { Vehicle = result.Vehicle.Add(summary) },
					ThreatKind.Aircraft => result with { Aircraft = result.Aircraft.Add(summary) },
					ThreatKind.Structure => result with { Structure = result.Structure.Add(summary) },
					ThreatKind.Defence => result with { Defence = result.Defence.Add(summary) },
					ThreatKind.Economy => result with { Economy = result.Economy.Add(summary) },
					_ => result
				};
			}

			return result;
		}

		public IEnumerator<ThreatValueSummary> GetEnumerator()
		{
			if (Unknown.Present)
				yield return Summary(ThreatKind.Unknown, Unknown);

			if (Infantry.Present)
				yield return Summary(ThreatKind.Infantry, Infantry);

			if (Vehicle.Present)
				yield return Summary(ThreatKind.Vehicle, Vehicle);

			if (Aircraft.Present)
				yield return Summary(ThreatKind.Aircraft, Aircraft);

			if (Structure.Present)
				yield return Summary(ThreatKind.Structure, Structure);

			if (Defence.Present)
				yield return Summary(ThreatKind.Defence, Defence);

			if (Economy.Present)
				yield return Summary(ThreatKind.Economy, Economy);
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		static ThreatValueSummary Summary(ThreatKind kind, in ThreatValueBucket bucket) =>
			new(kind, bucket.Count, bucket.Value);
	}
}
