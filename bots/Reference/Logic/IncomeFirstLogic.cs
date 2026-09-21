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

namespace AutoCnC.Reference.Logic
{
	/// <summary>Tunable knobs for <see cref="IncomeFirstLogic"/>.</summary>
	/// <remarks>
	/// <paramref name="HarvesterPrice"/> is a ruleset price, not a tuned number: <c>harv</c>
	/// costs 1,100 for both factions and needs only <c>proc</c>. It is the bar because it is
	/// exactly the sum the barracks has to stop taking for the vehicle queue to finish one.
	/// <paramref name="ScreenVehiclePrice"/> is the larger price in the faction-portable
	/// <see cref="ReferencePlans.ScreenVehicles"/> pair: 400 for <c>jeep</c>, while <c>bggy</c>
	/// costs 300. Reserving the larger amount makes the same gate sufficient for either faction.
	/// <para>
	/// <paramref name="GarrisonBodies"/> is the plan's own idea of "enough not to die to a
	/// rush": <see cref="ReferencePlans.OpeningTrain"/> opens on four rifles and four rockets
	/// before it buys anything else, so four is what a barracks is allowed to keep replacing
	/// while the economy is being rescued. It is a cap, never a target — a rung already asking
	/// for four or fewer is left exactly as its doctrine wrote it.
	/// </para>
	/// </remarks>
	public readonly record struct IncomeFirstTuning(
		int HarvesterPrice,
		int ScreenVehiclePrice,
		int GarrisonBodies,
		int EmplacementPrice)
	{
		public static IncomeFirstTuning Default { get; } = new(
			HarvesterPrice: 1100,
			ScreenVehiclePrice: 400,
			GarrisonBodies: 4,

			// A ruleset price, not a tuned number: gtwr and gun both cost 600, both reach six
			// cells, and both need only a barracks.
			EmplacementPrice: 600);
	}

	/// <summary>What <see cref="IncomeFirstLogic.Hold"/> did, and on what evidence.</summary>
	/// <remarks>
	/// The counts come back so the caller can put them in the decision reason. Whether this rule
	/// fired is not inferable from what got built — a barracks that buys four riflemen looks the
	/// same whether it was capped at four or simply had no fifth rung to reach — so it has to
	/// say so itself.
	/// </remarks>
	public readonly record struct IncomeHold(
		IReadOnlyList<ProductionStep> Plan,
		int Standing,
		int ShortBelow,
		int Cash,
		int RungsCapped)
	{
		public bool Held => RungsCapped > 0;
	}

	/// <summary>
	/// The prices and the floor the opening-bank reservation is written against.
	/// </summary>
	/// <remarks>
	/// <paramref name="RefineryPrice"/> is a ruleset price, not a tuned number: <c>proc</c>
	/// costs 1,500 for both factions, needs only <c>anypower</c>, and ships a harvester with it.
	/// <paramref name="RefineryFloor"/> is where the reservation stops: three earners is the
	/// point at which <see cref="ReferencePlans.Economy"/> has bought its barracks and its
	/// vehicle factory as well, so the side has both an economy and the means to replace it and
	/// no longer needs the bank held open.
	/// </remarks>
	public readonly record struct OpeningBankTuning(int RefineryPrice, int RefineryFloor)
	{
		public static OpeningBankTuning Default { get; } = new(
			RefineryPrice: 1500,
			RefineryFloor: 3);
	}

	/// <summary>How long a construction hold may wait for a harvester that is not arriving.</summary>
	/// <remarks>
	/// Ticks rather than evaluations, because a mode is re-evaluated on demand as well as on a
	/// clock: the construction yard held 149 times in the 131 seconds from 600s to 731s on 16:9,
	/// so a bound counted in evaluations would mean a different thing in a quiet minute than in a
	/// loud one. Sixty game seconds at the nominal 25 ticks a second is comfortably longer than
	/// any healthy delivery — an 1,100 credit harvester is about thirty seconds of a working
	/// economy — and far shorter than a deadlock.
	/// </remarks>
	public readonly record struct RecoveryHoldTuning(int StallTicks)
	{
		public static RecoveryHoldTuning Default { get; } = new(StallTicks: 1500);
	}

	/// <summary>What a bounded construction hold remembers between evaluations.</summary>
	/// <remarks>
	/// Per-yard memory, so it belongs in a mode instance field rather than a static. See
	/// <see cref="IncomeFirstLogic.TrackRecovery"/>.
	/// </remarks>
	public readonly record struct RecoveryHoldWatch(int LastRemainingCost, int FundedTick)
	{
		/// <summary>Nothing observed yet: the next evaluation starts the clock.</summary>
		public static RecoveryHoldWatch Start { get; } = new(int.MaxValue, int.MinValue);
	}

	/// <summary>Whether the recovery hold is still buying a harvester, and what to remember.</summary>
	public readonly record struct RecoveryHoldOutcome(
		bool Hold, bool Stalled, RecoveryHoldWatch Watch);

