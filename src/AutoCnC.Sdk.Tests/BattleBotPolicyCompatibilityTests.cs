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
using NUnit.Framework;

namespace AutoCnC.Sdk.Tests
{
	[TestFixture]
	public sealed class BattleBotPolicyCompatibilityTests
	{
		[Test]
		public void LegacyDirectImplementationsDefaultToNoProductionReservation()
		{
			IBattleBot bot = new LegacyBot();
			IModeHost host = new LegacyModeHost();

			Assert.Multiple(() =>
			{
				Assert.That(bot.ReserveProductionBudget(BattleState.Empty),
					Is.EqualTo(ProductionBudget.None));
				Assert.That(host.CurrentProductionBudget, Is.EqualTo(ProductionBudget.None));
			});
		}

		[Test]
		public void BattleBotBaseCanOverrideTheOptionalProductionPolicy()
		{
			var bot = new BudgetBot();
			var budget = bot.ReserveProductionBudget(BattleState.Empty);

			Assert.Multiple(() =>
			{
				Assert.That(budget.ReservedCash, Is.EqualTo(1500));
				Assert.That(budget.Queue, Is.EqualTo("Vehicle"));
				Assert.That(budget.ReasonId, Is.EqualTo("production.reserve.medium-tank"));
			});
		}

		[Test]
		public void ModeContextExposesReadOnlyProductionBudgetState()
		{
			var property = typeof(ModeContext).GetProperty(nameof(ModeContext.CurrentProductionBudget));

			Assert.Multiple(() =>
			{
				Assert.That(property, Is.Not.Null);
				Assert.That(property.PropertyType, Is.EqualTo(typeof(ProductionBudget)));
				Assert.That(property.CanRead, Is.True);
				Assert.That(property.CanWrite, Is.False);
			});
		}

		sealed class LegacyBot : IBattleBot
		{
			public string Name => "Legacy";
			public string Description => "Implements the pre-budget source surface.";

			public void Configure(IBattleBotBuilder builder) { }

			public DoctrineDecision Reassess(in BattleState state) => DoctrineDecision.Continue;
		}

		sealed class BudgetBot : BattleBot
		{
			public override string Name => "Budget";
			public override string Description => "Reserves vehicle production cash.";

			public override void Configure(IBattleBotBuilder builder) { }

			public override ProductionBudget ReserveProductionBudget(in BattleState state) =>
				ProductionBudget.Reserve(
					1500,
					"Vehicle",
					"save for a medium tank",
					"production.reserve.medium-tank");
		}

		sealed class LegacyModeHost : IModeHost
		{
			public IReadOnlyList<BuildStep> BuildPlan => Array.Empty<BuildStep>();
			public IReadOnlyList<ProductionStep> ProductionPlan => Array.Empty<ProductionStep>();
			public string ActiveDoctrine => null;
			public IReadOnlyList<string> DoctrineNames => Array.Empty<string>();

			public void RequestDoctrine(string doctrine, string reason) { }
		}
	}
}
