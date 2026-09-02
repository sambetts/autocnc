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
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class ImprovementWindowTests
	{
		[Test]
		public void DedicatedWindowStartsOnLiveProgressAndExposesItsInputs()
		{
			var root = Path.Combine(Path.GetTempPath(), "AutoCnC Output Window",
				Guid.NewGuid().ToString("N"));
			var workspace = Path.Combine(root, "Bot");
			Directory.CreateDirectory(workspace);
			var project = Path.Combine(workspace, "Bot.csproj");
			var guide = Path.Combine(root, "guide.md");
			var rules = Path.Combine(root, "rules.json");
			var template = string.Join(Environment.NewLine,
			[
				"Edit only files under {workspace}.", "{gameGuide}", "{gameRules}", "{fightManifest}", "{battleLog}",
				"{telemetry}", "{decisionTrace}", "{battle}", "{result}", "{sourceRevision}",
				"{nextPromptContract}"
			]);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(guide, "# Rules\nUse visible information.");
			File.WriteAllText(rules, "{\"actors\":[{\"id\":\"mtnk\",\"hitPoints\":45000}]}");

			try
			{
				var run = TrainingRun.Create(project, new TrainingBattleConfiguration(),
					Path.Combine(root, "runs"));
				TrainingAgent.PrepareContext(run, guide, rules, template);
				run.AgentStarted("fake-agent");

				using var window = new ImprovementWindow();
				window.StartAgentRun(run);
				var parser = new TerminalTextParser();
				var progress = parser.ParseLine(
					"\x1B[36;1m├─ inspecting resolved unit rules →\x1B[0m");
				window.AppendAgentOutput(progress);

				Assert.That(window.SelectedView, Is.EqualTo("Progress"));
				Assert.That(window.AgentProgressText, Does.Contain("inspecting resolved unit rules"));
				Assert.That(window.ProgressColorAt(0), Is.EqualTo(progress.Spans[0].Style.Foreground));
				Assert.That(window.AgentPromptText, Does.Contain(run.GameGuidePath));
				Assert.That(window.GameGuideText, Does.Contain("visible information"));
				Assert.That(window.GameRulesText, Does.Contain("\"mtnk\""));

				var nextPrompt = template + Environment.NewLine + "Inspect economy first.";
				run.AgentFinished(0, 1, nextPrompt);
				window.CompleteAgentRun(run);
				string accepted = null;
				window.NextPromptAccepted += (_, value) => accepted = value;
				window.SubmitNextPrompt();

				Assert.That(window.NextPromptText, Is.EqualTo(nextPrompt));
				Assert.That(window.CanAcceptNextPrompt, Is.True);
				Assert.That(accepted, Is.EqualTo(nextPrompt));
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}
	}
}
