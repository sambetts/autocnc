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

using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class FitnessTests
	{
		/// <summary>
		/// Under scale 1, survival was game seconds against 1,800, so of two wins the slower
		/// scored higher. Once the champion won every benchmark game, a slower win was the only
		/// way a candidate could tie-break its way to promotion.
		/// </summary>
		[Test]
		public void AQuickerWinNeverScoresBelowASlowerOne()
		{
			var quick = FitnessScore.From(Headline(600), null, "Won");
			var slow = FitnessScore.From(Headline(1500), null, "Won");

			Assert.That(quick.Total, Is.EqualTo(slow.Total));
			Assert.That(quick.Components.Single(c => c.Name == "survival").Score, Is.EqualTo(1));
			Assert.That(quick.Components.Single(c => c.Name == "survival").Value, Is.EqualTo(600),
				"the value still reports how long the match took");
		}

		[Test]
		public void ALossIsStillScoredByHowLongItLasted()
		{
			var early = FitnessScore.From(Headline(600), null, "Lost");
			var late = FitnessScore.From(Headline(1200), null, "Lost");

			Assert.That(late.Total, Is.GreaterThan(early.Total));
			Assert.That(early.Components.Single(c => c.Name == "survival").Score,
				Is.EqualTo(600 / 1800d).Within(0.0001));
		}

		[Test]
		public void TheScaleVersionSaysSurvivalChanged()
		{
			Assert.That(FitnessScore.CurrentScaleVersion, Is.EqualTo(2));
			Assert.That(FitnessScore.From(Headline(600), null, "Won").ScaleVersion, Is.EqualTo(2));
		}

		static SummaryHeadline Headline(int seconds) => new()
		{
			DurationSeconds = seconds,
			CreditsEarnedPerSecond = 40,
			ValueExchangeRatio = 1.5,
			MeanArmyValue = 4000,
			BuildingsKilled = 5,
			CellsExplored = 250
		};
	}
}
