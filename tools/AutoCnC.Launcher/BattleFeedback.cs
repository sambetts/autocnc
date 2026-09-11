// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System.IO;

namespace AutoCnC.Launcher
{
	internal static class BattleFeedback
	{
		internal static bool CanReview(TrainingRun run) => run?.HasRecordedBattle == true &&
			(run.CanProvideFeedback || File.Exists(run.ReplayPath));

		internal static bool ShouldPrompt(TrainingRun run, bool automated) =>
			!automated && run?.Manifest.Status == "finished" && run.CanProvideFeedback &&
			!run.IsHeadless && !run.HasPlayerFeedback;

		internal static string ActionText(TrainingRun run) =>
			run?.NeedsReplayForFeedback == true ? "Watch replay && add feedback"
				: run?.HasPlayerFeedback == true ? "Edit feedback" : "Add feedback";

		internal static string Status(TrainingRun run) =>
			run == null ? "No battle selected"
				: run.Manifest.CompletedUtc == null ? "Battle in progress"
				: !run.HasRecordedBattle ? "No battle recorded"
				: run.HasPlayerFeedback ? "Feedback provided" : "No feedback yet";

		internal static string Description(TrainingRun run, bool busy = false)
		{
			if (run == null)
				return "Fight a battle, then add your observations to help improve your bot.";
			if (run.Manifest.CompletedUtc == null)
				return "Feedback will be available when this battle finishes.";
			if (!run.HasRecordedBattle)
				return "No battle telemetry was recorded for this session. Check the build output.";
			if (busy)
				return "Wait for the current operation to finish before reviewing or adding feedback.";
			if (run.NeedsReplayForFeedback)
				return File.Exists(run.ReplayPath)
					? "Watch this headless battle's replay before adding your observations."
					: "No replay was captured. Feedback is unavailable for this headless battle.";
			return run.HasPlayerFeedback
				? "Saved with this battle and included the next time this battle is analyzed."
				: "What did you notice? Your feedback joins the logs and stats in this battle's improvement prompt.";
		}
	}
}