	/// <summary>
	/// The prices and bounds the mid-game harvester reservation is written against.
	/// </summary>
	/// <remarks>
	/// <paramref name="HarvesterPrice"/> is the same ruleset price
	/// <see cref="IncomeFirstTuning.HarvesterPrice"/> carries: 1,100 for both factions, needing
	/// only <c>proc</c>. It is what must be kept out of the other queues' reach, because it is
	/// exactly the sum the vehicle queue has to hold at once and never does.
	/// <para>
	/// <paramref name="RefineryFloor"/> is <see cref="OpeningBankTuning.RefineryFloor"/>, read
	/// from it rather than written down again, because the two reservations hand over to each
	/// other and a handover expressed as two separate numbers is a hole.
	/// <b>It used to be one higher, and the hole was 145 seconds of the only calm window 16:9
	/// ever gave this bot.</b> The opening bank stood down when the third refinery landed at
	/// 250s; the fourth did not stand until 455s. Across those 205 seconds neither reservation
	/// was active, the fleet sat at two and three harvesters against the six docking places
	/// three refineries provide, and cash read 0 at 32 of the 41 assessments in the window. The
	/// harvester ordered at 344s took 91 seconds to deliver an 1,100-credit item out of that
	/// trickle. By the time the floor of four was met the base had been under attack for fifty
	/// seconds and stayed that way for the rest of the match, so
	/// <c>economy.bank-replaces-harvester</c> was recorded at <b>none</b> of the 267
	/// assessments: the rule was unreachable by construction rather than wrong.
	/// </para>
	/// <para>
	/// The old argument for the gap was that <see cref="BattleState"/> cannot see a vehicle
	/// factory, so handing over at three would arm the reservation in the window the yard needs
	/// 2,000 credits clear to buy one. Two things answer it. A reservation naming a queue the
	/// side does not own resolves as <em>unmatched</em> and suppresses nothing, so before a
	/// factory stands this rule costs the yard nothing at all; and <paramref name="DutySeconds"/>
	/// stands the reservation down for a full interval whenever it has not grown the fleet, so
	/// even a reservation that somehow bit would be released half the time rather than latched.
	/// A bounded worst case beats a guaranteed hole.
	/// </para>
	/// <para>
	/// <paramref name="HarvestersPerRefinery"/> is the docking ratio the rest of the bot already
	/// plans to — see <see cref="ExpansionTuning.HarvestersPerRefinery"/> — so the fleet this
	/// defends is the one the refineries already standing were bought to feed, not a number
	/// invented here.
	/// </para>
	/// <para>
	/// <paramref name="FleetCeiling"/> is <c>ReferencePlans</c>' own saturation figure, so this
	/// rule defends the fleet the <c>Vehicle</c> plan already asks for rather than a second,
	/// larger target of its own. It is a ceiling rather than a goal: past it the vehicle queue is
	/// free to buy whatever else it wants without this rule having an opinion.
	/// </para>
	/// <para>
	/// <paramref name="DutySeconds"/> is the bound, and it runs in both directions on purpose.
	/// See <see cref="IncomeFirstLogic.ReserveHarvesterRecovery"/>.
	/// </para>
	/// </remarks>
	public readonly record struct HarvesterBankTuning(
		int HarvesterPrice,
		int RefineryFloor,
		int HarvestersPerRefinery,
		int FleetCeiling,
		int DutySeconds)
	{
		public static HarvesterBankTuning Default { get; } = new(
			HarvesterPrice: 1100,

			// The opening reservation's own floor, so the handover is exact rather than
			// approximately right in two places.
			RefineryFloor: OpeningBankTuning.Default.RefineryFloor,
			HarvestersPerRefinery: 2,
			FleetCeiling: 8,

			// Sixty game seconds, the same bound RecoveryHoldTuning.StallTicks uses for the
			// construction-side hold and comfortably longer than an 1,100 credit delivery on a
			// working economy.
			DutySeconds: 60);
	}

	/// <summary>What the harvester reservation remembers between assessments.</summary>
	/// <remarks>
	/// Per-match memory, so it belongs in a field on the bot rather than a static. A reservation
	/// is armed from the fleet count it was armed at, so "has this bought anything" is a
	/// comparison rather than a guess.
	/// </remarks>
	public readonly record struct HarvesterBankWatch(
		int ArmedSeconds, int ArmedFleet, int ReleasedUntilSeconds)
	{
		/// <summary>Nothing armed and nothing standing down: the next assessment decides freely.</summary>
		public static HarvesterBankWatch Idle { get; } = new(int.MinValue, int.MinValue, int.MinValue);
	}

	/// <summary>The reservation to return, and what to remember for the next assessment.</summary>
	public readonly record struct HarvesterBankOutcome(
		ProductionBudget Budget, HarvesterBankWatch Watch);

