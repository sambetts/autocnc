#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace AutoCnC.Launcher.Tests
{
	[TestFixture]
	[Apartment(ApartmentState.STA)]
	public sealed class TaskbarProgressTests
	{
		[Test]
		public void BusyStateCanBeSetAndCleared()
		{
			using var form = new Form();
			using var progress = new TaskbarProgress();

			Assert.DoesNotThrow(() =>
			{
				progress.SetBusy(form.Handle, busy: true);
				progress.SetBusy(form.Handle, busy: false);
			});
		}
	}
}
