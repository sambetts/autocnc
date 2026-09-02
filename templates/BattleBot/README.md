# __AUTOCNC_BOT_IDENTIFIER__

This is a standalone AutoC&C battle bot workspace. Its project consumes `AutoCnC.Core` and
`AutoCnC.Sdk` from the checkout recorded in `Directory.Build.props`.

```powershell
dotnet test .\Tests\__AUTOCNC_BOT_IDENTIFIER__.Tests.csproj
dotnet build .\__AUTOCNC_BOT_IDENTIFIER__.sln
```

Use **Open code** to edit it, **Deploy bot** to build and install it, and **Fight** to measure it.
After a fight, **Analyze & improve** can ask your configured local coding agent to make a tested,
reversible improvement from the saved battle evidence. **History & trends** compares KPIs across
iterations; **Continuous improvement** repeats the fight-and-improve cycle until you stop it.

Start with `Doctrines/StarterDoctrine.cs` for the build and production plan,
`Logic/StarterLogic.cs` for testable decisions, and `Modes/StarterMode.cs` for the engine-facing
sense/act layer.
