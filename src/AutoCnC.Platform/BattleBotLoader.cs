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
	/// Finds and loads battle bots from disk.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Bots are ordinary .NET assemblies dropped into a folder, deliberately <b>not</b> listed in
	/// <c>mod.yaml</c>. That keeps them player artifacts rather than part of the mod: you can have
	/// several installed, swap between them, and share one without anybody editing the mod.
	/// </para>
	/// <para>
	/// An assembly that declares no <see cref="IBattleBot"/> is read as one bot per
	/// <see cref="IDoctrine"/> it contains, each owning that doctrine and never switching. A
	/// doctrine written before bots existed is therefore still a thing you can put in a battle,
	/// and the rule is the model rather than a shim: a bot with one doctrine has nothing to
	/// decide.
	/// </para>
	/// <para>
	/// Scanned locations, in order:
	/// </para>
	/// <list type="number">
	/// <item><c>&lt;bin&gt;/bots</c> — where the reference bot builds to</item>
	/// <item><c>^SupportDir/autocnc/bots</c> — where a player installs downloaded bots</item>
	/// <item><c>Launch.BattleBotPath</c> — a specific assembly the launcher wants played</item>
	/// </list>
	/// </remarks>
	public static class BattleBotLoader
	{
		static readonly object SyncRoot = new();
		static List<LoadedBattleBot> loaded;
		static readonly List<string> LoadErrors = [];

		public sealed class LoadedBattleBot
		{
			/// <summary>The author's object, or null for a lone doctrine we wrapped ourselves.</summary>
			public IBattleBot Instance { get; }

			public BattleBotDefinition Definition { get; }
			public string SourcePath { get; }

			public LoadedBattleBot(IBattleBot instance, BattleBotDefinition definition, string sourcePath)
			{
				Instance = instance;
				Definition = definition;
				SourcePath = sourcePath;
			}
		}

		/// <summary>Every bot found, ordered by name.</summary>
		public static IReadOnlyList<LoadedBattleBot> Bots
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

		public static LoadedBattleBot Find(string name)
		{
			EnsureScanned();
			return loaded.FirstOrDefault(b =>
				string.Equals(b.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// The first bot that came from <paramref name="path"/> — either that exact assembly or,
		/// when it is a folder, anything inside it. Used to resolve the launcher's choice without
		/// making the player's class name and their file name agree.
		/// </summary>
		public static LoadedBattleBot FindFrom(string path)
		{
			EnsureScanned();

			if (string.IsNullOrWhiteSpace(path))
				return null;

			var full = Normalise(path);
			if (full == null)
				return null;

			var isDirectory = Directory.Exists(full);

			return loaded.FirstOrDefault(b => isDirectory
				? string.Equals(Normalise(Path.GetDirectoryName(b.SourcePath)), full, StringComparison.OrdinalIgnoreCase)
				: string.Equals(Normalise(b.SourcePath), full, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// A path in the one form we compare in. Returns null for anything unusable.
		/// </summary>
		/// <remarks>
		/// <see cref="Path.GetFullPath"/> keeps a trailing separator while
		/// <see cref="Path.GetDirectoryName"/> never produces one, so without trimming, a folder
		/// the user typed with a trailing backslash would never match the folder its bots were
		/// found in.
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

		/// <summary>Directories searched for bot assemblies.</summary>
		public static IEnumerable<string> SearchPaths
		{
			get
			{
				// Fully qualified: our own namespace is AutoCnC.Platform, which otherwise
				// shadows OpenRA's Platform helper.
				yield return Path.Combine(OpenRA.Platform.EngineDir, "bin", "bots");
				yield return Path.Combine(OpenRA.Platform.SupportDir, "autocnc", "bots");

				// A bot the launcher pointed us at, played straight out of its own build output.
				// Nothing is copied, so there is no stale installed copy to get confused by when
				// the author rebuilds.
				var launchPath = LaunchOptions.BattleBotPath;
				if (!string.IsNullOrEmpty(launchPath) && Directory.Exists(launchPath))
					yield return launchPath;
			}
		}

		/// <summary>
		/// Assembly files to scan, in the order they get to claim an assembly identity.
		/// </summary>
		/// <remarks>
		/// <c>Launch.BattleBotPath</c> comes first, deliberately. <see cref="Assembly.LoadFrom"/>
		/// binds by assembly <i>identity</i>, not by path: once an assembly is in the default
		/// context, a later call naming a different file with the same identity silently hands
		/// back the first one. Loading the launcher's choice first is therefore what makes "play
		/// this exact build" true rather than aspirational — otherwise a same-named copy left in
		/// <c>engine/bin/bots</c> would quietly win and you would play stale code.
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

		/// <summary><c>Launch.BattleBotPath</c> as files: it may name one assembly or a folder.</summary>
		static IEnumerable<string> LaunchCandidates()
		{
			var launchPath = LaunchOptions.BattleBotPath;
			if (string.IsNullOrEmpty(launchPath))
				yield break;

			if (File.Exists(launchPath))
				yield return launchPath;
			else if (Directory.Exists(launchPath))
				foreach (var file in Directory.GetFiles(launchPath, "*.dll"))
					yield return file;
		}

		/// <summary>Drops the cache so the next access rescans.</summary>
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

				var found = new List<LoadedBattleBot>();
				var identities = new HashSet<Assembly>();
				LoadErrors.Clear();

				foreach (var file in CandidateFiles())
					LoadFrom(file, found, identities);

				loaded = found.OrderBy(b => b.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
			}
		}

		static void LoadFrom(string file, List<LoadedBattleBot> found, HashSet<Assembly> identities)
		{
			try
			{
				// LoadFrom rather than Load: dependencies (the SDK, OpenRA) are already resolved
				// in the default context, so a bot only needs to bring itself.
				var assembly = Assembly.LoadFrom(file);

				// LoadFrom binds by identity, so two files that are the same assembly hand back
				// one instance. Registering it once — under the first path that asked for it —
				// keeps a build installed in engine/bin/bots and the same build played from its
				// own output folder from showing up as two bots.
				if (!identities.Add(assembly))
					return;

				var types = assembly.GetTypes();

				// One bot per doctrine, but only for an assembly that declared no bot of its own.
				// An assembly that has one has already said how its doctrines fit together, and
				// listing them separately would offer the player a choice its author did not make.
				//
				// Counted as declared rather than as loaded, deliberately: a bot whose Configure
				// throws must stay broken and say so. Falling back to its doctrines would answer
				// a bug by quietly playing something else, which is the one thing this loader is
				// careful never to do.
				if (LoadBots(types, file, found) == 0)
					Orphans(types, file, found);
			}
			catch (ReflectionTypeLoadException ex)
			{
				// Almost always a bot built against a different SDK version.
				var detail = ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message;
				LoadErrors.Add($"{Path.GetFileName(file)}: could not load types — {detail}");
			}
			catch (FileLoadException)
			{
				// Two different builds that call themselves the same assembly. .NET will not have
				// both in one context, and the one already loaded is the one we were asked for
				// first — which is why Launch.BattleBotPath is scanned ahead of everything else.
				LoadErrors.Add($"{Path.GetFileName(file)}: skipped, because {AlreadyLoadedFrom(file, identities)} " +
					"is a different build of the same assembly and got there first. " +
					"Two builds of one bot cannot run at once — delete the copy you don't want.");
			}
			catch (Exception ex)
			{
				LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
				Log.Write("debug", $"Failed to load battle bot '{file}': {ex}");
			}
		}

		/// <summary>
		/// Loads every battle bot in an assembly. Returns how many it <em>found</em>, whether or
		/// not they could be built, because that is the question the caller is asking.
		/// </summary>
		static int LoadBots(Type[] types, string file, List<LoadedBattleBot> found)
		{
			// Counted from the types themselves rather than from what was built, so a bot that
			// cannot be constructed at all still counts as declared.
			var declared = types.Count(t => !t.IsAbstract && !t.IsInterface && typeof(IBattleBot).IsAssignableFrom(t));

			foreach (var type in Constructible<IBattleBot>(types, "battle bots"))
			{
				try
				{
					var instance = (IBattleBot)Activator.CreateInstance(type);
					var definition = BattleBotBuilder.Build(instance);

					if (string.IsNullOrWhiteSpace(definition.Name))
					{
						LoadErrors.Add($"{type.Name}: Name must not be empty.");
						continue;
					}

					found.Add(new LoadedBattleBot(instance, definition, file));
				}
				catch (Exception ex)
				{
					// A bot that declared no doctrines, or two by the same name. Its own problem,
					// and not one the other bots in the file should be punished for.
					LoadErrors.Add($"{type.Name}: {ex.Message}");
				}
			}

			return declared;
		}

		/// <summary>Wraps every doctrine in a bot-less assembly as a bot that owns just it.</summary>
		static void Orphans(Type[] types, string file, List<LoadedBattleBot> found)
		{
			foreach (var type in Constructible<IDoctrine>(types, "doctrines"))
			{
				try
				{
					var doctrine = DoctrineBuilder.Build((IDoctrine)Activator.CreateInstance(type));

					if (string.IsNullOrWhiteSpace(doctrine.Name))
					{
						LoadErrors.Add($"{type.Name}: Name must not be empty.");
						continue;
					}

					found.Add(new LoadedBattleBot(null, BattleBotBuilder.Wrap(doctrine), file));
				}
				catch (Exception ex)
				{
					LoadErrors.Add($"{type.Name}: {ex.Message}");
				}
			}
		}

		/// <summary>Types in an assembly we can actually make one of, complaining about the rest.</summary>
		static IEnumerable<Type> Constructible<T>(Type[] types, string plural)
		{
			foreach (var type in types)
			{
				if (type.IsAbstract || type.IsInterface || !typeof(T).IsAssignableFrom(type))
					continue;

				if (type.GetConstructor(Type.EmptyTypes) == null)
				{
					LoadErrors.Add($"{type.Name}: {plural} need a public parameterless constructor.");
					continue;
				}

				yield return type;
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

			return "another bot already loaded";
		}
	}
}
