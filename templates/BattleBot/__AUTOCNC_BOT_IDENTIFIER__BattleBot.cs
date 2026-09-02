using __AUTOCNC_BOT_ROOT_NAMESPACE__.Doctrines;
using AutoCnC.Sdk;

namespace __AUTOCNC_BOT_ROOT_NAMESPACE__
{
	public sealed class __AUTOCNC_BOT_IDENTIFIER__BattleBot : BattleBot
	{
		public override string Name => "__AUTOCNC_BOT_DISPLAY_NAME_CSHARP__";

		public override string Description =>
			"A small starter force that engages nearby visible enemies.";

		public override void Configure(IBattleBotBuilder builder) =>
			builder.Open<StarterDoctrine>();
	}
}
