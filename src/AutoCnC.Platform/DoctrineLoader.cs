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
	/// <item><c>Launch.DoctrinePath</c> — a specific assembly the launcher wants played</item>
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

		/// <summary>
		/// The first doctrine that came from <paramref name="path"/> — either that exact assembly
		/// or, when it is a folder, anything inside it. Used to resolve the launcher's choice
		/// without making the player's class name and their file name agree.
		/// </summary>
		public static LoadedDoctrine FindFrom(string path)
		{
			EnsureScanned();

			if (string.IsNullOrWhiteSpace(path))
				return null;

			var full = Normalise(path);
			if (full == null)
				return null;

			var isDirectory = Directory.Exists(full);

			return loaded.FirstOrDefault(m => isDirectory
				? string.Equals(Normalise(Path.GetDirectoryName(m.SourcePath)), full, StringComparison.OrdinalIgnoreCase)
				: string.Equals(Normalise(m.SourcePath), full, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// A path in the one form we compare in. Returns null for anything unusable.
		/// </summary>
		/// <remarks>
		/// <see cref="Path.GetFullPath"/> keeps a trailing separator while
		/// <see cref="Path.GetDirectoryName"/> never produces one, so without trimming, a folder
		/// the user typed with a trailing backslash would never match the folder its doctrines
		/// were found in.
		/// </remarks>
		static string Normalise(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
				return null;

			try
			{
				return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			}
			catch (Exception)
			{
				return null;
			}
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

				// A doctrine the launcher pointed us at, played straight out of its own build
				// output. Nothing is copied, so there is no stale installed copy to get confused
				// by when the author rebuilds.
				var launchPath = LaunchOptions.DoctrinePath;
				if (!string.IsNullOrEmpty(launchPath) && Directory.Exists(launchPath))
					yield return launchPath;
			}
		}

		/// <summary>
		/// Assembly files to scan, in the order they get to claim an assembly identity.
		/// </summary>
		/// <remarks>
		/// <para>
		/// <c>Launch.DoctrinePath</c> comes first, deliberately. <see cref="Assembly.LoadFrom"/>
		/// binds by assembly <i>identity</i>, not by path: once an assembly is in the default
		/// context, a later call naming a different file with the same identity silently hands
		/// back the first one. Loading the launcher's choice first is therefore what makes
		/// "play this exact build" true rather than aspirational — otherwise a same-named copy
		/// left in <c>engine/bin/doctrines</c> would quietly win and you would play stale code.
		/// </para>
		/// </remarks>
		static IEnumerable<string> CandidateFiles()
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var file in LaunchCandidates())
				if (seen.Add(Path.GetFullPath(file)))
					yield return file;

			foreach (var directory in SearchPaths)
				if (Directory.Exists(directory))
					foreach (var file in Directory.GetFiles(directory, "*.dll"))
						if (seen.Add(Path.GetFullPath(file)))
							yield return file;
		}

		/// <summary><c>Launch.DoctrinePath</c> as files: it may name one assembly or a folder.</summary>
		static IEnumerable<string> LaunchCandidates()
		{
			var launchPath = LaunchOptions.DoctrinePath;
			if (string.IsNullOrEmpty(launchPath))
				yield break;

			if (File.Exists(launchPath))
				yield return launchPath;
			else if (Directory.Exists(launchPath))
				foreach (var file in Directory.GetFiles(launchPath, "*.dll"))
					yield return file;
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
				var identities = new HashSet<Assembly>();
				LoadErrors.Clear();

				foreach (var file in CandidateFiles())
					LoadFrom(file, found, identities);

				loaded = found.OrderBy(m => m.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}
		}

		static void LoadFrom(string file, List<LoadedDoctrine> found, HashSet<Assembly> identities)
		{
			try
			{
				// LoadFrom rather than Load: dependencies (the SDK, OpenRA) are already resolved
				// in the default context, so a module only needs to bring itself.
				var assembly = Assembly.LoadFrom(file);

				// LoadFrom binds by identity, so two files that are the same assembly hand back
				// one instance. Registering it once — under the first path that asked for it —
				// keeps a build installed in engine/bin/doctrines and the same build played from
				// its own output folder from showing up as two doctrines.
				if (!identities.Add(assembly))
					return;

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
			catch (FileLoadException)
			{
				// Two different builds that call themselves the same assembly. .NET will not have
				// both in one context, and the one already loaded is the one we were asked for
				// first — which is why Launch.DoctrinePath is scanned ahead of everything else.
				LoadErrors.Add($"{Path.GetFileName(file)}: skipped, because {AlreadyLoadedFrom(file, identities)} " +
					"is a different build of the same assembly and got there first. " +
					"Two builds of one doctrine cannot run at once — delete the copy you don't want.");
			}
			catch (Exception ex)
			{
				LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
				Log.Write("debug", $"Failed to load doctrine '{file}': {ex}");
			}
		}

		/// <summary>Where the assembly that beat <paramref name="file"/> to its identity came from.</summary>
		static string AlreadyLoadedFrom(string file, HashSet<Assembly> identities)
		{
			try
			{
				var name = AssemblyName.GetAssemblyName(file).Name;
				var winner = identities.FirstOrDefault(a => a.GetName().Name == name);
				if (!string.IsNullOrEmpty(winner?.Location))
					return winner.Location;
			}
			catch (Exception)
			{
			}

			return "another doctrine already loaded";
		}
	}
}
