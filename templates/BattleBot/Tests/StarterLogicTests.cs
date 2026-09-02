using __AUTOCNC_BOT_ROOT_NAMESPACE__.Logic;
using AutoCnC.Core;
using NUnit.Framework;

namespace __AUTOCNC_BOT_ROOT_NAMESPACE__.Tests
{
	[TestFixture]
	public class StarterLogicTests
	{
		[Test]
		public void ArmedUnitAttacksNearestTarget()
		{
			var decision = StarterLogic.Decide(new StarterState(
				HasWeapon: true,
				IsIdle: true,
				Threats:
				[
					Threat(actorId: 10, distance: 4_000),
					Threat(actorId: 20, distance: 2_000),
				]));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Attack));
			Assert.That(decision.TargetActorId, Is.EqualTo(20u));
		}

		[Test]
		public void EqualDistanceUsesActorIdForStableTieBreak()
		{
			var target = StarterLogic.SelectNearestTarget(
			[
				Threat(actorId: 8, distance: 2_000),
				Threat(actorId: 3, distance: 2_000),
			]);

			Assert.That(target.HasValue, Is.True);
			Assert.That(target.Value.ActorId, Is.EqualTo(3u));
		}

		[Test]
		public void UnattackableContactIsIgnored()
		{
			var decision = StarterLogic.Decide(new StarterState(
				HasWeapon: true,
				IsIdle: true,
				Threats: [Threat(actorId: 10, distance: 1_000, isAttackable: false)]));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Hold));
		}

		[Test]
		public void UnarmedUnitIsLeftAlone()
		{
			var decision = StarterLogic.Decide(new StarterState(
				HasWeapon: false,
				IsIdle: true,
				Threats: [Threat(actorId: 10, distance: 1_000)]));

			Assert.That(decision.Action, Is.EqualTo(UnitAction.Continue));
		}

		[TestCase(true, UnitAction.Hold)]
		[TestCase(false, UnitAction.Continue)]
		public void UnitWithoutTargetOnlyHoldsWhenIdle(bool isIdle, UnitAction expected)
		{
			var decision = StarterLogic.Decide(new StarterState(
				HasWeapon: true,
				IsIdle: isIdle,
				Threats: []));

			Assert.That(decision.Action, Is.EqualTo(expected));
		}

		static ThreatSnapshot Threat(uint actorId, int distance, bool isAttackable = true) =>
			new(
				ActorId: actorId,
				DistanceUnits: distance,
				HealthPercent: 100,
				Kind: ThreatKind.Infantry,
				IsAttackable: isAttackable,
				CanHitUs: true);
	}
}
