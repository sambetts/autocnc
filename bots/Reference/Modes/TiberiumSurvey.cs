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
using System.Runtime.CompilerServices;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;
using OpenRA;

namespace AutoCnC.Reference.Modes
{
	/// <summary>
	/// The side's one pass over its own half of the map, flown by whichever screen vehicle is
	/// free to fly it.
	/// </summary>
	/// <remarks>
	/// The route and how far along it the side has got belong to the side, not to a unit: a
	/// jeep that dies at point six hands the survey to the next one at point six rather than
	/// point one, and a doctrine switch that moves the surveyor into another mode does not
	/// restart it. Keyed on the owning player, like <see cref="EnemyBaseSightings"/>, so it
	/// cannot leak between sides or matches.
	/// <para>
	/// Called at the top of every mode a <c>jeep</c> or <c>bggy</c> runs —
	/// <see cref="ScoutMode"/> in Opening and Scout, <see cref="HarvesterEscortMode"/> in
	/// Defence, <see cref="WatchMode"/> in Attack — because a survey that only ran in one
	/// doctrine would only run when that doctrine happened to coincide with a live screen
	/// vehicle. On 16:9 that was 34 seconds of a 1,405-second match; in a later one the Attack
	/// doctrine, which then sent screen vehicles into the push, stopped it at point 3 of 17.
	/// One surveyor at a time; the rest keep their doctrine's job.
	/// </para>
	/// <para>
	/// Exploration is permanent, so once the route is done it is done: this answers false for
	/// every later call and the modes behave exactly as they did before it existed.
	/// </para>
	/// </remarks>
	public static class TiberiumSurvey
	{
		sealed class Survey
		{
			public List<SurveyPoint> Plan;
			public SurveyBounds Bounds;
			public SurveyProgress Progress = SurveyProgress.Start;
			public uint Surveyor;
			public int ClaimTick = int.MinValue;
			public bool Announced;
		}

		static readonly ConditionalWeakTable<Player, Survey> Surveys = new();

		static readonly SurveyTuning Tuning = SurveyTuning.Default;

		/// <summary>Whether this actor type flies the survey: the cheap, fast, far-sighted ones.</summary>
		public static bool Flies(string actorType)
		{
			var screens = ReferencePlans.ScreenVehicles;
			for (var i = 0; i < screens.Length; i++)
				if (string.Equals(screens[i], actorType, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		/// <summary>
		/// Whether this side has flown its whole survey, and so can see its own ground.
		/// </summary>
		/// <remarks>
		/// False until a surveyor has planned the route, so a side with no screen vehicle yet
		/// reads as unsurveyed — which is the truth. Asked by <see cref="HarvesterMode"/>: until
		/// this is true, the resource layer this side can read is a sample of its home ground
		/// rather than a map of it. See <see cref="HarvesterLogic.DefersToEngine"/>.
		/// </remarks>
		public static bool IsFlown(Player owner)
		{
			if (owner == null || !Surveys.TryGetValue(owner, out var survey) || survey.Plan == null)
				return false;

			return SurveyLogic.IsComplete(survey.Plan, survey.Progress);
		}

		/// <summary>
		/// The survey's order for this unit, when this unit is the one flying it.
		/// </summary>
		/// <returns>False when the unit should do its mode's own job instead.</returns>
		public static bool TryStep(Actor self, ModeContext ctx, out UnitDecision decision)
		{
			decision = UnitDecision.Continue;
			if (self == null || ctx == null || self.Owner == null || !ctx.CanMove || !Flies(self.Info.Name))
				return false;

			var survey = Surveys.GetOrCreateValue(self.Owner);
			if (survey.Plan == null)
			{
				var bounds = ctx.World.Map.Bounds;
				var home = ctx.BaseCenter;
				survey.Bounds = new SurveyBounds(
					bounds.Left, bounds.Top, bounds.Left + bounds.Width - 1, bounds.Top + bounds.Height - 1);
				survey.Plan = SurveyLogic.Plan(survey.Bounds, home.X, home.Y, Tuning);
			}

			if (SurveyLogic.IsComplete(survey.Plan, survey.Progress))
			{
				if (survey.Announced)
					return false;

				// Said once, so the trace can prove the whole route was flown.
				survey.Announced = true;
				decision = UnitDecision.Continue with
				{
					Reason = $"tiberium survey complete: {survey.Plan.Count} points on our half of the map",
					ReasonId = SurveyLogic.CompleteReasonId
				};
				return true;
			}

			var tick = ctx.WorldTick;
			if (survey.Surveyor != 0 && survey.Surveyor != self.ActorID)
			{
				var holder = ctx.ResolveActor(survey.Surveyor);
				var holderLive = holder != null && !holder.IsDead && holder.IsInWorld;
				if (holderLive && tick - survey.ClaimTick <= Tuning.ClaimLeaseTicks)
					return false;
			}

			if (survey.Surveyor != self.ActorID)
			{
				survey.Surveyor = self.ActorID;
				survey.Progress = SurveyLogic.Handover(survey.Progress);
			}

			survey.ClaimTick = tick;

			// Only now is anything sensed, so a caller that got false back still owns whatever
			// sensing buffer it read before asking.
			var threats = ctx.SenseThreats(new WDist(Tuning.ThreatRadiusCells * 1024));
			EnemySightings.Record(self.Owner, threats);

			var threatened = false;
			var threatX = 0;
			var threatY = 0;
			var nearest = int.MaxValue;
			for (var i = 0; i < threats.Count; i++)
			{
				var t = threats[i];
				if (!t.CanHitUs || t.DistanceUnits >= nearest)
					continue;

				threatened = true;
				nearest = t.DistanceUnits;
				threatX = t.CellX;
				threatY = t.CellY;
			}

			var here = self.Location;
			var outcome = SurveyLogic.Decide(
				new SurveyState(ctx.CanMove, here.X, here.Y, threatened, threatX, threatY, survey.Bounds),
				survey.Plan,
				survey.Progress,
				Tuning);

			survey.Progress = outcome.Progress;
			if (SurveyLogic.IsComplete(survey.Plan, survey.Progress))
			{
				survey.Surveyor = 0;
				survey.Announced = true;
			}

			decision = outcome.Decision;
			return true;
		}
	}
}
