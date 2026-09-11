// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

namespace AutoCnC.Launcher
{
	internal sealed class RecordedBattleChoice
	{
		public int Number { get; init; }
		public TrainingRun Run { get; init; }

		public override string ToString()
		{
			var result = Run.Manifest.Result;
			var outcome = result?.Outcome ?? (Run.Manifest.CompletedUtc == null ? "running" : Run.Manifest.Status);
			var duration = result == null ? "" : $" / {Csv.Clock(result.DurationSeconds)}";
			return $"Battle #{Number} / {Run.Manifest.CreatedUtc.ToLocalTime():g} / {outcome}{duration}";
		}
	}
}
