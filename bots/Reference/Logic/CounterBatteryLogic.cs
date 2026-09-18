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
	public readonly record struct CounterBatteryState(
		bool HasShellingTarget,
		uint ShellingActorId,
		int DistanceUnits,
		int WeaponRangeUnits,
		int TicksSinceHit,
		int SpentEvaluations,
		bool CanMove,
		bool HasWeapon);

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
		/// Close on the gun, or null if this rule has no opinion and the normal assault rules
		/// should answer instead.
		/// </summary>
		public static UnitDecision? Decide(in CounterBatteryState s, in CounterBatteryTuning t)
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

			return UnitDecision.Attack(s.ShellingActorId,
				$"counter-battery, closing {s.DistanceUnits}u on what outranges us");
		}
	}
}
