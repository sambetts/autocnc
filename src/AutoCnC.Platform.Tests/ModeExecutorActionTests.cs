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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoCnC.Core;
using AutoCnC.Platform.Traits;
using AutoCnC.Sdk;
using NUnit.Framework;
using OpenRA;

namespace AutoCnC.Platform.Tests
{
	[TestFixture]
	public sealed class ModeExecutorActionTests
	{
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
		public void CancellationUsesPersistentIntentInsteadOfControllerIdleState()
		{
			Assert.Multiple(() =>
			{
				Assert.That(ModeExecutor.IsSingleShotAction(UnitAction.CancelProduction), Is.False);
				Assert.That(ModeExecutor.UsesPersistentIntent(UnitAction.CancelProduction), Is.True);
				Assert.That(ModeExecutor.IsSingleShotAction(UnitAction.RepairBuilding), Is.False);
				Assert.That(ModeExecutor.UsesPersistentIntent(UnitAction.RepairBuilding), Is.True);
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
			var firstPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 4, 5, "fire")).Value;
			var samePowerElsewhere = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_3", 9, 10, "fire")).Value;
			var otherPower = PlayerScopedActionKey.From(
				UnitDecision.ActivateSupportPower("AirstrikeOrder_5", 4, 5, "fire")).Value;

			Assert.Multiple(() =>
			{
				Assert.That(firstPower, Is.EqualTo(samePowerElsewhere),
					"one concrete power key can only be activated once per tick");
				Assert.That(firstPower, Is.Not.EqualTo(otherPower),
					"different concrete instances remain independently actionable");
				Assert.That(PlayerScopedActionKey.From(UnitDecision.MoveTo(1, 2, "move")), Is.Null);
			});
		}

		[Test]
		public void CancellationIntentPersistsAcrossStaggeredEvaluationLatency()
		{
			var queueItem = new object();
			var revision = new ProductionQueueRevisionState<object>(
				[new ProductionQueueEntry("mtnk", false)],
				[queueItem]);
			var pending = new PendingPlayerActions();
			var first = Cancellation(queueActorId: 20, item: "mtnk", count: 2);
			var firstPayload = ActionOrderBuilder.EncodeCancellationPayload(
				"mtnk", revision.Revision);

			pending.BeginTick(
				intent => intent.ExpectedQueueRevision == revision.Revision,
				_ => true);
			var firstControllerReserved = pending.TryReserve(7, first, firstPayload);

			pending.BeginTick(
				intent => intent.ExpectedQueueRevision == revision.Revision,
				_ => true);
			var staggeredControllerReserved = pending.TryReserve(7, first, firstPayload);

			revision.Advance(
				[new ProductionQueueEntry("mtnk", false)],
				[queueItem]);
			var secondPayload = ActionOrderBuilder.EncodeCancellationPayload(
				"mtnk", revision.Revision);
			pending.BeginTick(
				intent => intent.ExpectedQueueRevision == revision.Revision,
				_ => true);
			var changedQueueReserved = pending.TryReserve(7, first, secondPayload);

			Assert.Multiple(() =>
			{
				Assert.That(firstControllerReserved, Is.True);
				Assert.That(staggeredControllerReserved, Is.False,
					"the in-flight intent must survive local tick boundaries");
				Assert.That(changedQueueReserved, Is.True,
					"a synchronized resolution or queue change releases the pending intent");
			});
		}

