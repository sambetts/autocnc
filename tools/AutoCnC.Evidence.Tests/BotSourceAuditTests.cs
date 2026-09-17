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

using System.IO;
using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class BotSourceAuditTests : EvidenceTestBase
	{
		[Test]
		public void ScanFlagsActiveLiteralCoordinatesButIgnoresCommentsBinAndObj()
		{
			WriteFile("Bot.cs", "public sealed class Bot\n{\n\tvoid Go()\n\t{\n\t\tvar target = new CPos(50, 18);\n\t}\n}\n");
			WriteFile("Commented.cs", "public sealed class Commented\n{\n\t// var target = new CPos(50, 18);\n}\n");
			WriteFile(Path.Combine("obj", "Generated.cs"), "public sealed class Generated { object P = new CPos(50, 18); }\n");
			WriteFile(Path.Combine("bin", "Generated.cs"), "public sealed class Binary { object P = new CPos(50, 18); }\n");

			var findings = BotSourceAudit.Scan(TempDirectory);

			Assert.That(findings.Count, Is.EqualTo(1));
			Assert.That(findings.Single().File, Is.EqualTo("Bot.cs"));
			Assert.That(findings.Single().Text, Does.Contain("new CPos(50, 18)"));
		}

		[Test]
		public void ScanFlagsMapNameComparisons()
		{
			WriteFile("Bot.cs", "public sealed class Bot\n{\n\tbool OnMap(string MapName) => MapName == \"foo\";\n}\n");

			var findings = BotSourceAudit.Scan(TempDirectory);

			Assert.That(findings.Count, Is.EqualTo(1));
			Assert.That(findings.Single().Text, Does.Contain("MapName == \"foo\""));
		}

		[Test]
		public void ScanIgnoresXmlDocComments()
		{
			WriteFile("DocComment.cs", "public sealed class Bot\n{\n\t/// new CPos(50, 18) documents an old replay\n\tvoid Go() { }\n}\n");

			var findings = BotSourceAudit.Scan(TempDirectory);

			Assert.That(findings.Count, Is.EqualTo(0));
		}

		[Test]
		public void ScanSkipsFilesUnderBin()
		{
			WriteFile(Path.Combine("bin", "Generated.cs"), "public sealed class Generated\n{\n\tobject P = new CPos(50, 18);\n}\n");

			var findings = BotSourceAudit.Scan(TempDirectory);

			Assert.That(findings.Count, Is.EqualTo(0));
		}

		[Test]
		public void CleanSourcesProduceNoFindingsAndRenderSaysSo()
		{
			WriteFile("Clean.cs", "public sealed class Clean\n{\n\tvoid Go(int x, int y) { }\n}\n");

			var findings = BotSourceAudit.Scan(TempDirectory);
			var rendered = BotSourceAudit.Render(findings);

			Assert.That(findings.Count, Is.EqualTo(0));
			Assert.That(rendered, Is.EqualTo("No hardcoded map coordinates or opponent names found in the bot sources."));
		}
	}
}
