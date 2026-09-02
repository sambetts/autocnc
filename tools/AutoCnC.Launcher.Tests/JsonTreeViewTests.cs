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
	public sealed class JsonTreeViewTests
	{
		[Test]
		public void JsonIsGroupedIntoLazyCollapsibleTopLevelNodes()
		{
			var path = Path.Combine(Path.GetTempPath(), $"autocnc-json-{Guid.NewGuid():N}.json");
			File.WriteAllText(path,
				"{\"actors\":[{\"id\":\"mtnk\",\"hitPoints\":45000}],\"weapons\":[{\"id\":\"120mm\"}]}");

			try
			{
				using var view = new JsonTreeView();
				view.LoadJsonFile(path, "missing");

				Assert.That(view.SourceText, Does.Contain("\"mtnk\""));
				Assert.That(view.TopLevelLabels, Is.EqualTo(new[] { "actors [1]", "weapons [1]" }));
			}
			finally
			{
				File.Delete(path);
			}
		}
	}
}
