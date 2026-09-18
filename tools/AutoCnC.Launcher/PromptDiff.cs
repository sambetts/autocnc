// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCnC.Launcher
{
	/// <summary>What one line of a rendered prompt comparison is doing there.</summary>
	public enum PromptDiffKind
	{
		/// <summary>In both templates, printed only to place the changes around it.</summary>
		Context,

		/// <summary>Only in the proposal, so accepting adds it.</summary>
		Added,

		/// <summary>Only in the prompt in force, so accepting loses it.</summary>
		Removed,

		/// <summary>A stretch of unchanged lines too far from any change to be worth printing.</summary>
		Elision
	}

	/// <summary>One line of a rendered prompt comparison.</summary>
	public sealed class PromptDiffLine
	{
		public PromptDiffLine(PromptDiffKind kind, string text)
		{
			Kind = kind;
			Text = text ?? "";
		}

		public PromptDiffKind Kind { get; }

		public string Text { get; }

		/// <summary>The line with the marker a unified diff would put in front of it.</summary>
		public string Display => Kind switch
		{
			PromptDiffKind.Added => "+ " + Text,
			PromptDiffKind.Removed => "- " + Text,
			PromptDiffKind.Elision => Text,
			_ => "  " + Text
		};
	}

	/// <summary>A comparison of the prompt in force against a proposed replacement.</summary>
	public sealed class PromptDiffResult
	{
		public PromptDiffResult(IReadOnlyList<PromptDiffLine> lines, int added, int removed, int unchanged)
		{
			Lines = lines;
			Added = added;
			Removed = removed;
			Unchanged = unchanged;
		}

		public IReadOnlyList<PromptDiffLine> Lines { get; }
		public int Added { get; }
		public int Removed { get; }
		public int Unchanged { get; }

		/// <summary>True when accepting the proposal would change nothing.</summary>
		public bool Identical => Added == 0 && Removed == 0;

		/// <summary>The one-line verdict shown beside the approve and reject buttons.</summary>
		public string Summary => Identical
			? "No change: this proposal is the prompt already in force."
			: $"{Added} line(s) added, {Removed} line(s) removed.";
	}

	/// <summary>
	/// Compares the agent prompt template in force with the replacement a round proposed.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A round proposes a whole template, not an edit to one, and accepting it overwrites the only
	/// copy there is. Read as two walls of text those are indistinguishable, so the decision the
	/// player is actually being asked to make — is this round sharpening the instructions or
	/// quietly dropping a paragraph that took ten rounds to earn — cannot be made at all. Printing
	/// only the lines that differ turns it back into a decision.
	/// </para>
	/// <para>
	/// Both sides are trimmed before comparison because that is exactly what adoption does to
	/// them, and line endings are normalised because a template that came back from an agent with
	/// LF endings is not a rewrite of one saved with CRLF. Anything this reports as a change would
	/// really be a change.
	/// </para>
	/// </remarks>
	public static class PromptDiff
	{
		/// <summary>Unchanged lines kept either side of a change so it can be located.</summary>
		public const int DefaultContextLines = 3;

		/// <summary>
		/// Above this many differing lines on either side the quadratic comparison is abandoned
		/// and the templates are reported as a wholesale replacement.
		/// </summary>
		/// <remarks>
		/// Templates are capped at 30,000 characters, so this is only reachable by a proposal made
		/// almost entirely of very short lines. Answering that one honestly and cheaply beats
		/// allocating a matrix big enough to stall the window that is drawing it.
		/// </remarks>
		const int MaxComparableLines = 2000;

		public static PromptDiffResult Compare(string current, string proposed,
			int contextLines = DefaultContextLines)
		{
			var before = SplitLines(current);
			var after = SplitLines(proposed);

			var tagged = Align(before, after);
			var added = tagged.Count(line => line.Kind == PromptDiffKind.Added);
			var removed = tagged.Count(line => line.Kind == PromptDiffKind.Removed);
			var unchanged = tagged.Count - added - removed;

			return new PromptDiffResult(Elide(tagged, Math.Max(0, contextLines)),
				added, removed, unchanged);
		}

		/// <summary>Splits text into comparable lines, ignoring how it ended its lines.</summary>
		static List<string> SplitLines(string value)
		{
			var text = value?.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Replace('\r', '\n')
				.Trim();

			return string.IsNullOrEmpty(text) ? [] : [.. text.Split('\n')];
		}

		/// <summary>
		/// Pairs the two line sequences up, longest common subsequence first, so that what is
		/// reported as added and removed is only what really moved.
		/// </summary>
		static List<PromptDiffLine> Align(List<string> before, List<string> after)
		{
			// Prompts evolve a paragraph at a time, so the overwhelmingly common case is a long
			// identical head and tail around one edit. Removing them first keeps the matrix below
			// small enough that the abandonment threshold is almost never reached.
			var head = 0;
			while (head < before.Count && head < after.Count &&
				string.Equals(before[head], after[head], StringComparison.Ordinal))
				head++;

			var tail = 0;
			while (tail < before.Count - head && tail < after.Count - head &&
				string.Equals(before[before.Count - 1 - tail], after[after.Count - 1 - tail],
					StringComparison.Ordinal))
				tail++;

			var middleBefore = before.GetRange(head, before.Count - head - tail);
			var middleAfter = after.GetRange(head, after.Count - head - tail);

			var lines = new List<PromptDiffLine>(before.Count + after.Count);
			for (var index = 0; index < head; index++)
				lines.Add(new PromptDiffLine(PromptDiffKind.Context, before[index]));

			lines.AddRange(middleBefore.Count > MaxComparableLines ||
				middleAfter.Count > MaxComparableLines
				? Wholesale(middleBefore, middleAfter)
				: Interleave(middleBefore, middleAfter));

			for (var index = after.Count - tail; index < after.Count; index++)
				lines.Add(new PromptDiffLine(PromptDiffKind.Context, after[index]));

			return lines;
		}

		static IEnumerable<PromptDiffLine> Wholesale(List<string> before, List<string> after) =>
			before.Select(line => new PromptDiffLine(PromptDiffKind.Removed, line))
				.Concat(after.Select(line => new PromptDiffLine(PromptDiffKind.Added, line)));

		/// <summary>Walks the common subsequence, emitting removals before the additions that replace them.</summary>
		static IEnumerable<PromptDiffLine> Interleave(List<string> before, List<string> after)
		{
			var common = CommonLengths(before, after);
			int row = 0, column = 0;
			while (row < before.Count && column < after.Count)
			{
				if (string.Equals(before[row], after[column], StringComparison.Ordinal))
				{
					yield return new PromptDiffLine(PromptDiffKind.Context, before[row]);
					row++;
					column++;
				}
				else if (common[row + 1, column] >= common[row, column + 1])
				{
					yield return new PromptDiffLine(PromptDiffKind.Removed, before[row]);
					row++;
				}
				else
				{
					yield return new PromptDiffLine(PromptDiffKind.Added, after[column]);
					column++;
				}
			}

			for (; row < before.Count; row++)
				yield return new PromptDiffLine(PromptDiffKind.Removed, before[row]);

			for (; column < after.Count; column++)
				yield return new PromptDiffLine(PromptDiffKind.Added, after[column]);
		}

		/// <summary>Longest-common-subsequence lengths for every remaining pair of tails.</summary>
		static int[,] CommonLengths(List<string> before, List<string> after)
		{
			var lengths = new int[before.Count + 1, after.Count + 1];
			for (var row = before.Count - 1; row >= 0; row--)
				for (var column = after.Count - 1; column >= 0; column--)
					lengths[row, column] =
						string.Equals(before[row], after[column], StringComparison.Ordinal)
							? lengths[row + 1, column + 1] + 1
							: Math.Max(lengths[row + 1, column], lengths[row, column + 1]);

			return lengths;
		}

		/// <summary>
		/// Replaces the unchanged stretches nobody needs to read with a note saying how long they
		/// were, so a one-line change to a three-hundred-line prompt reads as a one-line change.
		/// </summary>
		static List<PromptDiffLine> Elide(List<PromptDiffLine> lines, int contextLines)
		{
			var keep = new bool[lines.Count];
			for (var index = 0; index < lines.Count; index++)
			{
				if (lines[index].Kind == PromptDiffKind.Context)
					continue;

				for (var near = Math.Max(0, index - contextLines);
					near <= Math.Min(lines.Count - 1, index + contextLines);
					near++)
					keep[near] = true;
			}

			var elided = new List<PromptDiffLine>(lines.Count);
			var dropped = 0;
			for (var index = 0; index < lines.Count; index++)
			{
				if (keep[index])
				{
					if (dropped > 0)
					{
						elided.Add(Gap(dropped));
						dropped = 0;
					}

					elided.Add(lines[index]);
				}
				else
					dropped++;
			}

			if (dropped > 0)
				elided.Add(Gap(dropped));

			return elided;
		}

		static PromptDiffLine Gap(int count) => new(PromptDiffKind.Elision,
			$"… {count} unchanged line{(count == 1 ? "" : "s")} …");
	}
}
