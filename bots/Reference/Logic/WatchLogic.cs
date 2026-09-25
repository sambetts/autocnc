// ============================================================================
//  WatchLogic — keep eyes on their base once it has been found.
//
//  Pure, like ScoutSearchLogic: where a watcher stands and when it looks in is a
//  function of our base, the structure this side remembers and the map's bounds.
//  No engine types, integer-only.
//
//  Licence: GPL-3.0-or-later. See LICENSE and NOTICE.md.
// ============================================================================

using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>The numbers the watch is tuned with. Distances in cells, time in ticks.</summary>
	public readonly record struct WatchTuning(
		int PostStandoffCells,   // how far short of their base, towards ours, the watcher sits
		int MaxStandoffBonusCells, // how much further back a post a tower reaches may be drawn
		int LookInRadiusCells,   // how far from the base's cell a look-in circles
		int DwellTicks,          // time on post between look-ins
		int ArrivedCells,        // close enough to a waypoint
		int StallEvaluations,    // evaluations without moving before a waypoint is written off
		int MaxEvasions,         // go-arounds in one look-in before the base is too hot to circle
		int SidestepCells,       // how far a go-around step moves
		int SameBaseCells)       // a mirror this near the remembered structure is the same base
	{
		public static WatchTuning Default { get; } = new(
			// The assault's own standoff (MusterTuning.StandoffUnits): outside the reach of every
			// static defence in the ruleset, measured from the structure this side last saw.
			PostStandoffCells: 14,

			// ...but their defences need not stand behind that structure. On 16:9 a gtwr stood
			// twelve cells in front of the yard the army was fighting, so a post measured from
			// the yard sat two cells from the tower. Each evaluation a static defence threatens
			// the post draws it back one sidestep, up to two.
			MaxStandoffBonusCells: 12,

			// jeep and bggy both see 8 cells, so circling at 8 keeps the base's own cell in view
			// from every waypoint and shows 16 cells beyond it.
			LookInRadiusCells: 8,

			// One game minute at 25 ticks a second. Looks alternate between the two places their
			// base may be, so each is looked into roughly every two and a half minutes.
			DwellTicks: 1500,

			// The same numbers ScoutSearchLogic and SurveyLogic use, for the same reasons.
			ArrivedCells: 4,
			StallEvaluations: 4,
			MaxEvasions: 3,
			SidestepCells: 6,

			// Twice the look-in radius: a mirror that near is already inside the look-in's view.
			SameBaseCells: 16);
	}

	/// <summary>What one watcher knows about itself and the side this evaluation.</summary>
	/// <remarks>
	/// <see cref="ThreatStatic"/> says the nearest thing that can hit us is a structure or a
	/// static defence, which will still be there next minute; a unit may not be.
	/// </remarks>
	public readonly record struct WatchState(
		bool CanMove,
		int X,
		int Y,
		int Tick,
		int SightingX,
		int SightingY,
		bool ThreatNearby,
		bool ThreatStatic,
		int ThreatX,
		int ThreatY,
		ScoutField Field);

	/// <summary>
	/// Per-watcher memory: which base this cycle watches, which leg of the cycle it is on, and
	/// whether it is moving.
	/// </summary>
	/// <remarks>
	/// <see cref="Leg"/> 0 is the post; 1 to 3 are the look-in waypoints. <see cref="HeadingX"/>
	/// and <see cref="HeadingY"/> are the post it was last sent to, so a post that has genuinely
	/// moved re-arms the stall watchdog while one jittering by a cell or two does not.
	/// <see cref="StandoffBonus"/> only ever grows: a tower that reached the post once is
	/// assumed to be there for the rest of this watcher's life.
	/// </remarks>
	public readonly record struct WatchMemory(
		int Cycle,
		int Leg,
		int DwellSinceTick,
		int Evasions,
		int LastX,
		int LastY,
		int StillEvaluations,
		int HeadingX,
		int HeadingY,
		int StandoffBonus)
	{
		public const int Unset = int.MinValue;

		public static WatchMemory Start { get; } =
			new(0, 0, Unset, 0, int.MinValue, int.MinValue, 0, int.MinValue, int.MinValue, 0);
	}

	public readonly record struct WatchOutcome(UnitDecision Decision, WatchMemory Memory);

	/// <summary>
	/// Where the side's light scout vehicle stands while the army is pushing, and when it looks
	/// into their base.
	/// </summary>
	/// <remarks>
	/// Until this existed the <c>Attack</c> doctrine put every <c>jeep</c> and <c>bggy</c> into
	/// <c>AttackBaseMode</c> with the rest of the army. They are the fastest things this bot
	/// builds, so they reached the enemy first and alone. On 16:9 at Hard both jeeps were
	/// conscripted at 225s and dead at 246s and 248s; nothing on this side looked at anything
	/// the army was not standing next to for the remaining 391 seconds. A helipad five cells
	/// from the airfield first seen at 237s went unseen until 587s while its helicopters struck
	/// our base from 395s, and every enemy unit type was first seen 10 seconds or less before it
	/// first hit us. The same conscription stopped the home tiberium survey at point 3 of 17.
	/// <para>
	/// A cycle is: stand on the approach <see cref="WatchTuning.PostStandoffCells"/> short of
	/// the base, towards ours, for <see cref="WatchTuning.DwellTicks"/>; then circle the base
	/// through three waypoints — one flank, the far side, the other flank; then start the next
	/// cycle. Cycles alternate between the structure this side remembers and the point mirror
	/// of our own base, because the army's memory follows the army while the mirror is where a
	/// symmetric map put their yard. When the two are within
	/// <see cref="WatchTuning.SameBaseCells"/> there is only one base to watch.
	/// </para>
	/// <para>
	/// A look-in that has to go around something <see cref="WatchTuning.MaxEvasions"/> times is
	/// abandoned for the next cycle. The watcher is worth what it sees, and a dead one sees
	/// nothing for the rest of the push.
	/// </para>
	/// </remarks>
	public static class WatchLogic
	{
		public const string ToPostReasonId = "watch.to-post";
		public const string OnPostReasonId = "watch.on-post";
		public const string LookInReasonId = "watch.look-in";
		public const string LookInContestedReasonId = "watch.look-in-contested";
		public const string EvadeReasonId = "watch.evade";
		public const string SearchReasonId = "watch.search";
		public const string FoundReasonId = "watch.found";

		const int LegPost = 0;
		const int LookInPoints = 3;

		public static WatchOutcome Decide(in WatchState s, in WatchMemory memory, in WatchTuning t)
		{
			var m = Observe(memory, s);
			if (!s.CanMove)
				return new WatchOutcome(UnitDecision.Continue, m);

			var target = Target(s, m.Cycle, t);

			if (m.Leg > LegPost)
				return LookingIn(s, m, target, t);

			var post = Post(s, target, m.StandoffBonus, t);
			if (!ScoutSearchLogic.Within(post.X, post.Y, m.HeadingX, m.HeadingY, t.ArrivedCells))
				m = m with { HeadingX = post.X, HeadingY = post.Y, StillEvaluations = 0 };

			if (s.ThreatNearby)
			{
				// A tower that reaches the post will reach it again after the go-around, so the
				// post itself moves back. A unit is only stepped around.
				if (s.ThreatStatic && m.StandoffBonus < t.MaxStandoffBonusCells)
				{
					m = m with
					{
						StandoffBonus = System.Math.Min(t.MaxStandoffBonusCells, m.StandoffBonus + t.SidestepCells)
					};
					post = Post(s, target, m.StandoffBonus, t);
				}

				var aside = Sidestep(s, post, t);
				return new WatchOutcome(
					UnitDecision.MoveTo(aside.X, aside.Y,
						$"threatened on watch over their base at {target.X},{target.Y}, going around; post {t.PostStandoffCells + m.StandoffBonus} cells short",
						EvadeReasonId),
					m with { StillEvaluations = 0 });
			}

			// A post it cannot reach is watched from as near as it got.
			var arrived = ScoutSearchLogic.Within(s.X, s.Y, post.X, post.Y, t.ArrivedCells)
				|| m.StillEvaluations >= t.StallEvaluations;

			if (arrived && m.DwellSinceTick == WatchMemory.Unset)
				m = m with { DwellSinceTick = s.Tick };

			if (m.DwellSinceTick != WatchMemory.Unset && s.Tick - m.DwellSinceTick >= t.DwellTicks)
			{
				m = m with { Leg = LegPost + 1, Evasions = 0, StillEvaluations = 0 };
				return new WatchOutcome(LookIn(s, target, m.Leg, t), m);
			}

			if (!arrived)
				return new WatchOutcome(
					UnitDecision.MoveTo(post.X, post.Y,
						$"watching their approach: to post {post.X},{post.Y}, {t.PostStandoffCells + m.StandoffBonus} cells short of their base at {target.X},{target.Y}",
						ToPostReasonId),
					m);

			return new WatchOutcome(
				UnitDecision.Continue with
				{
					Reason = $"on watch at {s.X},{s.Y} over their base at {target.X},{target.Y}",
					ReasonId = OnPostReasonId
				},
				m);
		}

		/// <summary>
		/// The base this cycle watches: the remembered structure on even cycles, the point mirror
		/// of our base on odd ones, unless the two are the same base.
		/// </summary>
		public static (int X, int Y) Target(in WatchState s, int cycle, in WatchTuning t)
		{
			var mirror = ScoutSearchLogic.Objective(0, s.Field, ScoutTuning.Default);
			if ((cycle & 1) == 0
				|| ScoutSearchLogic.Within(s.SightingX, s.SightingY, mirror.X, mirror.Y, t.SameBaseCells))
				return (s.SightingX, s.SightingY);

			return mirror;
		}

		/// <summary>
		/// The cell on the line from their base towards ours, the standoff plus
		/// <paramref name="bonus"/> short of it.
		/// </summary>
		public static (int X, int Y) Post(in WatchState s, (int X, int Y) target, int bonus, in WatchTuning t) =>
			Clamp(AssaultStagingLogic.StagingCell(
				target.X, target.Y, s.Field.BaseX, s.Field.BaseY, t.PostStandoffCells + bonus), s.Field);

		/// <summary>
		/// Look-in waypoint <paramref name="index"/> of three: a flank, the far side from us, the
		/// other flank, each <see cref="WatchTuning.LookInRadiusCells"/> from the base's cell.
		/// </summary>
		public static (int X, int Y) LookInPoint(in WatchState s, (int X, int Y) target, int index, in WatchTuning t)
		{
			var dx = target.X - s.Field.BaseX;
			var dy = target.Y - s.Field.BaseY;
			var distance = AssaultStagingLogic.IntSqrt(dx * dx + dy * dy);
			if (distance <= 0)
			{
				dx = 1;
				dy = 0;
				distance = 1;
			}

			var r = t.LookInRadiusCells;
			var alongX = dx * r / distance;
			var alongY = dy * r / distance;

			var point = index switch
			{
				0 => (target.X - alongY, target.Y + alongX),
				1 => (target.X + alongX, target.Y + alongY),
				_ => (target.X + alongY, target.Y - alongX),
			};

			return Clamp(point, s.Field);
		}

		static WatchOutcome LookingIn(in WatchState s, WatchMemory m, (int X, int Y) target, in WatchTuning t)
		{
			var point = LookInPoint(s, target, m.Leg - 1, t);

			if (s.ThreatNearby)
			{
				if (m.Evasions >= t.MaxEvasions)
					return NextCycle(s, m, t,
						$"look-in at {target.X},{target.Y} contested after {m.Evasions} go-arounds",
						LookInContestedReasonId);

				var aside = Sidestep(s, point, t);
				return new WatchOutcome(
					UnitDecision.MoveTo(aside.X, aside.Y,
						$"threatened looking into their base at {target.X},{target.Y}, going around",
						EvadeReasonId),
					m with { Evasions = m.Evasions + 1, StillEvaluations = 0 });
			}

			if (ScoutSearchLogic.Within(s.X, s.Y, point.X, point.Y, t.ArrivedCells)
				|| m.StillEvaluations >= t.StallEvaluations)
			{
				if (m.Leg >= LookInPoints)
					return NextCycle(s, m, t, $"looked into their base at {target.X},{target.Y}", ToPostReasonId);

				m = m with { Leg = m.Leg + 1, StillEvaluations = 0 };
			}

			return new WatchOutcome(LookIn(s, target, m.Leg, t), m);
		}

		static UnitDecision LookIn(in WatchState s, (int X, int Y) target, int leg, in WatchTuning t)
		{
			var point = LookInPoint(s, target, leg - 1, t);
			return UnitDecision.MoveTo(point.X, point.Y,
				$"looking into their base at {target.X},{target.Y}: waypoint {leg}/{LookInPoints} at {point.X},{point.Y}",
				LookInReasonId);
		}

		static WatchOutcome NextCycle(in WatchState s, in WatchMemory m, in WatchTuning t, string why, string reasonId)
		{
			var next = m with
			{
				Cycle = m.Cycle + 1,
				Leg = LegPost,
				DwellSinceTick = WatchMemory.Unset,
				Evasions = 0,
				StillEvaluations = 0
			};

			var target = Target(s, next.Cycle, t);
			var post = Post(s, target, next.StandoffBonus, t);
			next = next with { HeadingX = post.X, HeadingY = post.Y };

			return new WatchOutcome(
				UnitDecision.MoveTo(post.X, post.Y,
					$"{why}; to post {post.X},{post.Y} over their base at {target.X},{target.Y}",
					reasonId),
				next);
		}

		static WatchMemory Observe(in WatchMemory m, in WatchState s) =>
			m.LastX == s.X && m.LastY == s.Y
				? m with { StillEvaluations = m.StillEvaluations + 1 }
				: m with { LastX = s.X, LastY = s.Y, StillEvaluations = 0 };

		static (int X, int Y) Sidestep(in WatchState s, (int X, int Y) objective, in WatchTuning t)
		{
			var scout = new ScoutState(
				CanMove: s.CanMove,
				X: s.X,
				Y: s.Y,
				StructureInSight: false,
				ThreatNearby: true,
				ThreatX: s.ThreatX,
				ThreatY: s.ThreatY,
				Field: s.Field);

			return ScoutSearchLogic.Sidestep(
				scout, objective.X, objective.Y, ScoutTuning.Default with { SidestepCells = t.SidestepCells });
		}

		static (int X, int Y) Clamp((int X, int Y) p, in ScoutField f) =>
			(System.Math.Clamp(p.X, f.MinX, f.MaxX), System.Math.Clamp(p.Y, f.MinY, f.MaxY));
	}
}
