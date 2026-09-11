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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class ActionButtonTests
	{
		/// <remarks>
		/// The bug this guards against did not look like a bug in any source file: a stock button
		/// on these windows inherits the panel's near-black face, and WinForms then derives the
		/// disabled label colour by darkening that face. The result rendered at 1.29:1, which is
		/// a label you cannot read. Only pixels can tell you that, so this asserts on pixels.
		/// </remarks>
		[Test]
		public void DisabledAndEnabledLabelsBothStayReadable()
		{
			Assert.Multiple(() =>
			{
				Assert.That(LabelContrast(enabled: false), Is.GreaterThanOrEqualTo(3.5),
					"A disabled action button must stay readable on the dark theme.");
				Assert.That(LabelContrast(enabled: true), Is.GreaterThanOrEqualTo(4.5),
					"An enabled action button must meet ordinary text contrast.");
				Assert.That(LabelContrast(enabled: true, primary: true), Is.GreaterThanOrEqualTo(4.5),
					"The amber deployment action must retain dark, readable text.");
				Assert.That(LabelContrast(enabled: false, primary: true), Is.GreaterThanOrEqualTo(4.5),
					"The disabled deployment action must stay readable.");
			});
		}

		static double LabelContrast(bool enabled, bool primary = false)
		{
			using var button = new ActionButton
			{
				Text = "Save assessment",
				AutoSize = false,
				Size = new Size(180, 30),
				Enabled = enabled,
				Primary = primary
			};

			return RenderedTextContrast(button, new Rectangle(4, 4, button.Width - 8, button.Height - 8));
		}

		internal static double RenderedTextContrast(Control control, Rectangle sample, bool mostCommonInk = false)
		{
			using var bitmap = new Bitmap(control.Width, control.Height);
			control.DrawToBitmap(bitmap, control.ClientRectangle);

			// Sample inside the border, so the frame cannot be mistaken for the label.
			var tally = new Dictionary<Color, int>();
			for (var y = sample.Top; y < sample.Bottom; y++)
				for (var x = sample.Left; x < sample.Right; x++)
				{
					var pixel = bitmap.GetPixel(x, y);
					tally[pixel] = tally.GetValueOrDefault(pixel) + 1;
				}

			var face = tally.MaxBy(entry => entry.Value).Key;
			var label = mostCommonInk
				? tally.Where(entry => entry.Key != face).MaxBy(entry => entry.Value).Key
				: tally.Keys.MaxBy(colour => Math.Abs(Luminance(colour) - Luminance(face)));
			return Contrast(face, label);
		}

		static double Contrast(Color a, Color b)
		{
			var high = Math.Max(Luminance(a), Luminance(b));
			var low = Math.Min(Luminance(a), Luminance(b));
			return (high + 0.05) / (low + 0.05);
		}

		/// <summary>Relative luminance, as WCAG defines it for contrast ratios.</summary>
		static double Luminance(Color colour) =>
			0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);

		static double Channel(byte value)
		{
			var v = value / 255.0;
			return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}
	}
}
