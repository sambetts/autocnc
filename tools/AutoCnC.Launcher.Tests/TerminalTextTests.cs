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
	}
}
