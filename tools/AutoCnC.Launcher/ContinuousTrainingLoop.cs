#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

namespace AutoCnC.Launcher
{
	public enum ContinuousTrainingStage
	{
		Idle,
		Fighting,
		Improving
	}

	public enum ContinuousTrainingAction
	{
		None,
		Fight,
		Improve
	}

	/// <summary>Controls the unattended fight-and-improve cycle independently of the UI queue.</summary>
	public sealed class ContinuousTrainingLoop
	{
		public ContinuousTrainingStage Stage { get; private set; }
		public bool IsRunning => Stage != ContinuousTrainingStage.Idle;

		public void Begin(bool enabled)
		{
			Stage = enabled ? ContinuousTrainingStage.Fighting : ContinuousTrainingStage.Idle;
		}

		public ContinuousTrainingAction BattleCompleted()
		{
			if (Stage != ContinuousTrainingStage.Fighting)
				return ContinuousTrainingAction.None;

			Stage = ContinuousTrainingStage.Improving;
			return ContinuousTrainingAction.Improve;
		}

		public ContinuousTrainingAction ImprovementCompleted()
		{
			if (Stage != ContinuousTrainingStage.Improving)
				return ContinuousTrainingAction.None;

			Stage = ContinuousTrainingStage.Fighting;
			return ContinuousTrainingAction.Fight;
		}

		public void Stop()
		{
			Stage = ContinuousTrainingStage.Idle;
		}
	}
}
