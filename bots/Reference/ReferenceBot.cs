// ============================================================================
//  ReferenceBot — the battle bot AutoC&C ships as an opponent and as an example.
//
//  A BATTLE BOT is the unit of authorship in AutoC&C, and the thing a battle is
//  played with. It owns several DOCTRINES — complete, self-contained ways of
//  fighting, each with its own build plan, production plan and mode assignments
//  — and decides which one the match needs as the match changes.
//
//  The split is the point:
//
//    Doctrine   HOW to fight one way, thoroughly.   (Doctrines/*.cs)
//    Bot        WHICH way to fight, and when.       (Reassess, below)
//
//  Plans do not survive contact. Rather than growing one doctrine a special
//  case at a time until nobody can say what it does, keep each one confident
//  about its own job and let the bot change its mind instead.
//
//  The platform ships no strategy at all. Load a bot and it plays; load none
//  and nothing deploys, builds or shoots.
//
//  TO WRITE YOUR OWN: copy this folder, rename the class and Name, change the
//  doctrines and the rules. Beating this bot is the goal.
//
//  Licence: GPL-3.0-or-later, like everything that links against OpenRA.
//  See LICENSE and NOTICE.md.
// ============================================================================

using AutoCnC.Core;
using AutoCnC.Reference.Doctrines;
using AutoCnC.Reference.Logic;
using AutoCnC.Sdk;

namespace AutoCnC.Reference
{
	public sealed class ReferenceBot : BattleBot
	{
		public override string Name => "Reference";

		public override string Description =>
			"Opens economic, scouts when it can afford to, turtles when hit and pushes when ahead.";

		public override void Configure(IBattleBotBuilder b)
		{
			// Opening first, because that is where every match starts: an economy, a small army,
			// and no idea yet what the other side is doing.
			b.Open<OpeningDoctrine>();

			b.Use<ScoutDoctrine>();
			b.Use<DefenceDoctrine>();
			b.Use<AttackDoctrine>();
		}

		/// <summary>
		/// Called every few seconds of game time with everything this side can see.
		/// </summary>
		/// <remarks>
		/// The rules themselves live in <see cref="ReferenceBotLogic"/>, which has no engine
		/// reference anywhere near it — so the interesting half of this bot is tested by writing
		/// down a situation and asserting the doctrine, in milliseconds, with no game running.
		/// Change the strategy there, not here.
		/// </remarks>
		public override DoctrineDecision Reassess(in BattleState state) =>
			ReferenceBotLogic.Decide(state);
	}
}