	/// <summary>
	/// Stops the cheapest queue on the field from spending the credits the only queue that can
	/// buy income is waiting for.
	/// </summary>
	/// <remarks>
	/// <b>Rung order cannot solve this, because the queues are separate.</b> Every previous fix
	/// in this bot reordered a production plan so that income sat above armour, and
	/// <see cref="ArmyBalanceLogic"/> then stopped an unmeetable income rung from starving what
	/// sat below it. Both of those arbitrate <em>within</em> one queue. Cash is shared across all
	/// of them, and nothing arbitrated that at all.
	/// <para>
	/// <b>What that cost on badland-ridges.</b> The airfield stood at 474s and lived for roughly
	/// 540 seconds. In that time the <c>Vehicle</c> queue issued <b>two</b> orders — both
	/// <c>harv</c>, 2,200 credits — and spent 520 of those seconds waiting: the harvester ordered
	/// when the airfield opened was not delivered until 617s, <b>143 seconds</b> for an 1,100
	/// credit item. Over the same match the <c>Infantry</c> queue issued <b>59</b> orders worth
	/// 8,900 credits, a 100-credit rifleman roughly every twenty seconds. Cash read 0 or 1 at
	/// every one of the twenty economy samples from 180s onward, and lifetime spend tracked
	/// lifetime earnings to the credit. A queue buying 100-credit items always wins that race
	/// against one buying an 1,100-credit item, and the item it beat was the entire economy: the
	/// fleet averaged <b>1.06 live harvesters</b> across a 1,187-second match with four
	/// refineries standing from 260s, income finished at 17.2 credits a second against a
	/// reference of 50, and 550 seconds of the match had no live harvester at all.
	/// </para>
	/// <para>
	/// The harvesters themselves were not the problem and that is worth stating, because three
	/// previous rounds looked there. Across 1,260 harvester-seconds the side earned 20,475
	/// credits — <b>16.3 credits per harvester per second</b>, which is a full load every
	/// forty-odd seconds and about as well as a harvester can do. There were simply almost none
	/// of them.
	/// </para>
	/// <para>
	/// So the rule is a cap on the cheap queue, conditioned on all three things being true at
	/// once, and it is deliberately narrow on each:
	/// </para>
	/// <list type="number">
	/// <item>A vehicle factory is standing, so a harvester is actually buyable. Before one is up
	/// the only harvester on offer is a refinery's free actor and holding infantry buys
	/// nothing.</item>
	/// <item>The fleet is short by the band <see cref="ArmyBalanceLogic.ReleaseAt"/> already
	/// defines, rather than by a second threshold invented here. A fleet inside that band is the
	/// one that queue is allowed to stop asking about, so it is also the one this rule must stop
	/// protecting.</item>
	/// <item>Cash is below a harvester's price. This is what makes the rule self-releasing: the
	/// instant the side can afford a harvester and a rifleman, ordering the rifleman costs the
	/// harvester nothing and the cap lifts on its own. On a healthy economy it never fires.</item>
	/// </list>
	/// <para>
	/// It caps rather than silences, so the base is never naked: every rung keeps replacing up to
	/// <see cref="IncomeFirstTuning.GarrisonBodies"/>, and the towers this bot builds from the
	/// <c>Support</c> queue were the most efficient thing on the field anyway — four <c>gtwr</c>
	/// cost 2,400 credits and killed 47 units for 10,740, at 51 credits a kill against the
	/// infantry queue's 171.
	/// </para>
	/// <para>
	/// Endless rungs are capped too, and that is load-bearing. The endless rung is the queue's
	/// answer whenever everything above it is met, so a rule that skipped it would hold the
	/// bounded rungs and leak the whole surplus through the last one.
	/// </para>
	/// <para>
	/// <see cref="HoldForPriority"/> applies the same arbitration to the light-vehicle screen.
	/// The harvester-specific entry point remains separate so economy rescue keeps its own
	/// threshold and reason.
	/// </para>
	/// </remarks>
	public static class IncomeFirstLogic
	{
		/// <summary>
		/// Holds the price of the next refinery out of the reach of every queue except the one
		/// that can build it, while the side is still short of earners.
		/// </summary>
		/// <remarks>
		/// <b>Every income-first rule this bot already has is switched off for the first two
		/// minutes of a match, by design, and that is the window the last one was lost in.</b>
		/// <see cref="Hold"/> and <see cref="HoldForPriority"/> both require the priority role to
		/// be <em>buildable</em>, and a harvester is not buildable until a vehicle factory
		/// stands. So between the construction yard landing and the factory landing, nothing
		/// arbitrates the shared bank at all and the cheapest queue wins every race.
		/// <para>
		/// On the latest 16:9 that window was 129 seconds long and it decided the match. The
		/// side spent, in order: <c>nuke</c> 500, <c>proc</c> 1,500, <c>nuke</c> 500,
		/// <c>hand</c> 500, four <c>e1</c> 400, <c>gtwr</c> 600, four <c>e3</c> 1,200,
		/// <c>afld</c> 2,000, <c>sam</c> 650, <c>bggy</c> 300 — <b>8,150 credits by 148s, with
		/// one refinery standing</b>. The second refinery, at 1,500 the cheapest harvester on
		/// offer and the only one buyable without a factory, could not be started until income
		/// had rebuilt its price on its own and stood at <b>249s</b>. One harvester worked from
		/// 51s to 249s; the match finished on <b>3,955 credits earned at 5.9 a second</b>
		/// against a prior median of 28.6, with a mean army of 636 against a reference of 6,000
		/// and a fitness of 0.183 against a prior median of 0.402. The army did not lose that
		/// match, the bank did: 2,850 of those credits went to <c>Infantry</c> and
		/// <c>Support</c> — nearly two refineries — and the 1,200 spent on four <c>e3</c>
		/// bought <b>zero</b> kills.
		/// </para>
		/// <para>
		/// A reservation is the right shape for this and a rung reorder is not, because the
		/// problem is <em>between</em> queues. <see cref="ReferencePlans.Economy"/> now asks for
		/// the second refinery before the barracks, but the yard can only ask for one thing at a
		/// time and the other three queues keep spending while it waits for a site, a power
		/// balance or a build time. The host arbitrates this centrally: the owning queue may
		/// spend the reservation and every other queue's new <c>Produce</c> is suppressed when
		/// it would take live cash below what is left of it.
		/// </para>
		/// <para>
		/// It is deliberately the narrowest reservation that fixes the observed loss:
		/// </para>
		/// <list type="number">
		/// <item><b>One refinery's price, never the whole ladder.</b> 1,500 against a 7,500
		/// opening bank leaves the other queues free until cash is genuinely scarce, so an
		/// opening garrison still gets bought — it is the <em>last</em> 1,500 that is defended,
		/// which is exactly the sum the yard was short of.</item>
		/// <item><b>Only while earners are short.</b> Past
		/// <see cref="OpeningBankTuning.RefineryFloor"/> the side has three refineries, a
		/// barracks and a factory, the existing harvester rules take over, and this one is
		/// silent for the rest of the match.</item>
		/// <item><b>Never while the base is being fought over.</b> Every hold in this file has
		/// at some point become a latch, because each is keyed on a condition the opponent
		/// controls — see <see cref="IsOpeningEmplacement"/> and <see cref="TrackRecovery"/> for
		/// the two that already had to be bounded. A refinery is worth nothing to a side losing
		/// its yard this minute, so enemies at the base release the bank to whatever can shoot
		/// them.</item>
		/// </list>
		/// </remarks>
		public static ProductionBudget ReserveOpeningBank(
			in BattleState s, string queue, in OpeningBankTuning t)
		{
			if (string.IsNullOrEmpty(queue) || t.RefineryPrice <= 0 || t.RefineryFloor <= 0)
				return ProductionBudget.None;

			// Bodies now beat income later once they are already inside the base.
			if (s.EnemiesNearBase > 0 || s.BaseUnderAttack)
				return ProductionBudget.None;

			if (s.Refineries >= t.RefineryFloor)
				return ProductionBudget.None;

			return ProductionBudget.Reserve(
				t.RefineryPrice,
				queue,
				$"opening bank held for refinery {s.Refineries + 1}: {s.Refineries} earner(s) standing "
					+ $"and {s.Harvesters} harvester(s) working on {s.IncomeEarned} credits earned",
				"economy.bank-buys-income");
		}

