using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class CommandInterfaceTests
	{
		sealed class CaptureHost : Form
		{
			protected override bool ShowWithoutActivation => true;
		}

		string root;
		LauncherSettings settings;

		[OneTimeSetUp]
		public void EnableVisualStyles() => Application.EnableVisualStyles();

		[SetUp]
		public void SetUp()
		{
			root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "command-ui-" + Guid.NewGuid().ToString("N"));
			Write("AutoCnC.sln", "");
			Write("scripts\\authoring-api.version", "5");
			Write("scripts\\run-bot.ps1", "");
			Write("scripts\\new-bot.ps1", "");
			Write("scripts\\train-bot.ps1", "");
			Write("scripts\\difficulties.json",
				"""{"default":"Normal","levels":[{"name":"Normal","botLabel":"Cabal","summary":"A balanced AI opponent for testing your doctrine."}]}""");
			Write("engine\\OpenRA.sln", "");
			Write("engine\\mods\\cnc\\maps\\test-range\\map.yaml",
				"Title: Test range (fixture)\nPlayers:\n\tPlayerReference@Multi0:\n\t\tPlayable: True\n\tPlayerReference@Multi1:\n\t\tPlayable: True\n");
			Write("mods\\autocnc\\mod.yaml",
				"GameSpeeds:\n\tDefaultSpeed: default\n\tSpeeds:\n\t\tdefault:\n\t\t\tTimestep: 40\n\t\tmaximum:\n\t\t\tTimestep: 1\n");
			Write("bots\\Reference\\ReferenceBot.csproj", "<Project />");
			settings = new LauncherSettings
			{
				RepositoryRoot = root,
				BattleBotPath = Path.Combine(root, "bots", "Reference", "ReferenceBot.csproj")
			};
		}

		void Write(string path, string content)
		{
			var target = Path.Combine(root, path);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.WriteAllText(target, content);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(root, recursive: true);

		MainForm Window()
		{
			var window = new MainForm(settings) { ShowInTaskbar = false };
			window.Show();
			window.Size = new Size(1200, 850);
			window.PerformLayout();
			return window;
		}

		[Test]
		public void StationsPreserveConfigurationAndExposeTheAuthoringLoop()
		{
			using var window = Window();
			Assert.That(window.SelectedStation, Is.EqualTo(1));
			var map = Named<ComboBox>(window, "Battle map");
			var chosen = map.SelectedItem;
			for (var i = 0; i < 4; i++)
			{
				window.SelectStation(i);
				Assert.That(window.SelectedStation, Is.EqualTo(i));
				Assert.That(Descendants(window).OfType<ActionButton>().Count(button => button.Selected), Is.EqualTo(1));
				Assert.That(map.SelectedItem, Is.SameAs(chosen));
			}
			Assert.That(Descendants(window).OfType<Button>().All(button => button is ActionButton), Is.True);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().BotName, Is.EqualTo("ReferenceBot"));
			Assert.That(((Button)window.AcceptButton).Enabled, Is.True);
		}

		[Test]
		public void ExecutionAndTrainingStatesStayConsistentAcrossStations()
		{
			using var window = Window();
			var execution = Named<ComboBox>(window, "Battle execution mode");
			var speed = Named<ComboBox>(window, "Rendered game speed");
			Assert.That(speed.Enabled, Is.False);
			execution.SelectedIndex = 1;
			Assert.That(speed.Enabled, Is.True);
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			continuous.Checked = true;
			Assert.That(((Button)window.AcceptButton).Text, Is.EqualTo("START AI TRAINING"));
			execution.SelectedIndex = 0;
			Assert.That(((Button)window.AcceptButton).Text, Is.EqualTo("START AI TRAINING"));
			Assert.That(speed.Enabled, Is.False);
			Assert.That(((GameSpeedInfo)speed.SelectedItem).Id, Is.EqualTo("maximum"));
		}

		[Test]
		public void PrebuiltBotsAndMissingMapsCannotClaimTrainingReadiness()
		{
			Write("bots\\Prebuilt.dll", "");
			settings.BattleBotPath = Path.Combine(root, "bots", "Prebuilt.dll");
			using var window = Window();
			var continuous = Descendants(window).OfType<CheckBox>().Single(box => box.Text.StartsWith("Repeat:"));
			Assert.That(continuous.Enabled, Is.False);
			Directory.Delete(Path.Combine(root, "engine", "mods", "cnc", "maps", "test-range"), recursive: true);
			Descendants(window).OfType<Button>().Single(button => button.Text == "Refresh").PerformClick();
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Named<TextBox>(window, "Battle bot project or assembly").Text += "missing";
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().Readiness, Does.StartWith("AWAITING BOT"));
		}

		[TestCase(1200, 850)]
		[TestCase(940, 700)]
		[TestCase(940, 600)]
		public void AllStationsRenderWithReachableDeploymentControls(int width, int height)
		{
			using var window = Window();
			window.MinimumSize = Size.Empty;
			window.Size = new Size(width, height);
			Assert.That(window.Size, Is.EqualTo(new Size(width, height)));
			for (var station = 0; station < 4; station++)
			{
				window.SelectStation(station);
				window.PerformLayout();
				using var bitmap = new Bitmap(window.Width, window.Height);
				Assert.That(() => window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)), Throws.Nothing);
				var launch = (Button)window.AcceptButton;
				var origin = window.PointToClient(launch.PointToScreen(Point.Empty));
				Assert.That(new Rectangle(Point.Empty, window.ClientSize).Contains(new Rectangle(origin, launch.Size)), Is.True,
					"The primary action must remain fully inside the window at every supported size.");
				var rail = Descendants(window).OfType<Label>().Single(label => label.AccessibleName == "Operation status").Parent;
				var heading = Descendants(rail).OfType<Label>().Single(label => label.Text == "COMMAND DECK");
				Assert.That(heading.Height, Is.GreaterThanOrEqualTo(heading.PreferredHeight));
				var visible = rail.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
				for (var i = 0; i < visible.Length; i++)
					for (var j = i + 1; j < visible.Length; j++)
						Assert.That(visible[i].Bounds.IntersectsWith(visible[j].Bounds), Is.False,
							$"{visible[i].Text} must not overlap {visible[j].Text}.");
				var output = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
				if (!string.IsNullOrEmpty(output))
				{
					Directory.CreateDirectory(output);
					bitmap.Save(Path.Combine(output, $"command-{width}-{station}.png"), ImageFormat.Png);
				}
			}
		}

		[Test]
		public void InvalidRepositoryClearsReadinessRatherThanUsingThePreviousCheckout()
		{
			using var window = Window();
			Named<TextBox>(window, "AutoC&C repository").Text = Path.Combine(root, "missing");
			Assert.That(((Button)window.AcceptButton).Enabled, Is.False);
			Assert.That(Named<ComboBox>(window, "Battle map").Items.Count, Is.Zero);
			Assert.That(Descendants(window).OfType<BotDossier>().Single().Readiness, Does.StartWith("SETUP REQUIRED"));
		}

		[Test]
		public void BattleWindowsUseTheSameNavigationAndActionLanguage()
		{
			using var results = new ResultsWindow(new MatchLog());
			using var output = new OutputWindow(new BattleEventLog());
			using var improvement = new ImprovementWindow();
			foreach (var window in new BattleWindow[] { results, output, improvement })
			{
				var tabs = Descendants(window).OfType<TabControl>().Single();
				Assert.That(tabs, Is.TypeOf<CommandTabs>());
				for (var index = 0; index < tabs.TabCount; index++)
				{
					tabs.SelectedIndex = index;
					Assert.That(tabs.SelectedTab, Is.SameAs(tabs.TabPages[index]));
				}
				Assert.That(window.BackColor, Is.EqualTo(CommandTheme.Background));
			}
		}

		[Test]
		public void SharedSurfacesRenderTheirControls()
		{
			using var results = new ResultsWindow(new MatchLog());
			using var output = new OutputWindow(new BattleEventLog());
			using var improvement = new ImprovementWindow();
			using var agent = new AgentSettingsDialog("copilot", TrainingAgent.DefaultArguments);
			using var newBot = new NewBotDialog(root);
			Form[] windows = [results, output, improvement, agent, newBot];
			for (var index = 0; index < windows.Length; index++)
			{
				var window = windows[index];
				using var host = new CaptureHost { ClientSize = new Size(960, 760), ShowInTaskbar = false };
				window.TopLevel = false;
				window.FormBorderStyle = FormBorderStyle.None;
				window.Dock = DockStyle.Fill;
				host.Controls.Add(window);
				host.Show();
				window.Show();
				window.PerformLayout();
				using var bitmap = new Bitmap(host.Width, host.Height);
				Assert.That(() => host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size)), Throws.Nothing);
				var capture = Environment.GetEnvironmentVariable("AUTOCNC_UI_CAPTURE_DIR");
				if (!string.IsNullOrEmpty(capture))
				{
					Directory.CreateDirectory(capture);
					bitmap.Save(Path.Combine(capture, $"shared-{index}.png"), ImageFormat.Png);
				}
			}
		}

		[TestCase(false)]
		[TestCase(true)]
		public void PrimaryActionPaintsInBothStates(bool enabled)
		{
			using var button = new ActionButton { Primary = true, Enabled = enabled, Text = "DEPLOY & FIGHT", Size = new Size(240, 56), AutoSize = false };
			using var bitmap = new Bitmap(240, 56);
			Assert.That(() => button.DrawToBitmap(bitmap, button.ClientRectangle), Throws.Nothing);
			Assert.That(bitmap.GetPixel(8, 8).ToArgb(),
				Is.EqualTo((enabled ? CommandTheme.Amber : CommandTheme.Surface).ToArgb()));
		}

		static T Named<T>(Control root, string name) where T : Control =>
			Descendants(root).OfType<T>().Single(control => control.AccessibleName == name);

		static IEnumerable<Control> Descendants(Control root)
		{
			foreach (Control child in root.Controls)
			{
				yield return child;
				foreach (var nested in Descendants(child))
					yield return nested;
			}
		}
	}
}
// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.
