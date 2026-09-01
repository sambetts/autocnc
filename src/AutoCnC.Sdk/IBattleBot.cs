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
	/// A commander: several doctrines, and the judgement to know which one this match needs.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the unit of authorship in AutoC&amp;C, and the thing a battle is played with. A
	/// <see cref="IDoctrine"/> is one way of fighting — an opening, a turtle, a push, a scouting
	/// run — complete with its own build plan, production plan and mode assignments. A bot owns
	/// several of them and moves between them as the match turns.
	/// </para>
	/// <para>
	/// The split is the point. A doctrine is a plan, and plans do not survive contact; deciding
	/// <em>which plan</em> is a different problem from executing one, and mixing the two is how a
	/// single doctrine ends up as a thicket of special cases. Keep an attack doctrine confident,
	/// keep a defence doctrine paranoid, and let the bot pick.
	/// </para>
	/// <para>
	/// <see cref="Reassess"/> is a pure function of <see cref="BattleState"/>, which contains only
	/// what your side can actually see. So the interesting half of a bot needs no engine to test:
	/// hand it a state, assert the doctrine it picks.
	/// </para>
	/// </remarks>
	/// <example>
	/// <code>
	/// public sealed class MyBot : BattleBot
	/// {
	///     public override string Name =&gt; "Adaptive";
	///     public override string Description =&gt; "Opens economic, turtles when hit, pushes when ahead.";
	///
	///     public override void Configure(IBattleBotBuilder b)
	///     {
	///         b.Open&lt;OpeningDoctrine&gt;();
	///         b.Use&lt;DefenceDoctrine&gt;();
	///         b.Use&lt;AttackDoctrine&gt;();
	///     }
	///
	///     public override DoctrineDecision Reassess(in BattleState s)
	///     {
	///         if (s.BuildingsLost &gt; 0)
	///             return DoctrineDecision.SwitchTo("Defence", "losing buildings");
	///
	///         if (s.ArmyValue &gt; 6000 &amp;&amp; s.EnemyBaseFound)
	///             return DoctrineDecision.SwitchTo("Attack", "army is worth spending");
	///
	///         return DoctrineDecision.Continue;
	///     }
	/// }
	/// </code>
	/// </example>
	public interface IBattleBot
	{
		/// <summary>Short name used to select this bot.</summary>
		string Name { get; }

		/// <summary>One line describing how it plays, shown when listing bots.</summary>
		string Description { get; }

		/// <summary>Declare which doctrines this bot fights with, and which it opens on.</summary>
		void Configure(IBattleBotBuilder builder);

		/// <summary>
		/// Called every assessment interval. Return the doctrine to change to, or
		/// <see cref="DoctrineDecision.Continue"/> to stay put.
		/// </summary>
		/// <remarks>
		/// Naming the doctrine you are already running is the same as continuing, so a rule can
		/// state its condition without also having to check what is loaded. The platform will not
		/// act on a switch until the current doctrine has had its minimum time, which is what
		/// stops two rules that disagree from flipping the army back and forth every few seconds.
		/// </remarks>
		DoctrineDecision Reassess(in BattleState state);
	}

	/// <summary>Fluent surface a bot uses to declare which doctrines it owns.</summary>
	public interface IBattleBotBuilder
	{
		/// <summary>
		/// The doctrine to start the match on. A bot needs exactly one; the last one declared
		/// wins, so this is not a thing you can get half-right.
		/// </summary>
		IBattleBotBuilder Open<TDoctrine>() where TDoctrine : IDoctrine, new();

		/// <summary>Another doctrine this bot can switch to.</summary>
		IBattleBotBuilder Use<TDoctrine>() where TDoctrine : IDoctrine, new();
	}

	/// <summary>
	/// Convenience base class, so a bot only writes the parts it cares about.
	/// </summary>
	/// <remarks>
	/// The default <see cref="Reassess"/> never switches, which makes a bot that simply bundles
	/// one doctrine a three-line class rather than a ceremony.
	/// </remarks>
	public abstract class BattleBot : IBattleBot
	{
		public abstract string Name { get; }

		public abstract string Description { get; }

		public abstract void Configure(IBattleBotBuilder builder);

		public virtual DoctrineDecision Reassess(in BattleState state) => DoctrineDecision.Continue;
	}

	/// <summary>
	/// What a configured bot resolved to. Produced by the platform, consumed by the executor.
	/// </summary>
	public sealed class BattleBotDefinition
	{
		public string Name { get; }
		public string Description { get; }

		/// <summary>The doctrine the match starts on.</summary>
		public string Opening { get; }

		/// <summary>Every doctrine this bot can run, keyed by name, in declaration order.</summary>
		public IReadOnlyDictionary<string, DoctrineDefinition> Doctrines { get; }

		/// <summary>Doctrine names in declaration order, for listing and for a mode to read.</summary>
		public IReadOnlyList<string> DoctrineNames { get; }

		public BattleBotDefinition(
			string name,
			string description,
			string opening,
			IReadOnlyDictionary<string, DoctrineDefinition> doctrines,
			IReadOnlyList<string> doctrineNames)
		{
			Name = name;
			Description = description;
			Opening = opening;
			Doctrines = doctrines;
			DoctrineNames = doctrineNames;
		}

		/// <summary>The named doctrine, or null. Names are compared case-insensitively.</summary>
		public DoctrineDefinition Find(string doctrine) =>
			!string.IsNullOrEmpty(doctrine) && Doctrines.TryGetValue(doctrine, out var found) ? found : null;
	}

	/// <summary>
	/// Collects a bot's declarations, then hands back a <see cref="BattleBotDefinition"/>.
	/// </summary>
	/// <remarks>
	/// Deliberately not engine-coupled, like <see cref="DoctrineBuilder"/>: configuring a bot
	/// needs no <c>World</c>, so what a bot declared can be inspected and unit-tested without
	/// launching the game.
	/// </remarks>
	public sealed class BattleBotBuilder : IBattleBotBuilder
	{
		readonly Dictionary<string, DoctrineDefinition> doctrines = new(StringComparer.OrdinalIgnoreCase);
		readonly List<string> order = [];

		string opening;

		IBattleBotBuilder IBattleBotBuilder.Open<TDoctrine>()
		{
			opening = Add(new TDoctrine());
			return this;
		}

		IBattleBotBuilder IBattleBotBuilder.Use<TDoctrine>()
		{
			Add(new TDoctrine());
			return this;
		}

		string Add(IDoctrine doctrine)
		{
			var definition = DoctrineBuilder.Build(doctrine);
			var name = definition.Name;

			if (string.IsNullOrWhiteSpace(name))
				throw new InvalidOperationException($"{doctrine.GetType().Name}: Name must not be empty.");

			// Declaring the same doctrine twice is a copy-paste slip, not an instruction. Silently
			// keeping one of the two would leave a bot switching to a doctrine that is not the one
			// its author is reading.
			if (!doctrines.TryAdd(name, definition))
				throw new InvalidOperationException(
					$"Two doctrines in this bot are both called '{name}'. Names have to be unique within a bot.");

			order.Add(name);
			return name;
		}

		/// <summary>Runs a bot's Configure and returns what it declared.</summary>
		public static BattleBotDefinition Build(IBattleBot bot)
		{
			ArgumentNullException.ThrowIfNull(bot);

			var builder = new BattleBotBuilder();
			bot.Configure(builder);

			if (builder.order.Count == 0)
				throw new InvalidOperationException(
					$"{bot.GetType().Name} declared no doctrines. A bot needs at least one — nothing else knows how to fight.");

			// A bot that never said which doctrine to open on gets the first it declared. Reading
			// order is a defensible answer, and refusing to start over it would not be.
			return new BattleBotDefinition(
				bot.Name,
				bot.Description,
				builder.opening ?? builder.order[0],
				builder.doctrines,
				builder.order);
		}

		/// <summary>
		/// Wraps a lone doctrine as a bot that owns it and never switches.
		/// </summary>
		/// <remarks>
		/// A doctrine assembly written before bots existed, or one that genuinely only has one way
		/// of playing, is still a thing you can put in a battle. This is the degenerate case of
		/// the model rather than a compatibility shim: a bot with one doctrine has nothing to
		/// decide, so it decides nothing.
		/// </remarks>
		public static BattleBotDefinition Wrap(DoctrineDefinition doctrine)
		{
			ArgumentNullException.ThrowIfNull(doctrine);

			return new BattleBotDefinition(
				doctrine.Name,
				doctrine.Description,
				doctrine.Name,
				new Dictionary<string, DoctrineDefinition>(StringComparer.OrdinalIgnoreCase) { [doctrine.Name] = doctrine },
				[doctrine.Name]);
		}
	}
}
