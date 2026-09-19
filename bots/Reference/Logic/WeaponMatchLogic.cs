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

using AutoCnC.Core;

namespace AutoCnC.Reference.Logic
{
	/// <summary>What a unit's warhead is actually good at killing.</summary>
	/// <remarks>
	/// Tiberian Dawn resolves every shot as <c>damage x versusArmorPercent[targetArmor]</c>, and
	/// the spread between the best and worst entry of that table is enormous — far larger than
	/// any of the tactical weights a target scorer normally juggles. Two roles is all this bot
	/// needs to capture it, because every armament in the ruleset it can field falls clearly on
	/// one side or the other.
	/// </remarks>
	public enum WeaponRole
	{
		/// <summary>Unmeasured. Scores exactly as the bot did before roles existed.</summary>
		Unknown = 0,

		/// <summary>Strong against <c>None</c> armour — infantry — and weak against everything plated.</summary>
		AntiInfantry,

		/// <summary>Strong against <c>Light</c>, <c>Heavy</c> and <c>Concrete</c>, feeble against infantry.</summary>
		AntiArmour,
	}

	/// <summary>
	/// Which targets a unit's own weapon can actually hurt.
	/// </summary>
	/// <remarks>
	/// Every target scorer in this bot used to rank threat classes with one table shared by the
	/// whole army — <c>Vehicle</c> above <c>Infantry</c>, for a rifleman and a rocket soldier
	/// alike. That is wrong in both directions at once, and badland-ridges priced it exactly.
	/// <para>
	/// Damage per game second, from <c>game-rules.json</c>: an <c>e1</c> rifle does <b>1,875</b>
	/// against infantry and <b>125</b> against Heavy armour, a 15:1 spread. An <c>e3</c> rocket
	/// is the mirror image — <b>319</b> against infantry and <b>1,593</b> against Light or Heavy,
	/// because its warhead reads <c>None 28%</c> against <c>Light 140%</c> and <c>Heavy 140%</c>.
	/// </para>
	/// <para>
	/// The bot spent the match aiming each of them at the other one's job. Of 1,313 engagement
	/// orders its mobile units issued, <b>422 were an <c>e1</c> shooting a vehicle</b> and
	/// <b>284 an <c>e3</c> shooting infantry</b>: 706 of 1,313, <b>54% of every shot the army
	/// ever aimed</b>, at the armour class its own warhead is worst against. It killed 56 and
	/// lost 93. Enemy infantry alone — <c>e1</c>, <c>e3</c>, <c>e2</c> — accounted for 60 of
	/// those 93 losses, 65%, while the thing most of the bot's own rockets were pointed at was
	/// a vehicle they were killing three times faster than the riflemen beside them could.
	/// </para>
	/// <para>
	/// The static defences are in it too, and they are the largest single population:
	/// <c>gtwr</c> issued 500 vehicle engagements against 54 infantry ones while its
	/// <c>HighV</c> gun does <b>3,000</b> to infantry and <b>900</b> to Heavy, and <c>atwr</c>
	/// 177 against 43 while its missile does <b>3,948</b> to Heavy and <b>2,053</b> to infantry.
	/// The tower with the machine gun was preferring tanks and the tower with the rockets was
	/// getting its second choice.
	/// </para>
	/// <para>
	/// <see cref="WeaponRole.Unknown"/> is the fallback, and it is deliberately flat: every kind
	/// scores <see cref="NeutralEffectiveness"/>, so an actor this table has never measured adds
	/// a constant to every candidate and the ordering collapses to exactly what it was before.
	/// A preference that degrades to the previous behaviour cannot be worse than not having one.
	/// </para>
	/// ZERO OpenRA dependencies by design, integer-only, so it is lockstep-safe.
	/// </remarks>
	public static class WeaponMatchLogic
	{
		/// <summary>Score awarded for a perfect weapon/armour match.</summary>
		/// <remarks>
		/// Sized against the threat-class weights it sits beside — <c>Aircraft</c> 2,000 down to
		/// <c>Structure</c> 200 — so that being able to hurt a target outweighs the class
		/// ordering, but neither term comes close to the "this shot is free, I am already in
		/// range" bonus. Wanting to shoot a tank is never a reason to walk past one.
		/// </remarks>
		public const int MatchWeight = 3000;

		/// <summary>What an unmeasured weapon scores against everything.</summary>
		public const int NeutralEffectiveness = 60;

