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
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class PromptDiffTests
	{
		[Test]
		public void AReplacedLineReadsAsOneRemovalAndOneAdditionInPlace()
		{
			var diff = PromptDiff.Compare(
				Lines("Improve the bot.", "Edit only {workspace}.", "Report what changed."),
				Lines("Improve the bot.", "Edit only {workspace}, economy first.", "Report what changed."));

			Assert.That(diff.Lines.Select(line => line.Display), Is.EqualTo(new[]
			{
				"  Improve the bot.",
				"- Edit only {workspace}.",
				"+ Edit only {workspace}, economy first.",
				"  Report what changed."
			}));
			Assert.That(diff.Added, Is.EqualTo(1));
			Assert.That(diff.Removed, Is.EqualTo(1));
			Assert.That(diff.Identical, Is.False);
			Assert.That(diff.Summary, Is.EqualTo("1 line(s) added, 1 line(s) removed."));
		}

		[Test]
		public void APromptThatOnlyChangedItsLineEndingsOrOuterWhitespaceHasNotChanged()
		{
			// Both sides are trimmed exactly as adoption trims them, so whitespace around the
			// whole template is not a change — but indentation within it survives and is.
			var diff = PromptDiff.Compare(
				"Improve the bot.\r\nEdit only {workspace}.",
				"\nImprove the bot.\n  Edit only {workspace}.\n\n");

			Assert.That(diff.Identical, Is.False);
			Assert.That(diff.Lines.Select(line => line.Display), Is.EqualTo(new[]
			{
				"  Improve the bot.",
				"- Edit only {workspace}.",
				"+   Edit only {workspace}."
			}));

			var same = PromptDiff.Compare(
				"Improve the bot.\r\nEdit only {workspace}.",
				"\nImprove the bot.\nEdit only {workspace}.\n\n");

			Assert.That(same.Identical, Is.True);
			Assert.That(same.Added, Is.Zero);
			Assert.That(same.Removed, Is.Zero);
			Assert.That(same.Summary, Does.Contain("No change"));
		}

		[Test]
		public void AOneLineChangeInALongPromptDoesNotPrintTheWholePrompt()
		{
			var before = Enumerable.Range(0, 40).Select(index => $"Line {index}").ToArray();
			var after = before.ToArray();
			after[20] = "Line 20, rewritten";

			var diff = PromptDiff.Compare(Lines(before), Lines(after));

			Assert.That(diff.Added, Is.EqualTo(1));
			Assert.That(diff.Removed, Is.EqualTo(1));
			Assert.That(diff.Unchanged, Is.EqualTo(39));

			var displayed = diff.Lines.Select(line => line.Display).ToArray();
			Assert.That(displayed, Has.Length.LessThan(15),
				"a reader asked to judge one line should not be handed forty");
			Assert.That(displayed, Does.Contain("- Line 20"));
			Assert.That(displayed, Does.Contain("+ Line 20, rewritten"));
			Assert.That(displayed, Does.Contain("  Line 17"), "changes keep their context");
			Assert.That(displayed, Does.Not.Contain("  Line 5"));
			Assert.That(displayed[0], Does.Contain("unchanged lines"));
			Assert.That(displayed[^1], Does.Contain("unchanged lines"));
			Assert.That(diff.Lines.Count(line => line.Kind == PromptDiffKind.Elision),
				Is.EqualTo(2));
		}

		[Test]
		public void TheFirstPromptEverAdoptedIsAllAddition()
		{
			var diff = PromptDiff.Compare(null, Lines("Improve the bot.", "Edit only {workspace}."));

			Assert.That(diff.Removed, Is.Zero);
			Assert.That(diff.Added, Is.EqualTo(2));
			Assert.That(diff.Lines.Select(line => line.Display), Is.EqualTo(new[]
			{
				"+ Improve the bot.",
				"+ Edit only {workspace}."
			}));
		}

		[Test]
		public void AParagraphDroppedAndOneAppendedAreReportedSeparately()
		{
			var diff = PromptDiff.Compare(
				Lines("Head.", "Earned advice.", "Tail."),
				Lines("Head.", "Tail.", "New advice."));

			Assert.That(diff.Lines.Select(line => line.Display), Is.EqualTo(new[]
			{
				"  Head.",
				"- Earned advice.",
				"  Tail.",
				"+ New advice."
			}), "a removal must not be disguised as part of an unrelated addition");
		}

		[Test]
		public void TwoPromptsSharingNoLinesAreReportedWithoutStalling()
		{
			var before = Lines(Enumerable.Range(0, 3000).Select(index => $"a{index}").ToArray());
			var after = Lines(Enumerable.Range(0, 3000).Select(index => $"b{index}").ToArray());

			var diff = PromptDiff.Compare(before, after);

			Assert.That(diff.Removed, Is.EqualTo(3000));
			Assert.That(diff.Added, Is.EqualTo(3000));
		}

		static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);
	}
}
