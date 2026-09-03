#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Network;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Runs a launched local battle without wall-clock pacing or rendered frames.",
		"Enabled only by Launch.Headless; normal games are untouched.")]
	public sealed class HeadlessSimulationInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) => new HeadlessSimulation();
	}

	/// <summary>
	/// Drives the same order and world stages as OpenRA's client loop, but without its scheduler.
	/// </summary>
	public sealed class HeadlessSimulation : IPostWorldLoaded
	{
		const double NominalTicksPerSecond = 25;
		const int CancellationCheckMask = 0x3F;

		static readonly FieldInfo OrderManagerField = typeof(World).GetField(
			"OrderManager", BindingFlags.Instance | BindingFlags.NonPublic) ??
			throw new MissingFieldException(typeof(World).FullName, "OrderManager");

		bool started;

		void IPostWorldLoaded.PostWorldLoaded(World world, WorldRenderer worldRenderer)
		{
			if (started || !LaunchOptions.Headless || world.Type != WorldType.Regular || world.IsReplay)
				return;

			started = true;

			// PostWorldLoaded is called while the StartGame order is still being received. Defer
			// until the next outer tick so the connection is no longer inside its receive path.
			Game.RunAfterTick(() => Run(world, worldRenderer));
		}

		static void Run(World world, WorldRenderer worldRenderer)
		{
			var orderManager = OrderManagerField.GetValue(world) as OrderManager ??
				throw new InvalidOperationException("The pinned OpenRA World.OrderManager field has an unexpected type.");
			var startedUtc = DateTime.UtcNow;
			var timer = Stopwatch.StartNew();
			var startingTick = world.WorldTick;
			long attempts = 0;
			var cancelled = false;
			var timedOut = false;
			var maxTicks = (long)LaunchOptions.HeadlessMaxGameSeconds * (long)NominalTicksPerSecond;

			Console.WriteLine("==> Headless simulation started; logic ticks are running at CPU speed.");

			try
			{
				while (!world.IsGameOver)
				{
					if ((attempts & CancellationCheckMask) == 0 && CancellationRequested())
					{
						cancelled = true;
						world.EndGame();
						break;
					}

					// Preserve the engine's per-logic-tick ordering. Delayed actions include
					// client-local housekeeping used by normal gameplay traits.
					Game.PerformDelayedActions();
					Game.Sound.Tick();
					Sync.RunUnsynced(world, orderManager.TickImmediate);

					var advanced = orderManager.TryTick();
					if (advanced)
					{
						Sync.RunUnsynced(world, () => world.OrderGenerator.Tick(world));
						world.Tick();

						// Normal play delays the final results panel using wall-clock time. There
						// is no panel here, so finalize as soon as every combatant has a result.
						if (HasFinalResult(world))
							world.EndGame();
						else if (maxTicks > 0 && world.WorldTick - startingTick >= maxTicks)
						{
							timedOut = true;
							world.EndGame();
						}
					}

					if (orderManager.LocalFrameNumber > 0)
						Sync.RunUnsynced(world, () => world.TickRender(worldRenderer));

					attempts++;
					if (!advanced)
						Thread.Yield();
				}

				var status = cancelled ? "cancelled" : timedOut ? "timed-out" : "completed";
				var error = timedOut
					? $"The match exceeded {LaunchOptions.HeadlessMaxGameSeconds:N0} nominal game seconds."
					: null;
				WriteReport(world, startedUtc, timer.Elapsed, world.WorldTick - startingTick,
					attempts, status, error);

				var ticksPerSecond = Rate(world.WorldTick - startingTick, timer.Elapsed);
				Console.WriteLine(
					$"==> Headless simulation {status} ({Result(world)}) after {world.WorldTick - startingTick:N0} ticks " +
					$"at {ticksPerSecond:N0} ticks/s ({ticksPerSecond / NominalTicksPerSecond:N1}x).");

				Game.CloseServer();
				Game.Exit();
			}
			catch (Exception ex)
			{
				try
				{
					WriteReport(world, startedUtc, timer.Elapsed, world.WorldTick - startingTick,
						attempts, "failed", ex.Message);
				}
				catch (Exception reportException)
				{
					throw new AggregateException("Headless simulation and report writing both failed.",
						ex, reportException);
				}

				throw;
			}
		}

		static bool HasFinalResult(World world)
		{
			var combatants = world.Players.Where(p => p.Playable && !p.NonCombatant).ToArray();
			return combatants.Length > 0 && combatants.All(p => p.WinState != WinState.Undefined);
		}

		static bool CancellationRequested()
		{
			var path = LaunchOptions.CancellationFile;
			return !string.IsNullOrEmpty(path) && File.Exists(path);
		}

		static string Result(World world) =>
			world.LocalPlayer?.WinState.ToString().ToLowerInvariant() ?? "completed";

		static double Rate(int ticks, TimeSpan elapsed) =>
			ticks / Math.Max(elapsed.TotalSeconds, 0.001);

		static void WriteReport(World world, DateTime startedUtc, TimeSpan elapsed,
			int worldTicks, long attempts, string status, string error)
		{
			var path = ResolvePath(LaunchOptions.HeadlessReport);
			if (path == null)
				return;

			var report = new
			{
				SchemaVersion = 1,
				Mode = "headless",
				Status = status,
				StartedUtc = startedUtc,
				CompletedUtc = DateTime.UtcNow,
				ElapsedMilliseconds = Math.Max(1, (long)elapsed.TotalMilliseconds),
				WorldTicks = worldTicks,
				LogicAttempts = attempts,
				TicksPerSecond = Rate(worldTicks, elapsed),
				SimulationSpeed = Rate(worldTicks, elapsed) / NominalTicksPerSecond,
				GameSeconds = worldTicks / NominalTicksPerSecond,
				MaxGameSeconds = LaunchOptions.HeadlessMaxGameSeconds,
				Result = Result(world),
				Error = error
			};

			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(report,
				new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
					WriteIndented = true
				}));
			File.Move(temporary, path, true);
		}

		static string ResolvePath(string file)
		{
			if (string.IsNullOrWhiteSpace(file) ||
				string.Equals(file, "none", StringComparison.OrdinalIgnoreCase))
				return null;

			return Path.IsPathRooted(file)
				? file
				: Path.Combine(OpenRA.Platform.SupportDir, "Logs", file);
		}
	}
}
