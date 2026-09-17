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
using System.Linq;

namespace AutoCnC.Evidence
{
	/// <summary>One named, separately reported part of the fitness score.</summary>
	public sealed class FitnessComponent
	{
		public string Name { get; set; }

		/// <summary>The measured quantity, in its own units.</summary>
		public double Value { get; set; }

		public string Unit { get; set; }

		/// <summary>The value that scores 1.0, so a reader can see what "good" was taken to be.</summary>
		public double Reference { get; set; }

		/// <summary>Value against reference, clamped to 0..1.</summary>
		public double Score { get; set; }

		public double Weight { get; set; }
		public double Contribution { get; set; }

		/// <summary>
		/// False when the evidence could not answer this component at all.
		/// </summary>
		/// <remarks>
		/// An unknown is not a zero. A run recorded before telemetry carried the earned/spent
		/// flows has no economic rate, and scoring that as 0.0 would both depress its total and
		/// show up in the trend as an economic collapse that never happened. An unknown component
		/// is reported, excluded from the total, and excluded from its weight.
		/// </remarks>
		public bool Known { get; set; } = true;

		public string Explanation { get; set; }
	}

	/// <summary>
	/// A graded score, so a round that lost is still distinguishable from a round that lost badly.
	/// </summary>
	/// <remarks>
	/// <para>
	/// One bit of reward per match is not enough signal to steer on. A change that doubles income
	/// and still loses is progress; a change that wins by luck on a favourable map is not. Scoring
	/// named components separately means the loop can see which part moved, and a component that
	/// improves while the match is still lost shows up as partial credit rather than as noise.
	/// </para>
	/// <para>
	/// The reference values are deliberately fixed constants rather than something derived from
	/// the run being scored. A score normalised against its own match would rate every match
	/// average and could never show a trend, which is the one thing this is for. They are round
	/// numbers chosen to sit near a competent match on the standard benchmark, and changing one
	/// invalidates comparison with existing history — so if you change one, bump
	/// <see cref="ScaleVersion"/> and the trend will say the scale moved rather than pretending
	/// the bot did.
	/// </para>
	/// </remarks>
	public sealed class FitnessScore
	{
		/// <summary>Bumped whenever a reference value or weight changes.</summary>
		public const int CurrentScaleVersion = 1;

		public int ScaleVersion { get; set; } = CurrentScaleVersion;

		/// <summary>
		/// Weighted sum of every known component, renormalised over the weight that was knowable.
		/// </summary>
		/// <remarks>
		/// Renormalised rather than simply summed so a fight missing one input is still scored on
		/// the same 0..1 scale as one that has everything. <see cref="KnownWeight"/> says how much
		/// of the scale was actually measured, so a total resting on half the components is
		/// visibly weaker evidence rather than silently equivalent.
		/// </remarks>
		public double Total { get; set; }

		/// <summary>Fraction of the total weight that could be measured, 0..1.</summary>
		public double KnownWeight { get; set; }

		/// <summary>The win/loss bit, kept separate so it can never hide the components.</summary>
		public string Outcome { get; set; }

		public bool Won { get; set; }

		public List<FitnessComponent> Components { get; set; } = [];

		// Reference values: what a component has to reach to score 1.0.
		const double EconomicRateReference = 50d;      // credits earned per game second
		const double ExchangeRatioReference = 2d;      // credits destroyed per credit lost
		const double ArmyIntegralReference = 6_000d;   // mean army value held across the match
		const double BuildingsKilledReference = 8d;    // enemy structures destroyed
		const double ExplorationReference = 300d;      // distinct cells the log ever reported
		const double SurvivalReference = 1_800d;       // game seconds survived

		public static FitnessScore From(SummaryHeadline headline, SummaryMap map, string outcome,
			SummaryProvenance provenance = null)
		{
			var won = string.Equals(outcome, "Won", StringComparison.OrdinalIgnoreCase);
			var score = new FitnessScore { Outcome = outcome, Won = won };

			// The economy components rest entirely on telemetry's earned/spent flows. A run
			// recorded before those columns existed cannot answer them, and scoring the silence as
			// zero would read as an economic collapse in the trend.
			var economyKnown = provenance?.HasEconomyFlows ?? true;
			var mapKnown = provenance?.HasMapFacts ?? true;

			score.Components.Add(Component("economicRate", headline.CreditsEarnedPerSecond,
				"credits/second", EconomicRateReference, 0.20,
				"credits harvested per game second; cash is a stock and cannot express this",
				economyKnown));

			score.Components.Add(Component("valueExchange", headline.ValueExchangeRatio,
				"credits killed per credit lost", ExchangeRatioReference, 0.25,
				"value destroyed over value lost; counts weight a rifleman and a tank alike"));

			score.Components.Add(Component("armyValueIntegral", headline.MeanArmyValue,
				"mean credits of army held", ArmyIntegralReference, 0.20,
				"army value integrated over the match, divided by its length"));

			score.Components.Add(Component("buildingsDestroyed", headline.BuildingsKilled,
				"enemy buildings", BuildingsKilledReference, 0.15,
				"structures destroyed; zero means the army never finished one"));

			score.Components.Add(Component("exploration", map?.ExploredCells ?? headline.CellsExplored,
				"cells ever observed", ExplorationReference, 0.10,
				"distinct cells this side ever had eyes on"));

			score.Components.Add(Component("survival", headline.DurationSeconds,
				"game seconds", SurvivalReference, 0.10,
				"how long this side lasted; weighted lightly, since hiding is not the goal"));

			var known = score.Components.Where(c => c.Known).ToList();
			var knownWeight = known.Sum(c => c.Weight);

			score.KnownWeight = Math.Round(knownWeight, 4);
			score.Total = knownWeight > 0
				? Math.Round(known.Sum(c => c.Contribution) / knownWeight, 4)
				: 0d;

			return score;
		}

		static FitnessComponent Component(string name, double value, string unit, double reference,
			double weight, string explanation, bool known = true)
		{
			var normalised = !known || reference <= 0 ? 0d : Math.Clamp(value / reference, 0d, 1d);
			return new FitnessComponent
			{
				Name = name,
				Value = known ? Math.Round(value, 3) : 0,
				Unit = unit,
				Reference = reference,
				Score = Math.Round(normalised, 4),
				Weight = weight,
				Contribution = Math.Round(normalised * weight, 4),
				Known = known,
				Explanation = known ? explanation : explanation + " (not recorded by this run)"
			};
		}
	}
}
