using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AutoCnC.Sdk;
using AutoCnC.Core;
using AutoCnC.Reference.Logic;
using OpenRA;
using OpenRA.Traits;

namespace AutoCnC.Reference.Modes
{
	/// <summary>Stays with a harvester and coordinates weapon-matched responses to its attackers.</summary>
	public sealed class HarvesterEscortMode : UnitMode
	{
		const string WardType = "harv";
		const int GuardRadius = 5 * 1024;
		const int SearchRadius = 30 * 1024;
		const int ThreatMemoryTicks = 300;
		// Transfers a dead responder's claim without letting every new hit reshuffle live ones.
		const int ClaimLeaseTicks = 75;

		uint wardId;
		WeaponRole role;
		bool isAntiInfantrySpecialist;

		public override void OnEnter(Actor self, ModeContext ctx)
		{
			wardId = 0;
			role = WeaponMatchLogic.RoleOf(self.Info.Name);
			isAntiInfantrySpecialist = self.Info.Name == "e2" || self.Info.Name == "e4";
			ctx.Anchor = ctx.BaseCenter;
		}

		public override UnitDecision OnTick(Actor self, ModeContext ctx)
		{
			var ward = ctx.ResolveActor(wardId);
			if (!IsLive(ward))
				ward = null;

			// Ground claims are deliberately stable, but an aircraft crossing weapon range is a
			// perishable shot. Take it now without chasing beyond the escort's current position.
			var aircraft = ctx.SenseThreats(new WDist(ctx.WeaponRangeUnits))
				.Where(t => t.Kind == ThreatKind.Aircraft
					&& t.IsAttackable
					&& t.DistanceUnits <= ctx.WeaponRangeUnits)
				.OrderByDescending(t => t.CanHitUs)
				.ThenBy(t => t.DistanceUnits)
				.ThenBy(t => t.ActorId)
				.FirstOrDefault();

			if (aircraft.ActorId != 0)
				return UnitDecision.Attack(aircraft.ActorId, "in-range anti-air harvester screen");

			if (HarvesterThreats.TryGetAssignment(
				self.Owner,
				self.ActorID,
				role,
				ctx.WorldTick,
				ThreatMemoryTicks,
				ClaimLeaseTicks,
				out var report)
				&& HarvesterThreats.TryClaim(
					self.Owner,
					report,
					self.ActorID,
					ctx.WorldTick,
					ThreatMemoryTicks,
					ClaimLeaseTicks,
					out var responderNumber))
			{
				var response = RespondTo(
					report,
					self,
					ctx,
					isAntiInfantrySpecialist
						? "specialist anti-infantry harvester response"
						: responderNumber > 1
						? "paired weapon-matched harvester response"
						: "stable weapon-matched harvester response");
				if (response.HasValue)
				{
					var reportedWard = ctx.ResolveActor(report.WardId);
					if (IsLive(reportedWard))
					{
						ward = reportedWard;
						wardId = reportedWard.ActorID;
					}

					return response.Value;
				}

				HarvesterThreats.Release(self.Owner, report.AttackerId, self.ActorID);
			}

			if (ward == null)
			{
				ward = ctx.SenseAllies(new WDist(SearchRadius), WardType)
					.Where(IsLive)
					.OrderBy(a => ctx.DistanceTo(a))
					.FirstOrDefault();

				wardId = ward?.ActorID ?? 0;
			}

			if (ward == null)
				return ctx.CanMove
					? UnitDecision.ReturnToAnchor("no harvester to escort, returning to base")
					: UnitDecision.Hold("no harvester to escort");

			var threat = ctx.SenseThreats(new WDist(GuardRadius))
				.Where(t => t.IsAttackable)
				.OrderByDescending(t => t.CanHitUs)
				.ThenBy(t => t.DistanceUnits)
				.FirstOrDefault();

			if (threat.ActorId != 0)
				return UnitDecision.Attack(threat.ActorId, "defending harvester");

			if (ctx.DistanceTo(ward) > GuardRadius)
				return UnitDecision.MoveTo(ward.Location.X, ward.Location.Y, "closing on harvester");

			return UnitDecision.Continue;
		}

