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
using System.Linq;

namespace AutoCnC.Modes.Core
{
	/// <summary>The scope a mode assignment applies to, from least to most specific.</summary>
	public enum AssignmentScope
	{
		/// <summary>Every programmable unit the player owns.</summary>
		All = 0,

		/// <summary>The mode an actor type naturally runs, declared in YAML.</summary>
		ActorDefault = 1,

		/// <summary>Every unit of one actor type, e.g. all harvesters.</summary>
		UnitType = 2,

		/// <summary>Every unit in one control group (1-9).</summary>
		Group = 3,

		/// <summary>One specific unit, set by selecting it.</summary>
		Unit = 4,
	}

	/// <summary>
	/// Resolves which mode a given unit should be running, from assignments made at four
	/// different scopes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Precedence is most-specific-wins: a per-unit override beats its control group, which beats
	/// its unit type, which beats the global default. That way
	/// <c>/mode all DefensiveMode</c> followed by <c>/mode type harv RunHomeMode</c> does what you
	/// would expect, and neither clobbers the other.
	/// </para>
	/// <para>
	/// This is deliberately engine-free so the precedence rules can be tested directly. It holds
	/// client-local policy only: it decides which orders a player's own client emits, and never
	/// touches the simulation.
	/// </para>
	/// </remarks>
	public sealed class ModeAssignments
	{
		readonly Dictionary<string, string> byUnitType = new(StringComparer.OrdinalIgnoreCase);
		readonly string[] byGroup;

		public int GroupCount { get; }

		/// <summary>Mode used when nothing more specific applies. May be null.</summary>
		public string GlobalMode { get; private set; }

		public ModeAssignments(string globalMode = null, int groupCount = 9)
		{
			if (groupCount < 1)
				throw new ArgumentOutOfRangeException(nameof(groupCount));

			GroupCount = groupCount;
			GlobalMode = globalMode;

			// Index 0 is unused so that group N maps directly to byGroup[N].
			byGroup = new string[groupCount + 1];
		}

		public bool IsValidGroup(int group) => group >= 1 && group <= GroupCount;

		/// <summary>
		/// Picks the mode for a unit. Returns null when no assignment applies at any scope.
		/// </summary>
		/// <param name="unitOverride">A mode set on this specific unit, or null.</param>
		/// <param name="groupId">The unit's control group (1-9), or 0 if unassigned.</param>
		/// <param name="actorType">The unit's actor type, e.g. "harv".</param>
		/// <param name="actorDefault">
		/// The mode this actor type naturally runs, declared in YAML. Sits above the global
		/// default so that <c>/mode all DefensiveMode</c> does not stop a construction yard
		/// building, but below the player's own type and group assignments so it can still be
		/// overridden deliberately.
		/// </param>
		public string Resolve(string unitOverride, int groupId, string actorType, string actorDefault = null)
		{
			if (!string.IsNullOrEmpty(unitOverride))
				return unitOverride;

			if (IsValidGroup(groupId) && !string.IsNullOrEmpty(byGroup[groupId]))
				return byGroup[groupId];

			if (!string.IsNullOrEmpty(actorType) && byUnitType.TryGetValue(actorType, out var typeMode) && !string.IsNullOrEmpty(typeMode))
				return typeMode;

			if (!string.IsNullOrEmpty(actorDefault))
				return actorDefault;

			return GlobalMode;
		}

		public void SetAll(string modeName) => GlobalMode = Normalise(modeName);

		public void SetUnitType(string actorType, string modeName)
		{
			if (string.IsNullOrWhiteSpace(actorType))
				return;

			var normalised = Normalise(modeName);
			if (normalised == null)
				byUnitType.Remove(actorType);
			else
				byUnitType[actorType] = normalised;
		}

		public void SetGroup(int group, string modeName)
		{
			if (!IsValidGroup(group))
				return;

			byGroup[group] = Normalise(modeName);
		}

		public string GetUnitType(string actorType) =>
			actorType != null && byUnitType.TryGetValue(actorType, out var mode) ? mode : null;

		public string GetGroup(int group) => IsValidGroup(group) ? byGroup[group] : null;

		/// <summary>Every unit-type assignment, ordered for stable display.</summary>
		public IEnumerable<KeyValuePair<string, string>> UnitTypeAssignments =>
			byUnitType.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

		/// <summary>Every group assignment as (group, mode) pairs, ordered by group.</summary>
		public IEnumerable<KeyValuePair<int, string>> GroupAssignments
		{
			get
			{
				for (var i = 1; i <= GroupCount; i++)
					if (!string.IsNullOrEmpty(byGroup[i]))
						yield return new KeyValuePair<int, string>(i, byGroup[i]);
			}
		}

		public void ClearUnitTypes() => byUnitType.Clear();

		public void ClearGroups()
		{
			for (var i = 0; i < byGroup.Length; i++)
				byGroup[i] = null;
		}

		/// <summary>Treats blank as "no assignment", so clearing a scope is just setting it empty.</summary>
		static string Normalise(string modeName) =>
			string.IsNullOrWhiteSpace(modeName) ? null : modeName.Trim();
	}
}