		/// <summary>
		/// Keeps one harvester's price out of every other queue's reach while the fleet is
		/// collapsing, for as long as that is buying a harvester and no longer.
		/// </summary>
		/// <remarks>
		/// <b><see cref="ReserveOpeningBank"/> stops at three refineries, and 16:9 was lost
		/// after it stopped.</b> The reservation it owns is the only cross-queue arbitration
		/// this bot has, it is switched off for the remaining ninety per cent of the match, and
		/// the race it was written for does not end when the opening does — it gets worse, because
		/// by then the barracks has five unmet rungs instead of two.
		/// <para>
		/// The fight it is written from. The fleet peaked at nine harvesters at 780s and was
		/// destroyed between 1,140s and 1,320s: nine, six, four, one, none. A war factory stood
		/// until 1,437s and a second until 1,505s, so a harvester was buyable throughout — and
		/// <b>the last <c>harv</c> of the match was ordered at 747s</b>, 690 seconds before the
		/// factory died. Nothing declined to buy one. The <c>Vehicle</c> queue simply never had
		/// 1,100 credits at once: cash sampled 1, 0 and 157 across that window while the
		/// <c>Infantry</c> queue delivered <c>e1</c> at 1,218s and <c>e3</c> at 1,440s out of the
		/// same trickle. Income froze at 55,930 credits from 1,260s, <b>585 seconds — 32% of the
		/// match — had no live harvester</b>, and the side finished at 30.8 credits a second
		/// against a reference of 50 and a prior median of 38.5.
		/// </para>
		/// <para>
		/// <see cref="Hold"/> is aimed at the same race and cannot win it, which is why this is a
		/// reservation rather than another cap. It caps each <c>Infantry</c> rung at
		/// <see cref="IncomeFirstTuning.GarrisonBodies"/>, and the barracks answers with the next
		/// rung: four rifles, four rockets, four tech infantry, four more rockets, four more
		/// rifles. Infantry die, so every one of those rungs is unmet again within seconds and
		/// the queue can always find a 100-credit item to spend the 100 credits that just
		/// arrived on. Capping a queue cannot stop it spending what it has; only the host's
		/// central arbitration can, and it already does exactly this.
		/// </para>
		/// <para>
		/// It is self-limiting by construction and needs no cash threshold of its own. The host
		/// suppresses another queue's new order only when that order's full cost would take live
		/// cash <em>below</em> what is left of the reservation, so a side with 2,000 in the bank
		/// buys its rifleman as usual and only the last 1,100 is defended — which is exactly the
		/// sum the vehicle queue was short of.
		/// </para>
		/// <para>
		/// Every hold in this file has at some point become a latch, so this one is bounded in
		/// both directions before it is bounded on either. It stands down after
		/// <see cref="HarvesterBankTuning.DutySeconds"/> in which the fleet has not grown —
		/// evidence that the reservation is funding nothing — and it re-arms the same interval
		/// later rather than permanently, because a fleet stuck at zero with a factory standing
		/// is the case that most needs it. The side is therefore never frozen for more than one
		/// interval at a time, and never gives up on its economy for more than one either.
		/// </para>
		/// <para>
		/// The worst case is milder than that anyway, and the host is what makes it so: a
		/// reservation naming a queue this side does not own resolves as <em>unmatched</em> and
		/// suppresses nothing at all. So the obvious way for this rule to strand the bank — the
		/// war factory dying while the fleet is short — cannot happen, because the queue the
		/// reservation names dies with it. The duty cycle covers the case the host cannot see:
		/// a factory that is standing and still not buying.
		/// </para>
		/// <para>
		/// The three releases each have a different shape now, and the middle one is where the
		/// last fight was lost. A base that is genuinely being overrun buys bodies rather than
		/// income — but "overrun" is the enemy at the base outvaluing the army standing in it,
		/// not merely a unit of theirs being visible, because the latter describes most of a
		/// match against an opponent that raids. A side below the refinery floor is the opening
		/// rule's business. And a fleet that has reached the docking places its refineries
		/// provide is not short of anything.
		/// </para>
		/// </remarks>
		public static HarvesterBankOutcome ReserveHarvesterRecovery(
			in BattleState s, string queue, in HarvesterBankWatch watch, in HarvesterBankTuning t)
		{
			if (string.IsNullOrEmpty(queue) || t.HarvesterPrice <= 0 || t.RefineryFloor <= 0)
				return new HarvesterBankOutcome(ProductionBudget.None, HarvesterBankWatch.Idle);

			// Bodies now beat income later once the base is actually losing the fight at home.
			//
			// "Any enemy near the base, or the base recently damaged" is not that test, and
			// against an opponent that raids continuously it is not a test at all: on 16:9
			// BaseUnderAttack read true at every one of the 186 assessments from 405s to the
			// end of the match — 70% of the fight, and 100% of it after the refinery floor was
			// met. A single sighted scout disarmed the only rule this bot has for keeping its
			// economy alive, and kept it disarmed for the rest of the game.
			//
			// So the release is proportionate instead: it lifts when what is standing in the
			// base is outvalued by what has come to kill it, which is when a rifleman bought
			// this second genuinely beats a harvester bought in thirty. At 405s that read 650
			// against 2,200 and the bank would have held; by 425s it read 3,750 against 2,400
			// and the bank stands down, which is the fight this clause was written for. Both
			// figures are ordinary visibility-filtered BattleState fields, so this knows no
			// more about the enemy than the sighting that produced it.
			if (s.EnemiesNearBase > 0 && s.EnemyValueNearBase > s.OwnArmyValueNearBase)
				return new HarvesterBankOutcome(ProductionBudget.None, HarvesterBankWatch.Idle);

			// Below the floor the opening reservation owns the bank, and handing over at exactly
			// its floor is what stops the two of them leaving a gap between them.
			if (s.Refineries < t.RefineryFloor)
				return new HarvesterBankOutcome(ProductionBudget.None, HarvesterBankWatch.Idle);

			var docking = s.Refineries * t.HarvestersPerRefinery;
			if (docking > t.FleetCeiling)
				docking = t.FleetCeiling;

			if (s.Harvesters >= docking)
				return new HarvesterBankOutcome(ProductionBudget.None, HarvesterBankWatch.Idle);

			// Standing down: wait out the rest of the interval before trying again.
			if (watch.ReleasedUntilSeconds != int.MinValue)
			{
				if (s.Seconds < watch.ReleasedUntilSeconds)
					return new HarvesterBankOutcome(ProductionBudget.None, watch);

				return Arm(s, queue, docking, t);
			}

			if (watch.ArmedSeconds == int.MinValue)
				return Arm(s, queue, docking, t);

			// The fleet growing is the only evidence that the reservation is buying anything.
			if (s.Harvesters > watch.ArmedFleet)
				return Arm(s, queue, docking, t);

			if (t.DutySeconds > 0 && s.Seconds - watch.ArmedSeconds >= t.DutySeconds)
				return new HarvesterBankOutcome(
					ProductionBudget.None,
					new HarvesterBankWatch(int.MinValue, int.MinValue, s.Seconds + t.DutySeconds));

			return new HarvesterBankOutcome(
				Reserve(s, queue, docking, t),
				watch);
		}

