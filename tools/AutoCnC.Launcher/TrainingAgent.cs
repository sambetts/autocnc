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
using System.Text.Json.Serialization;

namespace AutoCnC.Launcher
{
	public sealed class TrainingAgentConfiguration
	{
		public string Command { get; set; }
		public List<string> Arguments { get; set; } = [];

		/// <summary>Text written to the agent's standard input, or null when it takes none.</summary>
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string Stdin { get; set; }
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
			"{gameMechanics}",
			"{gameGuide}",
			"{gameRules}",
			"{fightManifest}",
			"{battleLog}",
			"{telemetry}",
			"{decisionTrace}",

			// Derived evidence. These are what a round should read first; the raw records above
			// are for the questions they cannot answer.
			"{summary}",
			"{units}",
			"{mapFacts}",
			"{checks}",
			"{checkResults}",
			"{trend}",

			// Generated prose, filled in after the evidence has been derived. These replace two
			// sections rounds used to hand-maintain and the next round verified by eye, if at all.
			"{checkReport}",
			"{trendReport}",
			"{botAudit}",

			"{battle}",
			"{result}",
			"{sourceRevision}",
			NextPromptContractPlaceholder
		];

		static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

		/// <summary>
		/// The rendered prompt goes in on standard input rather than in <c>-p</c>.
		/// </summary>
		/// <remarks>
		/// Windows caps a process command line at 32,767 characters. The prompt inlines the
		/// mechanics gospel and passed that on its own, so every improvement died before the agent
		/// started with Windows' misleading "The filename or extension is too long". Standard input
		/// has no such limit, and the prompt is only going to keep growing.
		/// </remarks>
		public const string DefaultStdin = "{prompt}";

		public static readonly string[] DefaultArguments =
		[
			"--session-id", "{sessionId}",
			"--allow-all-tools",
			"--no-ask-user",
			"--no-custom-instructions",
			"--no-remote-export",
			"--add-dir", "{evidence}"
		];

		/// <summary>Copilot defaults from earlier rounds, newest first, upgraded on load.</summary>
		static readonly string[][] SupersededDefaultArguments =
		[
			[
				"--allow-all-tools",
				"--no-ask-user",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			],
			[
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			],
			[
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-color",
				"--no-custom-instructions",
				"--no-remote-export",
				"--screen-reader",
				"--add-dir", "{evidence}"
			],
			[
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-color",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			],
			[
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-color",
				"--no-custom-instructions",
				"--no-remote-export",
				"--silent"
			]
		];

		public static string[] UpgradeDefaultArguments(string command, string[] arguments)
		{
			if (arguments is not { Length: > 0 })
				return [.. DefaultArguments];

			if (!string.Equals(command, "copilot", StringComparison.OrdinalIgnoreCase))
				return arguments;

			return SupersededDefaultArguments.Any(superseded =>
				arguments.SequenceEqual(superseded, StringComparer.Ordinal))
				? [.. DefaultArguments]
				: arguments;
		}

		/// <summary>Whether an argument carries the prompt itself, rather than a path to it.</summary>
		public static bool CarriesPrompt(IEnumerable<string> arguments) =>
			arguments is not null && arguments.Any(argument =>
				argument is not null &&
				(argument.Contains("{prompt}", StringComparison.Ordinal) ||
					argument.Contains("{promptFile}", StringComparison.Ordinal)));

		/// <summary>
		/// Settles which channel carries the prompt, given a possibly older saved configuration.
		/// </summary>
		/// <remarks>
		/// The prompt travels through exactly one channel. A configuration that still names it in
		/// its arguments keeps doing so, because that is what the player asked for and only they
		/// know whether their agent reads standard input. Everything else gets it on standard
		/// input, which is the only channel that a growing prompt cannot overflow.
		/// </remarks>
		public static string UpgradePromptChannel(string[] arguments, string stdin) =>
			CarriesPrompt(arguments) ? null :
			string.IsNullOrWhiteSpace(stdin) ? DefaultStdin : stdin;

		public static void PrepareContext(TrainingRun run, string gameGuidePath, string mechanicsPath,
			string gameRulesPath, string promptTemplate, string recoveryContext = null)
		{
			if (!run.IsEditable)
				throw new InvalidOperationException("AI improvement requires a battle bot project, not a prebuilt assembly.");

			if (!File.Exists(gameGuidePath))
				throw new FileNotFoundException("The agent game guide is missing.", gameGuidePath);

			if (!File.Exists(mechanicsPath))
				throw new FileNotFoundException("The mechanics and SDK gospel is missing.", mechanicsPath);

			if (!File.Exists(gameRulesPath))
				throw new FileNotFoundException("The resolved game-rules snapshot is missing.", gameRulesPath);

			if (!ValidatePromptTemplate(promptTemplate, out var error))
				throw new InvalidOperationException("The saved agent prompt is invalid: " + error);

			Directory.CreateDirectory(run.EvidenceDirectory);
			run.EnsureAgentSessionId();
			File.Copy(gameGuidePath, run.GameGuidePath, true);
			File.Copy(mechanicsPath, run.MechanicsPath, true);
			if (!string.Equals(Path.GetFullPath(gameRulesPath), Path.GetFullPath(run.GameRulesPath),
				StringComparison.OrdinalIgnoreCase))
				File.Copy(gameRulesPath, run.GameRulesPath, true);
			run.ExportFightManifest();
			File.WriteAllText(run.PromptPath,
				RenderPrompt(run, promptTemplate, recoveryContext));
		}

		public static void Prepare(TrainingRun run, string gameGuidePath, string mechanicsPath,
			string gameRulesPath, string promptTemplate, string command, IReadOnlyList<string> arguments,
			string stdin = null, string recoveryContext = null)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new InvalidOperationException("The agent command is empty.");

			PrepareContext(run, gameGuidePath, mechanicsPath, gameRulesPath, promptTemplate, recoveryContext);
			WriteConfiguration(run, command, arguments, stdin);
		}

