#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TerminalTextTests
	{
		[Test]
		public void AnsiStyleBecomesColoredSpansWithoutPollutingPlainText()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine("\x1B[36;1m├─ inspect rules →\x1B[0m plain");

			Assert.That(line.PlainText, Is.EqualTo("├─ inspect rules → plain"));
			Assert.That(line.Spans, Has.Count.EqualTo(2));
			Assert.That(line.Spans[0].Style.Foreground, Is.Not.Null);
			Assert.That(line.Spans[0].Style.Bold, Is.True);
			Assert.That(line.Spans[1].Style.Foreground, Is.Null);
			Assert.That(line.Spans[1].Style.Bold, Is.False);
		}

		[Test]
		public void OscLinksAndCursorControlsAreRemoved()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine(
				"\x1B]8;;https://example.test\x1B\\label\x1B]8;;\x1B\\\x1B[2K");

			Assert.That(line.PlainText, Is.EqualTo("label"));
		}

		[Test]
		public void AnsiTextKeepsTheOriginalColourCodesVerbatim()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine("\x1B[36;1m├─ inspect rules →\x1B[0m plain");

			// Forwarded rather than rebuilt from the parsed spans, so the terminal's own palette
			// still decides what cyan looks like.
			Assert.That(line.AnsiText, Is.EqualTo("\x1B[36;1m├─ inspect rules →\x1B[0m plain\x1B[0m"));
		}

		[Test]
		public void AnsiTextDropsEverythingThatIsNotColour()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine(
				"\x1B]8;;https://example.test\x1B\\\x1B[2K\x1B[31mred\x1B[0m\x1B[1;5H tail");

			Assert.That(line.AnsiText, Is.EqualTo("\x1B[31mred\x1B[0m tail\x1B[0m"),
				"Cursor moves and hyperlinks mean nothing in a line-by-line log.");
		}

		[Test]
		public void AnsiTextIsPlainWhenTheLineHasNoColour()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine("==> Building engine");

			Assert.That(line.AnsiText, Is.EqualTo("==> Building engine"));
			Assert.That(line.AnsiText, Is.EqualTo(line.PlainText));
		}

		[Test]
		public void BackspaceErasesFromColouredTextWithoutEatingItsEscape()
		{
			var parser = new TerminalTextParser();

			var line = parser.ParseLine("\x1B[32mdone!\b\x1B[0m");

			Assert.That(line.PlainText, Is.EqualTo("done"));
			Assert.That(line.AnsiText, Is.EqualTo("\x1B[32mdone\x1B[0m\x1B[0m"));
		}

		[Test]
		public void PlainLinesCarryTheirTextAsAnsi()
		{
			var line = TerminalLine.Plain("no escapes here");

			Assert.That(line.AnsiText, Is.EqualTo("no escapes here"));
		}
	}
}
