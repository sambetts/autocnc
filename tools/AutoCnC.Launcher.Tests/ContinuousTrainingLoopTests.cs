#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class ContinuousTrainingLoopTests
	{
		[Test]
		public void EnabledLoopAlternatesFightAndImprovementUntilStopped()
		{
			var loop = new ContinuousTrainingLoop();

			loop.Begin(enabled: true);
			Assert.That(loop.Stage, Is.EqualTo(ContinuousTrainingStage.Fighting));
			Assert.That(loop.BattleCompleted(), Is.EqualTo(ContinuousTrainingAction.Improve));
			Assert.That(loop.Stage, Is.EqualTo(ContinuousTrainingStage.Improving));
			Assert.That(loop.ImprovementCompleted(), Is.EqualTo(ContinuousTrainingAction.Fight));
			Assert.That(loop.Stage, Is.EqualTo(ContinuousTrainingStage.Fighting));

			loop.Stop();
			Assert.That(loop.IsRunning, Is.False);
			Assert.That(loop.BattleCompleted(), Is.EqualTo(ContinuousTrainingAction.None));
		}

		[Test]
		public void DisabledLoopDoesNotScheduleAnotherStep()
		{
			var loop = new ContinuousTrainingLoop();

			loop.Begin(enabled: false);

			Assert.That(loop.IsRunning, Is.False);
			Assert.That(loop.BattleCompleted(), Is.EqualTo(ContinuousTrainingAction.None));
			Assert.That(loop.ImprovementCompleted(), Is.EqualTo(ContinuousTrainingAction.None));
		}
	}
}
