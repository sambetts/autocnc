using System.Collections.Generic;
using AutoCnC.Core;

namespace __AUTOCNC_BOT_ROOT_NAMESPACE__.Logic
{
	public readonly record struct StarterState(
		bool HasWeapon,
		bool IsIdle,
		IReadOnlyList<ThreatSnapshot> Threats);

	public static class StarterLogic
	{
		public static UnitDecision Decide(in StarterState state)
		{
			if (!state.HasWeapon)
				return UnitDecision.Continue;

			var target = SelectNearestTarget(state.Threats);
			if (target.HasValue)
				return UnitDecision.Attack(
					target.Value.ActorId,
					$"engaging nearby {target.Value.Kind.ToString().ToLowerInvariant()}");

			return state.IsIdle
				? UnitDecision.Hold("waiting for a visible target")
				: UnitDecision.Continue;
		}

		public static ThreatSnapshot? SelectNearestTarget(IReadOnlyList<ThreatSnapshot> threats)
		{
			if (threats == null)
				return null;

			ThreatSnapshot? nearest = null;
			for (var i = 0; i < threats.Count; i++)
			{
				var candidate = threats[i];
				if (!candidate.IsAttackable)
					continue;

				if (!nearest.HasValue ||
					candidate.DistanceUnits < nearest.Value.DistanceUnits ||
					(candidate.DistanceUnits == nearest.Value.DistanceUnits &&
						candidate.ActorId < nearest.Value.ActorId))
					nearest = candidate;
			}

			return nearest;
		}
	}
}
