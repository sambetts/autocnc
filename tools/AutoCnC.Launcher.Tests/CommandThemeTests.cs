// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class CommandThemeTests
	{
		[TestCase("label", false)]
		[TestCase("label", true)]
		[TestCase("link", false)]
		[TestCase("link", true)]
		[TestCase("checkbox", false)]
		[TestCase("checkbox", true)]
		public void DisabledCaptionsRemainReadableWithoutEnablingTheControls(string kind, bool disableParent)
		{
			using var panel = new Panel { BackColor = CommandTheme.Surface, Size = new Size(450, 110) };
			using Control caption = kind switch
			{
				"link" => new LinkLabel { Text = "Agent settings" },
				"checkbox" => new CheckBox { Text = "Repeat: fight > analyze > improve > fight", Checked = true },
				_ => new Label
				{
					Text = "TRAIN FROM BATTLE\nMap: badland-ridges / Normal / Feedback provided",
					Padding = new Padding(3),
					UseMnemonic = false
				}
			};
			caption.Font = CommandTheme.Body;
			caption.ForeColor = CommandTheme.Green;
			caption.Size = new Size(430, 90);
			panel.Controls.Add(caption);
			CommandTheme.Apply(panel);
			if (disableParent)
				panel.Enabled = false;
			else
				caption.Enabled = false;

			AssertReadableDisabledText(caption);
			Assert.That(caption.Enabled, Is.False);
			Assert.That(caption.CanSelect, Is.False);
			if (caption is CheckBox checkBox)
				Assert.That(checkBox.Checked, Is.True);

			panel.Enabled = true;
			caption.Enabled = true;
			Assert.That(caption.ForeColor, Is.EqualTo(CommandTheme.Green));
		}

		internal static void AssertReadableDisabledText(Control control)
		{
			Assert.That(control.Enabled, Is.False, control.Text);
			var inset = control is CheckBox ? 32 * control.DeviceDpi / 96 : 4;
			var sample = new Rectangle(inset, 2, control.Width - inset - 2, control.Height - 4);
			Assert.That(ActionButtonTests.RenderedTextContrast(control, sample, mostCommonInk: true),
				Is.GreaterThanOrEqualTo(4.5), $"Disabled caption must remain readable: {control.Text}");
		}
	}
}
