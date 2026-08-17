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
	/// Collects a module's declarations, then hands back a
	/// <see cref="DoctrineDefinition"/>.
	/// </summary>
	/// <remarks>
	/// Deliberately not engine-coupled: configuring a module needs no <c>World</c>, so a module's
	/// declarations can be inspected and unit-tested without launching the game.
	/// </remarks>
	public sealed class DoctrineBuilder : IDoctrineBuilder
	{
		readonly List<BuildStep> buildPlan = [];
		readonly List<ProductionStep> productionPlan = [];
		readonly ModeAssignments assignments = new();
		readonly Dictionary<string, Type> modes = new(StringComparer.OrdinalIgnoreCase);

		IPlanStep IDoctrineBuilder.Build(params string[] candidates) =>
			new PlanStep(this, null, candidates);

		IPlanStep IDoctrineBuilder.Train(string queue, params string[] candidates) =>
			new PlanStep(this, queue, candidates);

		IAssignment IDoctrineBuilder.Assign<TMode>()
		{
			var type = typeof(TMode);
			modes[type.Name] = type;
			return new Assignment(this, type.Name);
		}

		IAssignment IDoctrineBuilder.Assign(string modeName) => new Assignment(this, modeName);

		void IDoctrineBuilder.Register<TMode>() => modes[typeof(TMode).Name] = typeof(TMode);

		/// <summary>Runs a module's Configure and returns what it declared.</summary>
		public static DoctrineDefinition Build(IDoctrine module)
		{
			ArgumentNullException.ThrowIfNull(module);

			var builder = new DoctrineBuilder();
			module.Configure(builder);

			return new DoctrineDefinition(
				module.Name,
				module.Description,
				builder.buildPlan,
				builder.productionPlan,
				builder.assignments,
				builder.modes);
		}

		sealed class PlanStep : IPlanStep
		{
			readonly DoctrineBuilder owner;
			readonly string queue;
			readonly string[] candidates;

			public PlanStep(DoctrineBuilder owner, string queue, string[] candidates)
			{
				this.owner = owner;
				this.queue = queue;
				this.candidates = candidates;
			}

			public IDoctrineBuilder Until(int count) => Add(count);

			public IDoctrineBuilder Forever() => Add(int.MaxValue);

			IDoctrineBuilder Add(int count)
			{
				if (candidates == null || candidates.Length == 0)
					return owner;

				if (queue == null)
					owner.buildPlan.Add(new BuildStep(candidates, count));
				else
					owner.productionPlan.Add(new ProductionStep(queue, candidates, count));

				return owner;
			}
		}

		sealed class Assignment : IAssignment
		{
			readonly DoctrineBuilder owner;
			readonly string modeName;

			public Assignment(DoctrineBuilder owner, string modeName)
			{
				this.owner = owner;
				this.modeName = modeName;
			}

			public IDoctrineBuilder ToAll()
			{
				owner.assignments.SetAll(modeName);
				return owner;
			}

			public IDoctrineBuilder ToUnitType(params string[] actorTypes)
			{
				foreach (var actorType in actorTypes)
					owner.assignments.SetUnitType(actorType, modeName);

				return owner;
			}

			public IDoctrineBuilder ToGroup(int group)
			{
				owner.assignments.SetGroup(group, modeName);
				return owner;
			}
		}
	}
}
