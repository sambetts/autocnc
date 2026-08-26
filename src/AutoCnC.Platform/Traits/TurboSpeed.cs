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
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Lets the fast game speeds actually reach their tick rate, and measures the speed the",
		"machine really manages. Attach this to the world actor.")]
	public class TurboSpeedInfo : TraitInfo
	{
		[Desc("Milliseconds per tick at or below which a match counts as turbo. 40ms is normal",
			"speed and 20ms is the engine's own 'fastest', so the default covers only the speeds",
			"AutoC&C adds on top of those.")]
		public readonly int TurboTimestep = 16;

		[Desc("Turn VSync off for the duration of a turbo match. The engine renders exactly one",
			"frame per logic tick during play, so with VSync on the tick rate cannot exceed the",
			"monitor's refresh rate: a 2ms timestep quietly becomes 2.4x on a 60Hz screen.")]
		public readonly bool DisableVSync = true;

		[Desc("How often, in seconds of real time, to write the achieved speed to debug.log.",
			"0 turns the reporting off. Asking for 20x and silently getting 6x would make a",
			"doctrine look slower than it is, so the number is worth having on the record.")]
		public readonly int ReportInterval = 10;

		public override object Create(ActorInitializer init) { return new TurboSpeed(init.World, this); }
	}

	/// <summary>
	/// Removes the display-side ceiling on the fast game speeds, and reports the speed actually
	/// achieved.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A game speed is a wall-clock rate, not a rule change: <c>ludicrous</c> runs exactly the
	/// simulation <c>default</c> does, ten times as fast. Watching a doctrine at 10x is therefore
	/// the cheapest honest test of it there is, and the match is no different for having been
	/// watched that way.
	/// </para>
	/// <para>
	/// What stands between the requested speed and the achieved one is not the timestep. The
	/// engine forces one rendered frame per logic tick during play, so VSync pins the tick rate to
	/// the refresh rate — which is the whole reason a 2ms timestep would otherwise land at about
	/// 2.4x on a 60Hz screen. Hence dropping VSync for the duration. Past that it is simply how
	/// fast the machine can simulate and draw the map, which is why the achieved rate is measured
	/// rather than assumed: a doctrine judged at a speed you did not actually get is a doctrine
	/// judged wrongly.
	/// </para>
	/// <para>
	/// VSync is driven straight on the renderer and never written to settings, so the player's own
	/// preference survives untouched and is simply reapplied by the next world to load — including
	/// the menu shellmap you land on when the match ends. That leaves no saved state to restore,
	/// and so nothing to leak if a match ends in a way this trait never sees.
	/// </para>
	/// </remarks>
	public class TurboSpeed : IWorldLoaded, ITickRender
	{
		/// <summary>Milliseconds per tick at <c>default</c> speed: the 1x every multiplier means.</summary>
		public const int NominalTimestep = 40;

		/// <summary>Real milliseconds each measurement covers. Long enough to ride out one slow tick.</summary>
		const int SampleWindow = 1000;

		readonly World world;
		readonly TurboSpeedInfo info;

		long sampleStarted;
		int sampleStartedTick;
		long nextReport;
		bool vsyncLifted;

		public TurboSpeed(World world, TurboSpeedInfo info)
		{
			this.world = world;
			this.info = info;
		}

		/// <summary>True while this match is running faster than the engine's own speeds go.</summary>
		public bool IsTurbo { get; private set; }

		/// <summary>The speed asked for, as a multiple of <c>default</c>.</summary>
		public float RequestedSpeed => (float)NominalTimestep / EffectiveTimestep;

		/// <summary>The speed being managed, as a multiple of <c>default</c>. 0 until first measured.</summary>
		public float AchievedSpeed { get; private set; }

		/// <summary>True once we have measured, and the machine is missing the asked-for speed.</summary>
		public bool IsFallingShort => AchievedSpeed > 0 && AchievedSpeed < 0.9f * RequestedSpeed;

		/// <summary>
		/// Why a turbo match is falling short, which depends on whether we lifted the display cap.
		/// </summary>
		public string ShortfallReason => info.DisableVSync
			? "the machine is the limit here, not the setting"
			: "VSync is still on, so the refresh rate is the ceiling";

		/// <summary>
		/// The tick rate actually in force. A replay's is its own, and moves while you watch.
		/// </summary>
		public int EffectiveTimestep => world.IsReplay && world.ReplayTimestep > 0 ? world.ReplayTimestep : Timestep;

		/// <summary>Guards the arithmetic against a mod.yaml that asks for a zero timestep.</summary>
		int Timestep => world.Timestep > 0 ? world.Timestep : NominalTimestep;

		/// <summary>
		/// Sets a replay's playback speed, as a multiple of <c>default</c>. 0 pauses it.
		/// </summary>
		/// <remarks>
		/// The engine's replay bar scales from the speed the match was <em>recorded</em> at, so on
		/// a 40x recording its slowest button is still 20x. This is absolute instead, which is the
		/// only way to watch a turbo match back at a speed a person can follow.
		/// </remarks>
		public bool SetReplaySpeed(float multiplier)
		{
			if (!world.IsReplay)
				return false;

			world.ReplayTimestep = multiplier <= 0
				? 0
				: Math.Clamp((int)Math.Round(NominalTimestep / multiplier), 1, 1000);

			return true;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// A replay of a turbo match would otherwise open at the speed it was recorded at, and
			// 40x is no more watchable played back than it was live. Start it at 1x; the replay
			// bar and /speed can take it from there.
			if (w.IsReplay && Timestep < NominalTimestep)
				w.ReplayTimestep = NominalTimestep;

			// The shellmap behind the menus ticks too, and nobody wants the menu uncapped.
			IsTurbo = w.Type == WorldType.Regular && EffectiveTimestep <= info.TurboTimestep;
			ApplyVSync();

			if (IsTurbo)
				Log.Write("debug", $"Turbo: {EffectiveTimestep}ms per tick, {RequestedSpeed:0.#}x." +
					(info.DisableVSync ? " VSync is off for the duration." : ""));

			nextReport = Game.RunTime + 1000L * info.ReportInterval;
			RestartSample();
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			// A replay's speed moves under us whenever the viewer presses a button, so what counts
			// as turbo — and whether the display cap needs lifting — has to be re-decided as we go.
			if (world.IsReplay)
			{
				IsTurbo = world.Type == WorldType.Regular && EffectiveTimestep <= info.TurboTimestep;
				ApplyVSync();
			}

			// Simulated ticks against the wall clock, which is the only measure that means
			// anything for a rate the engine merely tries for. This hook runs once per logic tick
			// and, unlike ITick, runs while paused as well — hence starting a fresh sample rather
			// than reporting a paused game as a slow machine.
			if (world.Paused || world.WorldTick < sampleStartedTick)
			{
				RestartSample();
				return;
			}

			var now = Game.RunTime;
			var elapsed = now - sampleStarted;
			if (elapsed < SampleWindow)
				return;

			AchievedSpeed = (world.WorldTick - sampleStartedTick) * (float)NominalTimestep / elapsed;
			RestartSample();

			if (!IsTurbo || info.ReportInterval <= 0 || now < nextReport)
				return;

			nextReport = now + 1000L * info.ReportInterval;
			Log.Write("debug", $"Turbo: asked for {RequestedSpeed:0.#}x, getting {AchievedSpeed:0.#}x" +
				(IsFallingShort ? $" — {ShortfallReason}." : "."));
		}

		/// <summary>
		/// Lifts or restores the display cap, only when the answer has changed.
		/// </summary>
		/// <remarks>
		/// Restoring matters more in a replay than it looks: replays do not force a frame per tick,
		/// so with the frame limiter off as well, a replay left VSync-free at 1x would spin the GPU
		/// as fast as it can draw for no benefit at all.
		/// </remarks>
		void ApplyVSync()
		{
			var lift = info.DisableVSync && IsTurbo;
			if (lift == vsyncLifted)
				return;

			vsyncLifted = lift;
			Game.Renderer?.SetVSyncEnabled(!lift && Game.Settings.Graphics.VSync);
		}

		void RestartSample()
		{
			sampleStarted = Game.RunTime;
			sampleStartedTick = world.WorldTick;
		}
	}
}
