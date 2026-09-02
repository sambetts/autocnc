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
			"{nextPromptContract}"
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
			string promptTemplate)
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
			File.WriteAllText(run.PromptPath, RenderPrompt(run, promptTemplate));
		}

		public static void Prepare(TrainingRun run, string gameGuidePath, string gameRulesPath,
			string promptTemplate, string command, IReadOnlyList<string> arguments)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new InvalidOperationException("The agent command is empty.");

			PrepareContext(run, gameGuidePath, gameRulesPath, promptTemplate);

			var configuration = new TrainingAgentConfiguration { Command = command.Trim() };
			configuration.Arguments.AddRange(arguments is { Count: > 0 } ? arguments : DefaultArguments);
			File.WriteAllText(run.AgentConfigurationPath,
				JsonSerializer.Serialize(configuration, JsonOptions));
		}

		public static string FindSuggestedNextPrompt(IReadOnlyList<string> output)
		{
			if (output is not { Count: > 0 })
				return null;

			var begin = -1;
			for (var i = output.Count - 1; i >= 0; i--)
			{
				var line = output[i] ?? "";
				if (line.Contains(NextPromptEnd, StringComparison.OrdinalIgnoreCase))
				{
					for (var j = i - 1; j >= 0; j--)
						if ((output[j] ?? "").Contains(NextPromptBegin, StringComparison.OrdinalIgnoreCase))
						{
							begin = j;
							var template = string.Join(Environment.NewLine,
								output.Skip(begin + 1).Take(i - begin - 1)).Trim();
							if (template.StartsWith("```", StringComparison.Ordinal))
								template = template[(template.IndexOf('\n') + 1)..];
							if (template.EndsWith("```", StringComparison.Ordinal))
								template = template[..^3].TrimEnd();

							return template.Length == 0 ? null :
								template.Length <= 30_000 ? template : template[..30_000];
						}

					break;
				}
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

			if (!template.Contains("edit only", StringComparison.OrdinalIgnoreCase))
			{
				error = "It must explicitly say to edit only the {workspace} bot workspace.";
				return false;
			}

			error = null;
			return true;
		}

		public static string RenderPrompt(TrainingRun run, string template)
		{
			var result = run.Manifest.Result;
			var battle = run.Manifest.Battle;
			var score = result?.Players is { Count: > 0 }
				? string.Join("; ", result.Players.Select(p =>
					$"{p.Name}: {p.Outcome ?? "undecided"}, army={p.ArmyValue}, buildings={p.Buildings}, killed={p.Killed}, lost={p.Lost}, cash={p.Cash}"))
				: "no final score was recorded";

			var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["{workspace}"] = run.Manifest.BotDirectory,
				["{gameGuide}"] = run.GameGuidePath,
				["{gameRules}"] = run.GameRulesPath,
				["{fightManifest}"] = run.FightManifestPath,
				["{battleLog}"] = run.BattleLogPath,
				["{telemetry}"] = run.TelemetryPath,
				["{decisionTrace}"] = run.DecisionTracePath,
				["{replay}"] = File.Exists(run.ReplayPath) ? run.ReplayPath : "not captured",
				["{battle}"] = $"map={battle?.Map}, difficulty={battle?.Difficulty}, opponents={battle?.Opponents}, " +
					$"faction={battle?.Faction}, opponent faction={battle?.BotFaction}, speed={battle?.GameSpeed}",
				["{result}"] = $"{result?.Outcome ?? "unknown"} after {result?.DurationSeconds ?? 0} game seconds; {score}",
				["{sourceRevision}"] = run.Manifest.SourceRevision,
				["{nextPromptContract}"] = NextPromptContract()
			};

			var prompt = template;
			foreach (var replacement in replacements)
				prompt = prompt.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);

			return prompt;
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

			It must explicitly restrict edits to {workspace}.

			Return the full template without a Markdown code fence, between these marker lines:

			{{NextPromptBegin}}
			<complete replacement prompt template>
			{{NextPromptEnd}}

			The player will review and edit it before it is saved.
			""";
	}
}
