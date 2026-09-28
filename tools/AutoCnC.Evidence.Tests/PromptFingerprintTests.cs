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
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class PromptFingerprintTests
	{
		string directory;

		[SetUp]
		public void SetUp()
		{
			directory = Path.Combine(Path.GetTempPath(), "autocnc-prompt-" + Path.GetRandomFileName());
			Directory.CreateDirectory(directory);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}

		/// <summary>
		/// Two rounds given the same template must share an id even though their rendered prompts
		/// differ — otherwise every round is unique and nothing can be attributed to a prompt.
		/// </summary>
		[Test]
		public void TheSameTemplateRenderedForTwoFightsKeepsOneIdentity()
		{
			var first = Write("a.txt",
				"## Evidence\nSummary: C:/runs/001/summary.json\nfitness 0.41\n## Work\nDo the thing.\n");
			var second = Write("b.txt",
				"## Evidence\nSummary: C:/runs/00217-longer-name/summary.json\nfitness 0.8834\n" +
				"## Work\nDo the thing.\n");

			var a = PromptFingerprint.Read(first, null);
			var b = PromptFingerprint.Read(second, null);

			Assert.That(a.Id, Is.EqualTo(b.Id));
			Assert.That(a.Characters, Is.Not.EqualTo(b.Characters), "the rendered text did differ");
		}

		[Test]
		public void RewritingTheSectionsChangesTheIdentity()
		{
			var before = Write("before.txt", "## Evidence\ntext\n## Work\ntext\n");
			var after = Write("after.txt", "## Evidence\ntext\n## Triage\ntext\n## Work\ntext\n");

			var a = PromptFingerprint.Read(before, null);
			var b = PromptFingerprint.Read(after, null);

			Assert.That(a.Id, Is.Not.EqualTo(b.Id));
			Assert.That(b.HeadingCount, Is.EqualTo(3));
		}

		/// <summary>
		/// The injected gospel is version-controlled and changes on its own schedule. Counting its
		/// headings would report a gospel edit as the agent having rewritten its own template.
		/// </summary>
		[Test]
		public void GospelHeadingsAreExcludedFromTheIdentity()
		{
			var mechanics = Write("mechanics.md", "## Economy, and the harvester trap\n## The SDK surface\n");

			var lean = Write("lean.txt", "## Evidence\ntext\n## Work\ntext\n");
			var withGospel = Write("full.txt",
				"## Evidence\ntext\n## Economy, and the harvester trap\ngospel\n" +
				"## The SDK surface\ngospel\n## Work\ntext\n");

			var a = PromptFingerprint.Read(lean, mechanics);
			var b = PromptFingerprint.Read(withGospel, mechanics);

			Assert.That(b.Id, Is.EqualTo(a.Id));
			Assert.That(b.Headings, Is.EqualTo(new[] { "evidence", "work" }));
		}

		[Test]
		public void HeadingsAreNormalisedSoRetitlingDoesNotForkTheIdentity()
		{
			var a = PromptFingerprint.Read(Write("a.txt", "## Evidence:\ntext\n## Work\ntext\n"), null);
			var b = PromptFingerprint.Read(Write("b.txt", "##   evidence\ntext\n###  WORK.\ntext\n"), null);

			Assert.That(a.Id, Is.EqualTo(b.Id));
		}

		[Test]
		public void AMissingOrHeadinglessPromptHasNoIdentity()
		{
			Assert.That(PromptFingerprint.Read(Path.Combine(directory, "absent.txt"), null), Is.Null);
			Assert.That(PromptFingerprint.Read(Write("flat.txt", "no headings at all\n"), null), Is.Null);
		}

		/// <summary>
		/// A template the loop rewrites every round keeps its headings while its body changes, and
		/// ten such revisions once shared one id. With the unrendered template kept beside the
		/// fight, identity is its content: different bodies differ, the same body rendered twice
		/// does not.
		/// </summary>
		[Test]
		public void TheKeptTemplateIdentifiesThePromptByItsContent()
		{
			string Fight(string name, string template, string rendered)
			{
				var fight = Directory.CreateDirectory(Path.Combine(directory, name)).FullName;
				File.WriteAllText(Path.Combine(fight, PromptFingerprint.TemplateFileName), template);
				var prompt = Path.Combine(fight, "agent-prompt.txt");
				File.WriteAllText(prompt, rendered);
				return prompt;
			}

			var first = PromptFingerprint.Read(Fight("one", "## Work\nFix harvesters.\n", "## Work\nFix harvesters. run 1\n"), null);
			var reworded = PromptFingerprint.Read(Fight("two", "## Work\nFix pushes.\n", "## Work\nFix pushes. run 2\n"), null);
			var again = PromptFingerprint.Read(Fight("three", "## Work\r\nFix harvesters.\r\n", "## Work\nFix harvesters. run 3\n"), null);

			Assert.That(reworded.Id, Is.Not.EqualTo(first.Id), "same headings, different template");
			Assert.That(again.Id, Is.EqualTo(first.Id), "same template, rendered for another fight");
		}

		string Write(string name, string text)
		{
			var path = Path.Combine(directory, name);
			File.WriteAllText(path, text);
			return path;
		}
	}
}
