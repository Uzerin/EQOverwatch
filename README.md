![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A Windows companion for EverQuest that reads your game logs and gives you timers, alerts and damage meters on screen, with no
changes to the game. It runs alongside EverQuest and works for as many of your characters as you want to monitor at once.

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

- **Custom triggers.** GINA-style triggers: match a line in your log, then show text, play a sound, speak it, or start a timer.
- **Raid triggers.** Recast timers for boss abilities, with a spoken announcement when the timer starts and another when 10 seconds
  remain. Pick the ability from your spell file or type the log text yourself, and edit saved triggers in place.
- **Spells & Songs tab.** Pick the buffs, debuffs and songs you care about and choose which overlay each one appears in. Bars start
  from your own casts (and your monitored characters' landings) using the messages in your spell file.
- **Character data.** Buttons on the Characters tab open a character's inventory, spellbook, missing spells and key ring, read from the
  files EverQuest's `/outputfile` command writes. Run the command in the game, then click the button.
- **AAs & Discs.** Track disciplines and AAs: an "active" bar for how long it lasts and a "cooldown" bar until it can be used again,
  for everyone in the raid or only your own characters, each in the overlay you choose.
- **CH chain.** Complete Heal chain bars from the raid-say position tags (for example `5-5-5`), with a late-cast alarm.
- **DPS & encounter history.** Live damage meter and a single newest-first list of fights, live and imported. Opening the app in the
  middle of a fight picks the fight up from your log. Copy the top 12 as chat text, or everyone as a picture for Discord. Every
  attacker shows a class (WAR, CLR, ...), and a pet's damage counts under its owner.
- **Players & pets.** The app remembers the players it has verified and which pet belongs to whom, and keeps them between runs.
  Classes are read from `/who`. To link a pet, target it and type `/pet leader`.
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

- Classes come from `/who` lines in your logs (run `/who all` in a zone and the Class column fills in). A player who has never been
  listed shows a blank class. Pets show "Pet" and are linked to their owner when you target the pet and type `/pet leader`.
  Review or forget what is stored under **Settings > Players & pets**.
- A pet's damage moves to its owner's row only for fights parsed after the pet is linked. Turn this off in the same place to list
  pets separately.
- The CH Chain overlay is off by default. With it off, chain bars and the late-cast alarm show in the Buffs overlay.
- Spell data comes from your own `spells_us.txt` (and `spells_us_str.txt` next to it). Discs have their recast and duration filled in
  from it; most AAs have no recast there, so type the name exactly as the log prints it and enter the times yourself.

## Changelog

### 1.0.20
- The Missing spells tab lists the scrolls any of your characters carry, not just the one you are looking at. A spell Reamiiv is
  missing shows "Ream: Spell: Sanctuary · General 2 · slot 3" when Ream has the scroll. Click a character's data button or Refresh so
  the other characters' newest files are read.

### 1.0.19
- **Character data windows.** The Characters tab has Inventory, Spellbook and Missing spells buttons for each character. They open a
  window built from the files EverQuest's `/outputfile` command writes into your game folder (`/outputfile inventory`,
  `/outputfile spellbook`, `/outputfile missingspells`). The window has Inventory, Spellbook, Missing spells and Key ring tabs and a
  filter box. Missing spells shows which ones you already hold a scroll for, in your bags or the shared bank. The shared bank is kept
  once per server. A "Data from" column shows when each character's newest export was read. If a character has no file yet, the
  button explains which command to type in the game.
- Raid boss triggers now start on the cast. Picking a spell fills in the casting line ("begins to cast a spell. <Dragon Roar>")
  instead of the landing message, so the bar starts when the boss begins the cast even if nobody is hit. Spells with no
  landing message at all can be picked too. Edit the log text if your log words it differently.

### 1.0.18
- **Characters** tab: Level and Class columns, filled in from your logs (`/who` lines and "Welcome to level N"). The extra characters
  you monitor are now a table with the same columns.
- **Spells & Songs** (was Buff & Song Tracker): long text wraps instead of being cut off.
- **AAs & Discs**: new "Starts when" and "Fades" columns.
- **Encounter DPS** (was DPS & Encounter History): the import bar is gone, since the Primary DPS log loads by itself; a status line and
  progress bar show only while it loads. The fight list and attacker table each take half the window.
- **Settings**: sections flow into one, two or three columns as you resize the window.

### 1.0.17
- Max Crit and Crit % now work. The game marks a critical hit on the damage line itself, such as `(Critical)` or
  `(Critical Double Bow Shot)`; the older separate "Scores a critical hit!" line is still understood.

### 1.0.16
- The DPS table, the DPS overlay and the Discord picture have a **Class** column.
- Verified players and pets are remembered. A player is verified by a `/who` line (which also gives the class) or by owning a pet;
  a pet is linked by targeting it and typing `/pet leader`.
- A known pet's damage is counted under its owner (can be switched off). **Settings > Players & pets** lists everything stored.

### 1.0.15
- The Discord picture now lists every attacker in the fight, not just the top 12. (Chat text still copies the top 12.)

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
- The Spells & Songs tab is a list you build: search a spell, pick its overlay, add it. Each row has its own overlay and on/off switch.
- The Raid Triggers boss box is a drop-down of mobs from ended fights.

## Building from source

Requires the .NET 8 SDK. The app itself is Windows-only (WPF); the core library and its tests run anywhere.

```powershell
dotnet test tests/EQOverwatch.Core.Tests     # any OS
dotnet test                                  # whole solution, Windows
dotnet run --project src/EQOverwatch.App     # Windows
dotnet publish src/EQOverwatch.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The solution is `EQOverwatch.sln`; the code uses the `EQOverwatch.*` namespaces and builds `EQOverwatch.exe`.

- `src/EQOverwatch.Core` is the platform-neutral engine: log tailing and parsing, triggers, timers, DPS, spell database, settings, sharing.
- `src/EQOverwatch.App` is the WPF app: view models, overlays, themes.
- `tests/` holds the unit tests for both.

See `RELEASING.md` for how a version is published.