		[Test]
		public void PersistentIntentAvailabilityDoesNotReserveBeforeOrderCommit()
		{
			var pending = new PendingPlayerActions();
			var cancellation = Cancellation(queueActorId: 20, item: "mtnk", count: 2);
			var cancellationPayload = ActionOrderBuilder.EncodeCancellationPayload("mtnk", 1);
			var repair = UnitDecision.RepairBuilding(30, "repair");
			var repairPayload = ActionOrderBuilder.EncodeRepairPayload(1);

			pending.BeginTick(_ => true, _ => true);

			Assert.Multiple(() =>
			{
				Assert.That(pending.CanReserve(7, cancellation, cancellationPayload), Is.True);
				Assert.That(pending.CanReserve(7, cancellation, cancellationPayload), Is.True,
					"a capacity check must not leave a persistent cancellation reservation");
				Assert.That(pending.CanReserve(7, repair, repairPayload), Is.True);
				Assert.That(pending.CanReserve(7, repair, repairPayload), Is.True,
					"a capacity check must not leave a persistent repair reservation");
			});

			Assert.That(pending.TryReserve(7, cancellation, cancellationPayload), Is.True);
			Assert.That(pending.TryReserve(7, repair, repairPayload), Is.True);

			Assert.Multiple(() =>
			{
				Assert.That(pending.CanReserve(7, cancellation, cancellationPayload), Is.False);
				Assert.That(pending.CanReserve(7, repair, repairPayload), Is.False);
			});
		}

