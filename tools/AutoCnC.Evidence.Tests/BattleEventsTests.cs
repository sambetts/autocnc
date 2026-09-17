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

using System.Linq;
using NUnit.Framework;

namespace AutoCnC.Evidence.Tests
{
	[TestFixture]
	public sealed class BattleEventsTests : EvidenceTestBase
	{
		[Test]
		public void KilledRowsCreditTheKillerNotTheVictimsOwner()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red") +
				BattleRow(42, "killed", "enemy", "e1", 200, "local", "e1", 10, "12", "13", "value=100"));

			var battle = BattleEvents.Read(System.IO.Path.Combine(TempDirectory, "battle.csv"));

			Assert.That(battle.KillsBy("local").Count(), Is.EqualTo(1));
			Assert.That(battle.Of("killed").Where(e => e.Player == "local").Count(), Is.EqualTo(0));
			Assert.That(battle.Of("killed").Where(e => e.Player == "enemy").Count(), Is.EqualTo(1));
		}

		[Test]
		public void LossesOfFiltersOnTheVictimsPlayer()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(10, "lost", "local", "e1", 1, "enemy", "e1", 10) +
				BattleRow(11, "lost", "enemy", "e1", 10, "local", "e1", 1));

			var battle = BattleEvents.Read(System.IO.Path.Combine(TempDirectory, "battle.csv"));

			Assert.That(battle.LossesOf("local").Count(), Is.EqualTo(1));
			Assert.That(battle.LossesOf("local").Single().ActorId, Is.EqualTo(1));
		}

		[Test]
		public void RosterParsesTheLocalPlayerFromSideYou()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red"));

			var battle = BattleEvents.Read(System.IO.Path.Combine(TempDirectory, "battle.csv"));

			Assert.That(battle.LocalPlayer, Is.EqualTo("local"));
		}

		[Test]
		public void RosterParsesFactionAndBotFromDetails()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(0, "player", "local", detail: "side=you faction=gdi bot=1 colour=gold") +
				BattleRow(0, "player", "enemy", detail: "side=enemy faction=nod bot=0 colour=red"));

			var battle = BattleEvents.Read(System.IO.Path.Combine(TempDirectory, "battle.csv"));

			Assert.That(battle.Roster["local"].Faction, Is.EqualTo("gdi"));
			Assert.That(battle.Roster["local"].IsBot, Is.True);
			Assert.That(battle.Roster["enemy"].Faction, Is.EqualTo("nod"));
			Assert.That(battle.Roster["enemy"].IsBot, Is.False);
		}

		[Test]
		public void OfReturnsNoRowsForAnAbsentKind()
		{
			WriteFile("battle.csv", BattleHeader +
				BattleRow(1, "built", "local", "e1", 1));

			var battle = BattleEvents.Read(System.IO.Path.Combine(TempDirectory, "battle.csv"));

			Assert.That(battle.Of("killed").Count(), Is.EqualTo(0));
		}
	}
}
