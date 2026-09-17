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

using System.Collections.Generic;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class CsvTests
	{
		[Test]
		public void SplitLineHandlesQuotedCommaAndDoubledQuote()
		{
			var fields = Csv.SplitLine("alpha,\"bravo, says \"\"hello\"\"\",charlie");

			Assert.That(fields, Is.EqualTo(new[] { "alpha", "bravo, says \"hello\"", "charlie" }));
		}

		[Test]
		public void EscapeRoundTripsThroughSplitLine()
		{
			var value = "look, \"quoted\" value";

			var fields = Csv.SplitLine(Csv.Escape(value));

			Assert.That(fields, Is.EqualTo(new[] { value }));
		}

		[Test]
		public void DetailsParsesDamageHitsAndHealth()
		{
			var details = Csv.Details("damage=10244 hits=17 health=9");

			Assert.That(Csv.DetailInt(details, "damage"), Is.EqualTo(10244));
			Assert.That(Csv.DetailInt(details, "hits"), Is.EqualTo(17));
			Assert.That(Csv.DetailInt(details, "health"), Is.EqualTo(9));
		}

		[Test]
		public void FieldReturnsEmptyForMissingAppendOnlyColumn()
		{
			var row = new[] { "10", "built" };
			var index = Csv.Index(new[] { "seconds", "event" });

			var value = Csv.Field(row, index, "detail");

			Assert.That(value, Is.EqualTo(""));
		}
	}
}
