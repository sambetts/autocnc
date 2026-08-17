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

namespace AutoCnC.Mod.Modes
{
	/// <summary>
	/// Finds <see cref="IUnitMode"/> implementations by name across every loaded mod assembly.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Discovery uses OpenRA's own <c>ObjectCreator</c> — the same reflection the engine uses to
	/// bind YAML trait names to <c>TraitInfo</c> classes. That is why player-authored modes need
	/// no bespoke loader: build them into an assembly, list it in <c>mod.yaml</c>'s
	/// <c>Assemblies:</c>, and they appear here automatically.
	/// </para>
	/// <para>Modes are addressed by their class name, e.g. <c>DefensiveMode</c>.</para>
	/// </remarks>
	public static class ModeRegistry
	{
		static readonly object SyncRoot = new();
		static Dictionary<string, Type> modeTypes;
		static readonly List<string> LoadErrors = [];

		/// <summary>All discovered mode names, ordered for stable listing.</summary>
		public static IEnumerable<string> AvailableModeNames
		{
			get
			{
				EnsureLoaded();
				return modeTypes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
			}
		}

		/// <summary>Problems found while scanning for modes, for surfacing to the player.</summary>
		public static IReadOnlyList<string> Errors
		{
			get
			{
				EnsureLoaded();
				return LoadErrors;
			}
		}

		public static bool IsKnownMode(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return false;

			EnsureLoaded();
			return modeTypes.ContainsKey(name);
		}

		/// <summary>Resolves a name case-insensitively to its canonical class name, or null.</summary>
		public static string CanonicalName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			EnsureLoaded();
			return modeTypes.TryGetValue(name, out var type) ? type.Name : null;
		}

		/// <summary>
		/// Creates a fresh mode instance, or null if unknown or construction fails. Each unit gets
		/// its own instance, so modes may hold per-unit state.
		/// </summary>
		public static IUnitMode CreateOrNull(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			EnsureLoaded();

			if (!modeTypes.TryGetValue(name, out var type))
				return null;

			try
			{
				return (IUnitMode)Activator.CreateInstance(type);
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to construct unit mode '{name}': {ex}");
				return null;
			}
		}

		static void EnsureLoaded()
		{
			if (modeTypes != null)
				return;

			lock (SyncRoot)
			{
				if (modeTypes != null)
					return;

				var discovered = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
				LoadErrors.Clear();

				foreach (var type in Game.ModData.ObjectCreator.GetTypesImplementing<IUnitMode>())
				{
					if (type.IsAbstract || type.IsInterface)
						continue;

					if (type.GetConstructor(Type.EmptyTypes) == null)
					{
						LoadErrors.Add($"{type.Name}: needs a public parameterless constructor.");
						continue;
					}

					if (discovered.TryGetValue(type.Name, out var existing))
					{
						LoadErrors.Add($"{type.Name}: duplicate name ({existing.FullName} and {type.FullName}).");
						continue;
					}

					discovered.Add(type.Name, type);
				}

				modeTypes = discovered;
			}
		}

		/// <summary>Drops the cache so the next lookup rescans loaded assemblies.</summary>
		public static void Invalidate()
		{
			lock (SyncRoot)
				modeTypes = null;
		}
	}
}
