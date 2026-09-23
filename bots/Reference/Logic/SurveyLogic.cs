// ============================================================================
//  SurveyLogic — walk our own half of the map once, so the economy can see it.
//
//  Pure, like ScoutSearchLogic: the route is a function of the map's bounds and
//  where our own base stands, nothing else. No engine types.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System;
using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>The numbers the tiberium survey is tuned with. Distances in cells.</summary>
	public readonly record struct SurveyTuning(
		int SpacingCells,
		int ReachCells,
		int ReachPercentOfMirror,
		int HomeSkipCells,
		int InsetCells,
		int ArrivedCells,
		int StallEvaluations,
		int MaxEvasions,
		int SidestepCells,
		int ClaimLeaseTicks,
		int ThreatRadiusCells)
	{
		public static SurveyTuning Default { get; } = new(
			// jeep and bggy both reveal 8 cells. On a 12-cell lattice no cell is more than 8.5
			// cells from a point, and the legs between points sweep 16-cell corridors, so one
			// pass leaves no patch the size of a tiberium field unseen.
			SpacingCells: 12,

			// A harvester is sent at most HarvesterTuning.MaxHaulCells (36) from its refinery,
			// and refineries stand within a few cells of the yard. Ground further out than this
			// is ground no harvester of ours will be sent to, so looking at it buys nothing.
			ReachCells: 40,

			// ...and never most of the way to where the other side probably lives.
			ReachPercentOfMirror: 65,

			// The yard and the first buildings already see this much.
			HomeSkipCells: 8,

			InsetCells: 2,

			// Same numbers ScoutSearchLogic uses, for the same reasons.
			ArrivedCells: 4,
			StallEvaluations: 4,

			// Evaluations spent going around something at one point before that point is written
			// off as contested. Contested ground is not ground a harvester can work anyway.
			MaxEvasions: 3,
			SidestepCells: 6,

			// How long a surveyor that stopped reporting keeps the job. Ten seconds: long enough
			// to ride out a doctrine switch's order latency, short enough that the next screen
			// vehicle picks the route up where the dead one left it.
			ClaimLeaseTicks: 250,

			ThreatRadiusCells: 6);
	}

	public readonly record struct SurveyPoint(int X, int Y);

	public readonly record struct SurveyBounds(int MinX, int MinY, int MaxX, int MaxY);

	/// <summary>How far along the route the side has got, and what the current surveyor is doing.</summary>
	public readonly record struct SurveyProgress(int Next, int LastX, int LastY, int StillEvaluations, int Evasions)
	{
		public static SurveyProgress Start { get; } = new(0, int.MinValue, int.MinValue, 0, 0);
	}

	public readonly record struct SurveyState(
		bool CanMove,
		int X,
		int Y,
		bool ThreatNearby,
		int ThreatX,
		int ThreatY,
		SurveyBounds Bounds);

	public readonly record struct SurveyOutcome(UnitDecision Decision, SurveyProgress Progress);

	/// <summary>
	/// One pass over the ground a harvester of ours could be sent to, so the resource layer can
	/// report the fields on it.
	/// </summary>
	/// <remarks>
	/// Resource reads are shroud-filtered, so a field nobody has looked at does not exist as far
	/// as <c>FindResourceFields</c> is concerned. On 16:9 the three fields beside the base were
	/// worked out by about 550s — the harvesters' own reasons read "field worked out, harvesting
	/// 4-9 cells (16-39 left)" from 554s — and income fell from 122 credits a second over
	/// 420-480s to 37 over 540-600s, before the siege began. A 37-cell field 24 to 33 cells
	/// from the southern refinery was never seen at all: the only scouting of the match was 34
	/// seconds of <c>ScoutMode</c> driving straight at the enemy's base, and the jeep that finds
	/// their base parks there until it dies. The enemy earned 107-142 credits a second
	/// throughout.
	/// <para>
	/// The route is a lattice over our half of the map — nearer our yard than the point mirror
	/// of it, inside the haul reach — visited nearest-first, preferring on ties the point further
	/// from the mirror, so the safe ground is seen first and the ground toward them last. It is
	/// derived from the map bounds and the yard at runtime and knows nothing about which map is
	/// being played.
	/// </para>
	/// </remarks>
	public static class SurveyLogic
	{
		public const string SurveyReasonId = "economy.survey-tiberium";
		public const string EvadeReasonId = "economy.survey-evade";
		public const string SkipReasonId = "economy.survey-skip-point";
		public const string CompleteReasonId = "economy.survey-complete";

		/// <summary>The survey route for a base at (<paramref name="baseX"/>, <paramref name="baseY"/>).</summary>
		public static List<SurveyPoint> Plan(in SurveyBounds b, int baseX, int baseY, in SurveyTuning t)
		{
			var spacing = Math.Max(1, t.SpacingCells);
			var inset = Math.Max(0, t.InsetCells);
			var lowX = b.MinX + inset;
			var highX = Math.Max(lowX, b.MaxX - inset);
			var lowY = b.MinY + inset;
			var highY = Math.Max(lowY, b.MaxY - inset);

			var mirrorX = b.MinX + b.MaxX - baseX;
			var mirrorY = b.MinY + b.MaxY - baseY;
			var homeToMirrorSq = Sq(mirrorX - baseX) + Sq(mirrorY - baseY);

			// Squared throughout, so there is no square root in a decision that becomes an order.
			var reachSq = Sq(t.ReachCells);
			if (homeToMirrorSq > 0)
			{
				var shareSq = homeToMirrorSq * t.ReachPercentOfMirror * t.ReachPercentOfMirror / 10_000;
				if (shareSq < reachSq)
					reachSq = shareSq;
			}

			var skipSq = Sq(t.HomeSkipCells);
			var points = new List<SurveyPoint>();

			foreach (var y in Axis(lowY, highY, Math.Clamp(baseY, lowY, highY), spacing))
			{
				foreach (var x in Axis(lowX, highX, Math.Clamp(baseX, lowX, highX), spacing))
				{
					var home = Sq(x - baseX) + Sq(y - baseY);
					if (home <= skipSq || home > reachSq)
						continue;

					// Their half: nearer the mirror of our yard than our yard.
					if (homeToMirrorSq > 0 && home > Sq(x - mirrorX) + Sq(y - mirrorY))
						continue;

					points.Add(new SurveyPoint(x, y));
				}
			}

			return Tour(points, baseX, baseY, mirrorX, mirrorY);
		}

		public static bool IsComplete(IReadOnlyList<SurveyPoint> plan, in SurveyProgress p) =>
			plan == null || p.Next >= plan.Count;

		/// <summary>A new surveyor takes over the route where the last one left it.</summary>
		public static SurveyProgress Handover(in SurveyProgress p) =>
			p with { LastX = int.MinValue, LastY = int.MinValue, StillEvaluations = 0, Evasions = 0 };

		public static SurveyOutcome Decide(
			in SurveyState s, IReadOnlyList<SurveyPoint> plan, in SurveyProgress progress, in SurveyTuning t)
		{
			var p = progress.LastX == s.X && progress.LastY == s.Y
				? progress with { StillEvaluations = progress.StillEvaluations + 1 }
				: progress with { LastX = s.X, LastY = s.Y, StillEvaluations = 0 };

			if (!s.CanMove || plan == null)
				return new SurveyOutcome(UnitDecision.Continue, p);

			p = PassReached(plan, p, s, t);
			if (IsComplete(plan, p))
				return Completed(plan, p);

			// Standing still with the route unfinished: the point is unreachable from here, or
			// the order was cancelled. Either way this point has told us all it will.
			if (p.StillEvaluations >= t.StallEvaluations)
			{
				p = PassReached(plan, p with { Next = p.Next + 1, StillEvaluations = 0, Evasions = 0 }, s, t);
				if (IsComplete(plan, p))
					return Completed(plan, p);

				var q = plan[p.Next];
				return new SurveyOutcome(
					UnitDecision.MoveTo(q.X, q.Y,
						$"survey point unreachable, moving on to point {p.Next + 1}/{plan.Count} at {q.X},{q.Y}",
						SkipReasonId),
					p);
			}

			var target = plan[p.Next];
			if (s.ThreatNearby)
			{
				if (p.Evasions >= t.MaxEvasions)
				{
					p = PassReached(plan, p with { Next = p.Next + 1, StillEvaluations = 0, Evasions = 0 }, s, t);
					if (IsComplete(plan, p))
						return Completed(plan, p);

					var q = plan[p.Next];
					return new SurveyOutcome(
						UnitDecision.MoveTo(q.X, q.Y,
							$"survey point {target.X},{target.Y} is contested, moving on to point {p.Next + 1}/{plan.Count} at {q.X},{q.Y}",
							SkipReasonId),
						p);
				}

				var aside = Sidestep(s, target, t);
				return new SurveyOutcome(
					UnitDecision.MoveTo(aside.X, aside.Y,
						$"threatened while surveying for tiberium, going around toward {target.X},{target.Y}",
						EvadeReasonId),
					p with { Evasions = p.Evasions + 1, StillEvaluations = 0 });
			}

			return new SurveyOutcome(
				UnitDecision.MoveTo(target.X, target.Y,
					$"surveying for tiberium, point {p.Next + 1}/{plan.Count} at {target.X},{target.Y}",
					SurveyReasonId),
				p);
		}

		static SurveyOutcome Completed(IReadOnlyList<SurveyPoint> plan, in SurveyProgress p) =>
			new(UnitDecision.Continue with
				{
					Reason = $"tiberium survey complete: {plan.Count} points on our half of the map",
					ReasonId = CompleteReasonId
				},
				p);

		/// <summary>Moves past every point the surveyor is already standing on.</summary>
		static SurveyProgress PassReached(
			IReadOnlyList<SurveyPoint> plan, SurveyProgress p, in SurveyState s, in SurveyTuning t)
		{
			while (p.Next < plan.Count && ScoutSearchLogic.Within(s.X, s.Y, plan[p.Next].X, plan[p.Next].Y, t.ArrivedCells))
				p = p with { Next = p.Next + 1, Evasions = 0 };

			return p;
		}

		static (int X, int Y) Sidestep(in SurveyState s, SurveyPoint target, in SurveyTuning t)
		{
			var b = s.Bounds;
			var scout = new ScoutState(
				CanMove: s.CanMove,
				X: s.X,
				Y: s.Y,
				StructureInSight: false,
				ThreatNearby: true,
				ThreatX: s.ThreatX,
				ThreatY: s.ThreatY,
				Field: new ScoutField(b.MinX, b.MinY, b.MaxX, b.MaxY, s.X, s.Y));

			return ScoutSearchLogic.Sidestep(scout, target.X, target.Y, ScoutTuning.Default with { SidestepCells = t.SidestepCells });
		}

		/// <summary>
		/// Lattice coordinates through <paramref name="origin"/>, plus the edge itself when the
		/// last lattice line leaves more than half a spacing of it unswept.
		/// </summary>
		static List<int> Axis(int low, int high, int origin, int spacing)
		{
			var values = new List<int>();
			for (var v = origin; v >= low; v -= spacing)
				values.Insert(0, v);

			for (var v = origin + spacing; v <= high; v += spacing)
				values.Add(v);

			var half = spacing / 2;
			if (values[0] - low > half)
				values.Insert(0, low);

			if (high - values[values.Count - 1] > half)
				values.Add(high);

			return values;
		}

		/// <summary>
		/// Nearest-neighbour from the yard; ties go to the point further from the mirror, then to
		/// the smaller row and column, so the route is identical on every client.
		/// </summary>
		static List<SurveyPoint> Tour(List<SurveyPoint> points, int baseX, int baseY, int mirrorX, int mirrorY)
		{
			var ordered = new List<SurveyPoint>(points.Count);
			var used = new bool[points.Count];
			var cx = baseX;
			var cy = baseY;

			for (var n = 0; n < points.Count; n++)
			{
				var best = -1;
				long bestNear = 0;
				long bestAway = 0;

				for (var i = 0; i < points.Count; i++)
				{
					if (used[i])
						continue;

					var pt = points[i];
					var near = Sq(pt.X - cx) + Sq(pt.Y - cy);
					var away = Sq(pt.X - mirrorX) + Sq(pt.Y - mirrorY);

					var better = best < 0
						|| near < bestNear
						|| (near == bestNear && away > bestAway)
						|| (near == bestNear && away == bestAway
							&& (pt.Y < points[best].Y || (pt.Y == points[best].Y && pt.X < points[best].X)));

					if (better)
					{
						best = i;
						bestNear = near;
						bestAway = away;
					}
				}

				used[best] = true;
				ordered.Add(points[best]);
				cx = points[best].X;
				cy = points[best].Y;
			}

			return ordered;
		}

		static long Sq(long v) => v * v;
	}
}
