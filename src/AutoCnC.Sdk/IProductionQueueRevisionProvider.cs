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

using OpenRA;
using OpenRA.Mods.Common.Traits;

namespace AutoCnC.Sdk
{
	internal interface IProductionQueueRevisionProvider
	{
		bool TryGetRevision(ProductionQueue queue, out ulong revision);
	}

	internal interface IRepairStateRevisionProvider
	{
		bool TryGetRevision(Actor building, out ulong revision);
	}
}
