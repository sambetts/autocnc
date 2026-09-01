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
using System.Linq;
using AutoCnC.Core;
using AutoCnC.Sdk;
using OpenRA;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace AutoCnC.Platform.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Runs the loaded battle bot for the local player: picks its doctrine as the match turns,",
		"and turns that doctrine's decisions into orders. Attach this to the world actor.")]
	public class ModeExecutorInfo : TraitInfo
	{
		[Desc("Battle bot to load at start. Leave empty to load the only installed bot,",
			"or to wait for the player to pick one with /bot.")]
		public readonly string DefaultBattleBot = null;

		[Desc("Number of addressable control groups.")]
		public readonly int GroupCount = 9;

		[Desc("Maximum orders emitted per tick, to keep the order stream sane with a large army.")]
		public readonly int MaxOrdersPerTick = 20;

		[Desc("Seconds of game time between asking the bot whether its doctrine still fits.")]
		public readonly int AssessInterval = 5;

		[Desc("Seconds a doctrine must run before another can replace it. This is what stops two",
			"rules that disagree from flipping the army back and forth; a bot can be stricter",
			"still by reading BattleState.DoctrineSeconds.")]
		public readonly int MinimumDoctrineSeconds = 30;

		[Desc("How far back BattleState's loss and kill counts reach, in seconds of game time.")]
		public readonly int AssessWindow = 60;

		[Desc("Cells from your base within which a visible enemy counts as being at your door.")]
		public readonly int BaseRadius = 15;

		[Desc("Log every decision to debug.log. Can also be toggled in-game with /modelog.")]
		public readonly bool LogDecisions = false;

		public override object Create(ActorInitializer init) { return new ModeExecutor(init.World, this); }
	}

	/// <summary>
	/// The host: runs whichever battle bot is loaded, for the local player's units only.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The platform contains <b>no strategy</b>. With no bot loaded this trait does nothing at all
	/// — nothing deploys, builds or shoots. All behaviour arrives from an
	/// <see cref="IBattleBot"/> the player authored or installed.
	/// </para>
	/// <para>
	/// Two decisions happen here, on very different clocks. Every tick, each unit's mode says what
	/// that unit should do. Every <c>AssessInterval</c> seconds, the bot says which doctrine the
	/// army should be running at all — and a doctrine carries its own build plan, production plan
	/// and assignments, so changing it changes what the whole side is trying to do.
	/// </para>
	/// <para>
	/// Execution is outside the lockstep simulation: this is client-local and only ever looks at
	/// <c>world.LocalPlayer</c>'s units, emitting <see cref="Order"/>s — the same channel a human
	/// player's clicks use. So a bot you wrote never has to exist on an opponent's machine.
	/// </para>
	/// </remarks>
	public class ModeExecutor : ITick, IWorldLoaded, IModeHost
	{
		readonly World world;
		readonly ModeExecutorInfo info;
		readonly List<Order> pending = [];

		BattleAssessor assessor;
		BattleLog battleLog;

		/// <summary>What a mode asked for, waiting for the next assessment to accept or refuse it.</summary>
		DoctrineDecision requested;

		int nextAssessTick;
		int doctrineStartedSeconds;

		/// <summary>The loaded battle bot, or null if none.</summary>
		public BattleBotDefinition Bot { get; private set; }

		/// <summary>The bot's own object, for asking it to reassess. Null for a wrapped doctrine.</summary>
		IBattleBot brain;

		/// <summary>The doctrine currently running, or null if none.</summary>
		public DoctrineDefinition Doctrine { get; private set; }

		/// <summary>Why the current doctrine is the one running. Empty for an opening.</summary>
		public string DoctrineReason { get; private set; }

		/// <summary>The last state the bot was shown, for /why and for the debug log.</summary>
		public BattleState LastAssessment { get; private set; } = BattleState.Empty;

		/// <summary>
		/// Live assignment state. Seeded from the doctrine, then mutable so a player can override
		/// in-game without editing code.
		/// </summary>
		public ModeAssignments Assignments { get; private set; }

		/// <summary>Base construction plan from the running doctrine. Empty if none.</summary>
		public IReadOnlyList<BuildStep> BuildPlan => Doctrine?.BuildPlan ?? [];

		/// <summary>Unit production plan from the running doctrine. Empty if none.</summary>
		public IReadOnlyList<ProductionStep> ProductionPlan => Doctrine?.ProductionPlan ?? [];

		/// <summary>The doctrine a mode is part of.</summary>
		public string ActiveDoctrine => Doctrine?.Name;

		/// <summary>The doctrines the loaded bot owns, in declaration order.</summary>
		public IReadOnlyList<string> DoctrineNames => Bot?.DoctrineNames ?? [];

		/// <summary>
		/// A mode asking for a different doctrine.
		/// </summary>
		/// <remarks>
		/// Recorded rather than acted on: the switch goes through the same assessment, and the
		/// same dwell time, as one the bot itself asked for. A scout that finds the enemy base can
		/// therefore say so every tick until somebody listens, without the army noticing — which
		/// is why a request naming the doctrine already running is dropped here rather than kept
		/// as a standing one nothing will ever clear.
		/// </remarks>
		public void RequestDoctrine(string doctrine, string reason)
		{
			if (Bot == null || string.IsNullOrEmpty(doctrine) || Bot.Find(doctrine) == null)
				return;

			if (string.Equals(doctrine, Doctrine?.Name, StringComparison.OrdinalIgnoreCase))
				return;

			requested = DoctrineDecision.SwitchTo(doctrine, reason);
		}

		/// <summary>Game time since the match started, on the clock every other part of this uses.</summary>
		int GameSeconds => (int)((long)world.WorldTick * TurboSpeed.NominalTimestep / 1000);

		/// <summary>When set, every issued decision is written to debug.log. Toggle with /modelog.</summary>
		public bool LogDecisions { get; set; }

		public ModeExecutor(World world, ModeExecutorInfo info)
		{
			this.world = world;
			this.info = info;
			LogDecisions = info.LogDecisions;
			Assignments = new ModeAssignments(null, info.GroupCount);
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// Proof of what actually happened, because the engine issues its own game speed order
			// when it launches a map and the two could silently disagree. 20ms/tick is 'fastest',
			// 40ms is 'default'.
			if (!string.IsNullOrEmpty(LaunchOptions.GameSpeed))
				Log.Write("debug", $"Game speed: {w.GameSpeed.Timestep}ms per tick (asked for '{LaunchOptions.GameSpeed}').");

			battleLog = w.WorldActor.TraitOrDefault<BattleLog>();

			if (w.LocalPlayer != null)
				assessor = new BattleAssessor(w, w.LocalPlayer, info.AssessWindow, info.BaseRadius);

			// A bot named on the command line wins: the launcher has just been told, very
			// explicitly, which code the player wants to watch fight.
			if (LoadFromLaunchArguments())
				return;

			// Otherwise load the configured bot, or the only one installed. Anything more
			// ambiguous is left to the player, so we never silently pick a strategy for them.
			var bots = BattleBotLoader.Bots;

			if (!string.IsNullOrEmpty(info.DefaultBattleBot))
				LoadBattleBot(info.DefaultBattleBot);
			else if (bots.Count == 1)
				Load(bots[0]);
		}

		/// <summary>
		/// Honours <c>Launch.BattleBot</c> / <c>Launch.BattleBotPath</c>. Returns false when
		/// neither was given, so the normal selection rules apply.
		/// </summary>
		bool LoadFromLaunchArguments()
		{
			var name = LaunchOptions.BattleBot;
			var path = LaunchOptions.BattleBotPath;

			if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(path))
				return false;

			// Resolving by path as well as by name means the launcher does not have to know the
			// bot's declared Name, which lives inside the assembly it was handed.
			var resolved = name != null ? BattleBotLoader.Find(name) : null;
			resolved ??= BattleBotLoader.FindFrom(path);

			if (resolved == null)
			{
				// Deliberately no fallback to DefaultBattleBot or the only installed bot: somebody
				// asked for specific code, and quietly playing different code instead would make a
				// losing match impossible to trust.
				var wanted = name ?? path;
				Log.Write("debug", $"Launch argument asked for battle bot '{wanted}', which is not installed.");
				TextNotificationsManager.Debug($"Could not load battle bot '{wanted}'. /bots lists what is installed.");

				foreach (var error in BattleBotLoader.Errors)
					TextNotificationsManager.Debug("Battle bot problem: " + error);

				return true;
			}

			Load(resolved);
			Log.Write("debug", $"Loaded battle bot '{resolved.Definition.Name}' from {resolved.SourcePath} (launch argument).");
			TextNotificationsManager.Debug($"Battle bot loaded: {resolved.Definition.Name} — {resolved.Definition.Description}");
			return true;
		}

		/// <summary>Loads a bot by name. Returns false if it isn't installed.</summary>
		public bool LoadBattleBot(string name)
		{
			var found = BattleBotLoader.Find(name);
			if (found == null)
				return false;

			Load(found);
			return true;
		}

		/// <summary>
		/// Loads a specific bot and starts it on its opening doctrine.
		/// </summary>
		/// <remarks>
		/// Taking the resolved bot rather than its name matters when two installed assemblies
		/// declare the same <c>Name</c> — looking the name up again would pick whichever was
		/// scanned first, not the one the caller had in its hand.
		/// </remarks>
		void Load(BattleBotLoader.LoadedBattleBot found)
		{
			Bot = found.Definition;
			brain = found.Instance;
			requested = DoctrineDecision.Continue;

			ApplyDoctrine(Bot.Find(Bot.Opening), reason: null);
		}

		/// <summary>
		/// Makes a doctrine the running one: its plans, its assignments, its modes.
		/// </summary>
		/// <remarks>
		/// Per-unit overrides are dropped, so every unit re-resolves against the new doctrine on
		/// the next tick. That is what makes a switch mean something — the army does not merely
		/// build different things, it fights differently.
		/// </remarks>
		void ApplyDoctrine(DoctrineDefinition doctrine, string reason)
		{
			if (doctrine == null)
				return;

			var previous = Doctrine?.Name;

			Doctrine = doctrine;
			DoctrineReason = reason;
			doctrineStartedSeconds = GameSeconds;
			Assignments = new ModeAssignments(doctrine.Assignments.GlobalMode, info.GroupCount);

			// Copy the doctrine's declared assignments into live, player-editable state.
			foreach (var kv in doctrine.Assignments.UnitTypeAssignments)
				Assignments.SetUnitType(kv.Key, kv.Value);

			foreach (var kv in doctrine.Assignments.GroupAssignments)
				Assignments.SetGroup(kv.Key, kv.Value);

			// Every unit re-resolves on the next tick, so a swap takes effect mid-match.
			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
				pair.Trait.ModeOverride = null;

			if (previous == null || previous == doctrine.Name)
				return;

			Log.Write("debug", $"[bot] {Bot?.Name}: {previous} -> {doctrine.Name} ({reason})");
			TextNotificationsManager.Debug($"Doctrine: {previous} -> {doctrine.Name} — {reason}");
			battleLog?.DoctrineChanged(previous, doctrine.Name, reason);
		}

		/// <summary>
		/// Asks the bot whether the doctrine it is running still fits the battle.
		/// </summary>
		/// <remarks>
		/// The bot is asked first and always, even when a mode has a request pending. A mode that
		/// wanted a switch every tick would otherwise starve the bot of its own assessment — and
		/// the rule it would starve first is the one that says the base is being taken apart.
		/// A request is the fallback: it carries when the bot has no opinion, which is exactly the
		/// case it exists for, a unit that has learned something the bot's view does not show yet.
		/// </remarks>
		void Assess()
		{
			nextAssessTick = world.WorldTick + Math.Max(1, 1000 * Math.Max(1, info.AssessInterval) / TurboSpeed.NominalTimestep);

			if (Bot == null || assessor == null)
				return;

			LastAssessment = assessor.Assess(Doctrine?.Name, GameSeconds - doctrineStartedSeconds);

			var asked = requested;
			requested = DoctrineDecision.Continue;

			var decision = DoctrineDecision.Continue;

			if (brain != null)
			{
				try
				{
					decision = brain.Reassess(LastAssessment);
				}
				catch (Exception ex)
				{
					// Author code runs here. A bot that throws keeps the doctrine it has rather
					// than taking the match down with it.
					Log.Write("debug", $"Battle bot '{Bot.Name}' threw while reassessing: {ex}");
					TextNotificationsManager.Debug($"Battle bot '{Bot.Name}' threw: {ex.Message}. Keeping {Doctrine?.Name}.");
					brain = null;
					return;
				}
			}

			if (!decision.WantsChange)
				decision = asked;

			// Naming the doctrine already running is the same as continuing, so a rule can state
			// its condition without also checking what is loaded.
			if (!decision.WantsChange || string.Equals(decision.Doctrine, Doctrine?.Name, StringComparison.OrdinalIgnoreCase))
				return;

			if (LastAssessment.DoctrineSeconds < info.MinimumDoctrineSeconds)
				return;

			var wanted = Bot.Find(decision.Doctrine);
			if (wanted == null)
			{
				Log.Write("debug", $"[bot] {Bot.Name} asked for doctrine '{decision.Doctrine}', which it does not own.");
				return;
			}

			ApplyDoctrine(wanted, decision.Reason);
		}

		/// <summary>Creates a mode instance from the running doctrine, or null.</summary>
		public IUnitMode CreateMode(string modeName)
		{
			if (Doctrine == null || string.IsNullOrEmpty(modeName))
				return null;

			if (!Doctrine.Modes.TryGetValue(modeName, out var type))
				return null;

			try
			{
				return (IUnitMode)Activator.CreateInstance(type);
			}
			catch (Exception ex)
			{
				Log.Write("debug", $"Failed to construct mode '{modeName}': {ex}");
				return null;
			}
		}

		public bool IsKnownMode(string modeName) =>
			Doctrine != null && !string.IsNullOrEmpty(modeName) && Doctrine.Modes.ContainsKey(modeName);

		public string CanonicalModeName(string modeName)
		{
			if (Doctrine == null || string.IsNullOrEmpty(modeName))
				return null;

			return Doctrine.Modes.TryGetValue(modeName, out var type) ? type.Name : null;
		}

		public IEnumerable<string> AvailableModeNames =>
			Doctrine == null
				? Array.Empty<string>()
				: Doctrine.Modes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

		/// <summary>Switches doctrine by hand, bypassing the bot. Returns false if it has no such doctrine.</summary>
		public bool ForceDoctrine(string name, string reason)
		{
			var wanted = Bot?.Find(name);
			if (wanted == null)
				return false;

			ApplyDoctrine(wanted, reason);
			return true;
		}

		void ITick.Tick(Actor self)
		{
			var player = world.LocalPlayer;
			if (player == null || world.IsReplay || Bot == null)
				return;

			// The bot's clock is slower than the units': what to build and how to fight is a
			// question about the shape of the match, and asking it every tick would answer it
			// with noise.
			if (world.WorldTick >= nextAssessTick)
				Assess();

			if (Doctrine == null)
				return;

			pending.Clear();

			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
			{
				var actor = pair.Actor;
				var controller = pair.Trait;

				if (actor.Owner != player || actor.IsDead || !actor.IsInWorld || controller.IsTraitDisabled)
					continue;

				SyncGroup(actor, controller);
				SyncMode(actor, controller);

				if (controller.ActiveMode == null)
					continue;

				if (world.WorldTick < controller.NextEvaluationTick)
					continue;

				var interval = Math.Max(1, controller.Info.TickInterval);
				controller.NextEvaluationTick = world.WorldTick + interval;

				Evaluate(actor, controller);

				if (pending.Count >= info.MaxOrdersPerTick)
					break;
			}

			foreach (var order in pending)
				world.IssueOrder(order);
		}

		void Evaluate(Actor actor, ProgrammableController controller)
		{
			UnitDecision decision;

			try
			{
				decision = controller.ActiveMode.OnTick(actor, controller.Context);
			}
			catch (Exception ex)
			{
				// Doctrine code runs here. One bad mode must not take the game down.
				Log.Write("debug", $"Mode '{controller.ActiveModeName}' threw on {actor.Info.Name}: {ex}");
				TextNotificationsManager.Debug($"Mode '{controller.ActiveModeName}' threw: {ex.Message}");
				controller.ModeOverride = null;
				controller.ApplyMode(null, this);
				return;
			}

			if (decision.Action == UnitAction.Continue)
				return;

			// A unit that is already idle has achieved Hold, so re-sending Stop would spam the
			// order stream every evaluation for every idle unit — which is most of an army, most
			// of the time.
			if (decision.Action == UnitAction.Hold && actor.IsIdle)
			{
				controller.LastIssued = decision;
				return;
			}

			// Re-issue only when the intent changed, or the unit has gone idle and still wants
			// something done. Otherwise a steady decision would emit an order every evaluation.
			var repeat = decision.SameIntent(controller.LastIssued);
			if (repeat && !actor.IsIdle)
				return;

			var order = controller.Context.BuildOrder(decision);
			if (order == null)
				return;

			if (LogDecisions)
				Log.Write("debug", $"[mode] {actor.Info.Name}#{actor.ActorID} {controller.ActiveModeName}: " +
					$"{decision.Action}{(decision.ItemName != null ? " " + decision.ItemName : "")} " +
					$"-> {order.OrderString} ({decision.Reason})");

			controller.LastIssued = decision;
			pending.Add(order);
		}

		/// <summary>Mirrors the engine's client-local control groups onto the controller.</summary>
		void SyncGroup(Actor actor, ProgrammableController controller)
		{
			var group = world.ControlGroups.GetControlGroupForActor(actor);
			controller.GroupId = group.HasValue ? group.Value + 1 : 0;
		}

		void SyncMode(Actor actor, ProgrammableController controller)
		{
			// Role lets a Doctrine say "whatever builds the base" without naming actor types.
			var role = controller.Info.Role;
			var byRole = role != null ? Assignments.GetUnitType(role) : null;

			var resolved = Assignments.Resolve(
				controller.ModeOverride, controller.GroupId, actor.Info.Name, byRole);

			if (resolved != null && !IsKnownMode(resolved))
				resolved = null;

			controller.ApplyMode(resolved, this);
		}

		/// <summary>Clears every per-unit override, so the Doctrine's assignments apply again.</summary>
		public void ClearUnitOverrides()
		{
			foreach (var pair in world.ActorsWithTrait<ProgrammableController>())
				if (pair.Actor.Owner == world.LocalPlayer)
					pair.Trait.ModeOverride = null;
		}

		/// <summary>Every programmable unit the local player owns.</summary>
		public IEnumerable<Actor> LocalUnits =>
			world.ActorsWithTrait<ProgrammableController>()
				.Where(p => p.Actor.Owner == world.LocalPlayer && !p.Actor.IsDead && p.Actor.IsInWorld)
				.Select(p => p.Actor);
	}
}
