// Copyright (c) The AutoC&C Developers and Contributors.
// Licensed under GPL-3.0-or-later. See LICENSE.

using System;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[SetUpFixture]
	public sealed class LauncherUiTestSetup
	{
		[OneTimeSetUp]
		public void ConfigureDpiAwareness()
		{
			// Awareness is process-wide in WinForms: test each mode in a fresh test host,
			// before any controls or font metrics are cached.
			var mode = Environment.GetEnvironmentVariable("AUTOCNC_UI_DPI_UNAWARE") == "1"
				? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2;
			Assert.That(Application.SetHighDpiMode(mode), Is.True, "Set the test host's DPI mode before creating controls.");
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
		}
	}
}
