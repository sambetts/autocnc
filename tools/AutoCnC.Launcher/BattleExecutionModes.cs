#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;

namespace AutoCnC.Launcher
{
	public static class BattleExecutionModes
	{
		public const string Headless = "Headless";
		public const string Rendered = "Rendered";
		public const string MaximumGameSpeed = "maximum";

		public static string Normalize(string value) =>
			string.Equals(value, Rendered, StringComparison.OrdinalIgnoreCase) ? Rendered : Headless;

		public static string EffectiveGameSpeed(string executionMode, string selectedGameSpeed) =>
			Normalize(executionMode) == Headless ? MaximumGameSpeed : selectedGameSpeed;
	}
}