		static HarvesterBankOutcome Arm(
			in BattleState s, string queue, int docking, in HarvesterBankTuning t) =>
			new(Reserve(s, queue, docking, t),
				new HarvesterBankWatch(s.Seconds, s.Harvesters, int.MinValue));

		static ProductionBudget Reserve(
			in BattleState s, string queue, int docking, in HarvesterBankTuning t) =>
			ProductionBudget.Reserve(
				t.HarvesterPrice,
				queue,
				$"bank held for harvester {s.Harvesters + 1}: {s.Harvesters} working the "
					+ $"{docking} docking place(s) {s.Refineries} refinery(ies) provide, "
					+ $"on {s.IncomeEarned} credits earned",
				"economy.bank-replaces-harvester");

		/// <summary>
		/// Whether <paramref name="item"/> is the base's first emplacement of a defensive role it
		/// does not yet have at all.
		/// </summary>
		/// <remarks>
		/// The economy holds in <see cref="Modes.BuildBaseMode"/> already exempt power plants,
		/// because a brownout slows the harvester they are saving for. A base with no emplacement
		/// loses that harvester outright, which is strictly worse, and the hold that protects it
		/// is keyed on a condition the <em>opponent</em> controls: harvesters below the release
		/// band. Harvesters that are being hunted are permanently below it, so the hold stops
		/// being a deferral and becomes a latch.
		/// <para>
		/// On 16:9 that latch froze the construction yard for 208 of its 593 evaluations, the
		/// yard ordered nothing whatsoever between 129s and 242s, and the first and only tower of
		/// the match was ordered at 399s and stood at 435s — 40 seconds <em>after</em> the assault
		/// that ended the match had already begun. Nothing anti-air ever finished, and enemy
		/// <c>heli</c> killed all four refineries. The one <c>gtwr</c> that did stand was the best
		/// buy on the field by a wide margin: 600 credits for 1,400 killed, against 675 credits a
		/// kill for <c>e1</c> and 750 for <c>e3</c>.
		/// </para>
		/// <para>
		/// So the exemption is deliberately the narrowest one that fixes it: <b>one of each
		/// role, and only while that role has nothing standing</b>. It cannot become a turtle,
		/// because it stops answering the moment a ground tower and an anti-air tower both
		/// exist; depth stays behind the hold where the doctrine's plan put it. Both roles are
		/// candidate lists, so the rule reads the same for either faction, and a faction whose
		/// anti-air is still behind tech simply builds the ground half and waits.
		/// </para>
		/// </remarks>
		public static bool IsOpeningEmplacement(
			string item,
			IReadOnlyDictionary<string, int> owned,
			IReadOnlyList<string> groundDefence,
			IReadOnlyList<string> airDefence)
		{
			if (string.IsNullOrEmpty(item))
				return false;

			if (Names(groundDefence, item) && ExpansionLogic.Standing(owned, groundDefence) == 0)
				return true;

			return Names(airDefence, item) && ExpansionLogic.Standing(owned, airDefence) == 0;
		}