		static UnitDecision? RespondTo(
			in HarvesterThreatReport report,
			Actor self,
			ModeContext ctx,
			string reason)
		{
			var attacker = ctx.ResolveActor(report.AttackerId);
			if (attacker != null)
			{
				if (!IsLive(attacker))
					return null;

				if (self.Owner.RelationshipWith(attacker.Owner) == PlayerRelationship.Enemy
					&& ctx.CanAttack(attacker))
					return UnitDecision.Attack(
						attacker.ActorID,
						$"{reason}: attacking reported {report.Kind}");

				return null;
			}

			// Ground artillery can fire beyond both the harvester's and its escort's sight.
			// The attacked harvester legitimately knows the last cell the shot came from.
			if (ctx.HasWeapon && ctx.CanMove && report.Kind != ThreatKind.Aircraft)
				return UnitDecision.AttackMoveTo(
					report.X,
					report.Y,
					$"{reason}: advancing on reported {report.Kind}");

			return null;
		}

		static bool IsLive(Actor actor) => actor != null && actor.IsInWorld && !actor.IsDead;
	}

	readonly record struct HarvesterThreatReport(
		uint WardId,
		uint AttackerId,
		int X,
		int Y,
		ThreatKind Kind,
		int LastHitTick);

	readonly record struct HarvesterThreatClaim(
		uint ResponderId,
		int RenewedTick);

	/// <summary>Side-local memory of information received by attacked harvesters.</summary>
	static class HarvesterThreats
	{
		sealed class ThreatState
		{
			public readonly Dictionary<uint, HarvesterThreatReport> ReportsByAttacker = [];
			public readonly Dictionary<uint, List<HarvesterThreatClaim>> ClaimsByAttacker = [];
		}

		static readonly ConditionalWeakTable<Player, ThreatState> States = new();

		public static void Record(
			Player owner,
			uint wardId,
			uint attackerId,
			int x,
			int y,
			ThreatKind kind,
			int tick)
		{
			if (owner == null || wardId == 0 || attackerId == 0)
				return;

			States.GetOrCreateValue(owner).ReportsByAttacker[attackerId] =
				new HarvesterThreatReport(wardId, attackerId, x, y, kind, tick);
		}

		public static bool TryClaim(
			Player owner,
			in HarvesterThreatReport report,
			uint responderId,
			int tick,
			int reportMemoryTicks,
			int claimLeaseTicks,
			out int responderNumber)
		{
			responderNumber = 0;
			if (owner == null
				|| responderId == 0
				|| !States.TryGetValue(owner, out var state)
				|| !state.ReportsByAttacker.TryGetValue(report.AttackerId, out var current)
				|| current.LastHitTick != report.LastHitTick)
				return false;

			if (!state.ClaimsByAttacker.TryGetValue(report.AttackerId, out var claims))
			{
				claims = [];
				state.ClaimsByAttacker.Add(report.AttackerId, claims);
			}

			for (var i = claims.Count - 1; i >= 0; i--)
			{
				if (tick - claims[i].RenewedTick > claimLeaseTicks)
					claims.RemoveAt(i);
			}

			for (var i = 0; i < claims.Count; i++)
			{
				if (claims[i].ResponderId != responderId)
					continue;

				claims[i] = new HarvesterThreatClaim(responderId, tick);
				responderNumber = i + 1;
				return true;
			}

			if (HasActiveAssignment(
				state,
				report.AttackerId,
				responderId,
				tick,
				reportMemoryTicks,
				claimLeaseTicks)
				|| claims.Count >= ResponderLimit(report.Kind))
				return false;

			claims.Add(new HarvesterThreatClaim(responderId, tick));
			responderNumber = claims.Count;
			return true;
		}

		public static void Release(Player owner, uint attackerId, uint responderId)
		{
			if (owner == null
				|| !States.TryGetValue(owner, out var state)
				|| !state.ClaimsByAttacker.TryGetValue(attackerId, out var claims))
				return;

			for (var i = claims.Count - 1; i >= 0; i--)
			{
				if (claims[i].ResponderId == responderId)
					claims.RemoveAt(i);
			}
		}