		/// <summary>
		/// Records which agent this fight talks to, without preparing a whole evidence packet.
		/// </summary>
		/// <remarks>
		/// Split out because a conversation can start before the first improvement round — steering
		/// the agent before it reads anything is most of the value of talking early — and without
		/// this the first message would fall back to a stock agent rather than the one the player
		/// configured.
		/// </remarks>
		public static void WriteConfiguration(TrainingRun run, string command,
			IReadOnlyList<string> arguments, string stdin = null)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new InvalidOperationException("The agent command is empty.");

			var configuration = new TrainingAgentConfiguration { Command = command.Trim() };
			configuration.Arguments.AddRange(arguments is { Count: > 0 } ? arguments : DefaultArguments);

			// Resolved here rather than trusted from the caller, so a configuration that names the
			// prompt nowhere still hands it over instead of starting the agent empty-handed.
			configuration.Stdin = UpgradePromptChannel([.. configuration.Arguments], stdin);
			Directory.CreateDirectory(run.RunDirectory);
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

		/// <summary>
		/// Puts <c>{gameMechanics}</c> back into a template that has lost it, or never had it.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Two kinds of template arrive without it: one saved before the prompt was split into a
		/// gospel half and a learned half, and one proposed by a round that simply forgot. Both are
		/// otherwise good work — an evolved template can be many rounds of learning — so repairing
		/// them beats rejecting them, and rejecting them is worse than it sounds: the player meets
		/// it as a modal refusal at the moment they press the button.
		/// </para>
		/// <para>
		/// Inserted above the first section so the mechanics are read before any advice that might
		/// contradict them, and below the opening line so the task still comes first.
		/// </para>
		/// </remarks>
		public static string EnsureMechanicsPlaceholder(string template)
		{
			if (string.IsNullOrWhiteSpace(template) ||
				template.Contains("{gameMechanics}", StringComparison.Ordinal))
				return template;

			var lines = SplitLines(template).ToList();
			string[] reference =
			[
				"## Mechanics and SDK reference",
				"",
				"{gameMechanics}",
				""
			];

			var at = lines.FindIndex(line => line.StartsWith("## ", StringComparison.Ordinal));
			if (at < 0)
				at = lines.FindIndex(line =>
					string.Equals(line.Trim(), NextPromptContractPlaceholder, StringComparison.Ordinal));

			if (at < 0)
				lines.AddRange(reference);
			else
				lines.InsertRange(at, reference);

			return string.Join(Environment.NewLine, lines);
		}