		/// <summary>
		/// The weapon role of an actor type, or <see cref="WeaponRole.Unknown"/> if this table
		/// has never measured it.
		/// </summary>
		/// <remarks>
		/// Keyed on actor id so it covers both factions from one list, which is what keeps the
		/// bot portable: GDI fields <c>e2</c>, <c>jeep</c>, <c>mtnk</c>, <c>msam</c>, <c>apc</c>,
		/// <c>gtwr</c> and <c>atwr</c>; Nod fields <c>bggy</c>, <c>ltnk</c>, <c>bike</c>,
		/// <c>ftnk</c>, <c>arty</c>, <c>stnk</c> and <c>gun</c>; both field <c>e1</c> and
		/// <c>e3</c>. Anything absent falls through to Unknown rather than to a guess.
		/// <para>
		/// Damage per game second against None / Light / Heavy armour, all from
		/// <c>game-rules.json</c>, which is why each entry is an observation rather than an
		/// opinion.
		/// </para>
		/// </remarks>
		public static WeaponRole RoleOf(string actorType)
		{
			if (string.IsNullOrEmpty(actorType))
				return WeaponRole.Unknown;

			switch (actorType.ToLowerInvariant())
			{
				// Strong against None, collapsing against plate.
				case "e1":    return WeaponRole.AntiInfantry;  // M16          1875 /  500 /  125
				case "e2":    return WeaponRole.AntiInfantry;  // Grenade      2500 / 2000 /  850
				case "e4":    return WeaponRole.AntiInfantry;  // Flamethrower
				case "jeep":  return WeaponRole.AntiInfantry;  // MachineGunH  5391 / 2516 /  359
				case "bggy":  return WeaponRole.AntiInfantry;  // MachineGun   4688 / 2188 /  312
				case "ftnk":  return WeaponRole.AntiInfantry;  // BigFlamer    5469 / 5002 / 1201
				case "arty":  return WeaponRole.AntiInfantry;  // Artillery    5390 / 4312 / 2888
				case "heli":  return WeaponRole.AntiInfantry;  // HeliAGGun    5000 / 3750 / 1250
				case "gtwr":  return WeaponRole.AntiInfantry;  // HighV        3000 / 2100 /  900

				// Strong against plate, feeble against None.
				case "e3":    return WeaponRole.AntiArmour;    // Rockets       319 / 1593 / 1593
				case "mtnk":  return WeaponRole.AntiArmour;    // 120mm         625 / 2500 / 2500
				case "ltnk":  return WeaponRole.AntiArmour;    // 70mm          500 / 2082 / 1833
				case "bike":  return WeaponRole.AntiArmour;    // BikeRockets   500 / 2213 / 2213
				case "stnk":  return WeaponRole.AntiArmour;    // 227mm.stnk    938 / 3750 / 3375
				case "msam":  return WeaponRole.AntiArmour;    // 227mm         577 / 2405 / 1154
				case "apc":   return WeaponRole.AntiArmour;    // APCGun        833 / 2084 /  694
				case "orca":  return WeaponRole.AntiArmour;    // OrcaAGM      1666 / 5832 / 4374
				case "atwr":  return WeaponRole.AntiArmour;    // TowerMissile 2053 / 3948 / 3948
				case "gun":   return WeaponRole.AntiArmour;    // TurretGun    1000 / 5000 / 5000
				case "sam":   return WeaponRole.AntiArmour;    // Dragon, Air only

				// Everything else — obelisks, unarmed actors, anything a later mod adds.
				default:      return WeaponRole.Unknown;
			}
		}

		/// <summary>
		/// How well this role's warhead does against a threat class, 0-100, where 100 is the
		/// class it was built for.
		/// </summary>
		/// <remarks>
		/// <see cref="ThreatKind"/> is the only thing a <see cref="ThreatSnapshot"/> carries
		/// about what a target is, so the armour classes have to be collapsed onto it:
		/// <c>Infantry</c> is <c>None</c>; <c>Vehicle</c> and <c>Aircraft</c> are Light or Heavy;
		/// <c>Structure</c>, <c>Defence</c> and <c>Economy</c> are Wood, Concrete, or a Heavy
		/// harvester. That is a coarser split than the ruleset makes, and it is the right one
		/// here: the bot does not need to know a tank from a buggy, it needs to stop pointing
		/// rockets at riflemen.
		/// </remarks>
		public static int Effectiveness(WeaponRole role, ThreatKind kind)
		{
			switch (role)
			{
				case WeaponRole.AntiInfantry:
					return kind switch
					{
						ThreatKind.Infantry => 100,
						ThreatKind.Vehicle => 30,
						ThreatKind.Aircraft => 35,
						ThreatKind.Economy => 30,
						ThreatKind.Defence => 25,
						ThreatKind.Structure => 25,
						_ => NeutralEffectiveness,
					};

				case WeaponRole.AntiArmour:
					return kind switch
					{
						ThreatKind.Infantry => 20,
						ThreatKind.Vehicle => 100,
						ThreatKind.Aircraft => 100,
						ThreatKind.Economy => 95,
						ThreatKind.Defence => 95,
						ThreatKind.Structure => 95,
						_ => NeutralEffectiveness,
					};

				default:
					// Flat on purpose. See the class remarks: a constant cannot reorder anything.
					return NeutralEffectiveness;
			}
		}

		/// <summary>
		/// <see cref="Effectiveness"/> scaled into the same units the target scorers count in.
		/// </summary>
		public static int MatchBonus(WeaponRole role, ThreatKind kind) =>
			Effectiveness(role, kind) * MatchWeight / 100;
	}
}
