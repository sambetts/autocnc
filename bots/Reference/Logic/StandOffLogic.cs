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
	/// <summary>Tunable knobs for <see cref="StandOffLogic"/>. Distances in world units (1024 == 1 cell).</summary>
	public readonly record struct StandOffTuning(
		int MarginUnits,
		int HoldBandUnits,
		int SiegeSlackUnits,
		int SenseUnits,
		int SiegeSenseUnits,
		int MaxTicks,
		int MaxTicksPerEvaluation)
	{
		public static StandOffTuning Default { get; } = new(
			// One cell outside the defence's reach. Distances are centre to centre, so a unit
			// stopped exactly on the edge of a tower's range is inside it about half the time.
			MarginUnits: 1024,

			// How far beyond that edge a line unit stops walking. Three cells puts the held line
			// between a gtwr's reach plus the margin (7 cells) and an arty's reach (11), which is
			// where the siege piece stands and so where its screen belongs: on 16:9 the arty that
			// killed the towers shelled them from 9.5 to 11.5 cells.
			HoldBandUnits: 3 * 1024,

			// How far past its own reach a siege piece may still be and count as shelling the
			// defence: the same three cells, because a siege piece walking up behind the line
			// stands about one band behind it.
			SiegeSlackUnits: 3 * 1024,

			// How far to look for a defence. The longest ground reach of any static defence in
			// the ruleset is the obelisk's 7.5 cells, so 7.5 + 1 + 3 = 11.5 cells is the widest
			// band this rule can ever act on; twelve covers it, and it is the same leash
			// CounterBatteryTuning.ReachUnits reads off the same ruleset.
			SenseUnits: 12 * 1024,

			// How far from this unit to look for the siege piece. It stands within its reach
			// plus the slack of the defence (14 cells), and this unit within 11.5 of it, so on
			// the same side of the defence — which is where a push arrives from — sixteen covers
			// it with room.
			SiegeSenseUnits: 16 * 1024,

			// The most game time one unit may spend standing off, across its whole life, and
			// never refilled — for the reason CounterBatteryTuning.MaxEvaluations is not: a budget
			// a fresh push refills is not a budget. Counted in ticks rather than evaluations,
			// because a unit in this mode is evaluated about three times a game second (3.1 to
			// 3.3 measured on 16:9, more under fire), not once every 1.4. The duel lab's four arty
			// (2,400 credits) destroy a gtwr in 16.7 s and an obelisk in 20.3 s, and on 16:9 four
			// or five took 16 s for a gun and a gtwr together. Forty-five seconds is two towers at
			// the lab's rate or one at half of it, and it is what bounds the wait when the siege
			// piece is in position but shelling something else.
			MaxTicks: 45 * 25,

			// The most one evaluation adds to that count: one game second. A unit that left the
			// band and came back later has not been standing off in between.
			MaxTicksPerEvaluation: 25);
	}

	/// <summary>Everything the stand-off rule needs, with no engine types in it.</summary>
	/// <remarks>
	/// The defence is the one this unit is deepest into; the mode chooses it and has already
	/// checked that a siege piece is in position against it (<see cref="StandOffLogic.IsSiegeAgainst"/>),
	/// because only the mode can sense allies. <paramref name="ShooterAnswerable"/> is whether the
	/// counter-battery rule has a live answer to something outranging this unit that stands
	/// outside the defence's reach. Cells are map cells; distances and ranges are world units.
	/// </remarks>
	public readonly record struct StandOffState(
		bool CanMove,
		bool HasWeapon,
		int WeaponRangeUnits,
		int X,
		int Y,
		int HomeX,
		int HomeY,
		int MapMinX,
		int MapMinY,
		int MapMaxX,
		int MapMaxY,
		bool HasDefence,
		string DefenceType,
		int DefenceX,
		int DefenceY,
		int DefenceDistanceUnits,
		int DefenceRangeUnits,
		bool SiegeInPosition,
		string SiegeType,
		bool HasTargetInReach,
		uint TargetActorId,
		string TargetType,
		bool ShooterAnswerable,
		int SpentTicks);

	/// <summary>
	/// Keeps line units out of the reach of a static defence that outranges them while a siege
	/// piece of this side shells it.
	/// </summary>
	/// <remarks>
	/// <b>The first push on 16:9 (Nod against Nod, won at 990s) broke on one tower pair.</b> It
	/// launched at 470s with 4,650 of army, took the forward base, and at 572s every rifleman
	/// was advancing on a <c>gun</c> at (68,27) standing beside a <c>gtwr</c> at (69,26). Both
	/// reach 6 cells; an <c>e1</c> reaches 4. The riflemen walked into both and the
	/// counter-battery rule then closed them on whichever had shot them: <b>thirteen <c>e1</c>
	/// died in fourteen seconds (579-593s), twelve to the one tower</b>. Riflemen did 6,200
	/// damage to guard towers all match, against 40,000 hit points each. The towers died anyway
	/// — the <c>gun</c> at 587s and the <c>gtwr</c> at 594s, both to our own <c>arty</c>, which
	/// took 168 damage from towers all match. Then the push had no screen, and five of those
	/// <c>arty</c> died to enemy <c>e1</c> and <c>e3</c> at 603-610s. The tower pair cost 4,300
	/// credits, 44% of the 9,700 the push lost, and the army came home at 1,400 to be attacked
	/// at 725s.
	/// <para>
	/// The duel lab measured exactly this (<c>docs/unit-matchups.md</c>, "Against defences"):
	/// 2,400 credits of <c>e1</c>, <c>bggy</c> or <c>jeep</c> are wiped out by one
	/// <c>gtwr</c>, while <c>arty</c> and <c>msam</c> destroy every tower in the game and lose
	/// nothing. A static defence cannot move, so anything that outranges it kills it for free,
	/// and anything it outranges pays for every second it spends inside its reach. The previous
	/// 16:9 fight lost fourteen <c>e1</c> and four <c>e2</c> to one forward <c>gtwr</c> in 32
	/// seconds.
	/// </para>
	/// <para>
	/// So a line unit — anything short of <see cref="AttackBaseLogic.SiegeRangeUnits"/> — that
	/// a visible, live enemy static defence outranges and can hit does not walk into its reach
	/// while a siege piece of ours stands within its own reach of that defence plus
	/// <see cref="StandOffTuning.SiegeSlackUnits"/>. Inside the reach plus
	/// <see cref="StandOffTuning.MarginUnits"/> it steps straight back out
	/// (<c>assault.stand-off-step-out</c>); within <see cref="StandOffTuning.HoldBandUnits"/>
	/// beyond that it shoots whatever is already in its own reach, which is the screen the
	/// siege piece needs (<c>assault.stand-off-screen</c>), and otherwise holds
	/// (<c>assault.stand-off-defence</c>) — unless something mobile is outranging it from outside
	/// the defence's reach, which the counter-battery rule still answers. The moment the defence
	/// dies, or no siege piece is in position, this has no opinion and the push resumes exactly
	/// as it would have.
	/// </para>
	/// <para>
	/// What it deliberately does not do: act without a siege piece in position (the push then
	/// has nothing that kills the tower for free, and the old rules decide), act for a unit that
	/// matches the defence's reach (an <c>e3</c> against a <c>gtwr</c> is an even range duel,
	/// not a free kill), or wait without end — see <see cref="StandOffTuning.MaxTicks"/>.
	/// </para>
	/// ZERO OpenRA dependencies by design and integer-only, so it is lockstep-safe.
	/// </remarks>
	public static class StandOffLogic
	{
		/// <summary>Whether a unit with this reach is a line unit rather than a siege piece.</summary>
		public static bool IsLineUnit(int weaponRangeUnits) =>
			weaponRangeUnits > 0 && weaponRangeUnits < AttackBaseLogic.SiegeRangeUnits;

		/// <summary>Whether a defence with this reach can shoot a unit that cannot shoot it back.</summary>
		public static bool Outranges(int defenceRangeUnits, int weaponRangeUnits) =>
			defenceRangeUnits > weaponRangeUnits;

		/// <summary>
		/// How many ticks of standing off this evaluation adds to the unit's lifetime count: the
		/// time since its last stand-off evaluation, but never more than
		/// <see cref="StandOffTuning.MaxTicksPerEvaluation"/>, and nothing for its first.
		/// </summary>
		public static int TicksToCharge(int nowTick, int lastStandOffTick, in StandOffTuning t)
		{
			if (lastStandOffTick == int.MinValue || nowTick <= lastStandOffTick)
				return 0;

			var elapsed = nowTick - lastStandOffTick;
			return elapsed > t.MaxTicksPerEvaluation ? t.MaxTicksPerEvaluation : elapsed;
		}

		/// <summary>The distance inside which a defence is shooting this unit, with the margin.</summary>
		public static int ReachUnits(int defenceRangeUnits, in StandOffTuning t) =>
			defenceRangeUnits + t.MarginUnits;

		/// <summary>
		/// How far past the edge of the defence's reach this unit stands: negative is inside it.
		/// </summary>
		public static int Depth(int distanceUnits, int defenceRangeUnits, in StandOffTuning t) =>
			distanceUnits - ReachUnits(defenceRangeUnits, t);

		/// <summary>Whether this defence is near enough to this unit for the rule to act on it.</summary>
		public static bool Concerns(int distanceUnits, int defenceRangeUnits, int weaponRangeUnits, in StandOffTuning t) =>
			IsLineUnit(weaponRangeUnits)
				&& Outranges(defenceRangeUnits, weaponRangeUnits)
				&& Depth(distanceUnits, defenceRangeUnits, t) <= t.HoldBandUnits;

		/// <summary>
		/// Whether an ally with this reach, this far from the defence, is a siege piece shelling it
		/// or about to.
		/// </summary>
		/// <remarks>
		/// Reach at or above <see cref="AttackBaseLogic.SiegeRangeUnits"/> — <c>arty</c> and
		/// <c>msam</c> — outranges every static defence in the ruleset by at least two and a half
		/// cells, which is what makes the kill free. The mode only offers mobile allies, so an
		/// anti-aircraft site's long reach can never count.
		/// </remarks>
		public static bool IsSiegeAgainst(int allyRangeUnits, int allyToDefenceUnits, in StandOffTuning t) =>
			allyRangeUnits >= AttackBaseLogic.SiegeRangeUnits
				&& allyToDefenceUnits <= allyRangeUnits + t.SiegeSlackUnits;

		/// <summary>
		/// Step out of the defence's reach, screen the siege piece from outside it, or hold; or
		/// null when this rule has no opinion and the push's own rules should answer.
		/// </summary>
		public static UnitDecision? Decide(in StandOffState s, in StandOffTuning t)
		{
			if (!s.HasDefence || !s.SiegeInPosition || !s.CanMove || !s.HasWeapon)
				return null;

			if (s.SpentTicks >= t.MaxTicks)
				return null;

			if (!Concerns(s.DefenceDistanceUnits, s.DefenceRangeUnits, s.WeaponRangeUnits, t))
				return null;

			var defence = string.IsNullOrEmpty(s.DefenceType) ? "the defence" : s.DefenceType;
			var siege = string.IsNullOrEmpty(s.SiegeType) ? "siege" : s.SiegeType;

			if (Depth(s.DefenceDistanceUnits, s.DefenceRangeUnits, t) <= 0)
			{
				var (x, y) = StepOutCell(s, t);
				return UnitDecision.MoveTo(x, y,
					$"standing off {defence}: {s.DefenceDistanceUnits}u inside its {s.DefenceRangeUnits}u reach while {siege} shells it",
					"assault.stand-off-step-out");
			}

			if (s.HasTargetInReach)
				return UnitDecision.Attack(s.TargetActorId,
					$"screening {siege} from outside {defence}'s {s.DefenceRangeUnits}u reach, engaging {(string.IsNullOrEmpty(s.TargetType) ? "what is in range" : s.TargetType)}",
					"assault.stand-off-screen");

			// Something mobile is outranging us from outside the tower's reach. Closing on it is
			// the counter-battery rule's job, and holding still under its fire is not a screen.
			if (s.ShooterAnswerable)
				return null;

			return UnitDecision.Hold(
				$"holding {s.DefenceDistanceUnits}u from {defence}, outside its {s.DefenceRangeUnits}u reach, while {siege} shells it",
				"assault.stand-off-defence");
		}

		/// <summary>
		/// The cell one margin beyond the defence's reach, straight away from it through this unit.
		/// </summary>
		/// <remarks>
		/// Away from the defence rather than towards home, because a unit that has walked past a
		/// tower would reach home through it. A unit standing on the defence's own cell has no
		/// direction to leave by, and takes the one towards home.
		/// </remarks>
		public static (int X, int Y) StepOutCell(in StandOffState s, in StandOffTuning t)
		{
			var vx = s.X - s.DefenceX;
			var vy = s.Y - s.DefenceY;
			if (vx == 0 && vy == 0)
			{
				vx = s.HomeX - s.DefenceX;
				vy = s.HomeY - s.DefenceY;
			}

			if (vx == 0 && vy == 0)
				vy = 1;

			var length = AssaultStagingLogic.IntSqrt(vx * vx + vy * vy);
			if (length <= 0)
				length = 1;

			var cells = (ReachUnits(s.DefenceRangeUnits, t) + t.MarginUnits + 1023) / 1024;
			var x = Clamp(s.DefenceX + vx * cells / length, s.MapMinX, s.MapMaxX);
			var y = Clamp(s.DefenceY + vy * cells / length, s.MapMinY, s.MapMaxY);
			return (x, y);
		}

		static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
	}
}
