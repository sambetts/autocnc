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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="SightingMemoryLogic"/>. Distances in ticks.</summary>
	public readonly record struct SightingMemoryTuning(int CorroborationTicks)
	{
		public static SightingMemoryTuning Default { get; } = new(
			// How recently somebody on this side must have seen an enemy structure for the
			// remembered location to count as still true. 2,250 ticks is 90 game seconds at the
			// ruleset's 40ms nominal tick and 25 ticks to the game second.
			//
			// This was 375 ticks — 15 game seconds — and that was measured against the wrong
			// thing. It was set from how often a unit *in contact* re-records, which is about
			// every 1.4 game seconds, and a window ten evaluations wide looked generous. But the
			// side is not always in contact, and the assault rule deliberately arranges for it
			// not to be: AssaultStagingLogic parks the army StandoffUnits (14 cells) short of
			// their base and up to one leg (16 cells) behind that, in fog, for a whole gather.
			// Nobody can see an enemy structure from there, so nobody corroborates, so the window
			// expires during the bot's own approach — and then one fast unit reaching the
			// remembered cell deletes the side's only target while the base is standing.
			//
			// On badland-ridges that fired twenty-four times. Every one of the 24 "nothing left
			// to attack, going looking" switches was followed 30 to 50 seconds later by "scout
			// found their base" — the same base, intact, which finished the match with 43
			// buildings having never dropped below 19.
			//
			// Three bounds pin the new number:
			//
			//  * Above the longest blind spell a committed push legitimately has. One gather is
			//    about 35 game seconds (9 evaluations of arrival spread plus 16 of patience, at
			//    1.4s each) and the final 14-cell run in from the standoff is 15s for e3 at 0.952
			//    cells a second, so 50s is the floor. 90s clears it with margin.
			//  * Far below ReferenceBotTuning.LostContactSeconds (300s), which is the real
			//    backstop for a base that genuinely no longer exists. At 30% of it a side that
			//    has actually levelled their base still forgets the spot inside a minute and a
			//    half and goes looking.
			//  * Above zero. Zero is forgetting on the word of one blind unit, which is what the
			//    corroboration guard exists to stop.
			CorroborationTicks: 2250);
	}

	/// <summary>
	/// What this side remembers about where the enemy lives, with no engine types in it.
	/// </summary>
	/// <remarks>
	/// The cell itself is deliberately absent: a map coordinate is an engine type and this half
	/// of the rule does not need one. All that is judged here is <em>whether the memory is still
	/// worth believing</em>.
	/// </remarks>
	public readonly record struct SightingMemory(bool Known, int LastSeenTick)
	{
		/// <summary>A side that has never seen an enemy structure.</summary>
		public static SightingMemory None { get; } = new(false, 0);
	}

	/// <summary>
	/// When a side may throw away the only enemy location it has.
	/// </summary>
	/// <remarks>
	/// The remembered sighting belongs to the whole side, and until now any single unit could
	/// delete it: <c>AttackBaseMode</c> forgot the spot whenever one unit stood within five cells
	/// of it and could not see a structure, and the doctrine rule then read "nothing left to
	/// attack" and pulled the entire army out of the assault.
	/// <para>
	/// On badland-ridges that lost the match outright. The push reached their base at about 500s
	/// and was shooting it — 20 <c>objective in range</c> and 74 <c>clearing ... en route</c>
	/// decisions between 480s and 520s, five enemy buildings destroyed. At 517s two <c>e3</c>
	/// were 5.4 and 5.8 cells from the remembered cell with nothing in sight; one evaluation
	/// later they crossed the five-cell arrival radius, the sighting was discarded, and at 520s
	/// the doctrine flipped to <c>Scout</c>. Within thirty seconds all 46 remaining assault
	/// decisions had become <c>DefensiveMode</c> — 15 of them shooting enemy harvesters at 5.4 to
	/// 10.5 cells, 8 of them walking home. The identical two decisions, at 5.0 and 5.3 cells,
	/// cancelled the third assault at 885s after 16 <c>objective in range</c> and 71
	/// <c>clearing</c> decisions; 666 <c>DefensiveMode</c> decisions followed in thirty seconds
	/// and 20 <c>e3</c> died at (40-41, 58-61) in the eight seconds after that.
	/// </para>
	/// <para>
	/// It also poisoned the assault that came next. The host's <c>BattleState.EnemyBaseFound</c>
	/// never goes back to false, so at 695s the doctrine rule entered <c>Attack</c> on a flag
	/// that was still true while this side's own memory was empty — an attack with nowhere to
	/// march, which cancelled itself thirty seconds later having produced zero
	/// <c>objective in range</c> and zero <c>clearing</c> decisions.
	/// </para>
	/// <para>
	/// So forgetting is corroborated rather than unilateral. One unit's blindness is evidence
	/// about that unit; a whole side seeing nothing for fifteen seconds is evidence about the
	/// base. <c>Record</c> already runs on every evaluation in which any unit can see an enemy
	/// structure, so the corroboration costs nothing to collect.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design — see <see cref="DefensiveLogic"/>.</para>
	/// </remarks>
	public static class SightingMemoryLogic
	{
		/// <summary>The memory after a unit has seen an enemy structure on this tick.</summary>
		public static SightingMemory Seen(int tick) => new(true, tick);

		/// <summary>
		/// Whether somebody on this side has vouched for the sighting recently enough for it to
		/// still be worth marching on.
		/// </summary>
		/// <remarks>
		/// A negative difference — which only a tick counter running backwards could produce —
		/// reads as corroborated, so the safe answer is the one a nonsense clock gives.
		/// </remarks>
		public static bool Corroborated(in SightingMemory memory, int nowTick, in SightingMemoryTuning t) =>
			memory.Known && nowTick - memory.LastSeenTick < t.CorroborationTicks;

		/// <summary>
		/// Whether a unit standing on the remembered spot and seeing nothing may discard it for
		/// the whole side.
		/// </summary>
		public static bool ShouldForget(in SightingMemory memory, int nowTick, in SightingMemoryTuning t) =>
			memory.Known && !Corroborated(memory, nowTick, t);
	}
}
