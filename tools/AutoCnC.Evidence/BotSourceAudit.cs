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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoCnC.Evidence
{
	public sealed class AuditFinding
	{
		public string File { get; set; }
		public int Line { get; set; }
		public string Text { get; set; }
		public string Why { get; set; }
	}

	/// <summary>
	/// Looks for a bot that has memorised one match instead of learning from it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Cross-run history is handed to the improvement agent, and an agent holding a previous
	/// match's trace will happily write <c>if (enemyBase == new CPos(50, 18))</c>. That wins the
	/// rerun and nothing else, and it is close to undetectable in a diff that also contains real
	/// work — the constant looks like tuning.
	/// </para>
	/// <para>
	/// This is a cheap mechanical smell test, not a proof: it flags map-sized coordinate pairs and
	/// map-name comparisons in strategy sources so a human or a later round can look. It is
	/// deliberately advisory. Failing a build on a regex would be worse than the problem, and a
	/// determined agent can evade any regex — the real defence is that the prompt templates forbid
	/// it in as many words and that history never reaches a running bot.
	/// </para>
	/// </remarks>
	public static class BotSourceAudit
	{
		/// <summary>A coordinate pair built from two literals, e.g. <c>new CPos(50, 18)</c>.</summary>
		static readonly Regex CellLiteral = new(
			@"new\s+(?:CPos|MPos|CVec)\s*\(\s*-?\d{1,3}\s*,\s*-?\d{1,3}\s*\)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		/// <summary>A map or opponent name compared against in code.</summary>
		static readonly Regex NameLiteral = new(
			@"(?:Map|MapName|MapTitle|MapUid|Opponent|EnemyName)\s*(?:==|\.Equals\s*\(|\.Contains\s*\()\s*""",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		public static List<AuditFinding> Scan(string directory)
		{
			var findings = new List<AuditFinding>();
			if (!Directory.Exists(directory))
				return findings;

			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
					file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var line = lines[i];
					var trimmed = line.TrimStart();

					// A comment explaining why a constant is not a memorised coordinate is not
					// itself a memorised coordinate.
					if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
						trimmed.StartsWith("///", StringComparison.Ordinal) ||
						trimmed.StartsWith('*'))
						continue;

					if (CellLiteral.IsMatch(line))
						findings.Add(Finding(directory, file, i + 1, line,
							"a literal map cell — strategy must derive positions from BattleState, " +
							"not from where things were in a previous match"));
					else if (NameLiteral.IsMatch(line))
						findings.Add(Finding(directory, file, i + 1, line,
							"a map or opponent name compared in code — strategy must not branch on " +
							"which map or opponent it drew"));
				}
			}

			return findings;
		}

		static AuditFinding Finding(string root, string file, int line, string text, string why) =>
			new()
			{
				File = Path.GetRelativePath(root, file),
				Line = line,
				Text = text.Trim(),
				Why = why
			};

		public static string Render(IReadOnlyList<AuditFinding> findings)
		{
			if (findings.Count == 0)
				return "No hardcoded map coordinates or opponent names found in the bot sources.";

			return $"{findings.Count} possible memorised constant(s) in the bot sources:\n" +
				string.Join('\n', findings.Select(f =>
					$"- {f.File}:{f.Line}  {f.Text}\n    {f.Why}"));
		}
	}
}
