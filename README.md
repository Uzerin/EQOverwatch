![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A Windows companion for EverQuest that reads your game logs and gives you timers, alerts and damage meters on screen, with no
changes to the game. It runs alongside EverQuest and works for as many of your characters as you want to monitor at once.

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

- **Custom triggers.** GINA-style triggers: match a line in your log, then show text, play a sound, speak it, or start a timer.
- **Raid triggers.** Recast timers for boss abilities, with a spoken announcement when the timer starts and another when 10 seconds
  remain. Pick the ability from your spell file or type the log text yourself, and edit saved triggers in place.
- **Buff & Song Tracker.** Pick the buffs, debuffs and songs you care about and choose which overlay each one appears in. Bars start
  from your own casts (and your monitored characters' landings) using the messages in your spell file.
- **AAs & Discs.** Track disciplines and AAs: an "active" bar for how long it lasts and a "cooldown" bar until it can be used again,
  for everyone in the raid or only your own characters, each in the overlay you choose.
- **CH chain.** Complete Heal chain bars from the raid-say position tags (for example `5-5-5`), with a late-cast alarm.
- **DPS & encounter history.** Live damage meter and a single newest-first list of fights, live and imported. Opening the app in the
  middle of a fight picks the fight up from your log. Copy results as chat text or as a picture.
- **Six overlays.** Buffs, Debuffs, Songs, Raid Triggers, CH Chain and DPS. Each is movable, resizable and lockable, with a theme bar
  for colors.
- **Sharing.** Share triggers in chat as `{EQO:...}` tokens. Nothing from a shared trigger is switched on until you accept it, and
  audio file paths are never imported. It can also read `{EQC:...}` tokens and, as a best effort, `{EQNag}` ones.
- **Update notifications.** When the app opens it checks this repository for a newer release and offers to open the download page.

## Install

1. Open the [latest release](https://github.com/Uzerin/EQOverwatch/releases/latest) and download `EQOverwatch-<version>-win-x64.zip`.
2. Unzip it anywhere and run `EQOverwatch.exe`. It is a single self-contained file; you do not need to install .NET.
3. On the **Settings** tab, choose your EverQuest folder. The app finds your character logs (`Logs\eqlog_<Character>_<Server>.txt`)
   and your `spells_us.txt`.
4. On the **Characters** tab, tick **Enable Triggers** for the characters you want monitored. Characters found in the EverQuest
   folder start with triggers off until you do.

EverQuest must be writing logs: type `/log on` in game.

Your settings are saved to `%LocalAppData%\EQOverwatch\settings.json`.

## Updating

If a newer version has been published, a bar appears at the top of the window with **Download**, **Later** and **Skip this version**.
Nothing is installed automatically: download the new zip and replace your `EQOverwatch.exe`. Your settings are kept. You can turn the
check off, or run it by hand, under **Settings > Updates**.

## Notes

- The CH Chain overlay is off by default. With it off, chain bars and the late-cast alarm show in the Buffs overlay.
- Spell data comes from your own `spells_us.txt` (and `spells_us_str.txt` next to it). Discs have their recast and duration filled in
  from it; most AAs have no recast there, so type the name exactly as the log prints it and enter the times yourself.

## Changelog

### 1.0.14
- New app icon: the radar from the logo, used for the exe, the taskbar and the window.

### 1.0.13
- Fixes a build error in 1.0.12 (the update check).

### 1.0.12
- Update notifications: the app checks GitHub for a newer release when it opens.
- Releases are built and published automatically when a version tag is pushed (`RELEASING.md`).

### 1.0.11
- Fixes build errors in 1.0.10.

### 1.0.10
- New **AAs & Discs** tab with active and cooldown bars, per-ability scope (everyone in the raid or only your characters) and
  per-bar overlays. The Divine Aura cooldown box moved here.

### 1.0.9
- The app now loads your Primary DPS log at start-up, so a fight already in progress appears on the meter, and live monitoring starts
  automatically (the Start/Stop button is gone).
- Live and imported fights share one list, newest first.
- The saved EverQuest folder is scanned at start-up; no need to press Refresh.
- Raid triggers can be edited in place.
- CH chain calls are recognised from the position tag and "cast now" in any letter case; the bar reads `<player>: Complete Heal <position>`.
  The "Total clerics in chain" box is gone.

### 1.0.3 to 1.0.8
- Spell messages are read from the correct columns of `spells_us_str.txt` (landing and fade text were wrong before).
- Songs start from your own `You begin singing <song>.` line, so a group's songs no longer create duplicate bars.
- Bard level and the good/bad flag are read from the correct spell-file columns, so buffs are no longer mistaken for songs or debuffs.
- The tracker list shows what starts each bar ("Starts when").
- Raid triggers have separate announcements for when the timer starts and when 10 seconds remain.

### 1.0.1
- The Buff & Song Tracker is a list you build: search a spell, pick its overlay, add it. Each row has its own overlay and on/off switch.
- The Raid Triggers boss box is a drop-down of mobs from ended fights.

## Building from source

Requires the .NET 8 SDK. The app itself is Windows-only (WPF); the core library and its tests run anywhere.

```powershell
dotnet test tests/EQCompanion.Core.Tests     # any OS
dotnet test                                  # whole solution, Windows
dotnet run --project src/EQCompanion.App     # Windows
dotnet publish src/EQCompanion.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The solution is `EQCompanion.sln`; the code keeps the `EQCompanion.*` namespaces from the project's earlier name and builds `EQOverwatch.exe`.

- `src/EQCompanion.Core` is the platform-neutral engine: log tailing and parsing, triggers, timers, DPS, spell database, settings, sharing.
- `src/EQCompanion.App` is the WPF app: view models, overlays, themes.
- `tests/` holds the unit tests for both.

See `RELEASING.md` for how a version is published.