		static bool Names(IReadOnlyList<string> role, string item)
		{
			if (role == null)
				return false;

			for (var i = 0; i < role.Count; i++)
				if (string.Equals(role[i], item, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		/// <summary>
		/// Whether a construction hold for harvester recovery is still paying for one.
		/// </summary>
		/// <remarks>
		/// <b>A deferral with no deadline is a deadlock, and this one had none.</b> The hold in
		/// <see cref="Modes.BuildBaseMode"/> keeps construction cash free so the vehicle queue can
		/// finish a harvester, and it is keyed on a condition the opponent controls — the fleet
		/// below its release band. <see cref="IsOpeningEmplacement"/> already documents that as a
		/// latch and answers it with one tower of each role; that exemption is a floor under the
		/// damage, not a release.
		/// <para>
		/// On 16:9 the yard returned that hold on <b>293</b> of roughly 600 evaluations, and
		/// <b>149</b> of them fell between 600s and 731s — a window in which lifetime earnings
		/// were frozen at 19,950 and cash read 0 at every sample. The hold was reserving credits
		/// that did not exist, for a harvester that could not be paid for, and while it ran the
		/// yard issued no construction order, reached neither the frontier nor the anti-air
		/// branch, and declined to repair: the <c>Support</c> queue was asked nine times in the
		/// whole match and the side finished with three guard towers and one anti-air tower
		/// against a plan that asks for six and two. Those four emplacements were the best buy on
		/// the field by a wide margin — 2,800 credits for 13,100 killed, against 3,200 credits
		/// for 6,900 from every rifleman built.
		/// </para>
		/// <para>
		/// So the hold is measured against the thing it claims to be funding. The vehicle queue's
		/// <c>CurrentRemainingCost</c> falls as an item is paid off, so a harvester whose
		/// remaining cost has not moved is a harvester nothing is being spent on. The clock resets
		/// the moment it moves, which makes a healthy economy's hold indefinite and a dead
		/// economy's hold bounded — the distinction the old rule could not draw. A vehicle queue
		/// building something other than a harvester counts as no progress for the same reason:
		/// there is no delivery to wait for.
		/// </para>
		/// </remarks>
		public static RecoveryHoldOutcome TrackRecovery(
			bool recovering,
			bool harvesterInProduction,
			int harvesterRemainingCost,
			int worldTick,
			in RecoveryHoldWatch watch,
			in RecoveryHoldTuning t)
		{
			if (!recovering)
				return new RecoveryHoldOutcome(false, false, RecoveryHoldWatch.Start);

			// A hold that has only just started has not failed at anything yet.
			var opening = watch.FundedTick == int.MinValue;
			var funded = opening
				|| (harvesterInProduction && harvesterRemainingCost < watch.LastRemainingCost);

			var next = new RecoveryHoldWatch(
				harvesterInProduction ? harvesterRemainingCost : int.MaxValue,
				funded ? worldTick : watch.FundedTick);

			var stalled = t.StallTicks > 0 && worldTick - next.FundedTick >= t.StallTicks;
			return new RecoveryHoldOutcome(!stalled, stalled, next);
		}

		/// <summary>
		/// Whether a refinery the yard wants to build is itself the harvester recovery the hold
		/// is waiting for.
		/// </summary>
		/// <remarks>
		/// <c>proc</c> carries <c>FreeActor</c>: it ships a harvester with it. So a refinery is
		/// 1,500 credits for an 1,100 credit harvester <em>and</em> the dock that harvester needs,
		/// and it is the only harvester available at all once the war factory has been killed.
		/// Holding construction cash to help the vehicle queue buy one, while refusing to build
		/// the structure that hands one over, is the rule working against its own purpose.
		/// <para>
		/// Three of the seven harvesters this side owned on 16:9 came free with a refinery and
		/// only four were bought, so the exempted path is the majority of the fleet rather than an
		/// edge case. Power is already exempt on the weaker argument that a brownout slows the
		/// harvester; a refinery does not slow it, it is it.
		/// </para>
		/// </remarks>
		public static bool IsRecoveryRefinery(string item, IReadOnlyList<string> refineries) =>
			!string.IsNullOrEmpty(item) && Names(refineries, item);

		/// <summary>
		/// Whether every unit queue should stop spending until the base has its first gun.
		/// </summary>
		/// <remarks>
		/// <b><see cref="IsOpeningEmplacement"/> unblocked the plan and never unblocked the
		/// bank, and 16:9 is what that costs.</b> That exemption fired 55 times, so the yard
		/// was free to ask <c>Support</c> for a tower whenever none was standing — and the side
		/// still spent <b>1,112 of 1,350 seconds with no gun at home</b>. The first
		/// <c>gtwr</c> stood at 143s and died at 199s; the second was not ordered until roughly
		/// 440s and stood at 479s; it died at 518s and nothing replaced it for the remaining
		/// 832 seconds. The <c>Support</c> queue was given <b>four orders in the whole match</b>
		/// and spent 181 seconds waiting for cash, while <c>Infantry</c> was given 23.
		/// <para>
		/// A queue buying 100-credit bodies always wins that race, and what it beat was the
		/// best trade on the field. Two <c>gtwr</c> cost 1,200 credits and took <b>11 of this
		/// side's 30 kills</b> — 109 credits a kill, 119,682 damage dealt against 4,775 taken,
		/// and 35,406 dealt to enemy <c>e1</c> for <b>nothing taken back</b>. The 3,700 credits
		/// of infantry bought beside them killed 8, at 325 credits a kill for <c>e1</c> and 600
		/// for <c>e3</c>, and seven of those bodies died <b>0 to 6 seconds</b> after leaving the
		/// barracks with a jeep parked outside it.
		/// </para>
		/// <para>
		/// It is not only a trade. Every retreat rule this bot owns sends its earners home —
		/// 314 <c>runs-home</c>, 214 <c>escape-en-route</c> and 197 <c>sheltering</c> decisions
		/// of the 841 the harvesters made — on the written premise that "home is where this
		/// side's guns are". On 16:9 there were no guns, and <b>33 of the side's 44 losses fell
		/// inside one six-cell circle</b> on the base. This rule is what makes that premise
		/// true.
		/// </para>
		/// <para>
		/// Narrow on every axis, and self-releasing on each, because every hold in this file has
		/// at some point become a latch:
		/// </para>
		/// <list type="number">
		/// <item><b>Only at zero.</b> The same "first of a role it does not have at all" bound
		/// <see cref="IsOpeningEmplacement"/> already uses. One tower standing silences this
		/// rule completely; depth stays behind the doctrine's own rungs and it can never become
		/// a turtle.</item>
		/// <item><b>Only while a tower is actually buildable.</b> No barracks, no power, or no
		/// construction yard means <c>Support</c> offers nothing, the saved credits would buy
		/// nothing, and the rule is silent — which is also what retires it when the yard
		/// dies.</item>
		/// <item><b>Only while the tower is unaffordable.</b> At 600 credits in hand, buying a
		/// rifleman costs the tower nothing and the hold lifts on its own. On a healthy economy
		/// it never fires at all.</item>
		/// </list>
		/// <para>
		/// Callers must still exempt harvester recovery, exactly as
		/// <see cref="ShouldFundCriticalRefinery"/> requires: income outranks a gun, because a
		/// gun bought with the last credits of a dead economy is the last thing the side ever
		/// buys.
		/// </para>
		/// </remarks>
		public static bool EmplacementBeforeBodies(
			int standingEmplacements,
			bool emplacementBuildable,
			int cash,
			in IncomeFirstTuning t)
		{
			if (!emplacementBuildable || t.EmplacementPrice <= 0)
				return false;

			if (standingEmplacements > 0)
				return false;

			return cash < t.EmplacementPrice;
		}

		/// <summary>
		/// Whether discretionary queues should yield to a refinery already being built.
		/// </summary>
		/// <remarks>
		/// A queued structure does not reserve shared cash. Finish the refinery while the base
		/// has no redundancy; callers may still allow the existing harvester recovery priority.
		/// </remarks>
		public static bool ShouldFundCriticalRefinery(
			int ownedRefineries,
			bool refineryInProduction)
		{
			if (ownedRefineries < 0)
				return false;

			// Owned counts include the active production item once its order is visible.
			var completedRefineries = refineryInProduction && ownedRefineries > 0
				? ownedRefineries - 1
				: ownedRefineries;
			return refineryInProduction
				&& completedRefineries <= 1;
		}

		/// <summary>
		/// Moves the first harvester rung to the front while the fleet is below its release floor.
		/// </summary>
		/// <remarks>
		/// Cross-queue cash holds cannot help when a cheaper combat rung leads the harvester in
		/// the Vehicle queue itself. The same release floor that normally lets the queue move
		/// beyond harvesters defines when recovery is complete. Callers may preserve one leading
		/// screen while an earner survives; once recovery is selected, this method moves it
		/// ahead of every cheaper rung. The original plan returns by reference as soon as the
		/// floor is restored.
		/// </remarks>
		public static IReadOnlyList<ProductionStep> PrioritizeRecovery(
			IReadOnlyList<ProductionStep> plan,
			IReadOnlyList<string> harvesterRole,
			int standingHarvesters,
			int shortBelow,
			bool harvestersBuildable)
		{
			if (plan == null || harvesterRole == null || harvesterRole.Count == 0)
				return plan;

			if (!harvestersBuildable || shortBelow <= 0 || standingHarvesters >= shortBelow)
				return plan;

			var firstHarvester = ExpansionLogic.FirstRungNaming(plan, harvesterRole);
			if (firstHarvester <= 0)
				return plan;

			var prioritized = new List<ProductionStep>(plan.Count);
			prioritized.Add(plan[firstHarvester]);
			for (var i = 0; i < plan.Count; i++)
				if (i != firstHarvester)
					prioritized.Add(plan[i]);

			return prioritized;
		}

		/// <summary>
		/// The plan with every rung of <paramref name="queue"/> capped at the garrison, while the
		/// side cannot afford the harvester it is short of.
		/// </summary>
		/// <remarks>
		/// Returns the plan it was given, by reference, whenever nothing needs rewriting — which
		/// is every evaluation before a factory stands, every evaluation on a healthy fleet, and
		/// every evaluation with a harvester's price in the bank.
		/// </remarks>
		public static IncomeHold Hold(
			IReadOnlyList<ProductionStep> plan,
			string queue,
			int cash,
			int standingHarvesters,
			int shortBelow,
			bool harvestersBuildable,
			in IncomeFirstTuning t)
		{
			return HoldForPriority(
				plan, queue, cash, standingHarvesters, shortBelow,
				harvestersBuildable, t.HarvesterPrice, t);
		}

		/// <summary>
		/// Caps a cheap queue while a buildable priority role is below its release band and
		/// cannot yet be funded.
		/// </summary>
		public static IncomeHold HoldForPriority(
			IReadOnlyList<ProductionStep> plan,
			string queue,
			int cash,
			int standingPriority,
			int shortBelow,
			bool priorityBuildable,
			int reservePrice,
			in IncomeFirstTuning t)
		{
			var idle = new IncomeHold(plan, standingPriority, shortBelow, cash, 0);

			if (plan == null || string.IsNullOrEmpty(queue) || t.GarrisonBodies < 0)
				return idle;

			if (!priorityBuildable || shortBelow <= 0 || reservePrice <= 0)
				return idle;

			if (standingPriority >= shortBelow)
				return idle;

			if (cash >= reservePrice)
				return idle;

			List<ProductionStep> capped = null;
			var rungs = 0;

			for (var i = 0; i < plan.Count; i++)
			{
				var step = plan[i];
				if (!string.Equals(step.Queue, queue, StringComparison.OrdinalIgnoreCase))
					continue;

				if (step.DesiredCount <= t.GarrisonBodies)
					continue;

				if (capped == null)
				{
					capped = new List<ProductionStep>(plan.Count);
					for (var c = 0; c < plan.Count; c++)
						capped.Add(plan[c]);
				}

				capped[i] = new ProductionStep(step.Queue, step.Candidates, t.GarrisonBodies);
				rungs++;
			}

			return capped == null
				? idle
				: new IncomeHold(capped, standingPriority, shortBelow, cash, rungs);
		}
	}
}
