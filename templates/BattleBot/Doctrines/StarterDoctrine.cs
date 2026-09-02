using __AUTOCNC_BOT_ROOT_NAMESPACE__.Modes;
using AutoCnC.Sdk;

namespace __AUTOCNC_BOT_ROOT_NAMESPACE__.Doctrines
{
	public sealed class StarterDoctrine : IDoctrine
	{
		public const string DoctrineName = "Starter";

		public string Name => DoctrineName;

		public string Description => "Build a small infantry force and react to nearby contacts.";

		public void Configure(IDoctrineBuilder builder)
		{
			builder.Build("powr", "nuke").Until(1);
			builder.Build("proc").Until(1);
			builder.Build("pyle", "hand").Until(1);

			builder.Train("Infantry", "e1").Until(8);
			builder.Train("Infantry", "e1", "e2").Forever();

			builder.Assign<StarterMode>().ToAll();
		}
	}
}