		public static bool TryGetAssignment(
			Player owner,
			uint responderId,
			WeaponRole role,
			int tick,
			int reportMemoryTicks,
			int claimLeaseTicks,
			out HarvesterThreatReport latest)
		{
			latest = default;
			if (owner == null || !States.TryGetValue(owner, out var state))
				return false;

			var found = false;
			foreach (var report in state.ReportsByAttacker.Values)
			{
				if (!IsCurrent(report, tick, reportMemoryTicks)
					|| !IsWeaponMatch(role, report.Kind)
					|| !HasClaim(state, report.AttackerId, responderId, tick, claimLeaseTicks))
					continue;

				SelectLatest(report, ref latest, ref found);
			}

			if (found)
				return true;

			foreach (var report in state.ReportsByAttacker.Values)
			{
				if (!IsCurrent(report, tick, reportMemoryTicks)
					|| !IsWeaponMatch(role, report.Kind)
					|| ActiveClaimCount(state, report.AttackerId, tick, claimLeaseTicks) >= ResponderLimit(report.Kind))
					continue;

				SelectLatest(report, ref latest, ref found);
			}

			return found;
		}

		static bool HasActiveAssignment(
			ThreatState state,
			uint exceptAttackerId,
			uint responderId,
			int tick,
			int reportMemoryTicks,
			int claimLeaseTicks)
		{
			foreach (var pair in state.ClaimsByAttacker)
			{
				if (pair.Key == exceptAttackerId
					|| !state.ReportsByAttacker.TryGetValue(pair.Key, out var report)
					|| !IsCurrent(report, tick, reportMemoryTicks))
					continue;

				for (var i = 0; i < pair.Value.Count; i++)
				{
					var claim = pair.Value[i];
					if (claim.ResponderId == responderId && tick - claim.RenewedTick <= claimLeaseTicks)
						return true;
				}
			}

			return false;
		}

		static bool HasClaim(
			ThreatState state,
			uint attackerId,
			uint responderId,
			int tick,
			int claimLeaseTicks)
		{
			if (!state.ClaimsByAttacker.TryGetValue(attackerId, out var claims))
				return false;

			for (var i = 0; i < claims.Count; i++)
			{
				var claim = claims[i];
				if (claim.ResponderId == responderId && tick - claim.RenewedTick <= claimLeaseTicks)
					return true;
			}

			return false;
		}

		static int ActiveClaimCount(ThreatState state, uint attackerId, int tick, int claimLeaseTicks)
		{
			if (!state.ClaimsByAttacker.TryGetValue(attackerId, out var claims))
				return 0;

			var count = 0;
			for (var i = 0; i < claims.Count; i++)
			{
				if (tick - claims[i].RenewedTick <= claimLeaseTicks)
					count++;
			}

			return count;
		}

		static bool IsCurrent(in HarvesterThreatReport report, int tick, int reportMemoryTicks) =>
			tick - report.LastHitTick <= reportMemoryTicks;

		// Aircraft are intercepted above only when already in range; a remembered hit is not a
		// destination a ground unit can catch.
		static bool IsWeaponMatch(WeaponRole role, ThreatKind kind) =>
			kind != ThreatKind.Aircraft
			&& (role == WeaponRole.Unknown
				|| kind == ThreatKind.Unknown
				|| WeaponMatchLogic.Effectiveness(role, kind) >= WeaponMatchLogic.NeutralEffectiveness);

		static int ResponderLimit(ThreatKind kind) =>
			kind == ThreatKind.Vehicle || kind == ThreatKind.Defence ? 2 : 1;

		static void SelectLatest(
			in HarvesterThreatReport report,
			ref HarvesterThreatReport latest,
			ref bool found)
		{
			if (found
				&& (report.LastHitTick < latest.LastHitTick
					|| (report.LastHitTick == latest.LastHitTick && report.AttackerId > latest.AttackerId)))
				return;

			latest = report;
			found = true;
		}
	}
}
