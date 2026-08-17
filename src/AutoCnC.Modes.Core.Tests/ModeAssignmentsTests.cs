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
using AutoCnC.Modes.Core;
using NUnit.Framework;

namespace AutoCnC.Modes.Core.Tests
{
	[TestFixture]
	public class ModeAssignmentsTests
	{
		static ModeAssignments Fresh() => new("DefensiveMode");

		[Test]
		public void FallsBackToGlobalMode()
		{
			var a = Fresh();
			Assert.That(a.Resolve(null, 0, "e1"), Is.EqualTo("DefensiveMode"));
		}

		[Test]
		public void UnitTypeBeatsGlobal()
		{
			var a = Fresh();
			a.SetUnitType("harv", "RunHomeMode");

			Assert.That(a.Resolve(null, 0, "harv"), Is.EqualTo("RunHomeMode"));
			Assert.That(a.Resolve(null, 0, "e1"), Is.EqualTo("DefensiveMode"), "other types are unaffected");
		}

		[Test]
		public void GroupBeatsUnitType()
		{
			var a = Fresh();
			a.SetUnitType("e1", "RunHomeMode");
			a.SetGroup(1, "AttackBaseMode");

			Assert.That(a.Resolve(null, 1, "e1"), Is.EqualTo("AttackBaseMode"));
			Assert.That(a.Resolve(null, 0, "e1"), Is.EqualTo("RunHomeMode"), "ungrouped still uses the type rule");
		}

		[Test]
		public void UnitOverrideBeatsEverything()
		{
			var a = Fresh();
			a.SetUnitType("e1", "RunHomeMode");
			a.SetGroup(1, "AttackBaseMode");

			Assert.That(a.Resolve("ScoutMode", 1, "e1"), Is.EqualTo("ScoutMode"));
		}

		[Test]
		public void FullPrecedenceChain()
		{
			// The scenario from the design: a global default, a type rule for harvesters,
			// and a group rule, all coexisting without clobbering each other.
			var a = Fresh();
			a.SetAll("DefensiveMode");
			a.SetUnitType("harv", "RunHomeMode");
			a.SetGroup(1, "AttackBaseMode");

			Assert.Multiple(() =>
			{
				Assert.That(a.Resolve(null, 0, "mtnk"), Is.EqualTo("DefensiveMode"), "unmatched -> global");
				Assert.That(a.Resolve(null, 0, "harv"), Is.EqualTo("RunHomeMode"), "harvester -> type rule");
				Assert.That(a.Resolve(null, 1, "mtnk"), Is.EqualTo("AttackBaseMode"), "grouped -> group rule");
				Assert.That(a.Resolve(null, 1, "harv"), Is.EqualTo("AttackBaseMode"), "group outranks type");
				Assert.That(a.Resolve("ScoutMode", 1, "harv"), Is.EqualTo("ScoutMode"), "unit override wins");
			});
		}

		[Test]
		public void UnitTypeIsCaseInsensitive()
		{
			var a = Fresh();
			a.SetUnitType("HARV", "RunHomeMode");

			Assert.That(a.Resolve(null, 0, "harv"), Is.EqualTo("RunHomeMode"));
		}

		[Test]
		public void ClearingAnAssignmentFallsBackAgain()
		{
			var a = Fresh();
			a.SetUnitType("harv", "RunHomeMode");
			Assert.That(a.Resolve(null, 0, "harv"), Is.EqualTo("RunHomeMode"));

			a.SetUnitType("harv", null);
			Assert.That(a.Resolve(null, 0, "harv"), Is.EqualTo("DefensiveMode"));
		}

		[Test]
		public void InvalidGroupsAreIgnored()
		{
			var a = Fresh();
			a.SetGroup(0, "AttackBaseMode");
			a.SetGroup(10, "AttackBaseMode");

			Assert.That(a.GetGroup(0), Is.Null);
			Assert.That(a.GetGroup(10), Is.Null);
			Assert.That(a.Resolve(null, 0, "e1"), Is.EqualTo("DefensiveMode"));
		}

		[Test]
		public void ResolvesToNullWhenNothingIsAssigned()
		{
			var a = new ModeAssignments();
			Assert.That(a.Resolve(null, 0, "e1"), Is.Null);
		}

		[Test]
		public void ReportsCurrentAssignmentsForDisplay()
		{
			var a = Fresh();
			a.SetUnitType("harv", "RunHomeMode");
			a.SetGroup(3, "AttackBaseMode");
			a.SetGroup(1, "ScoutMode");

			Assert.That(a.UnitTypeAssignments.Select(kv => kv.Key), Is.EqualTo(new[] { "harv" }));
			Assert.That(a.GroupAssignments.Select(kv => kv.Key), Is.EqualTo(new[] { 1, 3 }), "ordered by group");
		}

		[Test]
		public void BlankModeNamesClearRatherThanAssign()
		{
			var a = Fresh();
			a.SetGroup(1, "  ");
			Assert.That(a.GetGroup(1), Is.Null);
		}
	}

	[TestFixture]
	public class UnitDecisionTests
	{
		[Test]
		public void SameIntentIgnoresTheReasonText()
		{
			var a = UnitDecision.Attack(7, "engaging vehicle");
			var b = UnitDecision.Attack(7, "totally different explanation");

			Assert.That(a.SameIntent(b), Is.True,
				"order suppression must key on the command, not the commentary");
		}

		[Test]
		public void SameIntentDistinguishesDifferentTargets()
		{
			Assert.That(UnitDecision.Attack(7, "x").SameIntent(UnitDecision.Attack(8, "x")), Is.False);
			Assert.That(UnitDecision.MoveTo(3, 4, "x").SameIntent(UnitDecision.MoveTo(3, 5, "x")), Is.False);
			Assert.That(UnitDecision.MoveTo(3, 4, "x").SameIntent(UnitDecision.AttackMoveTo(3, 4, "x")), Is.False);
		}
	}
}
