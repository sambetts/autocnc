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
using System.Security.Cryptography;
using System.Text;

namespace AutoCnC.Evidence
{
	/// <summary>Which learned prompt a round was given, in a form two rounds can be compared on.</summary>
	public sealed class PromptIdentity
	{
		/// <summary>Short stable id for this prompt's structure.</summary>
		public string Id { get; init; }

		/// <summary>The learned half's section headings, in order.</summary>
		public string[] Headings { get; init; } = [];

		/// <summary>
		/// Characters of the learned half, with the injected gospel discounted.
		/// </summary>
		/// <remarks>
		/// The rendered prompt is mostly gospel — twenty thousand characters of mechanics and SDK
		/// reference inlined from version control — and reporting that total would make a lean
		/// template look bloated and hide the thing worth watching. Since the gospel is inlined
		/// verbatim from the copy kept beside the fight, subtracting its length leaves very nearly
		/// the learned half, which is the part an agent actually wrote and the part that has grown
		/// unchecked before.
		/// </remarks>
		public int Characters { get; init; }

		/// <summary>The whole rendered prompt, gospel included.</summary>
		public int RenderedCharacters { get; init; }

		public int HeadingCount => Headings.Length;
	}

	/// <summary>
	/// Identifies the learned half of a rendered prompt, so its effect can be measured.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The prompt is the only artifact in this loop that rewrites itself every round and is never
	/// graded. Bot code faces a match; the prompt faces nothing. That asymmetry is why a saved
	/// template grew to twenty-seven thousand characters of triage recipes, why one claimed an API
	/// did not exist for many rounds after it shipped, and why a rule stated only in the mutable
	/// half was silently dropped and the next round undid the work it protected. Measuring it is
	/// what turns a prompt that drifts into one that evolves.
	/// </para>
	/// <para>
	/// Identity is taken from the learned half's section headings rather than from the bytes of
	/// the file. A rendered prompt embeds paths, scores and the injected gospel, all of which
	/// differ between two rounds that were given exactly the same template — hashing the file
	/// would make every round unique and measure nothing. Headings change when a round rewrites
	/// its template and stay put when it does not, which is precisely the event worth attributing
	/// an outcome to.
	/// </para>
	/// <para>
	/// The gospel's own headings are removed first, using the copy of the mechanics reference
	/// stored beside the fight. Otherwise a change to version-controlled gospel would read as the
	/// agent having rewritten its template.
	/// </para>
	/// </remarks>
	public static class PromptFingerprint
	{
		public static PromptIdentity Read(string promptPath, string mechanicsPath)
		{
			if (!File.Exists(promptPath))
				return null;

			var prompt = File.ReadAllText(promptPath);
			var mechanics = File.Exists(mechanicsPath) ? File.ReadAllText(mechanicsPath) : null;

			var gospel = mechanics != null
				? new HashSet<string>(Headings(mechanics), StringComparer.Ordinal)
				: [];

			var headings = Headings(prompt).Where(h => !gospel.Contains(h)).ToArray();
			if (headings.Length == 0)
				return null;

			// The gospel is inlined verbatim, so subtracting its length leaves the learned half.
			// Clamped, because a template that never had the gospel inserted would otherwise
			// report a negative size.
			var learned = mechanics != null
				? Math.Max(0, prompt.Length - mechanics.Trim().Length)
				: prompt.Length;

			return new PromptIdentity
			{
				Id = Hash(string.Join('\n', headings)),
				Headings = headings,
				Characters = learned,
				RenderedCharacters = prompt.Length
			};
		}

		/// <summary>Markdown section headings, normalised so trivial edits do not fork an id.</summary>
		static IEnumerable<string> Headings(string text)
		{
			foreach (var raw in text.Split('\n'))
			{
				var line = raw.Trim();
				if (!line.StartsWith("##", StringComparison.Ordinal))
					continue;

				// Case and trailing punctuation carry no meaning here, and a round that retitles
				// a section without changing it should not read as a new prompt.
				var heading = line.TrimStart('#').Trim().TrimEnd('.', ':').ToLowerInvariant();
				if (heading.Length > 0)
					yield return heading;
			}
		}

		static string Hash(string text)
		{
			var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
			return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
		}
	}
}
