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

using System.Collections.Generic;
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>
	/// An enemy building this side has seen, as a siege piece or a line unit reads it now.
	/// </summary>
	/// <remarks>
	/// Everything but <see cref="DistanceUnits"/>, <see cref="Visible"/> and
	/// <see cref="IsAttackable"/> was copied from the snapshot of the moment somebody on this side
	/// last saw it, so it is memory rather than sensing. <see cref="GroundDefence"/> is a static
	/// defence that could shoot a ground unit when it was seen: a <c>gtwr</c>, <c>gun</c>,
	/// <c>atwr</c> or <c>obli</c>, never an air-only <c>sam</c>.
	/// </remarks>
	public readonly record struct RememberedStructure(
		uint ActorId,
		string ActorType,
		ThreatKind Kind,
		int CellX,
		int CellY,
		int RangeUnits,
		bool GroundDefence,
		int DistanceUnits,
		bool Visible,
		bool IsAttackable);

	/// <summary>Tunable knobs for <see cref="SiegeTargetLogic"/>. Distances in world units (1024 == 1 cell).</summary>
	public readonly record struct SiegeTuning(
		int SeekSlackUnits,
		int MaxShellTicks,
		int MaxTicksPerEvaluation)
	{
		public static SiegeTuning Default { get; } = new(
			// How far past its own reach a siege piece will walk to shell something it remembers.
			// Five cells: a push that marches on their base passes within this of every tower
			// facing it, and walking five cells at arty's 1.758 cells a second is under three
			// seconds, so the piece never leaves the push to go hunting.
			SeekSlackUnits: 5 * 1024,

			// How long one siege piece may spend shelling one target before it gives it up for this
			// push. One arty alone needs about 67 seconds for a gtwr (the duel lab's four take
			// 16.7s), and a building the enemy is repairing, or one the piece cannot path into range
			// of, would otherwise hold it forever. Ninety seconds clears the single-piece kill with
			// margin. Only evaluations that actually shell the target are charged.
			MaxShellTicks: 90 * 25,

			// The most one evaluation charges to that clock: one game second. A piece that turned
			// to answer infantry for a minute has not been shelling the tower in between.
			MaxTicksPerEvaluation: 25);
	}

	/// <summary>
	/// What a siege piece shells: the buildings this side remembers, towers first.
	/// </summary>
	/// <remarks>
	/// <b>A siege piece cannot see as far as it shoots.</b> <c>arty</c> sees 5 cells and reaches 11;
	/// <c>msam</c> sees 6 and reaches 11. Sensing only returns what is visible now, so until this
	/// rule a siege piece could only pick a building somebody else was standing next to, and the
	/// somebody was a rifleman inside the tower's reach. On 16:9 (Nod against GDI, lost at 943s)
	/// the first push lost 25 <c>e1</c> to one <c>gtwr</c> in 26 seconds (398-423s) and the second
	/// lost 13 more to one <c>atwr</c> (593-661s). The <c>arty</c> killed that <c>atwr</c> only at
	/// 667s, and two of them had already died walking into its reach and a <c>gun</c>'s (526s,
	/// 561s). Both towers had been seen long before: the <c>gtwr</c> at 258s by the scout it
	/// killed, the <c>atwr</c> at 566s.
	/// <para>
	/// Both siege pieces carry <c>TargetFrozenActors</c>, so the engine lets them fire at the image
	/// of a building left under the fog once it has been seen — exactly what a human does with
	/// artillery. So a siege piece within its reach plus <see cref="SiegeTuning.SeekSlackUnits"/>
	/// of a building this side remembers shells it from where it stands, without anybody walking
	/// in to spot it. Ground defences come first, nearest first, because they are what kills the
	/// line; everything else is ranked as <see cref="AttackBaseLogic.SelectObjective(IReadOnlyList{ThreatSnapshot}, uint)"/>
	/// ranks it, less the damage weight, which cannot be read through the fog.
	/// </para>
	/// ZERO OpenRA dependencies by design and integer-only, so it is lockstep-safe.
	/// </remarks>
	public static class SiegeTargetLogic
	{
		/// <summary>What keeping the current target is worth, in cells of distance.</summary>
		/// <remarks>
		/// Every candidate is within reach plus the slack, so no two differ by more than about 16
		/// cells: this keeps a piece on the building it started until that building is gone or
		/// given up, which is what concentrates the fire of pieces that cannot see what they hit.
		/// </remarks>
		public const int StickinessCells = 32;

		/// <summary>Whether a unit with this reach is a siege piece.</summary>
		public static bool IsSiege(int weaponRangeUnits) =>
			weaponRangeUnits >= AttackBaseLogic.SiegeRangeUnits;

		/// <summary>
		/// How many ticks of shelling this evaluation charges to the piece's current target: the
		/// time since it last shelled it, never more than
		/// <see cref="SiegeTuning.MaxTicksPerEvaluation"/>, and nothing for the first.
		/// </summary>
		public static int TicksToCharge(int nowTick, int lastShellTick, in SiegeTuning t)
		{
			if (lastShellTick == int.MinValue || nowTick <= lastShellTick)
				return 0;

			var elapsed = nowTick - lastShellTick;
			return elapsed > t.MaxTicksPerEvaluation ? t.MaxTicksPerEvaluation : elapsed;
		}

		/// <summary>
		/// The index of the building a siege piece with <paramref name="weaponRangeUnits"/> should
		/// shell, or -1 when nothing it remembers is close enough.
		/// </summary>
		/// <param name="candidates">Remembered buildings, with their distance from this piece.</param>
		/// <param name="weaponRangeUnits">This piece's reach.</param>
		/// <param name="currentTargetId">What it shelled last evaluation, or 0.</param>
		/// <param name="t">Tuning.</param>
		public static int SelectTarget(
			IReadOnlyList<RememberedStructure> candidates,
			int weaponRangeUnits,
			uint currentTargetId,
			in SiegeTuning t)
		{
			if (candidates == null || candidates.Count == 0 || !IsSiege(weaponRangeUnits))
				return -1;

			var limit = weaponRangeUnits + t.SeekSlackUnits;
			var best = -1;
			var bestScore = long.MinValue;

			for (var i = 0; i < candidates.Count; i++)
			{
				var c = candidates[i];
				if (!c.IsAttackable || c.DistanceUnits > limit)
					continue;

				var score = Score(c);
				if (score == long.MinValue)
					continue;

				if (c.ActorId == currentTargetId)
					score += StickinessCells;

				if (score > bestScore
					|| (score == bestScore && best >= 0 && c.ActorId < candidates[best].ActorId))
				{
					bestScore = score;
					best = i;
				}
			}

			return best;
		}

		/// <summary>The class of a remembered building, then nearness in whole cells.</summary>
		static long Score(in RememberedStructure c)
		{
			long score;
			if (c.GroundDefence)
				score = 1_000_000;
			else
			{
				switch (c.Kind)
				{
					case ThreatKind.Structure:
						score = 3_000;
						break;
					case ThreatKind.Economy:
						score = 2_000;
						break;
					case ThreatKind.Defence:
						score = 1_000;
						break;
					default:
						return long.MinValue;
				}
			}

			var cells = c.DistanceUnits <= 0 ? 0 : c.DistanceUnits / 1024;
			return score + (cells >= 64 ? 0 : 64 - cells);
		}
	}
}
