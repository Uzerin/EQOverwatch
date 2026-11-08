# EQ Overwatch: working notes for Claude Code

C# .NET 8 WPF companion app for EverQuest (CommunityToolkit.Mvvm). It reads EQ log files and provides GINA-style triggers/timers,
DPS parsing with overlays and a Discord picture, buff/song/debuff tracking, raid bosses, loot, attendance, sync, backup, overlay
profiles and character data. Repo: https://github.com/Uzerin/EQOverwatch (pushing a `v*` tag runs the release workflow).

## Layout
- `src/EQOverwatch.Core` (logic, implicit usings), `src/EQOverwatch.App` (WPF; ImplicitUsings but NOT System.IO / System.Net.Http,
  add those usings explicitly), `tests/EQOverwatch.Core.Tests`, `tests/EQOverwatch.App.Tests`.
- Pipeline: LogTailService -> LogEventProcessor.Dispatch -> sinks. `ITriggerLineSink.OnLine(character, message, receivedUtc)`;
  `IDpsEventSink` (OnCombatEvent, BeginBatch/EndBatch, OnNonCombatMessage, OnLogOwner, OnLogClock). `ILogActivityObserver`
  (CharacterActivityService) sees every live line.
- DPS: `DpsTrackerService` (LiveDpsTracker / HistoricalDpsTracker) -> `CombatEncounter` -> immutable `EncounterSnapshot`
  (`Models/Snapshots.cs`). Snapshots compare by value (records; lists use value-equality wrappers such as AttackSliceList/ValueList).
- Discord picture: `App/Services/EncounterImageRenderer.cs` (+ `DpsGraph.cs`, shared with `Controls/DpsGraphControl`).
- Parser: `Core/Parsing/CombatLineParser.cs` (regexes; keep them tolerant of trailing tags like `(Critical)`, `(Flurry)`).

## Build, test, publish (Windows, from the repo root)
```
dotnet test
dotnet publish src\EQOverwatch.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Drive\EQ_App\publish
```
Stop a running `EQOverwatch` process first. Core tests must be Linux-safe and use InvariantCulture. DPS test hits must be at most
8 seconds apart (the encounter inactivity timeout), and a fight only counts damage taken while it is open.

## How the user wants work done (important)
- When the user gives things to fix or add: DO NOT build yet. Reply that you understand, list the plan, ask short questions with
  your proposed defaults, and build only when they say "Build it" ("Build it" accepts the defaults).
- Every release: bump SemVer in both csproj (Version/AssemblyVersion/FileVersion), the doc comments in `Core/AppInfo.cs` and
  `App/ViewModels/MainViewModel.Characters.cs`, the version asserts in `tests/EQOverwatch.App.Tests/MainViewModelPhase7Tests.cs` and
  `tests/EQOverwatch.Core.Tests/Phase7SettingsTests.cs`, and add a README changelog entry (plain language, no "Phase" numbers).
  A change without a version bump uses a "b" suffix. Until the user has reported test results for a version, further changes are
  rolled into that same version; afterwards they go in the next one.
- After tests are green the user releases: copy the build to the repo folder, commit "EQ Overwatch vX", push, `git tag vX`,
  `git push origin vX`; then check Actions is green and the release lists the zip and its `.sha256`. Don't push or tag unless asked.
- Never suggest these again: Raid night summary, Wipe and death recap, Fight comparison, CH chain report, Auto-clicker stats, Loot council
  helper, Trigger tester, Per-boss overlay profiles, Cooldown readiness board, Mob-specific triggers, Death/rez tracker, Buff gap
  warnings, Focus profile copy, Clicky timers, Missing-spell shopping list, Item watch list, Camp timers, Log health check,
  Trigger sharing export.

## Product decisions already made
- Keep the main Settings tab uncluttered; colors live in the separate Colors window (color wheel popup on each swatch).
- Discord picture bars stay as semi-transparent segments behind the numbers. Legend shows only attack types seen in the fight.
  The DPS-over-time graph is full width under the mob name. Tanking table shows overall Avoid % only; the app's Damage taken
  tab has Dodge/Parry/Block/Riposte/Miss/Other percentages (0.01% precision).
- Damage shields (thorns/flames/frost) are their own attack type; a mob's shield on players is damage taken, with a summary line.
- Overlays: a character is "active" while its log had a line in the last 60 s. Timers/buffs show only for active characters;
  nothing shows (locked overlays) with nobody active; the DPS overlay needs combat lines from an active character. Out-of-combat
  opacity and the DPS hide timeout are settings.
- Throwing counts as "hit". Pet damage merges into the owner when pet merging is on.
- In-app "Install update" (checksum-verified, not during combat); update re-check every N hours (setting, default 4).

## Real log data
EQ logs are in `C:\EQ-Stack\Logs\eqlog_<Character>_frostreaver.txt` (hundreds of MB; search with grep, don't open whole). Lines:
`[Day Mon dd hh:mm:ss yyyy] message`. Verified shapes: misses `X tries to hit YOU, but YOU dodge!` / `..., but misses! (Riposte)` /
`... magical skin absorbs the blow! (Rampage)`; damage shields `A mob is burned by YOUR flames for 5 points of non-melee damage.`
(`!` when it hurts you); heals `You healed X for 638 (16830) hit points by Complete Heal. (Critical)`.
Check regex changes against these logs before releasing.
