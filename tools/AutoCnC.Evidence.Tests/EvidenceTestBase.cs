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
using System.IO;
using System.Text;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	public abstract class EvidenceTestBase
	{
		string tempDirectory;

		protected string TempDirectory => tempDirectory;

		[SetUp]
		public void SetUpTempDirectory()
		{
			var projectDirectory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
				"..", "..", ".."));
			tempDirectory = Path.Combine(projectDirectory, ".test-work",
				"evidence-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDirectory);
		}

		[TearDown]
		public void DeleteTempDirectory()
		{
			if (tempDirectory != null && Directory.Exists(tempDirectory))
				Directory.Delete(tempDirectory, true);
		}

		protected string WriteFile(string relativePath, string contents)
		{
			var path = Path.Combine(tempDirectory, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents.Replace("\r\n", "\n"), new UTF8Encoding(false));
			return path;
		}

		protected static string BattleHeader =>
			"seconds,event,player,actor,actorid,otherplayer,otheractor,otheractorid,x,y,detail\n";

		protected static string BattleRow(int seconds, string kind, string player, string actor = "", uint actorId = 0,
			string otherPlayer = "", string otherActor = "", uint otherActorId = 0, string x = "", string y = "",
			string detail = "")
		{
			return string.Join(',',
				Csv.Number(seconds), Csv.Escape(kind), Csv.Escape(player), Csv.Escape(actor),
				Csv.Number((int)actorId), Csv.Escape(otherPlayer), Csv.Escape(otherActor),
				Csv.Number((int)otherActorId), x, y, Csv.Escape(detail)) + "\n";
		}

		protected static string TelemetryHeader =>
			"seconds,player,faction,bot,units,army,buildings,basevalue,assets,cash,killed,lost," +
			"buildingskilled,buildingslost,state,earned,spent,power,powerprovided,powerdrained,harvesters,queued\n";

		protected static string TelemetryRow(int seconds, string player, int units, int army, int assets,
			int earned = 0, int spent = 0, int killed = 0, int lost = 0, int buildingsKilled = 0,
			int buildingsLost = 0, int harvesters = 1)
		{
			return string.Join(',', seconds, Csv.Escape(player), "gdi", player == "local" ? "1" : "0",
				units, army, "1", "500", assets, "100", killed, lost, buildingsKilled, buildingsLost,
				"playing", earned, spent, "1", "10", "9", harvesters, "0") + "\n";
		}

		protected static string MinimalRules(params (string Id, int Cost, string Kind)[] actors)
		{
			var builder = new StringBuilder();
			builder.Append("{\"schemaVersion\":2,\"nominalTickMilliseconds\":40,\"actors\":[");
			for (var i = 0; i < actors.Length; i++)
			{
				if (i > 0)
					builder.Append(',');

				builder.Append("{\"id\":\"").Append(actors[i].Id).Append("\",")
					.Append("\"kind\":\"").Append(actors[i].Kind).Append("\",")
					.Append("\"cost\":").Append(actors[i].Cost).Append('}');
			}

			builder.Append("]}");
			return builder.ToString();
		}

		protected EvidenceSet LoadEvidence()
		{
			return new EvidenceSet(tempDirectory).Load();
		}
	}
}
