<#
.SYNOPSIS
	Repeats fight, improve, build and paired evaluation without opening the launcher.

.DESCRIPTION
	Windows only. Uses the same snapshots, workspace locks and promotion gate as the UI.
	Every round records fresh battle evidence, invokes a coding agent, verifies its build,
	and benchmarks immutable candidate and champion assemblies before promoting or restoring.
	An invalid evaluation or failed improvement stops the loop rather than accepting an
	unmeasured edit. Ctrl+C stops the worker process tree and retains recovery evidence.

	A promoted bot is committed to the bot workspace, because a champion that exists only as
	an uncommitted working tree is one manual edit away from being lost, and has been. Only
	the workspace is staged, so the engine submodule's applied patch never rides along. Use
	-NoCommit to leave version control alone.

	This edits bot source and uses the configured agent's account and quota. Defaults to
	Copilot CLI and the repository prompt, not the launcher's saved agent/prompt settings.
	Run setup.ps1 and install the game content and an authenticated coding agent first.

.PARAMETER BattleBot
	An editable .csproj, its directory, or a folder name under bots/. Defaults to Reference.

.PARAMETER Rounds
	Number of new fight/improvement rounds. 0 (the default) repeats until stopped.
	Resuming an interrupted, verified candidate's evaluation does not consume a round.

.PARAMETER Map
	Training battle map name or UID. Defaults to 16-9.oramap.

.PARAMETER ExecutionMode
	Headless (default) runs without rendering. Rendered shows training battles; the paired
	benchmark remains headless.

.PARAMETER MaxGameSeconds
	Time limit for each training battle in Headless mode. 0 means unlimited. Benchmark
	time limits come from the selected set in scripts/benchmarks.json instead.

.PARAMETER Benchmark
	Paired promotion gate from scripts/benchmarks.json. Defaults to hard-16-9.

.PARAMETER BenchmarkDifficulty
	Explicit difficulty for both benchmark arms. Defaults to Hard, independently of Difficulty.

.PARAMETER RunsRoot
	Optional training history root, outside the bot workspace. Defaults to
	LOCALAPPDATA\AutoCnC\TrainingRuns, shared with the launcher.

.PARAMETER AgentConfiguration
	Provider-neutral JSON with command, arguments and optional stdin properties, as accepted
	by train-bot.ps1. Defaults to Copilot CLI. Paths and prompts use the same placeholders.

.PARAMETER PromptTemplate
	Optional prompt template file. Defaults to docs/agent-prompt-template.md. Agent proposals
	are saved for review, never automatically adopted during the loop.

.PARAMETER RestoreRun
	Recovery only: discard edits to the bot since this unresolved run's pre-agent snapshot
	and restore that snapshot, then exit. This also discards subsequent manual source edits.
	Refuses to restore while another worker owns the run. Supports -WhatIf.

.PARAMETER NoCommit
	Do not commit promoted bots. By default every promotion is committed to the bot workspace,
	so an improvement the paired gate measured survives the next round instead of living only
	in the working tree. Nothing else in the checkout is staged, and a restore never commits.

.PARAMETER NoColor
	Print the agent and script output without colour. Colour is on by default and is dropped
	automatically when output is redirected, when NO_COLOR is set, or on a console that cannot
	render it.

.PARAMETER NoBuild
	Use the existing Release build of the console host instead of rebuilding it. Bot builds
	and verification still run on every round.

.EXAMPLE
	./scripts/train-loop.ps1 -BattleBot Reference -Rounds 3

.EXAMPLE
	./scripts/train-loop.ps1 -BattleBot Reference -Rounds 5 -NoCommit
	Trains without touching version control, leaving promotions in the working tree.

.EXAMPLE
	./scripts/train-loop.ps1 -BattleBot C:\bots\MyBot\MyBot.csproj -Map 16-9.oramap -Difficulty Hard
	Repeats until Ctrl+C.

.EXAMPLE
	./scripts/train-loop.ps1 -RestoreRun 'C:\path\to\training-run'
	Restores an unresolved run's pre-agent source snapshot without starting training.
