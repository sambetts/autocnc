#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class BattleExecutionModesTests
	{
		[Test]
		public void NewLaunchersDefaultToHeadlessTraining()
		{
			Assert.That(new LauncherSettings().ExecutionMode, Is.EqualTo(BattleExecutionModes.Headless));
			Assert.That(BattleExecutionModes.Normalize("rendered"), Is.EqualTo(BattleExecutionModes.Rendered));
			Assert.That(BattleExecutionModes.Normalize("unknown"), Is.EqualTo(BattleExecutionModes.Headless));
			Assert.That(BattleExecutionModes.EffectiveGameSpeed("Headless", "default"),
				Is.EqualTo("maximum"));
			Assert.That(BattleExecutionModes.EffectiveGameSpeed("Rendered", "fastest"),
				Is.EqualTo("fastest"));
		}
	}
}
