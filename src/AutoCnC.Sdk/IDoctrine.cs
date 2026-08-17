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
using AutoCnC.Core;

namespace AutoCnC.Sdk
{
	/// <summary>
	/// A complete, authored description of how an army fights.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the unit of authorship in AutoC&amp;C. The platform ships no strategy of its own —
	/// it deploys nothing, builds nothing and shoots nothing until a doctrine is loaded.
	/// A module declares everything: what to construct, what to train, which behaviours exist,
	/// and which units run them.
	/// </para>
	/// <para>
	/// Build your module against the SDK, drop the assembly into <c>bin/doctrines</c>, pick it
	/// in-game, and watch it play. Because assignments come from the module, there is nothing to
	/// re-enter at the start of each match.
	/// </para>
	/// </remarks>
	/// <example>
	/// <code>
	/// public sealed class MyModule : IDoctrine
	/// {
	///     public string Name =&gt; "Rush";
	///     public string Description =&gt; "Fast barracks, early pressure.";
	///
	///     public void Configure(IDoctrineBuilder b)
	///     {
	///         b.Build("powr", "nuke").Until(2);
	///         b.Build("proc").Until(2);
	///         b.Train("Infantry", "e1").Until(20);
	///         b.Assign&lt;MyDefensiveMode&gt;().ToAll();
	///         b.Assign&lt;MyRushMode&gt;().ToGroup(1);
	///     }
	/// }
	/// </code>
	/// </example>
	public interface IDoctrine
	{
		/// <summary>Short name used to select this module in-game.</summary>
		string Name { get; }

		/// <summary>One line describing the strategy, shown when listing modules.</summary>
		string Description { get; }

		/// <summary>Declare the module's plans and mode assignments.</summary>
		void Configure(IDoctrineBuilder builder);
	}

	/// <summary>Fluent surface a module uses to declare itself.</summary>
	public interface IDoctrineBuilder
	{
		/// <summary>
		/// Add a structure to the base build plan. Candidates are alternatives for one role, so
		/// <c>Build("powr", "nuke")</c> means "a power plant" whichever faction you are.
		/// </summary>
		IPlanStep Build(params string[] candidates);

		/// <summary>
		/// Add a unit to the production plan. <paramref name="queue"/> is a production category
		/// such as <c>Infantry</c> or <c>Vehicle</c>.
		/// </summary>
		IPlanStep Train(string queue, params string[] candidates);

		/// <summary>Assign a mode to units. Call <see cref="IAssignment"/> to choose the scope.</summary>
		IAssignment Assign<TMode>() where TMode : IUnitMode, new();

		/// <summary>Assign a mode by name, for modes resolved dynamically.</summary>
		IAssignment Assign(string modeName);

		/// <summary>
		/// Register a mode without assigning it, so players can select it manually in-game.
		/// </summary>
		void Register<TMode>() where TMode : IUnitMode, new();
	}

	/// <summary>A pending build or train step awaiting its target count.</summary>
	public interface IPlanStep
	{
		/// <summary>Keep building until this many exist. Counts anything already queued.</summary>
		IDoctrineBuilder Until(int count);

		/// <summary>Keep building these indefinitely.</summary>
		IDoctrineBuilder Forever();
	}

	/// <summary>A pending mode assignment awaiting its scope.</summary>
	public interface IAssignment
	{
		/// <summary>Every unit that has no more specific assignment.</summary>
		IDoctrineBuilder ToAll();

		/// <summary>Every unit of this actor type, e.g. <c>harv</c>.</summary>
		IDoctrineBuilder ToUnitType(params string[] actorTypes);

		/// <summary>Every unit the player puts in this control group (1-9).</summary>
		IDoctrineBuilder ToGroup(int group);
	}

	/// <summary>
	/// What a configured module resolved to. Produced by the platform, consumed by the executor.
	/// </summary>
	public sealed class DoctrineDefinition
	{
		public string Name { get; }
		public string Description { get; }

		/// <summary>Ordered base construction plan.</summary>
		public IReadOnlyList<BuildStep> BuildPlan { get; }

		/// <summary>Ordered unit production plan.</summary>
		public IReadOnlyList<ProductionStep> ProductionPlan { get; }

		/// <summary>Mode assignments declared by the module.</summary>
		public ModeAssignments Assignments { get; }

		/// <summary>Every mode type the module made available, keyed by name.</summary>
		public IReadOnlyDictionary<string, Type> Modes { get; }

		public DoctrineDefinition(
			string name,
			string description,
			IReadOnlyList<BuildStep> buildPlan,
			IReadOnlyList<ProductionStep> productionPlan,
			ModeAssignments assignments,
			IReadOnlyDictionary<string, Type> modes)
		{
			Name = name;
			Description = description;
			BuildPlan = buildPlan;
			ProductionPlan = productionPlan;
			Assignments = assignments;
			Modes = modes;
		}
	}
}
