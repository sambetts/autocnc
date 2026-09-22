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

using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="CounterBatteryLogic"/>. Distances in world units (1024 == 1 cell).</summary>
	public readonly record struct CounterBatteryTuning(
		int ReachUnits,
		int MemoryTicks,
		int MaxEvaluations)
	{
		public static CounterBatteryTuning Default { get; } = new(
			// How far a unit may close on something shelling it. Read off the ruleset rather
			// than tuned: the longest reach any mobile weapon in Tiberian Dawn has is 11 cells
			// — Nod's ArtilleryShell and GDI's 227mm, both at rangeCells 11 — so twelve is
			// "whatever can shell me from its maximum reach, plus a cell of margin", and
			// nothing further than that exists to be chased. A leash derived from the longest
			// weapon in the game cannot be a constant tuned to one map or one opponent.
			ReachUnits: 12 * 1024,

			// How long a hit is remembered. A tick is 40ms, so this is 12 game seconds.
			// ArtilleryShell fires 0.385 times a second — one shell every 2.6s — so twelve
			// seconds spans about four rounds: long enough that the gap between shells does
			// not drop the target, short enough that a piece which has displaced or stopped
			// firing is forgotten rather than followed.
			MemoryTicks: 300,

			// Hard cap on how many evaluations this rule may hold one unit across its whole
			// life. Never reset — not by a new target and not by a new push — for exactly the
			// reason MusterTuning.MaxStagingEvaluations is not: a budget that a fresh push
			// refills is not a budget, and that lesson has already cost this bot one match.
			//
			// An evaluation is about 1.4 game seconds, so 24 is roughly 34 seconds. Closing
			// the 7 cells between an e1's 4-cell reach and artillery's 11 takes about 6
			// seconds at 1.2 cells a second, so this affords several attempts and still sits
			// well inside the 70-second mean Attack episode. It is what bounds the damage if
			// the shelling comes from across terrain the pathfinder cannot cross — a real
			// risk on a ridge map, where walking at an unreachable gun could otherwise repeat
			// for the rest of the match.
			MaxEvaluations: 24);
	}

	/// <summary>Everything the counter-battery rule needs, with no engine types in it.</summary>
	/// <remarks>
	/// <paramref name="CanAttackTarget"/> is the difference between a gun that can be shot at
	/// and one that is still sitting in fog. Both are worth answering and they are answered
	/// differently: see <see cref="CounterBatteryLogic.Decide"/>.
	/// </remarks>
	public readonly record struct CounterBatteryState(
		bool HasShellingTarget,
		uint ShellingActorId,
		int TargetX,
		int TargetY,
		bool CanAttackTarget,
		int DistanceUnits,
		int WeaponRangeUnits,
		int TicksSinceHit,
		int SpentEvaluations,
		bool CanMove,
		bool HasWeapon);

	/// <summary>
	/// A gun that hit one of this side's actors from further away than that actor could answer.
	/// </summary>
	/// <remarks>
	/// <paramref name="StandoffUnits"/> is how far the shot came from, measured from the thing
	/// it hit. It is what ranks two live reports against each other — see
	/// <see cref="CounterBatteryLogic.Prefer"/> — because the report worth keeping is the one
	/// nothing standing there can answer.
	/// </remarks>
	public readonly record struct ShellingReport(
		bool HasReport,
		uint AttackerId,
		int X,
		int Y,
		int StandoffUnits,
		int Tick)
	{
		public static ShellingReport None { get; } = default;
	}

	/// <summary>
	/// What to do about something that is shooting us from further away than we can shoot back.
	/// </summary>
	/// <remarks>
	/// <b>This is the single largest hole in the bot, and badland-ridges priced it exactly.</b>
	/// Of 264 units lost, <b>139 — 53% — were killed by enemy <c>arty</c></b>, which took
	/// 789,717 damage from this side and returned 42,120. The worst line in the whole
	/// engagement matrix is <c>e1</c> against <c>arty</c>: <b>365,569 damage taken, 0 dealt,
	/// 113 riflemen dead, zero kills</b>. Not a bad trade — no trade at all.
	/// <para>
	/// The cause is reach, and <c>game-rules.json</c> states it plainly. <c>ArtilleryShell</c>
	/// is <c>rangeCells 11</c>, 10,000 damage, and <b>140% against <c>None</c> armour</b>, which
	/// is what every infantryman in the game wears. This bot's weapons reach 4 cells
	/// (<c>e1</c>, <c>e2</c>, <c>jeep</c>), 4.75 (<c>mtnk</c>) and 6 (<c>e3</c>). Only
	/// <c>msam</c> reaches 11. So an artillery piece parked at its maximum range is, to
	/// everything else this bot fields, a weapon that cannot be answered at all.
	/// </para>
	/// <para>
	/// And the bot had no rule that even noticed. <see cref="Modes.AttackBaseMode"/> senses
	/// threats within its own weapon range, so a gun 11 cells away is not merely deprioritised,
	/// it is <em>never in the candidate list</em> — both <see cref="AttackBaseLogic.SelectBlocker"/>
	/// and <see cref="AttackBaseLogic.SelectLastStandTarget"/> filter on
	/// <c>DistanceUnits &gt; WeaponRangeUnits</c>, correctly, because you cannot shoot what you
	/// cannot reach. The damage callback was the only route to the knowledge and it was
	/// documented as "intentionally does nothing". That is why 156 units and 28,800 credits
	/// died inside one six-cell circle between 870s and 1556s: a clustered army standing still,
	/// being shelled by something no rule in the bot could see.
	/// </para>
	/// <para>
	/// The guide sanctions this route explicitly — a damage callback may identify an unseen
	/// attacker, because the attacked unit receives that notification like any player's would.
	/// It is deliberately <em>not</em> generalised into map-wide knowledge: one unit learns
	/// about one gun that hit it, and forgets after <see cref="CounterBatteryTuning.MemoryTicks"/>.
	/// </para>
	/// <para>
	/// <b>It was wired into the wrong mode, and 16:9 priced that too.</b> Until this round the
	/// only caller was <see cref="Modes.AttackBaseMode"/>, and the Attack doctrine ran for
	/// <b>zero seconds</b> of that 1,400-second match: the army peaked at 2,200 credits against
	/// a 4,000 commitment bar, so not one <c>assault.*</c> decision appears anywhere in the
	/// trace. The side spent <b>1,215 of 1,400 seconds in Defence</b>, where
	/// <see cref="Modes.DefensiveMode"/> returned <c>Hold("on post, no threats")</c>
	/// <b>7,368 times</b> while <c>msam</c> took the base apart from eleven cells. The rule was
	/// right and unreachable, which is worth exactly as much as being wrong.
	/// </para>
	/// <para>
	/// <b>The screen is not who gets shelled.</b> Of the 863,494 damage those guns dealt, about
	/// 13,000 landed on something with a weapon; the rest went into refineries, power plants,
	/// the airstrip, the construction yard and six harvesters. A per-unit memory written only by
	/// the victim therefore hears almost none of the siege, so the report is side-scoped — see
	/// <see cref="Modes.ShellingReports"/> — exactly as
	/// <see cref="Modes.HarvesterThreats"/> already is, and under the same bound: one gun, one
	/// standoff, forgotten in twelve seconds, and useless to any unit that is not already within
	/// twelve cells of it.
	/// </para>
	/// <para>
	/// <b>This does not break the never-chase rule.</b> The caller only consults it when nothing
	/// at all is inside the unit's weapon range, so it can never displace a shot; the leash is
	/// re-checked every evaluation against <see cref="CounterBatteryTuning.ReachUnits"/>, so the
	/// unit reverts the moment the target outruns it; and the lifetime budget caps the whole
	/// mechanism however the match goes. Refusing to be baited off an assault is a virtue.
	/// Standing in the open while a gun you were never told about kills 113 of you is not.
	/// </para>
	/// ZERO OpenRA dependencies by design and integer-only, so it is lockstep-safe.
	/// </remarks>
	public static class CounterBatteryLogic
	{
		/// <summary>
		/// Whether a hit from this distance is worth remembering at all.
		/// </summary>
		/// <remarks>
		/// Two bounds, and they are the whole filter. Inside our own reach there is nothing to
		/// fix: the ordinary blocker and last-stand scorers already rank that attacker against
		/// everything else in range, and they weigh warhead match and threat class, which this
		/// rule does not. Beyond the leash there is nothing to be done either — a unit that
		/// walked that far would be doing the chase this mode exists to refuse.
		/// </remarks>
		public static bool ShouldRecord(int distanceUnits, int weaponRangeUnits, in CounterBatteryTuning t)
			=> weaponRangeUnits > 0
				&& distanceUnits > weaponRangeUnits
				&& distanceUnits <= t.ReachUnits;

		/// <summary>
		/// Whether a hit on one of this side's actors is worth telling the screen about.
		/// </summary>
		/// <remarks>
		/// The same two bounds as <see cref="ShouldRecord"/>, minus its requirement that the
		/// victim be armed — and that omission is the point. On 16:9 <c>msam</c> killed
		/// <b>28 of this side's 82 losses, 17,400 credits, 47% of everything it lost</b>, and
		/// took <b>zero damage in return all match</b>. Of the 863,494 damage those guns dealt,
		/// only about 13,000 landed on a unit with a weapon: the rest went into refineries,
		/// power plants, the airstrip, the construction yard and the harvester fleet, none of
		/// which can shoot back and three of which cannot even move. A predicate that only
		/// listens to armed victims is deaf to the entire siege.
		/// <para>
		/// An unarmed victim has a reach of zero, so everything outranges it and every hit on it
		/// is reportable. That is not as loose as it sounds, because the report is only a
		/// candidate: <see cref="Prefer"/> keeps the one that came from furthest out, and the
		/// unit that reads it re-tests the distance against <em>its own</em> reach before moving
		/// a step. A rifleman standing next to the jeep that is shooting the refinery therefore
		/// declines, and the ordinary scorers handle it as they always did.
		/// </para>
		/// </remarks>
		public static bool ShouldReport(int distanceUnits, int victimWeaponRangeUnits, in CounterBatteryTuning t)
			=> distanceUnits > victimWeaponRangeUnits
				&& distanceUnits <= t.ReachUnits;

		/// <summary>Whether a remembered gun is recent enough to still be worth answering.</summary>
		public static bool StillHot(in ShellingReport report, int nowTick, in CounterBatteryTuning t) =>
			report.HasReport && nowTick >= report.Tick && nowTick - report.Tick <= t.MemoryTicks;

		/// <summary>
		/// Which of two shelling reports a side should be answering.
		/// </summary>
		/// <remarks>
		/// Sticky for the reason every other report in this bot is: a base under siege is hit
		/// several times a second, and a rule that simply took the latest hit would re-aim the
		/// whole screen every tick and deliver it nowhere. The same gun firing again is this
		/// siege continuing, so it refreshes rather than competes.
		/// </remarks>
		public static ShellingReport Choose(
			in ShellingReport held, in ShellingReport candidate, int nowTick, in CounterBatteryTuning t)
		{
			if (!candidate.HasReport)
				return StillHot(held, nowTick, t) ? held : ShellingReport.None;

			if (!StillHot(held, nowTick, t))
				return candidate;

			if (candidate.AttackerId == held.AttackerId)
				return candidate;

			return Prefer(candidate, held) ? candidate : held;
		}

		/// <summary>
		/// Which of two guns is the one worth walking at. Further out wins, then the lower actor
		/// id, so the ordering is total and every unit on the side agrees on it.
		/// </summary>
		/// <remarks>
		/// Further out, rather than closer or more recent, because standoff is the whole
		/// complaint. Something shooting a refinery from four cells is already inside the reach
		/// of the riflemen standing on it and will be shot by the ordinary scorers; something
		/// shooting it from eleven is not, and it is the one nothing on this side has ever
		/// touched.
		/// </remarks>
		public static bool Prefer(in ShellingReport candidate, in ShellingReport held) =>
			candidate.StandoffUnits != held.StandoffUnits
				? candidate.StandoffUnits > held.StandoffUnits
				: candidate.AttackerId < held.AttackerId;

		/// <summary>
		/// Close on the gun, or null if this rule has no opinion and the normal rules should
		/// answer instead.
		/// </summary>
		public static UnitDecision? Decide(in CounterBatteryState s, in CounterBatteryTuning t, string reasonId)
		{
			if (!s.HasShellingTarget || !s.HasWeapon || !s.CanMove)
				return null;

			// Spent. Everything below this line is a machine that walks a unit toward something,
			// and a unit that has used its allowance goes back to fighting the assault it was
			// sent to fight, for the rest of its life.
			if (s.SpentEvaluations >= t.MaxEvaluations)
				return null;

			// Stopped shooting, or moved on. Either way this is no longer the thing killing us.
			if (s.TicksSinceHit > t.MemoryTicks)
				return null;

			// Now inside our reach: hand it back to the scorers, which will pick it — or
			// something better — on exactly the grounds they always use.
			if (s.WeaponRangeUnits <= 0 || s.DistanceUnits <= s.WeaponRangeUnits)
				return null;

			// Outrun us. Closing further is the chase, not the counter-battery.
			if (s.DistanceUnits > t.ReachUnits)
				return null;

			// A gun that is still in fog cannot be given as a target at all, and that is the
			// ordinary case rather than the exception: an eleven-cell piece parked outside
			// everything this side can see is exactly what this rule exists for. Walking at the
			// cell it fired from is the same answer HarvesterEscortMode already gives, and it
			// ends in a shot for the same reason — the unit arrives with the gun inside its own
			// reach and the ordinary scorers take over.
			if (!s.CanAttackTarget)
				return UnitDecision.AttackMoveTo(s.TargetX, s.TargetY,
					$"counter-battery, advancing {s.DistanceUnits}u on where the shelling came from",
					reasonId);

			return UnitDecision.Attack(s.ShellingActorId,
				$"counter-battery, closing {s.DistanceUnits}u on what outranges us",
				reasonId);
		}
	}
}
