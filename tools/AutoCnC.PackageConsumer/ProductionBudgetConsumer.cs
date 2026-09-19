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
using AutoCnC.Sdk;

namespace AutoCnC.PackageConsumer
{
	public sealed class ProductionBudgetConsumer : BattleBot
	{
		public override string Name => "Package API Probe";

		public override string Description => "Compiles the packaged production-budget API.";

		public override void Configure(IBattleBotBuilder builder) { }

		public override ProductionBudget ReserveProductionBudget(in BattleState state) =>
			state.Cash < 2000
				? ProductionBudget.Reserve(
					1000,
					"Building",
					"compile the packaged reservation API",
					"production.reserve.package-probe")
				: ProductionBudget.None;

		public static ProductionBudget ReadCurrentBudget(ModeContext context) =>
			context.CurrentProductionBudget;
	}
}