#>
[CmdletBinding(DefaultParameterSetName = 'Loop', SupportsShouldProcess = $true)]
param(
	[Parameter(ParameterSetName = 'Loop')]
	[string]$BattleBot = 'Reference',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateNotNullOrEmpty()]
	[string]$Map = '16-9.oramap',
	[Parameter(ParameterSetName = 'Loop')]
	[string]$Difficulty = 'Hard',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateRange(1, [int]::MaxValue)]
	[int]$Opponents = 1,
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateSet('gdi', 'nod', 'Random')]
	[string]$Faction = 'Random',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateSet('gdi', 'nod', 'Random')]
	[string]$BotFaction = 'Random',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateSet('Headless', 'Rendered')]
	[string]$ExecutionMode = 'Headless',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateSet('slowest', 'slower', 'default', 'fast', 'faster', 'fastest', 'turbo', 'ludicrous', 'plaid', 'maximum')]
	[string]$GameSpeed = 'maximum',
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateRange(0, [int]::MaxValue)]
	[int]$Rounds = 0,
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateRange(0, [int]::MaxValue)]
	[int]$Seed = 0,
	[Parameter(ParameterSetName = 'Loop')]
	[ValidateRange(0, [int]::MaxValue)]
	[int]$MaxGameSeconds = 5400,
	[Parameter(ParameterSetName = 'Loop')]
	[string]$Benchmark = 'hard-16-9',
	[Parameter(ParameterSetName = 'Loop')]
	[string]$BenchmarkDifficulty = 'Hard',
	[Parameter(ParameterSetName = 'Loop')]
	[string]$RunsRoot,
	[Parameter(ParameterSetName = 'Loop')]
	[string]$AgentConfiguration,
	[Parameter(ParameterSetName = 'Loop')]
	[string]$PromptTemplate,
	[Parameter(ParameterSetName = 'Loop')]
	[switch]$NoCommit,
	[switch]$NoColor,
	[Parameter(Mandatory, ParameterSetName = 'Restore')]
	[string]$RestoreRun,
	[switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) {
	throw 'The training loop host currently requires Windows, like the launcher.'
}

$target = if ($RestoreRun) { $RestoreRun } else { $BattleBot }
$action = if ($RestoreRun) { 'Discard source edits and restore the pre-agent snapshot' } else {
	'Run agent-driven training with paired benchmark promotion'
}
if (-not $PSCmdlet.ShouldProcess($target, $action)) { return }

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'tools\AutoCnC.Training\AutoCnC.Training.csproj'
$hostDll = Join-Path $repoRoot 'tools\AutoCnC.Training\bin\Release\net8.0-windows\AutoCnC.Training.dll'

function Resolve-OptionalFile([string]$path) {
	if ($path) { return (Resolve-Path -LiteralPath $path).Path }
	return $null
}

$options = @{
	RepoRoot = $repoRoot
	BattleBot = if (Test-Path -LiteralPath $BattleBot) { (Resolve-Path -LiteralPath $BattleBot).Path } else { $BattleBot }
	Map = $Map
	Difficulty = $Difficulty
	Opponents = $Opponents
	Faction = $Faction
	BotFaction = $BotFaction
	ExecutionMode = if ($ExecutionMode -eq 'Headless') { 'Headless' } else { 'Rendered' }
	GameSpeed = $GameSpeed
	Rounds = $Rounds
	Seed = $Seed
	MaxGameSeconds = $MaxGameSeconds
	Benchmark = $Benchmark
	BenchmarkDifficulty = $BenchmarkDifficulty
	RunsRoot = if ($RunsRoot) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RunsRoot) } else { $null }
	AgentConfiguration = Resolve-OptionalFile $AgentConfiguration
	PromptTemplate = Resolve-OptionalFile $PromptTemplate
	RestoreRun = Resolve-OptionalFile $RestoreRun
	Commit = -not $NoCommit
	Color = -not $NoColor
}

if (-not $NoBuild) {
	Write-Host '==> Building the training loop host' -ForegroundColor Cyan
	dotnet build $hostProject -c Release -v quiet --nologo
	if ($LASTEXITCODE -ne 0) { throw 'Training loop host build failed.' }
}
if (-not (Test-Path -LiteralPath $hostDll)) {
	throw 'Training loop host is missing. Run this command without -NoBuild first.'
}

$optionsFile = [IO.Path]::GetTempFileName()
try {
	$options | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $optionsFile -Encoding UTF8
	& dotnet $hostDll --options $optionsFile
	$code = $LASTEXITCODE
} finally {
	Remove-Item -LiteralPath $optionsFile -Force -ErrorAction SilentlyContinue
}
if ($code -ne 0) { throw "Training loop exited with code $code. See the preceding output and saved run." }