		[Test]
		public void CancellationIntentKeyIncludesPlayerQueueItemCountAndRevision()
		{
			var pending = new PendingPlayerActions();
			var baseline = Cancellation(queueActorId: 20, item: "mtnk", count: 2);
			var revisionOne = ActionOrderBuilder.EncodeCancellationPayload("mtnk", 1);
			var revisionTwo = ActionOrderBuilder.EncodeCancellationPayload("mtnk", 2);
			var otherItem = ActionOrderBuilder.EncodeCancellationPayload("e1", 1);

			pending.BeginTick(_ => true, _ => true);

			Assert.Multiple(() =>
			{
				Assert.That(pending.TryReserve(7, baseline, revisionOne), Is.True);
				Assert.That(pending.TryReserve(7, baseline, revisionOne), Is.False);
				Assert.That(pending.TryReserve(8, baseline, revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(21, "mtnk", 2), revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "e1", 2), otherItem), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 1), revisionOne), Is.True);
				Assert.That(pending.TryReserve(
					7, Cancellation(20, "mtnk", 2), revisionTwo), Is.True);
			});
		}

		[Test]
		public void RepairIntentClearsAfterFastCompletionAndRedamage()
		{
			var revision = new RepairStateRevision(
				SdkActionResolver.CreateRepairStateSnapshot(
					isOwned: true,
					isLive: true,
					isRepairable: true,
					hitPoints: 50,
					maxHitPoints: 100,
					repairRequested: false,
					repairActive: false));
			var pending = new PendingPlayerActions();
			var decision = UnitDecision.RepairBuilding(20, "repair");
			var firstPayload = ActionOrderBuilder.EncodeRepairPayload(revision.Revision);

			pending.BeginTick(
				_ => true,
				intent => intent.ExpectedRepairRevision == revision.Revision);
			var firstReserved = pending.TryReserve(7, decision, firstPayload);
			var duplicateReserved = pending.TryReserve(7, decision, firstPayload);

			revision.Advance(SdkActionResolver.CreateRepairStateSnapshot(
				true, true, true, 60, 100, true, false));
			revision.Observe(SdkActionResolver.CreateRepairStateSnapshot(
				true, true, true, 100, 100, false, false));
			revision.Observe(SdkActionResolver.CreateRepairStateSnapshot(
				true, true, true, 50, 100, false, false));

			var redamagedPayload = ActionOrderBuilder.EncodeRepairPayload(revision.Revision);
			pending.BeginTick(
				_ => true,
				intent => intent.ExpectedRepairRevision == revision.Revision);
			var redamagedReserved = pending.TryReserve(7, decision, redamagedPayload);

			Assert.Multiple(() =>
			{
				Assert.That(firstReserved, Is.True);
				Assert.That(duplicateReserved, Is.False);
				Assert.That(revision.Revision, Is.EqualTo(4));
				Assert.That(redamagedReserved, Is.True);
			});
		}

		[Test]
		public void SustainedDamageDoesNotInvalidateInFlightRepair()
		{
			var initiallyDamaged = SdkActionResolver.CreateRepairStateSnapshot(
				true, true, true, 80, 100, false, false);
			var damagedAgain = SdkActionResolver.CreateRepairStateSnapshot(
				true, true, true, 35, 100, false, false);
			var revision = new RepairStateRevision(initiallyDamaged);
			var pending = new PendingPlayerActions();
			var decision = UnitDecision.RepairBuilding(20, "repair");
			var payload = ActionOrderBuilder.EncodeRepairPayload(revision.Revision);

			pending.BeginTick(
				_ => true,
				intent => intent.ExpectedRepairRevision == revision.Revision);
			var firstReserved = pending.TryReserve(7, decision, payload);

			var revisionAfterDamage = revision.Observe(damagedAgain);
			pending.BeginTick(
				_ => true,
				intent => intent.ExpectedRepairRevision == revision.Revision);
			var duplicateReserved = pending.TryReserve(7, decision, payload);

			Assert.Multiple(() =>
			{
				Assert.That(initiallyDamaged, Is.EqualTo(damagedAgain));
				Assert.That(revisionAfterDamage, Is.EqualTo(1));
				Assert.That(firstReserved, Is.True);
				Assert.That(duplicateReserved, Is.False);
			});
		}

		[Test]
		public void QueueRevisionAdvancesAcrossAbaMutation()
		{
			var originalItem = new object();
			var replacementItem = new object();
			var state = new ProductionQueueRevisionState<object>(
				[new ProductionQueueEntry("mtnk", false)],
				[originalItem]);
			var initialRevision = state.Revision;

			state.Advance([new ProductionQueueEntry("mtnk", false)], [originalItem]);
			state.Observe([new ProductionQueueEntry("e1", false)], [replacementItem]);
			state.Advance([new ProductionQueueEntry("e1", false)], [replacementItem]);
			var restoredRevision = state.Observe(
				[new ProductionQueueEntry("mtnk", false)],
				[originalItem]);

			Assert.Multiple(() =>
			{
				Assert.That(initialRevision, Is.EqualTo(1));
				Assert.That(restoredRevision, Is.EqualTo(5));
			});
		}

		[Test]
		public void QueueRevisionDoesNotDependOnTheLegacyFingerprint()
		{
			var first = new[]
			{
				new ProductionQueueEntry("iws", false),
				new ProductionQueueEntry("9975g", false),
				new ProductionQueueEntry("uu", false),
				new ProductionQueueEntry("h6edr", false),
				new ProductionQueueEntry("mtnk", false)
			};
			var collision = new[]
			{
				new ProductionQueueEntry("gmvq", false),
				new ProductionQueueEntry("mtnk", false)
			};
			var state = new ProductionQueueRevisionState<object>(
				first, [new object(), new object(), new object(), new object(), new object()]);

			var revision = state.Observe(collision, [new object(), new object()]);

			Assert.Multiple(() =>
			{
				Assert.That(LegacyQueueFingerprint(first), Is.EqualTo(0x8D6B64F6u));
				Assert.That(LegacyQueueFingerprint(collision), Is.EqualTo(0x8D6B64F6u));
				Assert.That(revision, Is.EqualTo(2));
			});
		}

		[Test]
		public void QueueMutationObserverRejectsMalformedActorsBeforePlayerAccess()
		{
			var ownerless = (Actor)RuntimeHelpers.GetUninitializedObject(typeof(Actor));

			Assert.Multiple(() =>
			{
				Assert.That(SdkQueueOrderObserver.Observe(null), Is.False);
				Assert.That(SdkQueueOrderObserver.Observe(ownerless), Is.False);
				Assert.That(
					SdkQueueOrderObserver.IsEligibleMutationTarget(
						hasOwner: false, hasPlayerActor: false, hasProductionQueue: false),
					Is.False);
				Assert.That(
					SdkQueueOrderObserver.IsEligibleMutationTarget(
						hasOwner: true, hasPlayerActor: false, hasProductionQueue: true),
					Is.False);
				Assert.That(
					SdkQueueOrderObserver.IsEligibleMutationTarget(
						hasOwner: true, hasPlayerActor: true, hasProductionQueue: false),
					Is.False);
			});
		}

		[TestCase("PlaceBuilding")]
		[TestCase("LineBuild")]
		[TestCase("PlacePlug")]
		public void DeferredPlacementMutationOrdersTolerateMalformedTargets(string orderString)
		{
			var observer = new SdkQueueOrderObserver();
			var order = new Order(orderString, null, false) { ExtraData = uint.MaxValue };

			Assert.That(
				observer.OrderValidation(null, null, 0, order),
				Is.True);
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

		[Test]
		public void ProductionBudgetArbitrationIsIndependentOfCandidateEnumerationOrder()
		{
			var budget = ProductionBudget.Reserve(1000, "Building", "save for construction");
			var candidates = new[]
			{
				Candidate(30, 3, "Vehicle", "mtnk", 700, ownsReservation: false),
				Candidate(10, 2, "Infantry", "e1", 400, ownsReservation: false)
			};

			var forward = ProductionBudgetArbitrator.Evaluate(
					budget, currentCash: 1800, candidates, maxOrders: 20)
				.ToDictionary(result => result.Candidate.ControllerActorId, result => result.Outcome);
			var reverse = ProductionBudgetArbitrator.Evaluate(
					budget, currentCash: 1800, candidates.Reverse(), maxOrders: 20)
				.ToDictionary(result => result.Candidate.ControllerActorId, result => result.Outcome);

			Assert.Multiple(() =>
			{
				Assert.That(reverse, Is.EqualTo(forward));
				Assert.That(forward[10], Is.EqualTo(ProductionBudgetOutcome.Allowed));
				Assert.That(forward[30], Is.EqualTo(ProductionBudgetOutcome.BudgetSuppressed));
			});
		}

		[Test]
		public void CapSaturationRotatesAdmissionWithoutDependingOnEvaluationOrder()
		{
			var budget = ProductionBudget.Reserve(1000, "Building", "save");
			var candidates = new[]
			{
				Candidate(10, 1, "Building", "nuke", 500, ownsReservation: true),
				Candidate(20, 2, "Vehicle", "mtnk", 500, ownsReservation: false),
				Candidate(30, 3, "Building", "weap", 500, ownsReservation: true)
			};
			var expected = new[] { 10u, 20u, 30u };

			for (ulong round = 0; round < (ulong)candidates.Length; round++)
			{
				var forward = ProductionBudgetArbitrator.Evaluate(
					budget, currentCash: 5000, candidates, maxOrders: 1, admissionRound: round);
				var reverse = ProductionBudgetArbitrator.Evaluate(
					budget, currentCash: 5000, candidates.Reverse(), maxOrders: 1, admissionRound: round);
				var admitted = forward.Single(result =>
					result.Outcome == ProductionBudgetOutcome.Allowed).Candidate.ControllerActorId;
				var reverseAdmitted = reverse.Single(result =>
					result.Outcome == ProductionBudgetOutcome.Allowed).Candidate.ControllerActorId;
				var controllerFirst = ModeExecutor.RotateAdmission(expected, round).First();

				Assert.Multiple(() =>
				{
					Assert.That(admitted, Is.EqualTo(expected[(int)round]));
					Assert.That(reverseAdmitted, Is.EqualTo(admitted));
					Assert.That(controllerFirst, Is.EqualTo(expected[(int)round]));
					Assert.That(forward.Count(result =>
						result.Outcome == ProductionBudgetOutcome.OrderLimit), Is.EqualTo(2));
				});
			}
		}

		[Test]
		public void OwningQueueCanSpendReservedCashBeforeOtherQueues()
		{
			var budget = ProductionBudget.Reserve(1000, "Building", "save for construction");
			var owner = Candidate(1, 11, "Building", "weap", 1000, ownsReservation: true);
			var exactRemainder = Candidate(2, 12, "Vehicle", "mtnk", 800, ownsReservation: false);
			var results = ProductionBudgetArbitrator.Evaluate(
					budget, currentCash: 1800, [exactRemainder, owner], maxOrders: 20)
				.ToDictionary(result => result.Candidate.ControllerActorId);
			var cashPoorOwner = ProductionBudgetArbitrator.Evaluate(
				budget,
				currentCash: 100,
				[Candidate(3, 11, "Building", "weap", 2000, ownsReservation: true)],
				maxOrders: 20).Single();

			Assert.Multiple(() =>
			{
				Assert.That(results[1].Outcome, Is.EqualTo(ProductionBudgetOutcome.Allowed));
				Assert.That(results[2].Outcome, Is.EqualTo(ProductionBudgetOutcome.Allowed));
				Assert.That(results[2].PostOrderCash, Is.Zero);
				Assert.That(cashPoorOwner.Outcome, Is.EqualTo(ProductionBudgetOutcome.Allowed),
					"the owner is not blocked by the cash it reserved for itself");
			});
		}

		[Test]
		public void ArbitrationUsesLiveCashAndHandlesOversizedReservations()
		{
			var budget = ProductionBudget.Reserve(
				int.MaxValue, "Building", "reserve everything");
			var candidate = Candidate(
				7, 12, "Vehicle", "mtnk", 1, ownsReservation: false);

			var enoughAtAssessment = ProductionBudgetArbitrator.Evaluate(
				ProductionBudget.Reserve(1000, "Building", "reserve"),
				currentCash: 1500,
				[candidate with { Cost = 500 }],
				maxOrders: 20).Single();
			var staleCashDropped = ProductionBudgetArbitrator.Evaluate(
				ProductionBudget.Reserve(1000, "Building", "reserve"),
				currentCash: 1499,
				[candidate with { Cost = 500 }],
				maxOrders: 20).Single();
			var oversized = ProductionBudgetArbitrator.Evaluate(
				budget, int.MaxValue, [candidate], maxOrders: 20).Single();

			Assert.Multiple(() =>
			{
				Assert.That(enoughAtAssessment.Outcome, Is.EqualTo(ProductionBudgetOutcome.Allowed));
				Assert.That(staleCashDropped.Outcome,
					Is.EqualTo(ProductionBudgetOutcome.BudgetSuppressed));
				Assert.That(oversized.Outcome,
					Is.EqualTo(ProductionBudgetOutcome.BudgetSuppressed));
				Assert.That(oversized.PostOrderCash, Is.EqualTo((long)int.MaxValue - 1));
			});
		}

		[Test]
		public void ReservationLeaseRefreshesExpiresAndClearsInvalidValues()
		{
			var lease = new ProductionBudgetLease();
			var first = ProductionBudget.Reserve(800, " Building ", "first");
			var second = ProductionBudget.Reserve(1200, "Vehicle", "second");

			lease.Refresh(first, nextAssessmentTick: 10);
			var normalized = lease.Current(worldTick: 9);
			lease.Refresh(second, nextAssessmentTick: 20);
			var refreshed = lease.Current(worldTick: 10);
			var expired = lease.Current(worldTick: 20);
			lease.Refresh(ProductionBudget.Reserve(-1, "Vehicle", "invalid"), 30);
			var negative = lease.Current(worldTick: 21);
			lease.Refresh(first, 40);
			lease.Clear();

			Assert.Multiple(() =>
			{
				Assert.That(normalized.Queue, Is.EqualTo("Building"));
				Assert.That(refreshed, Is.EqualTo(second));
				Assert.That(expired, Is.EqualTo(ProductionBudget.None));
				Assert.That(negative, Is.EqualTo(ProductionBudget.None));
				Assert.That(lease.Current(22), Is.EqualTo(ProductionBudget.None),
					"a doctrine change clears the previous reservation immediately");
			});
		}

		[Test]
		public void MissingOwnerQueueDisablesReservationEnforcement()
		{
			var budget = ProductionBudget.Reserve(1000, "Building", "save");
			var queues = new[]
			{
				new ProductionQueueIdentity("Vehicle", "Vehicle.GDI"),
				new ProductionQueueIdentity(null, "Infantry.GDI")
			};

			Assert.Multiple(() =>
			{
				Assert.That(ProductionBudgetArbitrator.TryResolveScope(
					budget, queues, out _), Is.False);
				Assert.That(ProductionBudgetArbitrator.TryResolveScope(
					budget,
					queues.Append(new ProductionQueueIdentity("building", "Building.GDI")),
					out _),
					Is.True);
			});
		}

		[Test]
		public void ProductionBudgetOwnershipUsesGroupBeforeType()
		{
			var budget = ProductionBudget.Reserve(1000, "Vehicle", "save");
			var queues = new[]
			{
				new ProductionQueueIdentity("Factory", "Vehicle"),
				new ProductionQueueIdentity("Vehicle", "Aircraft")
			};

			Assert.That(
				ProductionBudgetArbitrator.TryResolveScope(budget, queues, out var groupScope),
				Is.True);
			Assert.Multiple(() =>
			{
				Assert.That(groupScope.UsesGroup, Is.True);
				Assert.That(groupScope.OwnsQueue("Factory", "Vehicle"), Is.False,
					"a Type match must not own the reservation when any Group matches");
				Assert.That(groupScope.OwnsQueue("Vehicle", "Aircraft"), Is.True);
			});

			Assert.That(
				ProductionBudgetArbitrator.TryResolveScope(
					budget,
					[new ProductionQueueIdentity("Factory", "Vehicle")],
					out var typeScope),
				Is.True);
			Assert.Multiple(() =>
			{
				Assert.That(typeScope.UsesGroup, Is.False);
				Assert.That(typeScope.OwnsQueue("Factory", "Vehicle"), Is.True);
			});
		}

		[Test]
		public void ProductionBudgetResolutionReportsNormalizedAssessmentStatus()
		{
			var queues = new[]
			{
				new ProductionQueueIdentity("Building", "Building.GDI")
			};
			var inactive = ProductionBudgetArbitrator.Resolve(ProductionBudget.None, queues);
			var invalid = ProductionBudgetArbitrator.Resolve(
				ProductionBudget.Reserve(-5, " Building ", "bad", "budget.bad"), queues);
			var unmatched = ProductionBudgetArbitrator.Resolve(
				ProductionBudget.Reserve(900, "Vehicle", "save", "budget.vehicle"), queues);
			var active = ProductionBudgetArbitrator.Resolve(
				ProductionBudget.Reserve(1200, " Building ", "save", "budget.building"), queues);

			Assert.Multiple(() =>
			{
				Assert.That(inactive.Status, Is.EqualTo(ProductionBudgetStatus.Inactive));
				Assert.That(inactive.IsActive, Is.False);
				Assert.That(invalid.Status, Is.EqualTo(ProductionBudgetStatus.Invalid));
				Assert.That(invalid.Budget.ReservedCash, Is.Zero);
				Assert.That(invalid.Budget.Queue, Is.EqualTo("Building"));
				Assert.That(invalid.Budget.ReasonId, Is.EqualTo("budget.bad"));
				Assert.That(unmatched.Status, Is.EqualTo(ProductionBudgetStatus.Unmatched));
				Assert.That(unmatched.Budget.ReservedCash, Is.EqualTo(900));
				Assert.That(active.Status, Is.EqualTo(ProductionBudgetStatus.Active));
				Assert.That(active.IsActive, Is.True);
				Assert.That(active.Scope.UsesGroup, Is.True);
			});
		}

		[Test]
		public void ProductionBudgetAssessmentTraceIncludesStatusAndNormalizedPolicy()
		{
			var path = Path.Combine(
				Path.GetTempPath(), $"autocnc-budget-assessment-{Guid.NewGuid():N}.jsonl");

			try
			{
				using (var trace = DecisionTrace.Open(path))
				{
					trace.ProductionBudgetAssessment(
						seconds: 12,
						doctrine: "Opening",
						ProductionBudget.Reserve(
							1200,
							"Building",
							"save for tech",
							"production.reserve.tech"),
						active: true,
						status: "active");
				}

				using var document = JsonDocument.Parse(File.ReadLines(path).Skip(1).Single());
				var root = document.RootElement;
				var reservation = root.GetProperty("productionBudget");

				Assert.Multiple(() =>
				{
					Assert.That(root.GetProperty("event").GetString(), Is.EqualTo("production-budget"));
					Assert.That(root.GetProperty("doctrine").GetString(), Is.EqualTo("Opening"));
					Assert.That(root.GetProperty("active").GetBoolean(), Is.True);
					Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("active"));
					Assert.That(reservation.GetProperty("reservedCash").GetInt32(), Is.EqualTo(1200));
					Assert.That(reservation.GetProperty("ownerQueue").GetString(), Is.EqualTo("Building"));
					Assert.That(reservation.GetProperty("reasonId").GetString(),
						Is.EqualTo("production.reserve.tech"));
				});
			}
			finally
			{
				File.Delete(path);
				File.Delete(path + ".1");
			}
		}

		[Test]
		public void BudgetSuppressionTraceIncludesStructuredReservationData()
		{
			var path = Path.Combine(
				Path.GetTempPath(), $"autocnc-budget-trace-{Guid.NewGuid():N}.jsonl");

			try
			{
				using (var trace = DecisionTrace.Open(path))
				{
					trace.ProductionBudgetSuppressed(
						seconds: 17,
						actor: "weap",
						actorId: 42,
						mode: "TrainUnitsMode",
						UnitDecision.Produce("Vehicle", "mtnk", "replace armour", "production.armour"),
						ProductionBudget.Reserve(
							1200,
							"Building",
							"save for tech",
							"production.reserve.tech"),
						itemCost: 800,
						currentCash: 1700,
						postOrderCash: 900,
						reservedCashRemaining: 1200);
				}

				using var document = JsonDocument.Parse(File.ReadLines(path).Skip(1).Single());
				var root = document.RootElement;
				var reservation = root.GetProperty("productionBudget");
				var production = root.GetProperty("production");

				Assert.Multiple(() =>
				{
					Assert.That(root.GetProperty("outcome").GetString(),
						Is.EqualTo("production-budget-suppressed"));
					Assert.That(reservation.GetProperty("reservedCash").GetInt32(), Is.EqualTo(1200));
					Assert.That(reservation.GetProperty("ownerQueue").GetString(), Is.EqualTo("Building"));
					Assert.That(reservation.GetProperty("reasonId").GetString(),
						Is.EqualTo("production.reserve.tech"));
					Assert.That(production.GetProperty("itemCost").GetInt32(), Is.EqualTo(800));
					Assert.That(production.GetProperty("currentCash").GetInt64(), Is.EqualTo(1700));
					Assert.That(production.GetProperty("postOrderCash").GetInt64(), Is.EqualTo(900));
				});
			}
			finally
			{
				File.Delete(path);
				File.Delete(path + ".1");
			}
		}

		static UnitDecision Cancellation(uint queueActorId, string item, int count) =>
			UnitDecision.CancelProduction("Vehicle", item, count, "cancel") with
			{
				TargetActorId = queueActorId
			};

		static ProductionBudgetCandidate Candidate(
			uint controllerActorId,
			uint queueActorId,
			string queue,
			string item,
			int cost,
			bool ownsReservation) =>
			new(controllerActorId, queueActorId, queue, item, cost, ownsReservation);

		static uint LegacyQueueFingerprint(ProductionQueueEntry[] entries)
		{
			const uint Offset = 2166136261;
			const uint Prime = 16777619;

			var hash = Offset;
			var count = 0u;
			foreach (var entry in entries)
			{
				hash = (hash ^ 0xFFu) * Prime;
				foreach (var character in entry.Item)
					hash = (hash ^ character) * Prime;

				hash = (hash ^ (entry.Infinite ? 1u : 0u)) * Prime;
				count++;
			}

			hash = (hash ^ count) * Prime;
			return hash == 0 ? 1u : hash;
		}
	}
}
