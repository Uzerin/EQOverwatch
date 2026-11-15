![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A free Windows companion for EverQuest. It reads your game logs and puts timers, alerts, damage meters and raid tools on screen, for
as many characters as you play, without touching the game.

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

**Timers and alerts**
- GINA-style triggers: text, sounds, speech and timers from any log line, shareable in chat.
- Buff, debuff and song bars for all your characters, paused in safe zones and while logged out.
- Raid boss ability timers, CH chain bars with a late-cast alarm, and AA and discipline cooldowns.
- Charm break alerts, and tells read aloud.

**Damage and raids**
- Live DPS meter and fight history, with tanking, healing, deaths, spell casts and boss abilities.
- One-click Discord picture of any fight, or automatic posts when a boss dies.
- Mob History with kill times, mob levels and resists shared by the raid.
- Loot, money and raid attendance tracking.

**Your characters**
- Inventory, spellbook and missing spells for every character, with the scrolls your other characters carry.
- Experience and AA per hour, and experience by mob at each level.
- Drop Table: drop rates by mob and item for each server, pooled online with other players if you like.
- Maps: add your own labels to the in-game maps.
- Search every character's chat history.

**Built for boxers**
- Six movable overlays that only show for characters who are logged in.
- Sync between several PCs, backups, and saved overlay layouts.
- Updates itself from inside the app.

Download: https://github.com/Uzerin/EQOverwatch/releases/latest

## Install

1. Download `EQOverwatch-<version>-win-x64.zip` from the [latest release](https://github.com/Uzerin/EQOverwatch/releases/latest) and unzip it anywhere.
2. Run `EQOverwatch.exe` (nothing else to install).
3. On **Settings**, choose your EverQuest folder.
4. On **Characters**, tick **Enable Triggers** for the characters to monitor.

Type `/log on` in game so EverQuest writes logs. Settings are saved in `%LocalAppData%\EQOverwatch`.

## Updating

When a new version is out, a bar at the top offers **Install update**, which downloads it, checks it and restarts the app. Your
settings are kept.

## Tips

- Run `/who all` to fill in classes and levels; target a pet and type `/pet leader` to count its damage with its owner.
- Run `/outputfile inventory`, `spellbook` and `missingspells` in game to fill in character data.
- Spell data comes from your own `spells_us.txt`.

## Changelog

### 1.0.58

- Fixed Install update failing when an old update folder was read-only.

### 1.0.57

- Drop Table can pool drops online with other players on your server (two switches under Sharing, both off by default).
- Tables on the Encounter DPS, Drop Table and Experience tabs fit their contents.
- Long lines in the Items & Scrolls tables wrap.
- Every tab name starts each word with a capital letter.

### 1.0.56

- Fixed a loading crash under certain circumstances.
- New Drop Table tab: drop rates by mob and item for each server, from your logs, shareable as files.

### 1.0.55

- New Maps tab: add, move and edit your own labels on the in-game maps.

### 1.0.54

- Windows open where you last left them.

### 1.0.53

- Levels, classes, pets and additional characters are kept per server.
- Experience by mob table on the Experience tab.
- Debuff bars show their counters and end when cured.
- AA and discipline bars for other raid members now show.

### 1.0.52

- Songs track themselves, with an option to show every bard's songs.
- Charm watch with a break alarm.
- New Experience tab.
- Mob resists on Mob History, shareable with the raid.
- New Log Search tab.
- Fixed a crash when opening a color swatch.

### 1.0.51

- Damage shields are their own attack type, and a mob's shield counts as damage taken.
- Tagged misses are counted, and avoidance is broken down by type.
- Color wheel for every color swatch.

### 1.0.41 to 1.0.50

- Overlays follow who is logged in, fade out of combat, and the DPS overlay hides between fights.
- DPS over time graph, tank avoidance and healing crits.
- Discord picture: attack-type colors, full-width graph, hits, misses, hit rate, overheal, and columns that fit.
- Colors window for attack types, pets and healing.
- Install update from inside the app, and update checks while it runs.
- Focus effects and AA duration lengthen buff and debuff bars.
- DPS overlay shows the top 5 plus your characters.
- Discord auto-post when a boss dies.
- Per-boss trend, Uptime tab, and buff bars that pause in safe zones, follow logouts and survive restarts.
- Loot search, and auto-click settings.

### 1.0.31 to 1.0.40

- Optional auto-click on your CH turn.
- Players & Pets tab with search.
- Tells read aloud.
- Max Hit leaves out crits; the Discord picture has two halves with deaths.
- Boss History became Mob History, with mob levels, per-character records and kill lists.
- Spell Casts tab.
- Deleted output files are forgotten, and a Forget button per character.
- Boss names ignore capital letters.

### 1.0.21 to 1.0.30

- Boss kills are counted, and older loot shows again.
- One Server box for every tab.
- Boss History tab, backup and restore, and overlay layouts.
- Sync between several PCs.
- Money, Deaths, Healing, Boss abilities, Loot, Attendance and Items & Scrolls tabs.
- Recommended raid triggers and timer packs.
- Encounter DPS shows damage taken beside damage dealt.
- Missing spells lists scrolls from your other characters on the same server.

### 1.0.11 to 1.0.20

- Character data windows from `/outputfile`.
- Raid triggers start on the cast.
- Level and Class columns, and remembered players and pets with pet damage merged.
- Max Crit and Crit % work.
- The Discord picture lists every attacker.
- New app icon.
- Update notifications and automatic releases.
- Build fixes.

### 1.0.1 to 1.0.10

- AAs & Discs tab.
- The Primary DPS log loads at start, and live and imported fights share one list.
- Raid triggers can be edited in place, and CH chain calls are recognised in any case.
- Spell messages, songs and spell flags are read correctly.
- A Spells list you build yourself, and a boss drop-down in Raid Triggers.

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
