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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	public sealed class TrainingRunTests
	{
		string root;
		string workspace;
		string project;
		string runs;
		string gameGuide;
		string mechanics;
		string gameRules;
		string promptTemplate;

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(Path.GetTempPath(), "AutoCnC.Launcher.Tests", Guid.NewGuid().ToString("N"));
			workspace = Path.Combine(root, "My Bot");
			runs = Path.Combine(root, "runs");
			gameGuide = Path.Combine(root, "agent-game-guide.md");
			mechanics = Path.Combine(root, "agent-mechanics.md");
			gameRules = Path.Combine(root, "game-rules.json");
			promptTemplate = string.Join(Environment.NewLine,
			[
				"Improve the bot. Edit only files under {workspace}.",
				"Mechanics: {gameMechanics}",
				"Read {gameGuide} and {gameRules}.",
				"Evidence: {fightManifest}, {battleLog}, {telemetry}, {decisionTrace}.",
				"Derived: {summary}, {units}, {mapFacts}, {checks}, {checkResults}, {trend}.",
				"{checkReport}",
				"{trendReport}",
				"{botAudit}",
				"Fight: {battle}. Result: {result}. Revision: {sourceRevision}.",
				"{nextPromptContract}"
			]);
			project = Path.Combine(workspace, "MyBot.csproj");
			Directory.CreateDirectory(workspace);
			File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "before");
			File.WriteAllText(Path.Combine(workspace, "Strategy.custom"), "custom before");
			File.WriteAllText(Path.Combine(workspace, "Notes.md"), "keep me");
			File.WriteAllText(gameGuide, "# Test game guide\nOnly use visible enemy information.");
			File.WriteAllText(mechanics, "# Test mechanics\nModeContext exposes FindResourceFields.");
			File.WriteAllText(gameRules, "{\"actors\":[{\"id\":\"e1\",\"hitPoints\":50}]}");
			Directory.CreateDirectory(Path.Combine(workspace, "bin"));
			File.WriteAllText(Path.Combine(workspace, "bin", "ignored.txt"), "build output");
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(root))
				Directory.Delete(root, true);
		}

		[Test]
		public void EveryFightGetsAUniqueDurableDirectory()
		{
			var first = NewRun();
			var second = NewRun();

			Assert.That(second.RunDirectory, Is.Not.EqualTo(first.RunDirectory));
			Assert.That(File.Exists(first.ManifestPath), Is.True);
			Assert.That(Directory.Exists(first.EvidenceDirectory), Is.True);
			Assert.That(first.TelemetryPath, Does.StartWith(first.EvidenceDirectory));
			Assert.That(first.Manifest.BotProject, Is.EqualTo(project));
			Assert.That(first.Manifest.SourceRevision, Does.StartWith("tree:"));
		}

		[Test]
		public void LegacyManifestWithoutExperimentMetadataLoadsAndKeepsManualSemantics()
		{
			var run = NewRun();
			run.Manifest.SchemaVersion = 7;
			run.Save();
			var legacy = File.ReadAllText(run.ManifestPath)
				.Replace("  \"Experiment\": null,\n", "", StringComparison.Ordinal);
			File.WriteAllText(run.ManifestPath, legacy);

			var loaded = TrainingRun.Load(run.RunDirectory);
			loaded.AgentStarted("agent");
			loaded.AgentFinished(0, 1, "manual draft");

			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Experiment, Is.Null);
				Assert.That(loaded.Manifest.Status, Is.EqualTo("improved"));
				Assert.That(loaded.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);
			});
		}

		[Test]
		public void InterruptedCandidateIsDurablyAbortedAndResumesWithoutOverwritingUserWork()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			run.AgentFinished(0, 1);
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "user work after interruption");
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Experiment.State,
					Is.EqualTo(TrainingExperimentStates.Aborted));
				Assert.That(loaded.Manifest.Status, Is.EqualTo("experiment-aborted"));
				Assert.That(loaded.CanResumeContinuousEvaluation, Is.True);
				Assert.That(loaded.CanImprove, Is.False);
				Assert.That(loaded.CanDelete, Is.False);
				Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")),
					Is.EqualTo("user work after interruption"));
			});

			loaded.ResumeContinuousExperiment();

			Assert.That(loaded.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Candidate));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")),
				Is.EqualTo("user work after interruption"));
		}

		[Test]
		public void InterruptedPartialRestoreCannotResumeAsACandidate()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			File.Delete(Path.Combine(workspace, "Notes.md"));
			File.WriteAllText(Path.Combine(workspace, "Added.cs"), "candidate addition");
			run.AgentFinished(0, 3);
			run.MarkContinuousRestoring();

			// Simulate a process dying after restoring one file but before the snapshot completed.
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "before");
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.That(loaded.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Aborted));
			Assert.That(loaded.CanResumeContinuousEvaluation, Is.False);
			Assert.That(loaded.Manifest.Experiment.AbortedFromState,
				Is.EqualTo(TrainingExperimentStates.Restoring));
			Assert.That(loaded.Manifest.Experiment.AbortReason,
				Does.Contain("restoration was interrupted"));
			Assert.That(() => loaded.ResumeContinuousExperiment(),
				Throws.InvalidOperationException);
			Assert.That(File.Exists(Path.Combine(workspace, "Notes.md")), Is.False);
			Assert.That(File.Exists(Path.Combine(workspace, "Added.cs")), Is.True);

			WorkspaceSnapshot.Restore(loaded);

			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")),
				Is.EqualTo("before"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Notes.md")),
				Is.EqualTo("keep me"));
			Assert.That(File.Exists(Path.Combine(workspace, "Added.cs")), Is.False);
			Assert.That(loaded.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Restored));
		}

		[Test]
		public void LegacyPreparedExperimentAbortsWithoutPretendingItCanResume()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.Manifest.Experiment = new TrainingExperiment
			{
				Continuous = true,
				State = TrainingExperimentStates.Prepared
			};
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.That(loaded.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Aborted));
			Assert.That(loaded.CanResumeContinuousEvaluation, Is.False);
			Assert.That(loaded.CanDelete, Is.False);
		}

		[Test]
		public void FailedContinuousAgentStartDoesNotPersistAnExperiment()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			Directory.CreateDirectory(run.ManifestPath + ".tmp");

			Assert.That(() => run.ContinuousAgentStarted("agent"),
				Throws.TypeOf<UnauthorizedAccessException>());

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.Experiment, Is.Null);
			Assert.That(loaded.Manifest.Agent, Is.Null);
		}

		[Test]
		public void ForeignLiveOwnerBlocksAbortResumeAndRestore()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			run.AgentFinished(0, 1);
			using var foreign = Process.Start(new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.SystemDirectory,
					"WindowsPowerShell", "v1.0", "powershell.exe"),
				Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
			Assert.That(foreign, Is.Not.Null);
			try
			{
				run.Manifest.Owner = new ProcessOwnership
				{
					ProcessId = foreign.Id,
					ClaimedUtc = DateTime.UtcNow
				};
				run.Save();

				var loaded = TrainingRun.Load(run.RunDirectory);
				Assert.That(loaded.IsBusy, Is.True);
				Assert.That(() => loaded.AbortContinuousExperiment("do not steal"),
					Throws.InvalidOperationException);
				Assert.That(() => WorkspaceSnapshot.Restore(loaded),
					Throws.InvalidOperationException);

				loaded.Manifest.Experiment.State = TrainingExperimentStates.Aborted;
				loaded.Manifest.Experiment.CanResumeEvaluation = true;
				Assert.That(() => loaded.ResumeContinuousExperiment(),
					Throws.InvalidOperationException);
			}
			finally
			{
				if (!foreign.HasExited)
					foreign.Kill(entireProcessTree: true);
			}
		}

		[Test]
		public void ExclusiveRunMutationSerializesConcurrentExperimentClaims()
		{
			var run = NewRun();
			Complete(run, "Lost", 180, army: 1000, opponentArmy: 2000);
			WorkspaceSnapshot.Capture(run);

			using (var first = TrainingRun.AcquireMutation(run))
			{
				Assert.That(() => TrainingRun.AcquireMutation(run),
					Throws.TypeOf<IOException>());
				first.Run.ContinuousAgentStarted("agent");
			}

			using var second = TrainingRun.AcquireMutation(run);
			Assert.That(second.Run.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Improving));
			Assert.That(second.Run.IsBusy, Is.True);
		}

		[Test]
		public void ReconciliationCannotClaimARunWhileAnotherProcessLockIsHeld()
		{
			var run = NewRun();
			Complete(run, "Lost", 180, army: 1000, opponentArmy: 2000);
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			run.AgentFinished(0, 1);
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			using var mutation = TrainingRun.AcquireMutation(run);
			var observed = TrainingRun.Load(run.RunDirectory);

			Assert.That(observed.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Candidate));
			Assert.That(observed.Manifest.Warnings,
				Has.Some.Contains("locked by another launcher"));
		}

		[Test]
		public void LiveOrphanWorkerDefersRecoveryUntilItsProcessTreeEnds()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			run.AgentFinished(0, 1);
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			using var worker = Process.Start(new ProcessStartInfo
			{
				FileName = Path.Combine(Environment.SystemDirectory,
					"WindowsPowerShell", "v1.0", "powershell.exe"),
				Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
			Assert.That(worker, Is.Not.Null);
			try
			{
				File.WriteAllText(run.WorkerOwnershipPath,
					JsonSerializer.Serialize(ProcessOwnership.ForProcess(worker)));

				var blocked = TrainingRun.Load(run.RunDirectory);
				Assert.That(blocked.IsBusy, Is.True);
				Assert.That(blocked.Manifest.Experiment.State,
					Is.EqualTo(TrainingExperimentStates.Candidate));
			}
			finally
			{
				if (!worker.HasExited)
					worker.Kill(entireProcessTree: true);
				worker.WaitForExit();
			}

			var recovered = TrainingRun.Load(run.RunDirectory);
			Assert.That(recovered.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Aborted));
		}

		[Test]
		public void WorkspaceLockIsCanonicalAcrossRunsAndBlocksOlderRestore()
		{
			var older = NewRun();
			Complete(older, "Lost", 180, army: 1000, opponentArmy: 2000);
			WorkspaceSnapshot.Capture(older);
			var newer = NewRun();
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "newer workspace");
			var olderLock = TrainingWorkspaceMutation.LockPathFor(
				older.Manifest.BotDirectory);
			var newerLock = TrainingWorkspaceMutation.LockPathFor(
				newer.Manifest.BotDirectory);
			Assert.That(newerLock, Is.EqualTo(olderLock));

			Directory.CreateDirectory(Path.GetDirectoryName(olderLock));
			using (var foreign = new FileStream(olderLock, FileMode.OpenOrCreate,
				FileAccess.ReadWrite, FileShare.None))
			{
				Assert.That(() => TrainingRun.AcquireWorkspaceMutation(newer),
					Throws.TypeOf<IOException>());
				Assert.That(() => WorkspaceSnapshot.Restore(older),
					Throws.TypeOf<IOException>());
				Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")),
					Is.EqualTo("newer workspace"));
			}

			File.Delete(olderLock);
		}

		[Test]
		public void FailedRunReloadReleasesItsMutationLock()
		{
			var run = NewRun();
			var validManifest = File.ReadAllText(run.ManifestPath);
			File.WriteAllText(run.ManifestPath, "{");

			Assert.That(() => TrainingRun.AcquireMutation(run),
				Throws.TypeOf<JsonException>());

			File.WriteAllText(run.ManifestPath, validManifest);
			Assert.That(() =>
			{
				using var mutation = TrainingRun.AcquireMutation(run);
			}, Throws.Nothing);
		}

		[TestCase(false)]
		[TestCase(true)]
		public void UnresolvedRunCanRestoreADeletedOrRenamedProject(bool renamed)
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			run.ContinuousAgentStarted("agent");
			var selectedPath = project;
			if (renamed)
			{
				selectedPath = Path.Combine(workspace, "RenamedBot.csproj");
				File.Move(project, selectedPath);
			}
			else
				File.Delete(project);
			run.AgentFinished(0, 1);
			run.Manifest.Owner = new ProcessOwnership { ProcessId = -1 };
			run.Save();

			var unresolved = TrainingHistory.LoadUnresolved(selectedPath, runs).Single();
			Assert.That(unresolved.HasUnresolvedContinuousExperiment, Is.True);
			Assert.That(unresolved.IsBusy, Is.False);

			WorkspaceSnapshot.Restore(unresolved);

			Assert.That(File.Exists(project), Is.True);
			Assert.That(File.Exists(Path.Combine(workspace, "RenamedBot.csproj")), Is.False);
			Assert.That(unresolved.HasUnresolvedContinuousExperiment, Is.False);
		}

		[Test]
		public void DiscoverySkipsAnIncompatibleRememberedCheckout()
		{
			var oldRoot = Path.Combine(root, "old-checkout");
			var currentRoot = Path.Combine(root, "current-checkout");
			CreateCheckout(oldRoot, authoringApi: null);
			CreateCheckout(currentRoot, authoringApi: "5");
			var nestedStart = Path.Combine(currentRoot, "tools", "AutoCnC.Launcher", "bin");
			Directory.CreateDirectory(nestedStart);

			var found = RepoLayout.Discover(oldRoot, [nestedStart]);

			Assert.That(found.Root, Is.EqualTo(currentRoot));
			Assert.That(found.SupportsTraining, Is.True);
			Assert.That(RepoLayout.For(oldRoot).SupportsTraining, Is.False);
		}

		[Test]
		public void SnapshotReportsAndRestoresOnlyEditableSource()
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);

			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "after");
			File.WriteAllText(Path.Combine(workspace, "Strategy.custom"), "custom after");
			File.Delete(Path.Combine(workspace, "Notes.md"));
			File.WriteAllText(Path.Combine(workspace, "Added.cs"), "added");
			File.WriteAllText(Path.Combine(workspace, "bin", "ignored.txt"), "changed build output");

			var changes = WorkspaceSnapshot.Compare(run);

			Assert.That(changes.Select(c => (c.Kind, c.RelativePath)), Is.EquivalentTo(new[]
			{
				("added", "Added.cs"),
				("deleted", "Notes.md"),
				("modified", "Strategy.cs"),
				("modified", "Strategy.custom")
			}));

			WorkspaceSnapshot.Restore(run);

			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")), Is.EqualTo("before"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.custom")), Is.EqualTo("custom before"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Notes.md")), Is.EqualTo("keep me"));
			Assert.That(File.Exists(Path.Combine(workspace, "Added.cs")), Is.False);
			Assert.That(File.ReadAllText(Path.Combine(workspace, "bin", "ignored.txt")), Is.EqualTo("changed build output"));
		}

		[TestCase("missing")]
		[TestCase("corrupt")]
		[TestCase("outside")]
		public void SnapshotPreflightFailureLeavesWorkspaceByteIdentical(string fault)
		{
			var run = NewRun();
			WorkspaceSnapshot.Capture(run);
			File.WriteAllText(Path.Combine(workspace, "Strategy.cs"), "candidate");
			File.WriteAllText(Path.Combine(workspace, "Added.cs"), "candidate addition");
			var before = BotWorkspace.Fingerprint(workspace);
			var manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(
				File.ReadAllText(run.SnapshotManifestPath));
			var entry = manifest.Files.Single(file =>
				file.RelativePath.EndsWith("Strategy.cs", StringComparison.OrdinalIgnoreCase));
			var snapshotFile = Path.Combine(run.SnapshotDirectory, entry.RelativePath);

			switch (fault)
			{
				case "missing":
					File.Delete(snapshotFile);
					break;
				case "corrupt":
					File.WriteAllText(snapshotFile, "corrupt snapshot");
					break;
				default:
					entry.RelativePath = "..\\outside.cs";
					File.WriteAllText(run.SnapshotManifestPath,
						JsonSerializer.Serialize(manifest));
					break;
			}

			Assert.That(() => WorkspaceSnapshot.Restore(run),
				Throws.TypeOf<InvalidDataException>());
			Assert.That(BotWorkspace.Fingerprint(workspace), Is.EqualTo(before));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Strategy.cs")),
				Is.EqualTo("candidate"));
			Assert.That(File.ReadAllText(Path.Combine(workspace, "Added.cs")),
				Is.EqualTo("candidate addition"));
		}

		[Test]
		public void AgentPacketIsProviderNeutralAndReferencesFightEvidence()
		{
			var run = NewRun();

			TrainingAgent.Prepare(run, gameGuide, mechanics, gameRules,
				promptTemplate,
				"other-agent", ["--input", "{promptFile}"]);

			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.DecisionTracePath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.GameGuidePath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.GameRulesPath));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(TrainingAgent.NextPromptBegin));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(run.Manifest.BotDirectory));
			Assert.That(File.ReadAllText(run.GameGuidePath), Is.EqualTo(File.ReadAllText(gameGuide)));
			Assert.That(File.ReadAllText(run.GameRulesPath), Is.EqualTo(File.ReadAllText(gameRules)));
			Assert.That(File.Exists(run.FightManifestPath), Is.True);
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("other-agent"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("{promptFile}"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Not.Contain("Stdin"),
				"an agent handed the prompt as a path is not also fed it on standard input");

			TrainingAgent.Prepare(run, gameGuide, mechanics, gameRules,
				promptTemplate, "copilot", TrainingAgent.DefaultArguments);
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Contain("{evidence}"));
			Assert.That(File.ReadAllText(run.AgentConfigurationPath), Does.Not.Contain("--no-color"));
			Assert.That(run.SnapshotDirectory, Does.Not.StartWith(run.EvidenceDirectory));
		}

		/// <summary>
		/// The rendered prompt is bigger than the 32,767 characters Windows allows on a command
		/// line, so it has to be handed over on standard input.
		/// </summary>
		[Test]
		public void TheCopilotPromptIsHandedOverOnStandardInputRatherThanInArgv()
		{
			var run = NewRun();

			TrainingAgent.Prepare(run, gameGuide, mechanics, gameRules, promptTemplate,
				"copilot", TrainingAgent.DefaultArguments);

			var configuration = JsonSerializer.Deserialize<TrainingAgentConfiguration>(
				File.ReadAllText(run.AgentConfigurationPath));
			Assert.That(configuration.Stdin, Is.EqualTo("{prompt}"));
			Assert.That(configuration.Arguments, Does.Not.Contain("-p"));
			Assert.That(TrainingAgent.CarriesPrompt(configuration.Arguments), Is.False,
				"nothing in argv may carry a prompt that can outgrow the command line");
		}

		/// <summary>
		/// The mechanics and SDK reference is gospel, and gospel that arrives as a file path is
		/// only a suggestion. It has to be in the prompt the agent is actually handed.
		/// </summary>
		[Test]
		public void TheMechanicsGospelIsInlinedIntoThePromptRatherThanLinked()
		{
			var run = NewRun();

			TrainingAgent.PrepareContext(run, gameGuide, mechanics, gameRules, promptTemplate);

			var rendered = File.ReadAllText(run.PromptPath);
			Assert.That(rendered, Does.Contain("ModeContext exposes FindResourceFields"),
				"the gospel text itself must appear in the rendered prompt");
			Assert.That(rendered, Does.Contain("Mechanics: # Test mechanics"),
				"the placeholder must be substituted where the template put it");
			Assert.That(File.ReadAllText(run.MechanicsPath), Is.EqualTo(File.ReadAllText(mechanics)),
				"the fight keeps its own copy, so an old prompt still shows the gospel it was given");
		}

		/// <summary>
		/// A template that has lost the gospel placeholder is repaired rather than refused. An
		/// evolved prompt can be many rounds of work, and the player meets a refusal as a modal at
		/// the moment they press the button.
		/// </summary>
		[Test]
		public void ATemplateMissingTheMechanicsPlaceholderIsRepaired()
		{
			var legacy = string.Join(Environment.NewLine,
			[
				"Improve the bot. Edit only files under {workspace}.",
				"## Evidence",
				"{gameGuide} {gameRules} {fightManifest} {battleLog} {telemetry} {decisionTrace}",
				"{summary} {units} {mapFacts} {checks} {checkResults} {trend}",
				"{checkReport} {trendReport} {botAudit}",
				"{battle} {result} {sourceRevision}",
				"{nextPromptContract}"
			]);

			Assert.That(TrainingAgent.ValidatePromptTemplate(legacy, out var before), Is.False);
			Assert.That(before, Does.Contain("{gameMechanics}"));

			var repaired = TrainingAgent.EnsureMechanicsPlaceholder(legacy);

			Assert.That(TrainingAgent.ValidatePromptTemplate(repaired, out _), Is.True);
			Assert.That(repaired, Does.Contain("{gameMechanics}"));
			Assert.That(repaired.IndexOf("{gameMechanics}", StringComparison.Ordinal),
				Is.LessThan(repaired.IndexOf("## Evidence", StringComparison.Ordinal)),
				"the gospel must be read before any advice that could contradict it");
			Assert.That(TrainingAgent.EnsureMechanicsPlaceholder(repaired), Is.EqualTo(repaired),
				"repairing twice must not insert it twice");
		}

		/// <summary>
		/// A template that dropped the derived-evidence placeholders is repaired, not rejected.
		/// </summary>
		/// <remarks>
		/// A round is handed its contract at the start and writes its replacement at the end, so
		/// whenever the required set grows the round already in flight was told an older list. It
		/// cannot know about the new entries, and discarding a whole round of analysis over a rule
		/// the agent was never shown would be the worst of both worlds.
		/// </remarks>
		[Test]
		public void ATemplateMissingTheDerivedEvidencePlaceholdersIsRepaired()
		{
			var stale = string.Join(Environment.NewLine,
			[
				"Improve the bot. Edit only files under {workspace}.",
				"{gameMechanics}",
				"## Evidence",
				"{gameGuide} {gameRules} {fightManifest} {battleLog} {telemetry} {decisionTrace}",
				"{battle} {result} {sourceRevision}",
				"{nextPromptContract}"
			]);

			Assert.That(TrainingAgent.ValidatePromptTemplate(stale, out var before), Is.False);
			Assert.That(before, Does.Contain("{summary}"));

			var repaired = TrainingAgent.EnsureRequiredPlaceholders(stale);

			Assert.That(TrainingAgent.ValidatePromptTemplate(repaired, out var after), Is.True, after);
			foreach (var placeholder in new[]
			{
				"{summary}", "{units}", "{mapFacts}", "{checks}", "{checkResults}", "{trend}",
				"{checkReport}", "{trendReport}", "{botAudit}"
			})
				Assert.That(repaired, Does.Contain(placeholder));

			// Restored above the contract, so the contract stays last and on its own line.
			Assert.That(repaired.IndexOf("{summary}", StringComparison.Ordinal),
				Is.LessThan(repaired.IndexOf("{nextPromptContract}", StringComparison.Ordinal)));

			Assert.That(TrainingAgent.EnsureRequiredPlaceholders(repaired), Is.EqualTo(repaired),
				"repairing twice must not insert them twice");
		}

		/// <summary>
		/// An agent that pastes the injected gospel into its proposal would pin today's SDK surface
		/// into a template that outlives it — the precise way "there is no resource/tiberium sensing
		/// API" survived the API by several rounds.
		/// </summary>
		[Test]
		public void GospelCopiedIntoAProposalIsFoldedBackIntoThePlaceholder()
		{
			var run = NewRun();
			TrainingAgent.PrepareContext(run, gameGuide, mechanics, gameRules, promptTemplate);

			var gospel = File.ReadAllText(run.MechanicsPath).Trim();
			var proposal = string.Join(Environment.NewLine,
			[
				TrainingAgent.NextPromptBegin,
				"Edit only files under {workspace}.",
				gospel,
				"{gameGuide} {gameRules} {fightManifest} {battleLog} {telemetry} {decisionTrace}",
				"{battle} {result} {sourceRevision}",
				"{nextPromptContract}",
				TrainingAgent.NextPromptEnd
			]);

			var extracted = TrainingAgent.FindSuggestedNextPrompt(
				proposal.Split(Environment.NewLine), run);

			Assert.That(extracted, Does.Contain("{gameMechanics}"));
			Assert.That(extracted, Does.Not.Contain("ModeContext exposes FindResourceFields"),
				"a pasted copy must collapse back to the placeholder so it cannot go stale");
			Assert.That(TrainingAgent.ValidatePromptTemplate(extracted, out _), Is.True);
		}

		[Test]
		public void CompleteNextPromptIsExtractedAndPersistedOnlyWhenAccepted()
		{
			var run = NewRun();
			var nextPrompt = promptTemplate + Environment.NewLine +
				"Start with the economy inflection point.";
			var extracted = TrainingAgent.FindSuggestedNextPrompt(
			[
				"Finished.",
				TrainingAgent.NextPromptBegin,
				.. nextPrompt.Split(Environment.NewLine),
				TrainingAgent.NextPromptEnd
			]);

			run.AgentStarted("agent");
			run.AgentFinished(0, 1, extracted);

			Assert.That(extracted, Is.EqualTo(nextPrompt));
			Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.False);

			run.AcceptSuggestedNextPrompt(extracted);

			Assert.That(run.Manifest.Agent.SuggestedNextPromptAccepted, Is.True);
			Assert.That(run.Manifest.Agent.SuggestedNextPrompt, Is.EqualTo(nextPrompt));
			Assert.That(TrainingAgent.FindSuggestedNextPrompt(
				[TrainingAgent.NextPromptBegin, TrainingAgent.NextPromptEnd]), Is.Null);
		}

		[Test]
		public void StalePromptAcceptancePreservesNewerManifestState()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(0, 1, "draft");
			var stale = TrainingRun.Load(run.RunDirectory);
			var newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Status = "newer-state";
			newer.Manifest.Warnings.Add("newer warning");
			newer.Manifest.Experiment = new TrainingExperiment
			{
				Id = "newer-experiment",
				Continuous = true,
				State = TrainingExperimentStates.Promoted
			};
			newer.Save();

			var current = TrainingRun.AcceptLatestSuggestedNextPrompt(
				stale, "approved prompt");

			Assert.That(current.Manifest.Status, Is.EqualTo("newer-state"));
			Assert.That(current.Manifest.Warnings, Does.Contain("newer warning"));
			Assert.That(current.Manifest.Experiment.Id, Is.EqualTo("newer-experiment"));
			Assert.That(current.Manifest.Experiment.State,
				Is.EqualTo(TrainingExperimentStates.Promoted));
			Assert.That(current.Manifest.Agent.SuggestedNextPrompt,
				Is.EqualTo("approved prompt"));
			Assert.That(current.Manifest.Agent.SuggestedNextPromptAccepted, Is.True);
		}

		[Test]
		public void StalePromptRejectionPreservesNewerManifestState()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(0, 1, "draft");
			var stale = TrainingRun.Load(run.RunDirectory);
			var newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Status = "newer-state";
			newer.Manifest.Warnings.Add("newer warning");
			newer.Manifest.Experiment = new TrainingExperiment
			{
				Id = "newer-experiment",
				Continuous = true,
				State = TrainingExperimentStates.Promoted
			};
			newer.Save();

			var current = TrainingRun.RejectLatestSuggestedNextPrompt(stale);

			Assert.That(current.Manifest.Status, Is.EqualTo("newer-state"));
			Assert.That(current.Manifest.Warnings, Does.Contain("newer warning"));
			Assert.That(current.Manifest.Experiment.Id, Is.EqualTo("newer-experiment"));
			Assert.That(current.Manifest.Agent.SuggestedNextPromptRejected, Is.True);
		}

		[Test]
		public void NestedContractMarkersAndRenderedValuesAreNormalized()
		{
			var run = NewRun();
			var copiedContract = string.Join(Environment.NewLine,
			[
				"## Create the complete prompt for the next round",
				"",
				"Keep these placeholders: " + run.Manifest.BotDirectory +
					", {gameGuide}, {gameRules}, {fightManifest}, {battleLog}, {telemetry}, " +
					"{decisionTrace}, {battle}, {result}, {sourceRevision}, {nextPromptContract}",
				"",
				"Return the template between these marker lines:",
				TrainingAgent.NextPromptBegin,
				"<complete replacement prompt template>",
				TrainingAgent.NextPromptEnd,
				"",
				"The player will review and edit it before it is saved. Continuous improvement records",
				"the draft but keeps the current prompt unchanged until that manual review."
			]);
			var proposal = promptTemplate
				.Replace("{workspace}", run.Manifest.BotDirectory, StringComparison.Ordinal)
				.Replace("{nextPromptContract}", copiedContract, StringComparison.Ordinal);

			var extracted = TrainingAgent.FindSuggestedNextPrompt(
			[
				"Finished.",
				TrainingAgent.NextPromptBegin,
				.. proposal.Split(Environment.NewLine),
				TrainingAgent.NextPromptEnd
			], run);

			Assert.That(extracted, Does.Contain("{workspace}"));
			Assert.That(extracted, Does.Not.Contain(run.Manifest.BotDirectory));
			Assert.That(extracted, Does.Not.Contain("<complete replacement prompt template>"));
			Assert.That(extracted.Split(Environment.NewLine)
				.Count(line => line == "{nextPromptContract}"), Is.EqualTo(1));
			Assert.That(TrainingAgent.ValidatePromptTemplate(extracted, out var error), Is.True,
				error);
		}

		[Test]
		public void NextPromptMustRetainDynamicRunPlaceholders()
		{
			Assert.That(TrainingAgent.ValidatePromptTemplate(promptTemplate, out var validError), Is.True);
			Assert.That(validError, Is.Null);
			Assert.That(TrainingAgent.ValidatePromptTemplate(
				promptTemplate.Replace("{telemetry}", "no telemetry", StringComparison.Ordinal),
				out var invalidError), Is.False);
			Assert.That(invalidError, Does.Contain("{telemetry}"));

			var inlineContract = promptTemplate.Replace(
				"{nextPromptContract}", "Keep {nextPromptContract} here.", StringComparison.Ordinal);
			Assert.That(TrainingAgent.ValidatePromptTemplate(inlineContract, out var contractError),
				Is.False);
			Assert.That(contractError, Does.Contain("on a line by itself"));
		}

		[Test]
		public void ApprovedReplacementTemplateUsesFreshValuesNextRound()
		{
			var first = NewRun();
			TrainingAgent.PrepareContext(first, gameGuide, mechanics, gameRules, promptTemplate);
			var replacement = promptTemplate + Environment.NewLine +
				"Begin with the earliest economy divergence before reviewing combat.";

			var second = NewRun();
			TrainingAgent.PrepareContext(second, gameGuide, mechanics, gameRules, replacement);
			var rendered = File.ReadAllText(second.PromptPath);

			Assert.That(rendered, Does.Contain("earliest economy divergence"));
			Assert.That(rendered, Does.Contain(second.TelemetryPath));
			Assert.That(rendered, Does.Not.Contain(first.TelemetryPath));
		}

		[Test]
		public void PlayerFeedbackIsDurableAndIncludedInAgentEvidence()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);

			run.SetPlayerFeedback("I expanded too late and never recovered map control.");
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Contain("never recovered map control"));
			TrainingAgent.PrepareContext(run, gameGuide, mechanics, gameRules, promptTemplate);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.Result.PlayerFeedback,
				Is.EqualTo("I expanded too late and never recovered map control."));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("I expanded too late"));
			Assert.That(() => run.SetPlayerFeedback(new string('x', TrainingRun.MaxPlayerFeedbackLength + 1)),
				Throws.ArgumentException);
		}

		[Test]
		public void StaleFeedbackAndReplayUpdatesPreserveNewerManifestFields()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			var stale = TrainingRun.Load(run.RunDirectory);
			var newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Status = "newer-state";
			newer.Manifest.Warnings.Add("newer warning");
			newer.Save();

			var current = TrainingRun.SetLatestPlayerFeedback(stale, "fresh feedback");

			Assert.That(current.Manifest.Status, Is.EqualTo("newer-state"));
			Assert.That(current.Manifest.Warnings, Does.Contain("newer warning"));
			Assert.That(current.Manifest.Result.PlayerFeedback, Is.EqualTo("fresh feedback"));

			current.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			current.Manifest.ReplayWatchedUtc = null;
			current.Save();
			File.WriteAllText(current.ReplayPath, "replay");
			stale = TrainingRun.Load(run.RunDirectory);
			newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Warnings.Add("replay warning");
			newer.Save();

			current = TrainingRun.RecordLatestReplayPlayback(stale, 0, cancelled: false);

			Assert.That(current.Manifest.Status, Is.EqualTo("newer-state"));
			Assert.That(current.Manifest.Warnings, Does.Contain("replay warning"));
			Assert.That(current.Manifest.ReplayWatchedUtc, Is.Not.Null);
		}

		[Test]
		public void StaleVerificationAndSessionUpdatesReloadLatestManifest()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(1, 1, failurePhase: "verification",
				failureMessage: "failed");
			var stale = TrainingRun.Load(run.RunDirectory);
			var newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Warnings.Add("newer warning");
			newer.Save();

			var current = TrainingRun.StartLatestVerification(stale);

			Assert.That(current.Manifest.Status, Is.EqualTo("verifying"));
			Assert.That(current.Manifest.Warnings, Does.Contain("newer warning"));

			current.Manifest.Owner = null;
			current.Manifest.Status = "improvement-failed";
			current.Save();
			stale = TrainingRun.Load(run.RunDirectory);
			newer = TrainingRun.Load(run.RunDirectory);
			newer.Manifest.Warnings.Add("session warning");
			newer.Save();

			var session = TrainingRun.EnsureLatestAgentSessionId(stale);

			Assert.That(session.Run.Manifest.Status, Is.EqualTo("improvement-failed"));
			Assert.That(session.Run.Manifest.Warnings, Does.Contain("session warning"));
			Assert.That(session.SessionId, Is.Not.Empty);
		}

		[Test]
		public void HeadlessFeedbackRequiresSuccessfulPlaybackOfThatBattlesReplay()
		{
			var first = NewRun();
			var second = NewRun();
			foreach (var run in new[] { first, second })
			{
				run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
				Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
				File.WriteAllText(run.ReplayPath, "recorded replay fixture");
			}

			Assert.That(() => first.SetPlayerFeedback("Not observed yet."), Throws.InvalidOperationException);
			Assert.That(first.RecordReplayPlayback(1, cancelled: false), Is.False);
			Assert.That(first.RecordReplayPlayback(0, cancelled: true), Is.False);
			Assert.That(TrainingRun.Load(first.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(first.CanProvideFeedback, Is.False);

			Assert.That(first.RecordReplayPlayback(0, cancelled: false), Is.True);
			var reloaded = TrainingRun.Load(first.RunDirectory);
			Assert.That(reloaded.Manifest.ReplayWatchedUtc, Is.Not.Null);
			Assert.That(reloaded.CanProvideFeedback, Is.True);
			Assert.That(second.CanProvideFeedback, Is.False);
			Assert.That(TrainingRun.Load(second.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);

			reloaded.SetPlayerFeedback("The replay showed the harvester was left exposed.");
			TrainingAgent.PrepareContext(reloaded, gameGuide, mechanics, gameRules, promptTemplate);
			Assert.That(File.ReadAllText(reloaded.FightManifestPath), Does.Contain("harvester was left exposed"));
			Assert.That(File.ReadAllText(reloaded.PromptPath), Does.Contain("harvester was left exposed"));
			Assert.That(TrainingAgent.RenderPrompt(second, promptTemplate), Does.Not.Contain("harvester was left exposed"));
		}

		[Test]
		public void UnrecordedBattlesAndMissingReplaysCannotUnlockFeedback()
		{
			var run = NewRun();
			Assert.That(() => run.SetPlayerFeedback("Still running."), Throws.InvalidOperationException);
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Result = new TrainingBattleResult();
			Assert.That(() => run.SetPlayerFeedback("No match happened."), Throws.InvalidOperationException);
			Assert.That(BattleFeedback.ShouldPrompt(run, automated: false), Is.False);

			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			Assert.That(() => run.RecordReplayPlayback(0, cancelled: false), Throws.InvalidOperationException);
			Assert.That(run.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(BattleFeedback.CanReview(run), Is.False);
		}

		[TestCase(BattleExecutionModes.Rendered, false, true)]
		[TestCase(BattleExecutionModes.Rendered, true, false)]
		[TestCase(BattleExecutionModes.Headless, false, false)]
		[TestCase(BattleExecutionModes.Headless, true, false)]
		[TestCase(null, false, true)]
		public void OnlyManualRenderedBattlesPromptForMissingFeedback(string execution, bool automated, bool expected)
		{
			var run = NewRun();
			run.Manifest.Battle.ExecutionMode = execution;
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.EqualTo(expected));
			run.Manifest.Result.PlayerFeedback = "Existing feedback.";
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.False);
			run.Manifest.Result.PlayerFeedback = null;
			run.Manifest.Status = "failed";
			Assert.That(BattleFeedback.ShouldPrompt(run, automated), Is.False);
		}

		[Test]
		public void EditingAndClearingFeedbackRefreshesTheSameBattlesEvidence()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.SetPlayerFeedback(new string('x', TrainingRun.MaxPlayerFeedbackLength));
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.Result.PlayerFeedback.Length,
				Is.EqualTo(TrainingRun.MaxPlayerFeedbackLength));
			run.SetPlayerFeedback("  An updated observation.  ");
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Contain("An updated observation."));
			Assert.That(TrainingAgent.RenderPrompt(run, promptTemplate), Does.Contain("An updated observation."));
			run.SetPlayerFeedback(" \r\n ");
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);
			Assert.That(File.ReadAllText(run.FightManifestPath), Does.Not.Contain("An updated observation."));
			Assert.That(TrainingAgent.RenderPrompt(run, promptTemplate), Does.Contain("No player assessment was provided."));
		}

		[Test]
		public void PrebuiltBotsCanKeepObservedBattleFeedbackWithoutBeingTrainable()
		{
			var assembly = Path.Combine(root, "Prebuilt.dll");
			File.WriteAllText(assembly, "assembly fixture");
			var run = TrainingRun.Create(assembly,
				new TrainingBattleConfiguration { ExecutionMode = BattleExecutionModes.Rendered }, runs);
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Assert.That(run.IsEditable, Is.False);
			run.SetPlayerFeedback("The bot never defended its expansion.");
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.True);
		}

		[Test]
		public void LegacyHeadlessRunsKeepTheirFeedbackButRequireAReplayBeforeEditing()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			run.Manifest.SchemaVersion = 4;
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			run.Manifest.Result.PlayerFeedback = "An assessment from an older launcher.";
			run.Save();
			var manifest = File.ReadAllText(run.ManifestPath)
				.Replace("  \"ReplayWatchedUtc\": null,\n", "", StringComparison.Ordinal);
			File.WriteAllText(run.ManifestPath, manifest);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.HasPlayerFeedback, Is.True);
			Assert.That(loaded.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(loaded.CanProvideFeedback, Is.False);
			Assert.That(() => loaded.SetPlayerFeedback("An unobserved edit."), Throws.InvalidOperationException);
		}

		[Test]
		public void AFailedManifestWriteCannotClaimFeedbackOrReplayReviewWasSaved()
		{
			var run = NewRun();
			Complete(run, "Lost", 185, army: 1200, opponentArmy: 4100);
			Directory.CreateDirectory(run.ManifestPath + ".tmp");
			Assert.That(() => run.SetPlayerFeedback("Unsaved feedback."),
				Throws.TypeOf<UnauthorizedAccessException>());
			Assert.That(run.HasPlayerFeedback, Is.False);
			Assert.That(TrainingRun.Load(run.RunDirectory).HasPlayerFeedback, Is.False);

			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			File.WriteAllText(run.ReplayPath, "replay fixture");
			Assert.That(() => run.RecordReplayPlayback(0, cancelled: false),
				Throws.TypeOf<UnauthorizedAccessException>());
			Assert.That(run.Manifest.ReplayWatchedUtc, Is.Null);
			Assert.That(TrainingRun.Load(run.RunDirectory).Manifest.ReplayWatchedUtc, Is.Null);
		}

		[Test]
		public void HistoryKeepsOnlyTheSelectedBotAndBuildsChronologicalKpis()
		{
			var later = NewRun();
			Complete(later, "Won", 140, army: 5200, opponentArmy: 900);
			later.Manifest.CreatedUtc = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
			later.Save();

			var earlier = NewRun();
			Complete(earlier, "Lost", 240, army: 1800, opponentArmy: 3600, opponents: 2);
			earlier.Manifest.CreatedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
			earlier.Save();

			var otherWorkspace = Path.Combine(root, "Other");
			Directory.CreateDirectory(otherWorkspace);
			var otherProject = Path.Combine(otherWorkspace, "MyBot.csproj");
			File.WriteAllText(otherProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
			var other = TrainingRun.Create(otherProject, new TrainingBattleConfiguration(), runs);
			Complete(other, "Won", 10, army: 9999, opponentArmy: 0);

			var history = TrainingHistory.Load(project, runs);

			Assert.That(history.Runs.Select(run => run.Manifest.Id),
				Is.EqualTo(new[] { earlier.Manifest.Id, later.Manifest.Id }));
			Assert.That(history.Iterations.Select(iteration => iteration.Number), Is.EqualTo(new[] { 1, 2 }));
			Assert.That(history.Iterations[0].Outcome, Is.EqualTo("Lost"));
			Assert.That(history.Iterations[0].DurationSeconds, Is.EqualTo(240));
			Assert.That(history.Iterations[0].LocalPlayer.ArmyValue, Is.EqualTo(1800));
			Assert.That(history.Iterations[0].Opponents.ArmyValue, Is.EqualTo(7200));
			Assert.That(history.Iterations[1].Outcome, Is.EqualTo("Won"));
		}

		[Test]
		public void FailedAttemptIsArchivedAndRecoveryPromptTargetsItsVerificationError()
		{
			var run = NewRun();
			TrainingAgent.PrepareContext(run, gameGuide, mechanics, gameRules, promptTemplate);
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent output" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "tests failed");
			run.AgentFinished(1, 4, failurePhase: "verification",
				failureMessage: "Bot tests failed.");

			var transcript = run.ArchiveAgentAttempt();
			var recovery = TrainingAgent.BuildRecoveryContext(run.Manifest.Agent, transcript);
			TrainingAgent.PrepareContext(run, gameGuide, mechanics, gameRules, promptTemplate, recovery);
			run.AgentStarted("agent", transcript);

			Assert.That(File.Exists(transcript), Is.True);
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("Recovery attempt"));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain("Bot tests failed."));
			Assert.That(File.ReadAllText(run.PromptPath), Does.Contain(transcript));
			Assert.That(run.Manifest.Agent.Attempt, Is.EqualTo(2));
			Assert.That(run.Manifest.Agent.RecoveryTranscript, Is.EqualTo(transcript));
		}

		[Test]
		public void LegacyFailedRunInfersVerificationPhaseFromTranscript()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent finished" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "tests failed");
			run.AgentFinished(1, 2);

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.That(loaded.Manifest.Agent.FailurePhase, Is.EqualTo("verification"));
			Assert.That(loaded.Manifest.Agent.FailureMessage,
				Is.EqualTo("Independent bot verification failed."));

			loaded.VerificationStarted();
			Assert.That(loaded.Manifest.Status, Is.EqualTo("verifying"));
			Assert.That(loaded.Manifest.Agent.ExitCode, Is.Null);
		}

		[Test]
		public void PreviousCopilotDefaultsUpgradeToColoredProgressOutput()
		{
			var previous = new[]
			{
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-color",
				"--no-custom-instructions",
				"--no-remote-export",
				"--screen-reader",
				"--add-dir", "{evidence}"
			};

			var upgraded = TrainingAgent.UpgradeDefaultArguments("copilot", previous);

			Assert.That(upgraded, Does.Not.Contain("--screen-reader"));
			Assert.That(upgraded, Does.Not.Contain("--no-color"));
			Assert.That(upgraded, Does.Not.Contain("--silent"));
		}

		/// <summary>
		/// Windows caps a command line at 32,767 characters, and the prompt outgrew that once the
		/// mechanics gospel was inlined into it. Every saved <c>-p {prompt}</c> configuration was
		/// therefore failing before the agent started, with Windows' misleading "The filename or
		/// extension is too long", so loading one has to move the prompt to standard input.
		/// </summary>
		[Test]
		public void CopilotDefaultsThatPassedThePromptInArgvUpgradeToStandardInput()
		{
			var previous = new[]
			{
				"-p", "{prompt}",
				"--allow-all-tools",
				"--no-ask-user",
				"--no-custom-instructions",
				"--no-remote-export",
				"--add-dir", "{evidence}"
			};

			var upgraded = TrainingAgent.UpgradeDefaultArguments("copilot", previous);

			Assert.That(upgraded, Does.Not.Contain("-p"));
			Assert.That(upgraded, Does.Not.Contain("{prompt}"));
			Assert.That(upgraded, Does.Contain("--add-dir"), "the evidence grant survives the upgrade");
			Assert.That(TrainingAgent.UpgradePromptChannel(upgraded, null),
				Is.EqualTo(TrainingAgent.DefaultStdin));
		}

		/// <summary>
		/// The prompt travels through exactly one channel, so a configuration that already names it
		/// in its arguments must not also be fed it on standard input.
		/// </summary>
		[Test]
		public void AnAgentGivenThePromptInItsArgumentsIsNotAlsoFedItOnStandardInput()
		{
			Assert.That(TrainingAgent.UpgradePromptChannel(["--input", "{promptFile}"], null), Is.Null);
			Assert.That(TrainingAgent.UpgradePromptChannel(["--input", "{promptFile}"], "{prompt}"), Is.Null);
			Assert.That(TrainingAgent.UpgradePromptChannel(["-p", "{prompt}"], "{prompt}"), Is.Null);
			Assert.That(TrainingAgent.UpgradePromptChannel(TrainingAgent.DefaultArguments, null),
				Is.EqualTo("{prompt}"));
			Assert.That(TrainingAgent.UpgradePromptChannel(TrainingAgent.DefaultArguments, "{promptFile}"),
				Is.EqualTo("{promptFile}"), "a deliberate choice of channel is kept");
		}

		[Test]
		public void HeadlessPerformanceIsStoredInTheDurableManifest()
		{
			var run = NewRun();
			run.Manifest.Battle.ExecutionMode = BattleExecutionModes.Headless;
			File.WriteAllText(run.TelemetryPath,
				"seconds,player,faction,bot,colour,units,army,buildings,basevalue,assets,cash,killed,lost,buildingskilled,buildingslost,state\n" +
				"42,Commander,gdi,0,C82020,5,2500,3,4200,7000,1000,8,3,2,1,Won\n" +
				"42,Watson,nod,1,12B572,0,0,0,0,0,0,3,8,1,2,Lost\n");
			File.WriteAllText(run.BattleLogPath,
				"seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail\n" +
				"0,player,Commander,,,,,,,,faction=gdi bot=0 colour=C82020 side=you\n" +
				"0,player,Watson,,,,,,,,faction=nod bot=1 colour=12B572 side=enemy\n" +
				"42,over,Commander,,,,,,,,result=Won\n");
			File.WriteAllText(run.PerformancePath,
				"{\"schemaVersion\":1,\"mode\":\"headless\",\"status\":\"completed\"," +
				"\"elapsedMilliseconds\":1000,\"worldTicks\":2500,\"logicAttempts\":2600," +
				"\"ticksPerSecond\":2500,\"simulationSpeed\":100,\"gameSeconds\":100,\"result\":\"won\"}");
			File.WriteAllText(run.CancellationPath, "stop");

			var match = new MatchLog();
			match.Watch(run.TelemetryPath);
			Assert.That(match.Refresh(), Is.True);
			var battle = new BattleEventLog();
			battle.Watch(run.BattleLogPath);
			Assert.That(battle.Refresh(), Is.True);

			run.Finish("finished", match, battle);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.That(loaded.Manifest.SchemaVersion, Is.EqualTo(12));
			Assert.That(loaded.Manifest.Result.Outcome, Is.EqualTo("Won"));
			Assert.That(loaded.Manifest.Performance.SimulationSpeed, Is.EqualTo(100));
			Assert.That(loaded.Manifest.Performance.TicksPerSecond, Is.EqualTo(2500));
			Assert.That(File.Exists(run.CancellationPath), Is.False);
		}

		/// <remarks>
		/// Stopping kills the process tree, so the exit code is indistinguishable from a crash.
		/// If that is all the next round knows, it opens as an investigation into a failure that
		/// never happened — which is both untrue and slow, because the agent reads a transcript
		/// and reruns the tests before starting the work that was actually wanted.
		/// </remarks>
		[Test]
		public void AStoppedAttemptIsNotTreatedAsAFailureNextRound()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"agent output" + Environment.NewLine + "=== Verification ===" +
				Environment.NewLine + "half way through");
			run.AgentFinished(-1, 3, failurePhase: "cancelled",
				failureMessage: "The improvement was stopped from the launcher before it finished.",
				cancelled: true);

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.True);
				Assert.That(loaded.Manifest.Status, Is.EqualTo("improvement-cancelled"));

				// The transcript names a verification section, which is what the legacy inference
				// reads to relabel an unexplained failure. It must not reinterpret a stop.
				Assert.That(loaded.Manifest.Agent.FailurePhase, Is.EqualTo("cancelled"));
			});

			var transcript = loaded.ArchiveAgentAttempt();
			Assert.That(TrainingAgent.BuildRecoveryContext(loaded.Manifest.Agent, transcript),
				Is.Null, "A stop is not a failure to be recovered from.");

			var cancellation = TrainingAgent.BuildCancellationContext(loaded.Manifest.Agent,
				transcript);
			TrainingAgent.PrepareContext(loaded, gameGuide, mechanics, gameRules, promptTemplate, cancellation);
			var prompt = File.ReadAllText(loaded.PromptPath);
			Assert.Multiple(() =>
			{
				Assert.That(prompt, Does.Not.Contain("Recovery attempt"));
				Assert.That(prompt, Does.Contain("stopped the previous improvement"));

				// The half-finished edits are the one thing it does have to be warned about.
				Assert.That(prompt, Does.Contain("3 file(s)"));
				Assert.That(prompt, Does.Contain(transcript));
			});
		}

		[Test]
		public void AStopThatChangedNothingSaysNothingToTheNextRound()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(-1, 0, failurePhase: "cancelled", cancelled: true);

			Assert.That(
				TrainingAgent.BuildCancellationContext(run.Manifest.Agent, "transcript.txt"),
				Is.Null, "With no edits left behind there is nothing the next round needs told.");
		}

		/// <remarks>
		/// Covers the guard itself rather than the path production takes. A cancelled run normally
		/// carries the "cancelled" phase, which stops the legacy inference on its own — so without
		/// a run that has no phase at all, the <c>Cancelled</c> clause would never be exercised and
		/// could be dropped by a later edit without any test noticing.
		/// </remarks>
		[Test]
		public void ACancelledRunWithNoPhaseIsStillNotRelabelledAsAFailure()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			File.WriteAllText(run.AgentTranscriptPath,
				"=== Verification ===" + Environment.NewLine + "stopped part way");
			run.AgentFinished(-1, 1, cancelled: true);
			Assert.That(run.Manifest.Agent.FailurePhase, Is.Null, "Precondition for this test.");

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.True);
				Assert.That(loaded.Manifest.Agent.FailurePhase, Is.Null);
				Assert.That(loaded.Manifest.Agent.FailureMessage, Is.Null);
			});
		}

		/// <remarks>
		/// The counterpart to the test above. Stopping a *repair* is not the same as stopping an
		/// improvement: whatever it was sent to fix is still broken, so the next attempt must
		/// still be told to fix it rather than being reassured that nothing failed.
		/// </remarks>
		[Test]
		public void StoppingARepairKeepsTheFailureItWasSentToFix()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(1, 2, failurePhase: "verification",
				failureMessage: "Bot tests failed.");
			var firstTranscript = run.ArchiveAgentAttempt();

			// The repair attempt, which the player then stops part way through.
			run.AgentStarted("agent", firstTranscript, repairing: true);
			Assert.That(run.Manifest.Agent.Repairing, Is.True, "Precondition for this test.");
			run.AgentFinished(1, 5, failurePhase: "verification",
				failureMessage: "Bot tests failed.");

			var loaded = TrainingRun.Load(run.RunDirectory);
			Assert.Multiple(() =>
			{
				Assert.That(loaded.Manifest.Agent.Cancelled, Is.False);
				Assert.That(loaded.Manifest.Status, Is.EqualTo("improvement-failed"));
			});

			var recovery = TrainingAgent.BuildRecoveryContext(loaded.Manifest.Agent,
				firstTranscript);
			Assert.That(recovery, Is.Not.Null,
				"A stopped repair still has a failure to recover from.");
			Assert.That(recovery, Does.Contain("Bot tests failed."));
		}

		[Test]
		public void AnUninspectableStopIsWarnedAboutRatherThanAssumedClean()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(-1, TrainingAgentResult.UnknownChangeCount,
				failurePhase: "cancelled", cancelled: true);

			var context = TrainingAgent.BuildCancellationContext(run.Manifest.Agent, null);

			Assert.That(context, Is.Not.Null,
				"An unreadable workspace is not the same as a clean one.");
			Assert.That(context, Does.Contain("could not be determined"));
			Assert.That(context, Does.Not.Contain("-1"));
		}

		[Test]
		public void ASuccessfulRunIsNeverRecordedAsCancelled()
		{
			var run = NewRun();
			run.AgentStarted("agent");
			run.AgentFinished(0, 2, cancelled: true);

			Assert.Multiple(() =>
			{
				Assert.That(run.Manifest.Agent.Cancelled, Is.False);
				Assert.That(run.Manifest.Status, Is.EqualTo("improved"));
			});
		}

		/// <remarks>
		/// The engine destroys everything a beaten player owns the moment they lose, and keeps
		/// recording until the survivors finish, so the closing rows of a defeat are uniformly
		/// zero. Scoring from them made every lost battle look identical — which is exactly the
		/// comparison the history and the trend charts exist to make.
		/// </remarks>
		[Test]
		public void DefeatIsScoredFromTheMomentItWasDecidedRatherThanTheWipeThatFollows()
		{
			var run = NewRun();
			WriteContestedBattle(run);

			var match = new MatchLog();
			match.Watch(run.TelemetryPath);
			Assert.That(match.Refresh(), Is.True);
			var battle = new BattleEventLog();
			battle.Watch(run.BattleLogPath);
			Assert.That(battle.Refresh(), Is.True);

			run.Finish("finished", match, battle);
			var you = run.Manifest.Result.Players.Single(player => player.Name == "Commander");

			Assert.Multiple(() =>
			{
				Assert.That(run.Manifest.Result.Outcome, Is.EqualTo("Lost"));
				Assert.That(you.Units, Is.EqualTo(3));
				Assert.That(you.ArmyValue, Is.EqualTo(900));
				Assert.That(you.Buildings, Is.EqualTo(4));
				Assert.That(you.BaseValue, Is.EqualTo(5200));
				Assert.That(you.Cash, Is.EqualTo(120));
				Assert.That(you.Killed, Is.EqualTo(11));
				Assert.That(you.Lost, Is.EqualTo(24));
				Assert.That(you.PeakUnits, Is.EqualTo(20));
				Assert.That(you.PeakArmyValue, Is.EqualTo(8000));
				Assert.That(you.PeakBuildings, Is.EqualTo(12));
				Assert.That(you.PeakBaseValue, Is.EqualTo(15000));
			});
		}

		/// <remarks>
		/// The samples that say how a lost battle actually went are still in the run's own
		/// telemetry, so a history recorded by an older launcher is worth rescoring rather than
		/// leaving as a column of zeros. Nothing is written back: the run is evidence.
		/// </remarks>
		[Test]
		public void BattlesRecordedBeforeTheFixAreRescoredFromTheirOwnTelemetry()
		{
			var run = NewRun();
			WriteContestedBattle(run);

			var match = new MatchLog();
			match.Watch(run.TelemetryPath);
			match.Refresh();
			var battle = new BattleEventLog();
			battle.Watch(run.BattleLogPath);
			battle.Refresh();
			run.Finish("finished", match, battle);

			// Exactly what an older launcher left behind: the final sample of a clean sweep.
			var stale = run.Manifest.Result.Players.Single(player => player.Name == "Commander");
			stale.Units = 0;
			stale.ArmyValue = 0;
			stale.Buildings = 0;
			stale.BaseValue = 0;
			stale.PeakUnits = 0;
			stale.PeakArmyValue = 0;
			stale.PeakBuildings = 0;
			stale.PeakBaseValue = 0;
			run.Manifest.SchemaVersion = 5;
			run.Save();

			var loaded = TrainingRun.Load(run.RunDirectory);
			var you = loaded.Manifest.Result.Players.Single(player => player.Name == "Commander");

			Assert.Multiple(() =>
			{
				Assert.That(you.Units, Is.EqualTo(3));
				Assert.That(you.ArmyValue, Is.EqualTo(900));
				Assert.That(you.PeakUnits, Is.EqualTo(20));
				Assert.That(you.PeakArmyValue, Is.EqualTo(8000));
				Assert.That(you.PeakBaseValue, Is.EqualTo(15000));
			});
		}

		/// <remarks>
		/// Rescoring reads the run's telemetry, and a run whose evidence has been cleared out
		/// must still open. Losing the numbers is a disappointment; losing the session is a bug.
		/// </remarks>
		[Test]
		public void ALegacyRunWithoutTelemetryStillLoads()
		{
			var run = NewRun();
			WriteContestedBattle(run);

			var match = new MatchLog();
			match.Watch(run.TelemetryPath);
			match.Refresh();
			var battle = new BattleEventLog();
			battle.Watch(run.BattleLogPath);
			battle.Refresh();
			run.Finish("finished", match, battle);
			run.Manifest.SchemaVersion = 4;
			run.Save();
			File.Delete(run.TelemetryPath);

			var loaded = TrainingRun.Load(run.RunDirectory);

			Assert.That(loaded, Is.Not.Null);
			Assert.That(loaded.Manifest.Result.Players, Has.Count.EqualTo(2));
		}

		/// <summary>A battle the local player builds up in, is ground down in, then loses.</summary>
		static void WriteContestedBattle(TrainingRun run)
		{
			File.WriteAllText(run.TelemetryPath,
				"seconds,player,faction,bot,colour,units,army,buildings,basevalue,assets,cash,killed,lost,buildingskilled,buildingslost,state\n" +
				"0,Commander,gdi,0,C82020,0,0,5,6000,6000,3000,0,0,0,0,Undefined\n" +
				"0,Watson,nod,1,12B572,0,0,5,6000,6000,3000,0,0,0,0,Undefined\n" +
				"10,Commander,gdi,0,C82020,20,8000,12,15000,23000,900,7,2,1,0,Undefined\n" +
				"10,Watson,nod,1,12B572,14,6100,11,13000,19100,400,2,7,0,1,Undefined\n" +
				"20,Commander,gdi,0,C82020,3,900,4,5200,6100,120,11,24,3,8,Undefined\n" +
				"20,Watson,nod,1,12B572,18,9400,9,11000,20400,650,24,11,8,3,Undefined\n" +

				// Defeat, and the clean sweep that comes with it.
				"21,Commander,gdi,0,C82020,0,0,0,0,0,0,11,24,3,8,Lost\n" +
				"21,Watson,nod,1,12B572,18,9400,9,11000,20400,650,24,11,8,3,Won\n" +
				"30,Commander,gdi,0,C82020,0,0,0,0,0,0,11,24,3,8,Lost\n" +
				"30,Watson,nod,1,12B572,18,9400,9,11000,20400,700,24,11,8,3,Won\n");
			File.WriteAllText(run.BattleLogPath,
				"seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail\n" +
				"0,player,Commander,,,,,,,,faction=gdi bot=0 colour=C82020 side=you\n" +
				"0,player,Watson,,,,,,,,faction=nod bot=1 colour=12B572 side=enemy\n" +
				"21,over,Commander,,,,,,,,result=Lost\n");
		}

		TrainingRun NewRun() =>
			TrainingRun.Create(project, new TrainingBattleConfiguration
			{
				Map = "test-map",
				Difficulty = "Normal",
				Opponents = 1,
				Faction = "gdi",
				BotFaction = "nod",
				GameSpeed = "maximum",
				ExecutionMode = BattleExecutionModes.Rendered
			}, runs);
		static void Complete(TrainingRun run, string outcome, int duration, int army,
			int opponentArmy, int opponents = 1)
		{
			run.Manifest.Status = "finished";
			run.Manifest.CompletedUtc = DateTime.UtcNow;
			run.Manifest.Owner = null;
			run.Manifest.Result = new TrainingBattleResult
			{
				DurationSeconds = duration,
				LocalPlayer = "You",
				Outcome = outcome,
				Players =
				[
					new TrainingPlayerResult
					{
						Name = "You",
						Outcome = outcome,
						Units = army / 100,
						ArmyValue = army,
						Buildings = 4,
						BaseValue = army / 2,
						Killed = army / 200,
						Lost = opponentArmy / 300
					},
					.. Enumerable.Range(1, opponents).Select(index => new TrainingPlayerResult
					{
						Name = "Opponent " + index,
						IsBot = true,
						Outcome = outcome == "Won" ? "Lost" : "Won",
						Units = opponentArmy / 100,
						ArmyValue = opponentArmy,
						Buildings = 5,
						BaseValue = opponentArmy / 2,
						Killed = opponentArmy / 200,
						Lost = army / 300
					})
				]
			};
			run.Save();
		}

		static void CreateCheckout(string checkout, string authoringApi)
		{
			var scripts = Path.Combine(checkout, "scripts");
			Directory.CreateDirectory(scripts);
			File.WriteAllText(Path.Combine(checkout, "AutoCnC.sln"), "");
			File.WriteAllText(Path.Combine(scripts, "run-bot.ps1"), "");
			if (authoringApi != null)
				File.WriteAllText(Path.Combine(scripts, "authoring-api.version"), authoringApi);
		}
	}
}
