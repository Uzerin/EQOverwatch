![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch v1.0.14 (formerly EQCompanion) — Phases 1-8

```
EQCompanion.sln                  (namespaces stay EQCompanion.*; the executable is EQOverwatch.exe)
src/EQCompanion.Core/            net8.0, platform-neutral
  AppInfo.cs   product name / version ("EQ Overwatch", "v1.0.14" read from the assembly)
  Discovery/   EqDirectoryScanner (eqlog_<Char>_<Server>.txt discovery, Logs -> parent folder, spells_us.txt)
  Export/      EncounterExport (chat text "1. Name Total (SDPS)")
  Models/      WatchedLogConfig(+Collection, CH chain slot), LogEvent, CombatEvent, CombatEncounter, ParticipantStats,
               TriggerDefinition, Snapshots, TimerOverlayCategory (Buffs, Debuffs, Songs, Raid Triggers, CH Chain)
  Parsing/     LogLineFormat, LogFileName, CombatLineParser
  Pipeline/    ILogSinks, LogEventProcessor, TailWorker, LogTailService, LiveLogMonitor,
               HistoricalLogImporter, CompositeTriggerSink, CompanionPipeline
  Services/    DpsTrackerService (+Live/Historical), TriggerEngineService, TriggerTemplate, TriggerRuleTester,
               BuffTrackerService (monitored-target filter), RaidBossTimerService,
               ChChainService (Complete Heal chain bars + late-cast alarm), CooldownTrackerService (Divine Aura)
  Settings/    AppSettings (+TriggerRule, RaidBossTimerRule, OverlaySettings, ChChainSettings), AppSettingsService
               (auto-migrates %LocalAppData%\EQCompanion\settings.json), OverlayPlacement
  Sharing/     TriggerShareSerializer ({EQO:...}, reads {EQC:...} and {EQNag:...}), SharedTriggerScanner
  Spells/      SpellDatabaseService (spells_us.txt, both layouts), SpellDatabase, SpellInfo, SpellDefinition
  Theming/     ThemePalette (9 colors), ThemePresets (3 dark presets), ThemeConfig, ThemeColors, ThemeDerived
src/EQCompanion.App/             net8.0-windows (WPF), AssemblyName EQOverwatch
  Interop/       Win32OverlayHelper
  Overlays/      OverlayWindowBase, TimerOverlayWindow, DpsOverlayWindow (Chat / Img export buttons)
  ViewModels/    MainViewModel (+ .Characters, .Sharing), TriggerRuleViewModel, EncounterViewModels, Timer/DpsOverlayViewModel
  Services/      AudioNotificationService, OverlayHost (6 overlays), ThemeManagerService, WpfDialogService,
                 WpfClipboardService, EncounterImageRenderer, Abstractions
  Themes/        OverlayStyles.xaml, ModernStyles.xaml
  MainWindow.xaml(.cs) six tabs: Characters, Custom Triggers, Raid Triggers, Buff & Song Tracker,
                 DPS & Encounter History, Settings; toolbar with Start/Stop, Lock, Mute, Test overlays
tests/EQCompanion.Core.Tests/    net8.0          (any OS)
tests/EQCompanion.App.Tests/     net8.0-windows  (view models, formatting; Windows only)

Settings file: %LocalAppData%\EQOverwatch\settings.json (atomic writes, debounced autosave; migrated from EQCompanion on first start)

dotnet test tests/EQCompanion.Core.Tests     # any OS
dotnet test                                  # whole solution, Windows
dotnet run --project src/EQCompanion.App     # Windows
```

## Phase 7 notes
- Characters discovered from the EverQuest folder start with triggers disabled until you tick **Enable Triggers**.
- The CH Chain overlay is off by default; with it off, chain bars and the late-cast alarm show in the Buffs overlay.
- Shared `{EQO}`/`{EQC}`/`{EQNag}` triggers are never enabled without your Accept; audio file paths are never imported.
- `{EQNag}` mapping is best-effort (foreign format).

## Raid Triggers: separate announcements
- A Raid Trigger now has two announcements: one spoken the moment it starts and one spoken when 10 seconds remain (it was a
  single text with a timing drop-down and a 5-second warning). The 10-second text is skipped when the recast is 10 seconds or less.
- Existing triggers and `{EQO}` tokens are converted automatically: "when the timer starts" becomes the start text, "at 5 seconds"
  becomes the 10-second text, "both" fills both.

## Phase 8 fix 2 (songs and spell messages)
- `spells_us_str.txt` columns are now read from its own header (`CASTEDMETXT` = cast on you, `CASTEDOTHERTXT` = cast on other,
  `SPELLGONE` = fades; normally 3, 4, 5). Before, columns 1-3 were read, which are the caster-side texts.
- Your own `You begin singing <song>.` starts (or restarts) the song's bar immediately; no landing text is needed.
- Buffs still start from the cast line plus the landing text (`Your eyes sharpen...` on you, `Name's eyes sharpen...` on others).
- Split spell files: Bard level is column 43 (class levels are columns 36-51) and the good/bad flag is column 28. Before, columns 110 and 78 were read, which are effect values and made buffs look like songs or debuffs.
- Songs never start from landing text (it appears in every group member's log); only the singer's own sing line starts a bar.
- The tracker list has a "Starts when" column showing what starts each bar.

## v1.0.14: app icon
- The radar from the logo is now the exe's icon and the window/taskbar icon (`src/EQCompanion.App/Assets/EQOverwatch.ico`, sizes 16-256).
  The full logo is in `docs/`.

## v1.0.12 (fixed in v1.0.13): update notifications and GitHub releases
- When the app opens it asks GitHub (`Uzerin/EQOverwatch`, public) for the latest published release. If it is newer than the running
  version, a bar at the top offers **Download** (opens the release page), **Later** and **Skip this version**. Nothing is installed
  automatically. A failed check (offline, rate limited) is silent; Settings > Updates shows the result and has **Check now** and a
  switch to turn the start-up check off.
- `RELEASING.md` explains how to publish a version; `.github/workflows/release.yml` builds and attaches the exe when a `v*` tag is pushed.

## v1.0.10 (fixed in v1.0.11): AAs & Discs tab
- New tab for AAs and disciplines. Each use starts an **active** bar (how long it lasts) and a **cooldown** bar (until it can be used
  again) for the person who used it. Seen in logs as `You activate <name>.` (your own log) and `<Name> activates <name>.` (anyone else).
- Per ability: **Everyone in the raid** (every user gets bars of their own, e.g. all 6 warriors' Defensive) or **My characters only**
  (e.g. just your own Trueshots). One person using it again restarts only their own bars, never another's.
- Per ability you also choose the overlay of the active bar and of the cooldown bar, edit the times (3:00, 10:30, 1:07:30 or seconds),
  and switch it on or off.
- The picker searches the spell file: discs have their recast and duration filled in. Most AAs have no recast in the spell file; type the
  name exactly as the log prints it and enter the times yourself.
- The Divine Aura box moved here ("Built-in: Divine Aura"); it is unchanged.

## Phase 8 fix 8 / v1.0.9 (start-up, one encounter list, raid edit, CH chain)
- **Versioning:** Phase 8 is 1.0.1; fix N is 1.0.(N+1), so fix 8 is 1.0.9. Releases after that are named by version (EQOverwatch-v1.0.10.zip). Shown in the window title.
- **Start-up:** the saved EverQuest folder is scanned on launch (no need to press Refresh), the Primary DPS log is read so a fight
  already in progress appears as if the app had been open all along, and live monitoring starts automatically and stays on while the app
  is open (there is no start/stop button or setting any more). The catch-up covers the history window (default 7 days).
- **DPS & Encounter History:** live and imported fights are now a single list, newest first. A fight that is both live and in an
  imported copy of the log is listed once.
- **Raid Triggers:** each saved trigger has an **Edit** button. It loads the trigger into the builder; **Save Changes** replaces it
  in place (same on/off state and position), **Cancel edit** abandons the change.
- **CH Chain:** a call is recognised by the position tag (e.g. `5-5-5`, any spacing/dash/case) in a raid say, and "cast now"
  (any case, anywhere in the line) moves the chain along. The bar reads `<player>: Complete Heal <position>`. The "Total
  clerics in chain" box is gone.

## Phase 8 notes (v1.0.1)
- **Buff & Song Tracker** is now a list you build: search a spell, pick the overlay it appears in, press Add to Tracker.
  Each row has its own overlay dropdown and On switch. Spells tracked in older versions are carried over once, with the
  spell file's suggested overlay, and can be changed afterwards. A status line shows how many bars have started.
- **Raid Triggers** boss box is a drop-down of mobs from ended fights (live and imported) with fight counts; any other name
  can be typed for a first-time fight.
- `settings.json` gains `TrackedSpells` (spell id, name, overlay, on/off); `TrackedSpellIds` is still written for older builds.
