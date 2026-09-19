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

namespace AutoCnC.Core
{
	internal enum DoctrineDwellResult : byte
	{
		Allowed = 0,
		MinimumDwell = 1,
		UrgentDwellBypass = 2,
		UrgentWithoutBaseAttack = 3
	}

	internal static class DoctrineTransitionPolicy
	{
		public static DoctrineDwellResult CheckDwell(
			in DoctrineDecision decision, in BattleState state, int minimumDoctrineSeconds)
		{
			if (state.DoctrineSeconds >= minimumDoctrineSeconds)
				return DoctrineDwellResult.Allowed;

			if (decision.CanBypassMinimumDwell(state))
				return DoctrineDwellResult.UrgentDwellBypass;

			return decision.IsUrgent
				? DoctrineDwellResult.UrgentWithoutBaseAttack
				: DoctrineDwellResult.MinimumDwell;
		}
	}
}
