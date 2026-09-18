// ============================================================================
//  ScoutSearchLogic — where a scout should look, and what it does when shot at.
//
//  Pure, like DefensiveLogic and HarvesterLogic: a scout's search is a function
//  of the map's shape and where our own base sits, and nothing else. No engine
//  types, so the rule can be read on its own.
//
//  This exists because the search it replaces was a uniform random walk that
//  ran home on contact, and that is not a search. See ScoutSearchLogic.Objective
//  for what a directed one looks like.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using System;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>The numbers the search is tuned with. Distances in cells.</summary>
	public readonly record struct ScoutTuning(
		int ArrivedCells,        // close enough to say "nothing here"
		int SidestepCells,       // how far a go-around step moves
		int StallEvaluations,    // evaluations without moving before the rung is unreachable
		int RimInsetEighths,     // how far inside the map edge the rim walk runs
		int RimStrideEighths,    // how far around the rim each step goes
		int RimSkipHomeDivisor,  // rim points nearer home than mapSize/this are already explored
		int RimSkipAttempts)     // ...and how many we will skip before taking one anyway
	{
		public static ScoutTuning Default { get; } = new(
			// A jeep that gets this close has had eyes on the cell and everything around it. Any
			// tighter and a blocked cell in the objective's own footprint stalls the whole ladder.
			ArrivedCells: 4,

			// Far enough to break contact with infantry, short enough that the detour is a detour
			// rather than a second journey.
			SidestepCells: 6,

			// Consecutive evaluations at the same cell before the rung is written off. The host
			// suppresses a duplicate order, so a scout whose move activity was cancelled would
			// otherwise stand still for the rest of the match re-issuing an order nobody hears.
			StallEvaluations: 4,

			RimInsetEighths: 1,

			// Three eighths of the perimeter per step visits eight well-separated points before
			// it repeats, which beats crawling around the rim a cell at a time.
			RimStrideEighths: 3,

			RimSkipHomeDivisor: 3,
			RimSkipAttempts: 8);
	}

	/// <summary>The map, and where on it we live.</summary>
	public readonly record struct ScoutField(int MinX, int MinY, int MaxX, int MaxY, int BaseX, int BaseY)
	{
		public int Width => Math.Max(1, MaxX - MinX);

		public int Height => Math.Max(1, MaxY - MinY);
	}

	public readonly record struct ScoutState(
		bool CanMove,
		int X,
		int Y,
		bool StructureInSight,
		bool ThreatNearby,
		int ThreatX,
		int ThreatY,
		ScoutField Field);

	/// <summary>Per-scout memory: which rung of the ladder it is on, and whether it is moving.</summary>
	public readonly record struct ScoutWatchdog(int Objective, int LastX, int LastY, int StillEvaluations)
	{
		public static ScoutWatchdog Start { get; } = new(0, int.MinValue, int.MinValue, 0);
	}

	public readonly record struct ScoutOutcome(UnitDecision Decision, ScoutWatchdog Watchdog);

	/// <summary>Deciding where to look for the other side, and how to stay alive getting there.</summary>
	/// <remarks>
	/// The mode this backs used to pick a uniformly random cell from the whole map and, on seeing
	/// anything that could shoot it, order itself all the way back to its anchor. Both halves are
	/// wrong, and badland-ridges priced them: in 1,136 seconds no scout ever saw a single enemy
	/// structure, <c>enemyBaseFound</c> first read true at 1,045s and only because their army had
	/// arrived at <em>our</em> base, and the Attack doctrine — which is gated on that flag —
	/// therefore never ran for one second of the match. Zero enemy buildings were destroyed, and
	/// the fitness component that measures that scored 0.0 out of 0.15.
	/// <para>
	/// A random destination has no expected progress: the mean random cell is the middle of the
	/// map, so successive targets undo one another and the scout loiters in the centre. That is
	/// exactly where all four jeeps died, around (45-54, 51-72) on a map whose centre is (48,48),
	/// having travelled halfway and turned round. And running home on contact discards every cell
	/// of progress at the first picket — the trace holds 17 "spotted, falling back" against 15
	/// "scouting", so more than half of every scout's orders were retreats.
	/// </para>
	/// </remarks>
	public static class ScoutSearchLogic
	{
		/// <summary>Rungs before the ladder falls back to walking the map's rim.</summary>
		public const int MirrorRungs = 3;

		/// <summary>
		/// The cell this scout should be looking at, for a given rung of the search.
		/// </summary>
		/// <remarks>
		/// Derived entirely from the map's bounds and our own base cell, both of which are runtime
		/// facts about this match. Nothing here knows which map is being played.
		/// <para>
		/// The first rung is our base reflected through the centre of the map, because skirmish
		/// maps place their spawn points symmetrically and that is overwhelmingly the best single
		/// guess. The next two are the single-axis mirrors, which is where the opposite spawn sits
		/// on a map mirrored about one axis rather than rotated. After that there is nothing left
		/// to deduce, so the search walks the rim — bases sit near the edges, and the middle is
		/// the one part of the map a scout crosses anyway on its way anywhere.
		/// </para>
		/// </remarks>
		public static (int X, int Y) Objective(int rung, in ScoutField f, in ScoutTuning t)
		{
			if (rung < 0)
				rung = 0;

			return rung switch
			{
				0 => Clamp(f.MinX + f.MaxX - f.BaseX, f.MinY + f.MaxY - f.BaseY, f),
				1 => Clamp(f.MinX + f.MaxX - f.BaseX, f.BaseY, f),
				2 => Clamp(f.BaseX, f.MinY + f.MaxY - f.BaseY, f),
				_ => Rim(rung - MirrorRungs, f, t),
			};
		}

		public static ScoutOutcome Decide(in ScoutState s, in ScoutWatchdog watchdog, in ScoutTuning t)
		{
			var seen = Observe(watchdog, s);

			// Nothing we order helps a scout that cannot move.
			if (!s.CanMove)
				return new ScoutOutcome(UnitDecision.Continue, seen);

			var here = Objective(seen.Objective, s.Field, t);

			// 1. Eyes on one of their structures. This scout is looking at the one fact the whole
			//    side is gated on, and the remembered sighting is only worth marching on while
			//    somebody can still see it — EnemyBaseSightings.Forget reads exactly that. So it
			//    stops searching and keeps watching, rather than ticking on to the next rung and
			//    letting the corroboration lapse during the army's approach march. Evading still
			//    applies: a dead watcher corroborates nothing.
			if (s.StructureInSight)
				return s.ThreatNearby
					? Evade(s, here.X, here.Y, t, seen)
					: new ScoutOutcome(UnitDecision.Continue, seen with { StillEvaluations = 0 });

			// 2. Arrived, or provably stuck getting there. Either way this rung has told us what
			//    it can — rule 1 above is what "there was something here" looks like — and the
			//    next rung is a different question.
			if (Within(s.X, s.Y, here.X, here.Y, t.ArrivedCells) || seen.StillEvaluations >= t.StallEvaluations)
			{
				var next = seen with { Objective = seen.Objective + 1, StillEvaluations = 0 };
				var to = Objective(next.Objective, s.Field, t);
				return new ScoutOutcome(
					UnitDecision.MoveTo(to.X, to.Y,
						$"searching for their base, rung {next.Objective} at {to.X},{to.Y}"),
					next);
			}

			// 3. Something that can shoot us, and we are not there yet. Step around it rather than
			//    go home. A scout is worth exactly the ground behind it, and an order back to the
			//    anchor throws all of that away to save a 400-credit jeep that has no other job.
			if (s.ThreatNearby)
				return Evade(s, here.X, here.Y, t, seen);

			// 4. Carry on. Restating the same destination costs nothing — the host suppresses a
			//    duplicate — and it re-issues the march whenever something cancelled it.
			return new ScoutOutcome(
				UnitDecision.MoveTo(here.X, here.Y,
					$"searching for their base, rung {seen.Objective} at {here.X},{here.Y}"),
				seen);
		}

		static ScoutOutcome Evade(in ScoutState s, int objX, int objY, in ScoutTuning t, in ScoutWatchdog seen)
		{
			var aside = Sidestep(s, objX, objY, t);
			return new ScoutOutcome(
				UnitDecision.MoveTo(aside.X, aside.Y, "threatened, going around"),
				seen with { StillEvaluations = 0 });
		}

		/// <summary>Whether this scout has moved since the last evaluation.</summary>
		public static ScoutWatchdog Observe(in ScoutWatchdog w, in ScoutState s) =>
			w.LastX == s.X && w.LastY == s.Y
				? w with { StillEvaluations = w.StillEvaluations + 1 }
				: w with { LastX = s.X, LastY = s.Y, StillEvaluations = 0 };

		/// <summary>
		/// A step that opens the range on the threat without giving up the journey.
		/// </summary>
		/// <remarks>
		/// Away from the threat and toward the objective at the same time. On an axis where those
		/// two disagree — the threat standing between us and where we are going — they cancel, and
		/// the step becomes a sideways move on the other axis, which is what going around is. If
		/// both axes cancel there is no such step, so it falls back to plain evasion; standing
		/// still while being shot is the one answer that is never right.
		/// </remarks>
		public static (int X, int Y) Sidestep(in ScoutState s, int objX, int objY, in ScoutTuning t)
		{
			var awayX = Math.Sign(s.X - s.ThreatX);
			var awayY = Math.Sign(s.Y - s.ThreatY);
			var toX = Math.Sign(objX - s.X);
			var toY = Math.Sign(objY - s.Y);

			// Standing on top of it: any direction beats none, so just press on.
			if (awayX == 0 && awayY == 0)
			{
				awayX = toX;
				awayY = toY;
			}

			var stepX = awayX + toX;
			var stepY = awayY + toY;
			if (stepX == 0 && stepY == 0)
			{
				stepX = awayX;
				stepY = awayY;
			}

			return Clamp(s.X + (stepX * t.SidestepCells), s.Y + (stepY * t.SidestepCells), s.Field);
		}

		/// <summary>A point on the map's rim, <paramref name="step"/> strides around from the corner.</summary>
		/// <remarks>
		/// Points that land near our own base are skipped: that ground is explored already, and a
		/// rung spent walking back into our own shroud-free corner is a rung not spent looking.
		/// The skip is bounded so a small map, where every rim point is near home, still returns
		/// one rather than looping.
		/// </remarks>
		public static (int X, int Y) Rim(int step, in ScoutField f, in ScoutTuning t)
		{
			if (step < 0)
				step = 0;

			var insetX = Math.Max(1, f.Width * t.RimInsetEighths / 8);
			var insetY = Math.Max(1, f.Height * t.RimInsetEighths / 8);
			var left = f.MinX + insetX;
			var right = Math.Max(left + 1, f.MaxX - insetX);
			var top = f.MinY + insetY;
			var bottom = Math.Max(top + 1, f.MaxY - insetY);

			var w = right - left;
			var h = bottom - top;
			var perimeter = 2 * (w + h);
			var stride = Math.Max(1, perimeter * t.RimStrideEighths / 8);
			var keepAway = Math.Max(f.Width, f.Height) / Math.Max(1, t.RimSkipHomeDivisor);

			var candidate = (X: left, Y: top);
			for (var attempt = 0; attempt <= t.RimSkipAttempts; attempt++)
			{
				var at = (int)((((long)step + attempt) * stride) % perimeter);
				candidate = WalkRim(at, left, top, w, h);

				if (!Within(candidate.X, candidate.Y, f.BaseX, f.BaseY, keepAway))
					break;
			}

			return candidate;
		}

		static (int X, int Y) WalkRim(int at, int left, int top, int w, int h)
		{
			if (at < w)
				return (left + at, top);

			at -= w;
			if (at < h)
				return (left + w, top + at);

			at -= h;
			return at < w
				? (left + w - at, top + h)
				: (left, top + h - (at - w));
		}

		/// <summary>Chebyshev distance, which is how a unit that can move diagonally travels.</summary>
		public static bool Within(int x, int y, int toX, int toY, int cells) =>
			Math.Abs(x - toX) <= cells && Math.Abs(y - toY) <= cells;

		static (int X, int Y) Clamp(int x, int y, in ScoutField f) =>
			(Math.Clamp(x, f.MinX, f.MaxX), Math.Clamp(y, f.MinY, f.MaxY));
	}
}
