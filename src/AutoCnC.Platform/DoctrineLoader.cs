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
using System.Linq;
using System.Reflection;
using AutoCnC.Sdk;
using OpenRA;

namespace AutoCnC.Platform
{
	/// <summary>
	/// Finds and loads doctrines from disk.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Modules are ordinary .NET assemblies dropped into a folder, deliberately <b>not</b> listed
	/// in <c>mod.yaml</c>. That keeps them player artifacts rather than part of the mod: you can
	/// have several installed, swap between them, and share one without anybody editing the mod.
	/// </para>
	/// <para>
	/// Scanned locations, in order:
	/// </para>
	/// <list type="number">
	/// <item><c>&lt;bin&gt;/doctrines</c> — where the reference module builds to</item>
	/// <item><c>^SupportDir/autocnc/doctrines</c> — where a player installs downloaded modules</item>
	/// </list>
	/// </remarks>
	public static class DoctrineLoader
	{
		static readonly object SyncRoot = new();
		static List<LoadedDoctrine> loaded;
		static readonly List<string> LoadErrors = [];

		public sealed class LoadedDoctrine
		{
			public IDoctrine Instance { get; }
			public DoctrineDefinition Definition { get; }
			public string SourcePath { get; }

			public LoadedDoctrine(IDoctrine instance, DoctrineDefinition definition, string sourcePath)
			{
				Instance = instance;
				Definition = definition;
				SourcePath = sourcePath;
			}
		}

		/// <summary>Every module found, ordered by name.</summary>
		public static IReadOnlyList<LoadedDoctrine> Doctrines
		{
			get
			{
				EnsureScanned();
				return loaded;
			}
		}

		/// <summary>Problems encountered while scanning, for surfacing to the player.</summary>
		public static IReadOnlyList<string> Errors
		{
			get
			{
				EnsureScanned();
				return LoadErrors;
			}
		}

		public static LoadedDoctrine Find(string name)
		{
			EnsureScanned();
			return loaded.FirstOrDefault(m =>
				string.Equals(m.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>Directories searched for module assemblies.</summary>
		public static IEnumerable<string> SearchPaths
		{
			get
			{
				// Fully qualified: our own namespace is AutoCnC.Platform, which otherwise
				// shadows OpenRA's Platform helper.
				yield return Path.Combine(OpenRA.Platform.EngineDir, "bin", "doctrines");
				yield return Path.Combine(OpenRA.Platform.SupportDir, "autocnc", "doctrines");
			}
		}

		/// <summary>Drops the cache so the next access rescans. Exposed for /reloadmodules.</summary>
		public static void Invalidate()
		{
			lock (SyncRoot)
				loaded = null;
		}

		static void EnsureScanned()
		{
			if (loaded != null)
				return;

			lock (SyncRoot)
			{
				if (loaded != null)
					return;

				var found = new List<LoadedDoctrine>();
				LoadErrors.Clear();

				foreach (var directory in SearchPaths)
				{
					if (!Directory.Exists(directory))
						continue;

					foreach (var file in Directory.GetFiles(directory, "*.dll"))
						LoadFrom(file, found);
				}

				loaded = found.OrderBy(m => m.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}
		}

		static void LoadFrom(string file, List<LoadedDoctrine> found)
		{
			try
			{
				// LoadFrom rather than Load: dependencies (the SDK, OpenRA) are already resolved
				// in the default context, so a module only needs to bring itself.
				var assembly = Assembly.LoadFrom(file);

				foreach (var type in assembly.GetTypes())
				{
					if (type.IsAbstract || type.IsInterface || !typeof(IDoctrine).IsAssignableFrom(type))
						continue;

					if (type.GetConstructor(Type.EmptyTypes) == null)
					{
						LoadErrors.Add($"{type.Name}: doctrines need a public parameterless constructor.");
						continue;
					}

					var instance = (IDoctrine)Activator.CreateInstance(type);
					var definition = DoctrineBuilder.Build(instance);

					if (string.IsNullOrWhiteSpace(definition.Name))
					{
						LoadErrors.Add($"{type.Name}: Name must not be empty.");
						continue;
					}

					found.Add(new LoadedDoctrine(instance, definition, file));
				}
			}
			catch (ReflectionTypeLoadException ex)
			{
				// Almost always a module built against a different SDK version.
				var detail = ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message;
				LoadErrors.Add($"{Path.GetFileName(file)}: could not load types — {detail}");
			}
			catch (Exception ex)
			{
				LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
				Log.Write("debug", $"Failed to load doctrine '{file}': {ex}");
			}
		}
	}
}
