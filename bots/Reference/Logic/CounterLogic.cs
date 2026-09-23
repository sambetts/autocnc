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
using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>One enemy unit type this side has recently seen, and how much of it.</summary>
	public readonly record struct Threat(string ActorType, int Value);

	/// <summary>Tunable knobs for <see cref="CounterLogic"/>.</summary>
	public readonly record struct CounterTuning(
		int MinimumThreatValue,
		int MinimumScore,
		int RetargetScore,
		int MinimumGap,
		int MaximumArmyScore,
		int MatchPercent,
		int MaxUnits,
		int AnticipatedAircraftValue,
		int MinimumAirThreatValue,
		int AntiAirScore)
	{
		public static CounterTuning Default { get; } = new(
			// Two tanks' worth. Below this a sighting is a scout or a straggler rather than an
			// army, and re-planning production around one jeep is how a bot dithers.
			MinimumThreatValue: 1500,

			// A counter rung is only inserted for a decisive answer: the duel lab's equal-cost
			// margin, weighted by what was seen, has to be at least +0.35. Below that the
			// candidate is merely not losing, and the plan's own ladder is the better judge.
			MinimumScore: 35,

			// The endless rungs are what every spare credit buys for the rest of the match, so
			// they follow a weaker preference: anything clearly better than an even trade.
			RetargetScore: 20,

			// ...and neither fires unless the answer beats what the side already has by this
			// much. The lab's fights are head-on on open ground, so small margins between two
			// good answers are noise about the real game. Without this gap the second version
			// bought buggies over riflemen at 56 against 55 per 100, twelve of them where the
			// champion built five, and lost a match the champion wins. A gap this size still
			// fires for aircraft met by riflemen, which is the case that decided ten losses.
			MinimumGap: 30,

			// ...and only while the army standing is losing to what it faces. The lab's margins
			// are for massed, equal-cost fights, and a counter bought one at a time is not
			// massed: the fourth version's buggies each died within seconds to rocket infantry
			// they "beat" at +62 per 100. An army already trading evenly is left to the plan.
			// One that is losing, such as riflemen facing orcas they cannot shoot, is not.
			MaximumArmyScore: -20,

			// Match most of what was seen, not all of it: the plan's own army is still there.
			MatchPercent: 80,
			MaxUnits: 6,

			// A helipad seen is aircraft coming. The champion lost all ten of its last sixteen
			// fights in which the enemy fielded orcas or helicopters, and twice their helipad had
			// been seen twelve to eighteen minutes before the aircraft arrived. Two aircraft is
			// what one pad turns out in about the time an answer takes to build.
			AnticipatedAircraftValue: 2400,

			// One aircraft is enough to answer. Aircraft are the one threat the general gate
			// above never fires for, because an army of riflemen still trades well against the
			// ground half of the same mix. The anti-air rule therefore scores the aircraft on
			// their own.
			MinimumAirThreatValue: 1200,

			// An anti-air answer has to be a real one: e3, apc, mlrs or a mammoth all clear
			// +50 against orcas and helicopters in the lab. Riflemen are -58 against orcas,
			// because they cannot shoot upwards at all.
			AntiAirScore: 50);
	}

	/// <summary>A unit type chosen to answer a threat, and how well it answers it.</summary>
	public readonly record struct CounterPick(string ActorType, string Queue, int Score)
	{
		public static CounterPick None { get; } = new(null, null, int.MinValue);

		public bool IsValid => ActorType != null;
	}

	/// <summary>What <see cref="CounterLogic.Apply"/> did to a plan, so the decision can say so.</summary>
	public readonly record struct CounterPlan(
		IReadOnlyList<ProductionStep> Plan,
		CounterPick Inserted,
		int InsertedTarget,
		CounterPick InfantryRung,
		CounterPick VehicleRung,
		int ThreatValue,
		string Summary,
		bool AntiAir = false)
	{
		public bool Changed => Inserted.IsValid || InfantryRung.IsValid || VehicleRung.IsValid;
	}

	/// <summary>
	/// Chooses what to build from what the enemy is actually fielding, using the duel lab's
	/// measured matchups rather than a rule of thumb.
	/// </summary>
	/// <remarks>
	/// Two blunt rules chose this bot's army before: an infantry body picked by whether the enemy
	/// looked more infantry or more "armour" (aircraft and harvesters counted as armour), and a
	/// fixed ladder for everything else. In 16 fights at d1a695b, 77% of the value lost went to
	/// unit types first seen less than 30 seconds before they started shooting, and the ladder
	/// never changed its mind about any of them.
	/// <para>
	/// <see cref="MatchupTable"/> answers the question those rules approximated: for each unit
	/// this side can build, how does it trade against each unit the enemy has been seen with,
	/// credit for credit. The score is that margin weighted by the value of each threat, so a
	/// counter to the bulk of their army beats a counter to one stray jeep.
	/// </para>
	/// <para>ZERO OpenRA dependencies by design, so the choice can be read on its own.</para>
	/// </remarks>
	public static class CounterLogic
	{
		/// <summary>
		/// How well one candidate answers a threat mix: the value-weighted equal-cost margin,
		/// -100 to +100. Threats the lab never measured are left out rather than guessed.
		/// </summary>
		public static int Score(string candidate, IReadOnlyList<Threat> threats)
		{
			if (!MatchupTable.Knows(candidate) || threats == null)
				return int.MinValue;

			long weighted = 0;
			long total = 0;
			for (var i = 0; i < threats.Count; i++)
			{
				var t = threats[i];
				if (t.Value <= 0 || !MatchupTable.Knows(t.ActorType))
					continue;

				weighted += (long)t.Value * MatchupTable.Margin(candidate, t.ActorType);
				total += t.Value;
			}

			return total > 0 ? (int)(weighted / total) : int.MinValue;
		}

		/// <summary>The best-scoring candidate, or <see cref="CounterPick.None"/>. Ties keep the earlier one.</summary>
		public static CounterPick Best(IEnumerable<string> candidates, string queue, IReadOnlyList<Threat> threats)
		{
			var best = CounterPick.None;
			if (candidates == null)
				return best;

			foreach (var candidate in candidates)
			{
				var score = Score(candidate, threats);
				if (score > best.Score)
					best = new CounterPick(candidate, queue, score);
			}

			return best;
		}

		public static int TotalValue(IReadOnlyList<Threat> threats)
		{
			var total = 0;
			if (threats != null)
				for (var i = 0; i < threats.Count; i++)
					if (MatchupTable.Knows(threats[i].ActorType))
						total += Math.Max(0, threats[i].Value);

			return total;
		}

		/// <summary>
		/// The plan with a counter to what was seen: a bounded rung for the best decisive answer,
		/// placed directly after the last harvester rung, and each queue's first endless rung
		/// that is not a protected role retargeted to that queue's best answer.
		/// </summary>
		/// <remarks>
		/// After the last harvester rung, never above it. Income still comes first, and the
		/// failures that shaped this bot's plans were all a combat rung starving the economy. The
		/// first version placed the rung after the first harvester rung instead. It lost two of
		/// the champion's wins, because the counter then bought ahead of harvester saturation and
		/// the siege core.
		/// <para>
		/// Protected rungs, the siege vehicles, are never retargeted. As Nod, the champion won by
		/// keeping about twenty-five arty behind a hundred riflemen. The duel lab scores a flame
		/// tank higher against that infantry in a head-on fight on open ground, but a counter
		/// chosen one unit type at a time cannot see what the composition around it is doing.
		/// </para>
		/// <para>
		/// The inserted rung is cumulative like every other, so it only buys the difference
		/// between what is standing and what the threat calls for. It disappears the moment the
		/// threat does, which includes every evaluation before first contact, when the plan comes
		/// back by reference.
		/// </para>
		/// </remarks>
		public static CounterPlan Apply(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyList<Threat> threats,
			CounterPick insert,
			CounterPick antiAir,
			CounterPick infantry,
			CounterPick vehicle,
			IReadOnlyDictionary<string, int> owned,
			Func<string, int> cost,
			string harvester,
			IReadOnlyList<string> protectedTypes,
			in CounterTuning t)
		{
			var total = TotalValue(threats);
			var none = new CounterPlan(plan, CounterPick.None, 0, CounterPick.None, CounterPick.None, total, null);
			if (plan == null || plan.Count == 0)
				return none;

			// Aircraft first, and on their own terms. See CounterTuning.MinimumAirThreatValue.
			var aircraft = Aircraft(threats);
			var airValue = TotalValue(aircraft);
			if (airValue >= t.MinimumAirThreatValue && antiAir.IsValid && antiAir.Score >= t.AntiAirScore)
			{
				var have = AnswerValue(owned, aircraft, cost, t.AntiAirScore);
				var want = (long)airValue * t.MatchPercent / 100;
				if (have < want)
				{
					var price = Math.Max(1, cost(antiAir.ActorType));
					var standing = owned != null && owned.TryGetValue(antiAir.ActorType, out var n) ? n : 0;
					var extra = (int)Math.Clamp((want - have + price - 1) / price, 1, t.MaxUnits);
					var air = new List<ProductionStep>(plan);
					var at = LastRungNaming(air, harvester) + 1;
					air.Insert(at, new ProductionStep(antiAir.Queue, [antiAir.ActorType], standing + extra));
					return new CounterPlan(air, antiAir, standing + extra, CounterPick.None, CounterPick.None,
						total, Describe(aircraft), true);
				}
			}

			if (total < t.MinimumThreatValue)
				return none;

			// How well what is already standing answers the threat, so a counter is only bought
			// when the army is losing and the answer is clearly better than more of the same.
			var armyScore = ArmyScore(owned, threats, cost);
			if (armyScore > t.MaximumArmyScore)
				return none;

			var best = insert;
			List<ProductionStep> rewritten = null;
			var inserted = CounterPick.None;
			var insertedTarget = 0;

			if (best.IsValid && best.Score >= t.MinimumScore && best.Score - armyScore >= t.MinimumGap)
			{
				var price = Math.Max(1, cost(best.ActorType));
				var wanted = (long)total * t.MatchPercent / 100;
				var count = (int)Math.Clamp((wanted + price - 1) / price, 1, t.MaxUnits);
				var standing = owned != null && owned.TryGetValue(best.ActorType, out var n) ? n : 0;
				if (standing < count)
				{
					rewritten = new List<ProductionStep>(plan);
					var at = LastRungNaming(rewritten, harvester) + 1;
					rewritten.Insert(at, new ProductionStep(best.Queue, [best.ActorType], count));
					inserted = best;
					insertedTarget = count;
				}
			}

			var infantryRung = CounterPick.None;
			var vehicleRung = CounterPick.None;
			if (infantry.IsValid && infantry.Score >= t.RetargetScore
				&& RetargetEndless(ref rewritten, plan, infantry, protectedTypes, threats, t.MinimumGap))
				infantryRung = infantry;

			if (vehicle.IsValid && vehicle.Score >= t.RetargetScore
				&& RetargetEndless(ref rewritten, plan, vehicle, protectedTypes, threats, t.MinimumGap))
				vehicleRung = vehicle;

			if (rewritten == null)
				return none;

			return new CounterPlan(rewritten, inserted, insertedTarget, infantryRung, vehicleRung, total, Describe(threats));
		}

		/// <summary>The biggest threats by value, as "orca 3600, e3 1800".</summary>
		public static string Describe(IReadOnlyList<Threat> threats)
		{
			if (threats == null || threats.Count == 0)
				return "nothing";

			var sorted = new List<Threat>(threats);
			sorted.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.ActorType, b.ActorType));
			var parts = new List<string>(3);
			for (var i = 0; i < sorted.Count && parts.Count < 3; i++)
				if (MatchupTable.Knows(sorted[i].ActorType))
					parts.Add($"{sorted[i].ActorType} {sorted[i].Value}");

			return parts.Count > 0 ? string.Join(", ", parts) : "nothing measured";
		}

		/// <summary>
		/// The value-weighted score of every measured combat unit standing, against the threat.
		/// Zero when nothing measured is standing, which makes any real answer clear the gap.
		/// </summary>
		public static int ArmyScore(IReadOnlyDictionary<string, int> owned, IReadOnlyList<Threat> threats, Func<string, int> cost)
		{
			if (owned == null)
				return 0;

			long weighted = 0;
			long total = 0;
			foreach (var kv in owned)
			{
				if (kv.Value <= 0 || !MatchupTable.Knows(kv.Key))
					continue;

				var score = Score(kv.Key, threats);
				if (score == int.MinValue)
					continue;

				var value = (long)kv.Value * Math.Max(1, cost(kv.Key));
				weighted += value * score;
				total += value;
			}

			return total > 0 ? (int)(weighted / total) : 0;
		}

		/// <summary>The aircraft in a threat mix, and nothing else.</summary>
		public static IReadOnlyList<Threat> Aircraft(IReadOnlyList<Threat> threats)
		{
			var aircraft = new List<Threat>();
			if (threats != null)
				for (var i = 0; i < threats.Count; i++)
					if (IsAircraft(threats[i].ActorType))
						aircraft.Add(threats[i]);

			return aircraft;
		}

		public static bool IsAircraft(string actorType) =>
			string.Equals(actorType, "orca", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(actorType, "heli", StringComparison.OrdinalIgnoreCase);

		/// <summary>Credits standing in units that answer this threat at least as well as <paramref name="minimumScore"/>.</summary>
		public static long AnswerValue(IReadOnlyDictionary<string, int> owned, IReadOnlyList<Threat> threats, Func<string, int> cost, int minimumScore)
		{
			long value = 0;
			if (owned == null)
				return value;

			foreach (var kv in owned)
				if (kv.Value > 0 && MatchupTable.Knows(kv.Key) && Score(kv.Key, threats) >= minimumScore)
					value += (long)kv.Value * Math.Max(1, cost(kv.Key));

			return value;
		}

		static bool RetargetEndless(
			ref List<ProductionStep> rewritten,
			IReadOnlyList<ProductionStep> original,
			in CounterPick pick,
			IReadOnlyList<string> protectedTypes,
			IReadOnlyList<Threat> threats,
			int minimumGap)
		{
			var steps = (IReadOnlyList<ProductionStep>)rewritten ?? original;
			for (var i = 0; i < steps.Count; i++)
			{
				var step = steps[i];
				if (step.DesiredCount != int.MaxValue
					|| !string.Equals(step.Queue, pick.Queue, StringComparison.OrdinalIgnoreCase)
					|| NamesAny(step.Candidates, protectedTypes))
					continue;

				if (step.Candidates != null && step.Candidates.Length == 1
					&& string.Equals(step.Candidates[0], pick.ActorType, StringComparison.OrdinalIgnoreCase))
					return false;

				// Only when the rung's own unit is clearly the wrong answer, for the reason
				// CounterTuning.MinimumGap gives.
				var current = int.MinValue;
				if (step.Candidates != null)
					for (var c = 0; c < step.Candidates.Length; c++)
						current = Math.Max(current, Score(step.Candidates[c], threats));

				if (current != int.MinValue && pick.Score - current < minimumGap)
					return false;

				rewritten ??= new List<ProductionStep>(original);
				rewritten[i] = new ProductionStep(step.Queue, [pick.ActorType], int.MaxValue);
				return true;
			}

			return false;
		}

		static bool NamesAny(string[] candidates, IReadOnlyList<string> types)
		{
			if (candidates == null || types == null)
				return false;

			for (var c = 0; c < candidates.Length; c++)
				for (var i = 0; i < types.Count; i++)
					if (string.Equals(candidates[c], types[i], StringComparison.OrdinalIgnoreCase))
						return true;

			return false;
		}

		static int LastRungNaming(IReadOnlyList<ProductionStep> plan, string actorType)
		{
			for (var i = plan.Count - 1; i >= 0; i--)
			{
				var candidates = plan[i].Candidates;
				if (candidates == null)
					continue;

				for (var c = 0; c < candidates.Length; c++)
					if (string.Equals(candidates[c], actorType, StringComparison.OrdinalIgnoreCase))
						return i;
			}

			return -1;
		}
	}
}
