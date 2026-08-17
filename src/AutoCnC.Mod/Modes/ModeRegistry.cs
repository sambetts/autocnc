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
using OpenRA;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// Resolves <see cref="IUnitMode"/> implementations by name, using OpenRA's own
	/// <see cref="ObjectCreator"/> so that modes are discovered from every loaded mod assembly.
	/// </summary>
	/// <remarks>
	/// This is why adding a mode needs no factory, switch statement or registration call: the same
	/// reflection mechanism the engine uses to bind YAML trait names to <c>TraitInfo</c> classes
	/// binds mode names to <see cref="IUnitMode"/> classes.
	/// </remarks>
	public static class ModeRegistry
	{
		static readonly object SyncRoot = new();
		static Dictionary<string, Type> modeTypes;

		/// <summary>All discovered mode names. Ordered for stable error messages and UI.</summary>
		public static IEnumerable<string> AvailableModeNames
		{
			get
			{
				EnsureLoaded();
				return modeTypes.Keys.OrderBy(k => k, StringComparer.Ordinal);
			}
		}

		public static bool IsKnownMode(string name)
		{
			if (string.IsNullOrEmpty(name))
				return false;

			EnsureLoaded();
			return modeTypes.ContainsKey(name);
		}

		/// <summary>
		/// Creates a fresh mode instance. Each unit gets its own, so modes may hold per-unit state.
		/// </summary>
		public static IUnitMode Create(string name)
		{
			EnsureLoaded();

			if (!modeTypes.TryGetValue(name, out var type))
				throw new InvalidOperationException(
					$"Unknown unit mode '{name}'. Known modes: {string.Join(", ", AvailableModeNames)}");

			return (IUnitMode)Activator.CreateInstance(type);
		}

		/// <summary>
		/// Validates a mode name at rules-load time so typos surface as a clean YAML error
		/// rather than an exception mid-match.
		/// </summary>
		public static void ValidateOrThrow(string name, string fieldName)
		{
			if (!IsKnownMode(name))
				throw new YamlException(
					$"{fieldName}: unknown unit mode '{name}'. Known modes: {string.Join(", ", AvailableModeNames)}");
		}

		static void EnsureLoaded()
		{
			if (modeTypes != null)
				return;

			lock (SyncRoot)
			{
				if (modeTypes != null)
					return;

				var discovered = new Dictionary<string, Type>(StringComparer.Ordinal);
				foreach (var type in Game.ModData.ObjectCreator.GetTypesImplementing<IUnitMode>())
				{
					if (type.IsAbstract || type.IsInterface)
						continue;

					if (type.GetConstructor(Type.EmptyTypes) == null)
						throw new InvalidOperationException(
							$"Unit mode '{type.Name}' must have a public parameterless constructor.");

					if (discovered.TryGetValue(type.Name, out var existing))
						throw new InvalidOperationException(
							$"Duplicate unit mode name '{type.Name}' ({existing.FullName} and {type.FullName}).");

					discovered.Add(type.Name, type);
				}

				modeTypes = discovered;
			}
		}

		/// <summary>Test/reload hook: drops the cache so the next lookup rescans assemblies.</summary>
		public static void Invalidate()
		{
			lock (SyncRoot)
				modeTypes = null;
		}
	}
}
