#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoCnC.Launcher
{
	public sealed class TrainingAgentConfiguration
	{
		public string Command { get; set; }
		public List<string> Arguments { get; set; } = [];
	}

	/// <summary>Builds the constrained evidence packet passed to a local coding agent.</summary>
	public static class TrainingAgent
	{
		public const string NextPromptBegin = "AUTOCNC_NEXT_PROMPT_BEGIN";
		public const string NextPromptEnd = "AUTOCNC_NEXT_PROMPT_END";
		const string NextPromptContractPlaceholder = "{nextPromptContract}";
		const string NextPromptHeading = "## Create the complete prompt for the next round";

		static readonly string[] RequiredPromptPlaceholders =
		[
			"{workspace}",
			"{gameGuide}",
			"{gameRules}",
			"{fightManifest}",
			"{battleLog}",
			"{telemetry}",
			"{decisionTrace}",
			"{battle}",
			"{result}",
			"{sourceRevision}",
			NextPromptContractPlaceholder
		];

		static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		public static readonly string[] DefaultArguments =
		[
			"-p", "{prompt}",
			"--allow-all-tools",
			"--no-ask-user",
			"--no-custom-instructions",
			"--no-remote-export",
			"--add-dir", "{evidence}"
		];

		static readonly string[] PreviousDefaultArguments =
		[
			"-p", "{prompt}",
			"--allow-all-tools",
			"--no-ask-user",
			"--no-color",
			"--no-custom-instructions",
			"--no-remote-export",
			"--screen-reader",
			"--add-dir", "{evidence}"
		];

		static readonly string[] EarlierDefaultArguments =
		[
			"-p", "{prompt}",
			"--allow-all-tools",
			"--no-ask-user",
			"--no-color",
			"--no-custom-instructions",
			"--no-remote-export",
			"--add-dir", "{evidence}"
		];

		static readonly string[] InitialDefaultArguments =
		[
			"-p", "{prompt}",
			"--allow-all-tools",
			"--no-ask-user",
			"--no-color",
			"--no-custom-instructions",
			"--no-remote-export",
			"--silent"
		];

		public static string[] UpgradeDefaultArguments(string command, string[] arguments)
		{
			if (arguments is not { Length: > 0 })
				return [.. DefaultArguments];

			if (!string.Equals(command, "copilot", StringComparison.OrdinalIgnoreCase))
				return arguments;

			return arguments.SequenceEqual(PreviousDefaultArguments, StringComparer.Ordinal) ||
				arguments.SequenceEqual(EarlierDefaultArguments, StringComparer.Ordinal) ||
				arguments.SequenceEqual(InitialDefaultArguments, StringComparer.Ordinal)
				? [.. DefaultArguments]
				: arguments;
		}

		public static void PrepareContext(TrainingRun run, string gameGuidePath, string gameRulesPath,
			string promptTemplate, string recoveryContext = null)
		{
			if (!run.IsEditable)
				throw new InvalidOperationException("AI improvement requires a battle bot project, not a prebuilt assembly.");

			if (!File.Exists(gameGuidePath))
				throw new FileNotFoundException("The agent game guide is missing.", gameGuidePath);

			if (!File.Exists(gameRulesPath))
				throw new FileNotFoundException("The resolved game-rules snapshot is missing.", gameRulesPath);

			if (!ValidatePromptTemplate(promptTemplate, out var error))
				throw new InvalidOperationException("The saved agent prompt is invalid: " + error);

			Directory.CreateDirectory(run.EvidenceDirectory);
			File.Copy(gameGuidePath, run.GameGuidePath, true);
			if (!string.Equals(Path.GetFullPath(gameRulesPath), Path.GetFullPath(run.GameRulesPath),
				StringComparison.OrdinalIgnoreCase))
				File.Copy(gameRulesPath, run.GameRulesPath, true);
			run.ExportFightManifest();
			File.WriteAllText(run.PromptPath,
				RenderPrompt(run, promptTemplate, recoveryContext));
		}

		public static void Prepare(TrainingRun run, string gameGuidePath, string gameRulesPath,
			string promptTemplate, string command, IReadOnlyList<string> arguments,
			string recoveryContext = null)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new InvalidOperationException("The agent command is empty.");

			PrepareContext(run, gameGuidePath, gameRulesPath, promptTemplate, recoveryContext);

			var configuration = new TrainingAgentConfiguration { Command = command.Trim() };
			configuration.Arguments.AddRange(arguments is { Count: > 0 } ? arguments : DefaultArguments);
			File.WriteAllText(run.AgentConfigurationPath,
				JsonSerializer.Serialize(configuration, JsonOptions));
		}

		public static string FindSuggestedNextPrompt(IReadOnlyList<string> output,
			TrainingRun run = null)
		{
			if (output is not { Count: > 0 })
				return null;

			// Parse backwards from the final complete block. The contract itself shows a nested
			// marker pair, and agents sometimes copy that example into their proposed template.
			// Pairing the final End with the nearest Begin used to extract only that example's
			// footer instead of the actual outer proposal.
			var depth = 0;
			var end = -1;
			for (var i = output.Count - 1; i >= 0; i--)
			{
				var line = (output[i] ?? "").Trim();
				if (string.Equals(line, NextPromptEnd, StringComparison.OrdinalIgnoreCase))
				{
					if (end < 0)
						end = i;

					depth++;
					continue;
				}

				if (!string.Equals(line, NextPromptBegin, StringComparison.OrdinalIgnoreCase) ||
					depth == 0)
					continue;

				depth--;
				if (depth > 0)
					continue;

				var template = string.Join(Environment.NewLine,
					output.Skip(i + 1).Take(end - i - 1)).Trim();
				if (template.StartsWith("```", StringComparison.Ordinal))
				{
					var firstNewline = template.IndexOf('\n');
					template = firstNewline >= 0 ? template[(firstNewline + 1)..] : "";
				}

				if (template.EndsWith("```", StringComparison.Ordinal))
					template = template[..^3].TrimEnd();

				template = NormalizeSuggestedPrompt(template, run);
				return template.Length == 0 ? null :
					template.Length <= 30_000 ? template : template[..30_000];
			}

			return null;
		}

		public static bool ValidatePromptTemplate(string template, out string error)
		{
			if (string.IsNullOrWhiteSpace(template))
			{
				error = "The prompt is empty.";
				return false;
			}

			if (template.Length > 30_000)
			{
				error = "The prompt exceeds the 30,000 character limit.";
				return false;
			}

			var missing = RequiredPromptPlaceholders
				.Where(placeholder => !template.Contains(placeholder, StringComparison.Ordinal))
				.ToArray();
			if (missing.Length > 0)
			{
				error = "It must retain these placeholders: " + string.Join(", ", missing);
				return false;
			}

			var contractOccurrences = CountOccurrences(template, NextPromptContractPlaceholder);
			var standaloneContract = SplitLines(template).Count(line =>
				string.Equals(line.Trim(), NextPromptContractPlaceholder, StringComparison.Ordinal));
			if (contractOccurrences != 1 || standaloneContract != 1)
			{
				error = $"It must contain {NextPromptContractPlaceholder} exactly once, on a line by itself.";
				return false;
			}

			if (!template.Contains("edit only", StringComparison.OrdinalIgnoreCase))
			{
				error = "It must explicitly say to edit only the {workspace} bot workspace.";
				return false;
			}

			error = null;
			return true;
		}

		public static string BuildRecoveryContext(TrainingAgentResult failedAttempt,
			string archivedTranscript)
		{
			if (failedAttempt?.ExitCode is not int exitCode || exitCode == 0)
				return null;

			var verification = string.Equals(failedAttempt.FailurePhase, "verification",
				StringComparison.OrdinalIgnoreCase);
			if (!verification && !string.IsNullOrEmpty(archivedTranscript) &&
				File.Exists(archivedTranscript))
			{
				try
				{
					verification = File.ReadAllText(archivedTranscript)
						.Contains("=== Verification ===", StringComparison.Ordinal);
				}
				catch (IOException)
				{
				}
			}

			var phase = verification
				? "independent build/test verification"
				: "the coding-agent process";
			var transcript = string.IsNullOrEmpty(archivedTranscript)
				? "No previous transcript was captured."
				: $"Read the archived attempt transcript first: `{archivedTranscript}`";
			var message = string.IsNullOrWhiteSpace(failedAttempt.FailureMessage)
				? $"The previous attempt exited with code {exitCode}."
				: failedAttempt.FailureMessage;

			return $"""
				# Recovery attempt

				The previous improvement failed during {phase}. Its source edits are still present in
				the bot workspace; do not restart from the original strategy or add another unrelated
				improvement.

				{transcript}

				Reported failure: {message}

				First reproduce the failure with the bot's build and test commands. Repair the current
				changes until every test and build exits successfully. If generated `bin`/`obj` output
				is corrupt, clean it and rerun before diagnosing source. Only revisit the strategic
				change if the tests prove its behavior is wrong.
				""";
		}

		public static string RenderPrompt(TrainingRun run, string template,
			string recoveryContext = null)
		{
			var prompt = template;
			foreach (var replacement in PromptReplacements(run))
				prompt = prompt.Replace(replacement.Placeholder, replacement.Value,
					StringComparison.Ordinal);

			return string.IsNullOrWhiteSpace(recoveryContext)
				? prompt
				: recoveryContext.Trim() + Environment.NewLine + Environment.NewLine + prompt;
		}

		static (string Placeholder, string Value)[] PromptReplacements(TrainingRun run)
		{
			var result = run.Manifest.Result;
			var battle = run.Manifest.Battle;
			var score = result?.Players is { Count: > 0 }
				? string.Join("; ", result.Players.Select(p =>
					$"{p.Name}: {p.Outcome ?? "undecided"}, army={p.ArmyValue}, buildings={p.Buildings}, killed={p.Killed}, lost={p.Lost}, cash={p.Cash}"))
				: "no final score was recorded";
			var playerFeedback = string.IsNullOrWhiteSpace(result?.PlayerFeedback)
				? "No player assessment was provided."
				: "Player assessment of why the battle was won or lost:" + Environment.NewLine +
					result.PlayerFeedback.Trim();

			return
			[
				("{workspace}", run.Manifest.BotDirectory),
				("{gameGuide}", run.GameGuidePath),
				("{gameRules}", run.GameRulesPath),
				("{fightManifest}", run.FightManifestPath),
				("{battleLog}", run.BattleLogPath),
				("{telemetry}", run.TelemetryPath),
				("{decisionTrace}", run.DecisionTracePath),
				("{replay}", File.Exists(run.ReplayPath) ? run.ReplayPath : "not captured"),
				("{battle}", $"map={battle?.Map}, difficulty={battle?.Difficulty}, opponents={battle?.Opponents}, " +
					$"faction={battle?.Faction}, opponent faction={battle?.BotFaction}, speed={battle?.GameSpeed}, " +
					$"execution={battle?.ExecutionMode ?? BattleExecutionModes.Rendered}"),
				("{result}", $"{result?.Outcome ?? "unknown"} after {result?.DurationSeconds ?? 0} game seconds; {score}" +
					Environment.NewLine + playerFeedback),
				("{sourceRevision}", run.Manifest.SourceRevision),

				// This must stay last so placeholders inside the contract remain literal.
				(NextPromptContractPlaceholder, NextPromptContract())
			];
		}

		static string NormalizeSuggestedPrompt(string template, TrainingRun run)
		{
			if (string.IsNullOrWhiteSpace(template))
				return "";

			if (run != null)
			{
				foreach (var replacement in PromptReplacements(run)
					.Where(replacement => !string.IsNullOrWhiteSpace(replacement.Value))
					.Where(replacement => replacement.Placeholder != "{replay}" ||
						File.Exists(run.ReplayPath))
					.OrderByDescending(replacement => replacement.Value.Length))
				{
					var comparison = IsPathPlaceholder(replacement.Placeholder)
						? StringComparison.OrdinalIgnoreCase
						: StringComparison.Ordinal;
					template = template.Replace(replacement.Value, replacement.Placeholder,
						comparison);
				}
			}

			var lines = SplitLines(template).ToList();
			var copiedContract = lines.FindIndex(line =>
				string.Equals(line.Trim(), NextPromptHeading, StringComparison.OrdinalIgnoreCase));
			if (copiedContract >= 0)
				lines.RemoveRange(copiedContract, lines.Count - copiedContract);

			var keptContract = false;
			for (var i = 0; i < lines.Count; i++)
			{
				if (string.Equals(lines[i].Trim(), NextPromptContractPlaceholder,
					StringComparison.Ordinal))
				{
					if (!keptContract)
					{
						lines[i] = NextPromptContractPlaceholder;
						keptContract = true;
					}
					else
						lines[i] = "";

					continue;
				}

				if (lines[i].Contains(NextPromptContractPlaceholder, StringComparison.Ordinal))
					lines[i] = lines[i].Replace(NextPromptContractPlaceholder,
						"the next-round prompt contract", StringComparison.Ordinal);
			}

			while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
				lines.RemoveAt(lines.Count - 1);

			if (!keptContract)
			{
				if (lines.Count > 0)
					lines.Add("");
				lines.Add(NextPromptContractPlaceholder);
			}

			return string.Join(Environment.NewLine, lines).Trim();
		}

		static bool IsPathPlaceholder(string placeholder) =>
			placeholder is "{workspace}" or "{gameGuide}" or "{gameRules}" or
				"{fightManifest}" or "{battleLog}" or "{telemetry}" or "{decisionTrace}" or
				"{replay}";

		static string[] SplitLines(string value) =>
			value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

		static int CountOccurrences(string value, string sought)
		{
			var count = 0;
			var offset = 0;
			while ((offset = value.IndexOf(sought, offset, StringComparison.Ordinal)) >= 0)
			{
				count++;
				offset += sought.Length;
			}

			return count;
		}

		static string NextPromptContract() =>
			$$"""
			## Create the complete prompt for the next round

			After finishing the code improvement, propose an entirely new, standalone prompt template
			for the next improvement round. Replace this prompt rather than adding advice to it.
			Optimize the next prompt to reduce analysis overhead and improve recommendation quality,
			using what this run taught you.

			The template must retain these placeholders exactly so the launcher can insert fresh data:
			{{string.Join(", ", RequiredPromptPlaceholders)}}

			Do not replace any placeholder with a path or value from this fight, even where the
			rendered prompt above shows that value.

			It must explicitly restrict edits to {workspace}.

			Put {nextPromptContract} on a line by itself where this section belongs. Do not copy this
			contract text or its marker example into the replacement; that placeholder inserts the
			current contract when the launcher renders the next round.

			Return the full template without a Markdown code fence, between these marker lines:

			{{NextPromptBegin}}
			<complete replacement prompt template>
			{{NextPromptEnd}}

			In manual mode the player will review and edit it before it is saved. Continuous
			improvement may accept a valid template automatically.
			""";
	}
}