		public static bool ValidatePromptTemplate(string template, out string error)		{
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
			if (failedAttempt?.ExitCode is not int exitCode || exitCode == 0 ||
				failedAttempt.Cancelled)
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
				? "independent build verification"
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

				First reproduce the failure with the bot's build command. Repair the current changes
				until the build exits successfully. If generated `bin`/`obj` output is corrupt, clean
				it and rebuild before diagnosing source. Only revisit the strategic change if the
				build proves it cannot compile as written. Do not add unit tests.
				""";
		}

		/// <summary>
		/// What to tell the next attempt about a run the player stopped on purpose.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Nothing went wrong, so there is nothing to diagnose — and saying otherwise is expensive
		/// as well as untrue, because an agent told to investigate a failure will go and read a
		/// transcript, rebuild and reason about a fault that does not exist before it
		/// starts on the work you actually wanted.
		/// </para>
		/// <para>
		/// The one thing it does need is a warning that the workspace may not be clean. A run
		/// stopped halfway can leave a half-finished edit behind, and an agent that believes it is
		/// starting from the last good state would build on top of it without ever looking.
		/// Silence is only safe when the stopped attempt changed nothing, which is why this
		/// returns null in that case rather than an empty reassurance.
		/// </para>
		/// </remarks>
		public static string BuildCancellationContext(TrainingAgentResult cancelledAttempt,
			string archivedTranscript)
		{
			if (cancelledAttempt is not { Cancelled: true } ||
				cancelledAttempt.RestoredUtc != null ||
				cancelledAttempt.ChangeCount == 0)
				return null;

			var edits = cancelledAttempt.ChangeCount < 0
				? "It may have left edits in the bot workspace — that could not be determined, so " +
					"assume there are some."
				: $"It had already changed {cancelledAttempt.ChangeCount} file(s) in the bot " +
					"workspace, and those edits are still there, possibly half-finished.";

			var transcript = string.IsNullOrEmpty(archivedTranscript)
				? ""
				: $"""

					What it had done before it was stopped is in `{archivedTranscript}`.
					""";

			return $"""
				# The previous attempt was stopped

				The player stopped the previous improvement before it finished. It did not fail and
				there is nothing to diagnose, so do not go looking for a fault.

				{edits} Read the current state of any file you intend to change rather than assuming
				it is the last known-good version. Finish or replace that work as the task below
				requires, and make sure the build passes before you are done.
				{transcript}
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
					$"{p.Name}: {p.Outcome ?? "undecided"}, army={p.ArmyValue} (peak {p.PeakArmyValue}), " +
					$"units={p.Units} (peak {p.PeakUnits}), buildings={p.Buildings} (peak {p.PeakBuildings}), " +
					$"killed={p.Killed}, lost={p.Lost}, cash={p.Cash}"))
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

				// Paths only. These files are derived by scripts/train-bot.ps1 after this prompt
				// is written and before the agent is called, so naming them here is safe, but
				// their contents cannot be read yet. The generated reports drawn from them —
				// {checkReport}, {trendReport}, {botAudit} — are filled in by that script, which
				// is why they are absent from this list and still literal at this point.
				("{summary}", run.SummaryPath),
				("{units}", run.UnitsPath),
				("{mapFacts}", run.MapFactsPath),
				("{checks}", run.ChecksPath),
				("{checkResults}", run.CheckResultsPath),
				("{trend}", run.TrendPath),

				("{replay}", File.Exists(run.ReplayPath) ? run.ReplayPath : "not captured"),
				("{battle}", $"map={battle?.Map}, difficulty={battle?.Difficulty}, opponents={battle?.Opponents}, " +
					$"faction={battle?.Faction}, opponent faction={battle?.BotFaction}, speed={battle?.GameSpeed}, " +
					$"execution={battle?.ExecutionMode ?? BattleExecutionModes.Rendered}"),
				("{result}", $"{result?.Outcome ?? "unknown"} after {result?.DurationSeconds ?? 0} game seconds; {score}" +
					Environment.NewLine + playerFeedback),
				("{sourceRevision}", run.Manifest.SourceRevision),

				// Inlined rather than referenced: the gospel is the half of the prompt the agent
				// must not be free to skip or to contradict, and a path in a prompt is only a
				// suggestion. Second to last so the contract's placeholders stay literal.
				("{gameMechanics}", Mechanics(run)),

				// This must stay last so placeholders inside the contract remain literal.
				(NextPromptContractPlaceholder, NextPromptContract())
			];
		}

		/// <summary>
		/// The mechanics and SDK gospel, read from the copy kept with the fight.
		/// </summary>
		/// <remarks>
		/// Reading the run's own copy rather than the repository keeps a rendered prompt honest
		/// when it is re-read months later: it shows the gospel that fight was actually given, not
		/// whatever the working tree says today.
		/// </remarks>
		static string Mechanics(TrainingRun run)
		{
			try
			{
				if (File.Exists(run.MechanicsPath))
					return File.ReadAllText(run.MechanicsPath).Trim();
			}
			catch (IOException)
			{
			}

			return "The mechanics and SDK reference could not be read for this run.";
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

			return EnsureMechanicsPlaceholder(
				EnsureRequiredPlaceholders(
					string.Join(Environment.NewLine, lines).Trim())).Trim();
		}

		/// <summary>
		/// Puts back any required placeholder the proposed template dropped.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Repairing beats rejecting. A round is given its contract at the start and writes its
		/// replacement at the end, so whenever the required set grows, the round already in flight
		/// was told an older list and cannot know about the new entries — and refusing its work for
		/// that reason throws away a whole round of analysis over a formatting rule the agent was
		/// never shown.
		/// </para>
		/// <para>
		/// The same applies when an agent simply forgets one. The placeholders are how evidence
		/// reaches the next round at all, so a template missing <c>{summary}</c> silently costs
		/// every future round its precomputed analysis. That is far too expensive a failure to
		/// leave to the agent remembering a list.
		/// </para>
		/// </remarks>
		internal static string EnsureRequiredPlaceholders(string template)
		{
			var missing = RequiredPromptPlaceholders
				.Where(placeholder => placeholder != NextPromptContractPlaceholder)
				.Where(placeholder => placeholder != "{gameMechanics}")
				.Where(placeholder => !template.Contains(placeholder, StringComparison.Ordinal))
				.ToArray();

			if (missing.Length == 0)
				return template;

			var section = new List<string>
			{
				"",
				"## Evidence (restored by the launcher)",
				"",
				"The template proposed for this round left these out. They are how the harness",
				"reaches the next round, so they have been put back rather than the round discarded.",
				""
			};

			section.AddRange(missing.Select(placeholder => "- " + PlaceholderDescription(placeholder)));

			var lines = SplitLines(template).ToList();
			var contract = lines.FindIndex(line =>
				string.Equals(line.Trim(), NextPromptContractPlaceholder, StringComparison.Ordinal));

			if (contract >= 0)
				lines.InsertRange(contract, section);
			else
				lines.AddRange(section);

			return string.Join(Environment.NewLine, lines);
		}

		static string PlaceholderDescription(string placeholder) => placeholder switch
		{
			"{summary}" => "Fight summary, precomputed and under 20 KB — read this first: {summary}",
			"{units}" => "Unit ledger, one row per unit lifecycle: {units}",
			"{mapFacts}" => "Map facts, including the random seed this match ran on: {mapFacts}",
			"{checks}" => "Checks the previous round wrote: {checks}",
			"{checkResults}" => "The harness's verdict on those checks: {checkResults}",
			"{trend}" => "Cross-run trend, including what each prompt revision did: {trend}",
			"{checkReport}" => "Checks carried in from the previous round:" +
				Environment.NewLine + Environment.NewLine + "{checkReport}",
			"{trendReport}" => "How this bot is trending:" +
				Environment.NewLine + Environment.NewLine + "{trendReport}",
			"{botAudit}" => "Hardcoded-constant audit of the bot sources:" +
				Environment.NewLine + Environment.NewLine + "{botAudit}",
			"{workspace}" => "Edit only the bot workspace: {workspace}",
			"{gameGuide}" => "Game and bot guide: {gameGuide}",
			"{gameRules}" => "Resolved actor and weapon stats: {gameRules}",
			"{fightManifest}" => "Fight manifest: {fightManifest}",
			"{battleLog}" => "Battle log, what this bot could observe: {battleLog}",
			"{telemetry}" => "Match telemetry, both sides' curves: {telemetry}",
			"{decisionTrace}" => "Decision trace: {decisionTrace}",
			"{battle}" => "Fight configuration: {battle}",
			"{result}" => "Result and player assessment: {result}",
			"{sourceRevision}" => "Source revision before the fight: {sourceRevision}",
			_ => placeholder
		};

		static bool IsPathPlaceholder(string placeholder) =>
			placeholder is "{workspace}" or "{gameGuide}" or "{gameRules}" or
				"{fightManifest}" or "{battleLog}" or "{telemetry}" or "{decisionTrace}" or
				"{replay}" or "{summary}" or "{units}" or "{mapFacts}" or "{checks}" or
				"{checkResults}" or "{trend}";

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

			The prompt has two halves and you are only writing one of them. The mechanics and SDK
			reference above is **gospel**: it is injected from version control by the launcher, it is
			generated from the compiled assemblies, and it is not yours to edit. Your template is the
			**learned** half — how to read this bot's evidence, what has already been diagnosed, and
			what to try next.

			So do not restate game mechanics, the `ModeContext` surface, `UnitAction` values,
			`UnitDecision` factories or engine constants in your template. Write {gameMechanics} on a
			line by itself where that reference belongs and the launcher will insert the current one.
			Copying those facts into your template is how they go stale: a template that claimed "there
			is no resource/tiberium sensing API" outlived the API by many rounds and steered every one
			of them away from the fix its harvesters needed.

			The template must retain these placeholders exactly so the launcher can insert fresh data:
			{{string.Join(", ", RequiredPromptPlaceholders)}}

			Do not replace any placeholder with a path or value from this fight, even where the
			rendered prompt above shows that value.

			Do not write triage recipes. {summary} and {units} are computed by the harness from the
			raw records, by tested code, before you are called: the per-unit-type ledger with
			credits per kill and share of spend, the economy series, the production and doctrine
			tables, the engagement matrix, the loss clusters and the telemetry crossover are all
			already there. A template that tells the next round to re-derive those from
			{decisionTrace} is spending its budget on arithmetic that has already been done, which
			is the single largest thing that was wrong with this loop. Read {decisionTrace} only
			for a question the summary genuinely cannot answer, and say which.

			Put {checkReport} and {trendReport} on lines by themselves where those sections belong.
			They are generated: {checkReport} is the harness evaluating the checks the previous
			round wrote, and {trendReport} is the cross-run comparison, including what each prompt
			revision did to the rounds it steered. Do not write either by hand, and do not maintain
			a prose list of "already diagnosed, verify this" — that list is what checks.json is for.

			Keep a section telling the next round to write checks.json into {workspace} before it
			finishes, and to give any new code path a reason literal nothing else uses so a
			`reason:` check can prove it ran.

			Your template is measured. Each run records which prompt steered it, and the trend
			reports the mean fitness change from the round a prompt was given to the round after
			it. Templates here have grown to twenty-seven thousand characters of recipes and lost
			fitness doing it. Make yours shorter and more specific than this one, not longer; if
			the report shows the current template losing fitness across several rounds, prefer
			reverting toward what came before it over adding more advice on top.

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
