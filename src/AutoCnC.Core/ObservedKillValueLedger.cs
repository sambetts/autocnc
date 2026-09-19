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

namespace AutoCnC.Core
{
	/// <summary>Tracks kill value only for enemies present in the latest visibility sample.</summary>
	internal sealed class ObservedKillValueLedger
	{
		readonly HashSet<uint> visibleEnemies = [];

		public int TotalValue { get; private set; }

		public void BeginVisibilitySample() => visibleEnemies.Clear();

		public void ObserveVisibleEnemy(uint actorId) => visibleEnemies.Add(actorId);

		public bool ObserveKill(uint actorId, bool killedBySelf, Func<int> valueFactory)
		{
			var wasVisible = visibleEnemies.Remove(actorId);
			if (!killedBySelf || !wasVisible)
				return false;

			TotalValue += Math.Max(0, valueFactory());
			return true;
		}

		public void Forget(uint actorId) => visibleEnemies.Remove(actorId);
	}
}
