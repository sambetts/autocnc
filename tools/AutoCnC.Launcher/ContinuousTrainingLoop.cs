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
		Improving,
		Evaluating,
		Restoring
	}

	public enum ContinuousTrainingAction
	{
		None,
		Fight,
		Improve,
		Evaluate,
		Restore
	}

	public enum ContinuousEvaluationDecision
	{
		Undefined,
		Promote,
		Restore,
		Reevaluate
	}

	/// <summary>Controls the promotion-gated unattended cycle independently of the UI queue.</summary>
	public sealed class ContinuousTrainingLoop
	{
		bool stopAfterRestore;

		public ContinuousTrainingStage Stage { get; private set; }
		public bool IsRunning => Stage != ContinuousTrainingStage.Idle;

		public void Begin(bool enabled)
		{
			stopAfterRestore = false;
			Stage = enabled ? ContinuousTrainingStage.Fighting : ContinuousTrainingStage.Idle;
		}

		public ContinuousTrainingAction BattleCompleted()
		{
			if (Stage != ContinuousTrainingStage.Fighting)
				return ContinuousTrainingAction.None;

			Stage = ContinuousTrainingStage.Improving;
			return ContinuousTrainingAction.Improve;
		}

		public ContinuousTrainingAction ImprovementCompleted(bool succeeded = true)
		{
			if (Stage != ContinuousTrainingStage.Improving)
				return ContinuousTrainingAction.None;

			Stage = succeeded
				? ContinuousTrainingStage.Evaluating
				: ContinuousTrainingStage.Restoring;
			stopAfterRestore = !succeeded;
			return succeeded
				? ContinuousTrainingAction.Evaluate
				: ContinuousTrainingAction.Restore;
		}

		public ContinuousTrainingAction EvaluationCompleted(ContinuousEvaluationDecision decision)
		{
			if (Stage != ContinuousTrainingStage.Evaluating)
				return ContinuousTrainingAction.None;

			if (decision == ContinuousEvaluationDecision.Reevaluate)
				return ContinuousTrainingAction.Evaluate;

			if (decision == ContinuousEvaluationDecision.Promote)
			{
				stopAfterRestore = false;
				Stage = ContinuousTrainingStage.Fighting;
				return ContinuousTrainingAction.Fight;
			}

			stopAfterRestore = decision == ContinuousEvaluationDecision.Undefined;
			Stage = ContinuousTrainingStage.Restoring;
			return ContinuousTrainingAction.Restore;
		}

		public ContinuousTrainingAction RestorationCompleted()
		{
			if (Stage != ContinuousTrainingStage.Restoring)
				return ContinuousTrainingAction.None;

			var stop = stopAfterRestore;
			stopAfterRestore = false;
			Stage = stop ? ContinuousTrainingStage.Idle : ContinuousTrainingStage.Fighting;
			return stop ? ContinuousTrainingAction.None : ContinuousTrainingAction.Fight;
		}

		public ContinuousTrainingAction WorkspaceChangedBeforeFight()
		{
			if (Stage != ContinuousTrainingStage.Fighting)
				return ContinuousTrainingAction.None;

			stopAfterRestore = false;
			Stage = ContinuousTrainingStage.Evaluating;
			return ContinuousTrainingAction.Evaluate;
		}

		public void Stop()
		{
			stopAfterRestore = false;
			Stage = ContinuousTrainingStage.Idle;
		}
	}
}
