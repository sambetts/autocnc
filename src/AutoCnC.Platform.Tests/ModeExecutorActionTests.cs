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
using AutoCnC.Platform.Traits;
using NUnit.Framework;

namespace AutoCnC.Platform.Tests
{
	[TestFixture]
	public sealed class ModeExecutorActionTests
	{
		[TestCase(UnitAction.RepairBuilding)]
		[TestCase(UnitAction.CancelProduction)]
		[TestCase(UnitAction.ActivateSupportPower)]
		public void PlayerScopedActionsAreNotRepeatedWhenTheControllerActorIsIdle(UnitAction action)
		{
			Assert.Multiple(() =>
			{
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(action, repeat: true, actorIsIdle: true),
					Is.True);
				Assert.That(ModeExecutor.IsSingleShotAction(action), Is.True);
			});
		}

		[Test]
		public void MovementCanStillBeRetriedAfterTheActorBecomesIdle()
		{
			Assert.Multiple(() =>
			{
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.MoveTo, repeat: true, actorIsIdle: true),
					Is.False);
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.MoveTo, repeat: true, actorIsIdle: false),
					Is.True);
				Assert.That(
					ModeExecutor.ShouldSuppressRepeatedIntent(
						UnitAction.CancelProduction, repeat: false, actorIsIdle: true),
					Is.False);
			});
		}

		[Test]
		public void PlayerScopedActionsCoalesceAcrossControllers()
		{
			var repair = PlayerScopedActionKey.From(
				UnitDecision.RepairBuilding(12, "repair")).Value;
			var sameRepair = PlayerScopedActionKey.From(
				UnitDecision.RepairBuilding(12, "other reason")).Value;
			var cancelOne = PlayerScopedActionKey.From(
				UnitDecision.CancelProduction("Vehicle", "mtnk", 1, "cancel") with
				{ TargetActorId = 20 }).Value;
			var cancelTwo = PlayerScopedActionKey.From(
				UnitDecision.CancelProduction("Vehicle", "mtnk", 2, "cancel more") with
				{ TargetActorId = 20 }).Value;
			var otherQueue = PlayerScopedActionKey.From(
				UnitDecision.CancelProduction("Vehicle", "mtnk", 1, "cancel") with
				{ TargetActorId = 21 }).Value;
			var firstPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 4, 5, "fire")).Value;
			var samePowerElsewhere = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 9, 10, "fire")).Value;
			var otherPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_5", 4, 5, "fire")).Value;

			Assert.Multiple(() =>
			{
				Assert.That(repair, Is.EqualTo(sameRepair));
				Assert.That(cancelOne, Is.EqualTo(cancelTwo),
					"counts must not accumulate through multiple controllers in one tick");
				Assert.That(cancelOne, Is.Not.EqualTo(otherQueue));
				Assert.That(firstPower, Is.EqualTo(samePowerElsewhere),
					"one concrete power key can only be activated once per tick");
				Assert.That(firstPower, Is.Not.EqualTo(otherPower),
					"different concrete instances remain independently actionable");
				Assert.That(PlayerScopedActionKey.From(UnitDecision.MoveTo(1, 2, "move")), Is.Null);
			});
		}

		[Test]
		public void ExactCancellationRejectsPartialResolution()
		{
			var queued = new[] { "e1", "mtnk" };

			Assert.Multiple(() =>
			{
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "MTNK", 1),
					Is.EqualTo("mtnk"));
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", 2),
					Is.Null);
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", 0),
					Is.Null);
				Assert.That(
					SdkActionResolver.FindExactCancellationItem(queued, "mtnk", uint.MaxValue),
					Is.Null);
			});
		}
	}
}
