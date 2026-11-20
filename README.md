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
- Charm break, aggro and range alerts for your boxes, and tells read aloud.

**Damage and raids**
- Live DPS meter from all your characters' logs and fight history, with tanking, healing, deaths, spell casts and boss abilities.
- One-click Discord picture of any fight, or automatic posts when a boss dies.
- Mob History with kill times, mob levels and resists shared by the raid.
- Loot, money and raid attendance tracking.

**Your characters**
- Inventory, worn gear, spellbook and missing spells for every character and class persona, with the scrolls your other characters carry.
- Experience and AA per hour, and experience by mob at each level.
- Drop Table: drop rates by mob and item for each server, pooled online with other players if you like.
- Maps: add your own labels to the in-game maps.
- Skills with their caps, tradeskill success rates and faction for each character.
- Never miss a tell: unanswered tells across all your characters, with an optional overlay.
- Search every character's chat history.

**Built for boxers**
- Six movable overlays that only show for characters who are logged in.
- Sync between several PCs, backups, and saved overlay layouts.
- Updates itself from inside the app.
- A friendly [Discord server](https://discord.gg/cDk3xTdkAW) for news, help and ideas.

Download: https://github.com/Uzerin/EQOverwatch/releases/latest · Join us on Discord: https://discord.gg/cDk3xTdkAW

## Install

1. Download `EQOverwatch-<version>-win-x64.zip` from the [latest release](https://github.com/Uzerin/EQOverwatch/releases/latest) and unzip it anywhere.
2. Run `EQOverwatch.exe` (nothing else to install).
3. On **Settings**, choose your EverQuest folder.
4. On **Characters**, tick **Enable Triggers** for the characters to monitor.

Type `/log on` in game so EverQuest writes logs. Settings are saved in `%LocalAppData%\EQOverwatch`.

**Player count:** once a day the app sends a random id and its version number, so we know how many people use it. No names and no IP
addresses are kept. Untick **Count me in the anonymous player count** on Settings to stop it.

## Updating

When a new version is out, a bar at the top offers **Install update**, which downloads it, checks it and restarts the app. Your
settings are kept.

## Tips

- Run `/who all` to fill in classes and levels; target a pet and type `/pet leader` to count its damage with its owner.
- Run `/outputfile inventory`, `spellbook` and `missingspells` in game to fill in character data.
- Spell data comes from your own `spells_us.txt`.

## Changelog

### 1.0.74

- Mob levels are saved per zone; Mob Card uses your current zone.

### 1.0.73

- Floating text: new numbers appear on top and push older ones down, no overlap.

### 1.0.72

- Account names link characters and keep a shared bank per account.
- Characters list shows account, color, logged-in dot and last played.
- Floating text now two overlays with misses, merging and font choice.
- Tell notifications, chat search, kill trends, mob max hits, plat by zone.

### 1.0.71

- New floating combat text overlays for damage, heals and damage taken.
- New Chat tab with every chat type, name highlights and player filter.
- DoT damage counted on players and bosses.
- Mob History opens fights on double-click and shows drops.
- Legacy XP bonus, character birthdates and Start with Windows.

### 1.0.61 to 1.0.70

- DPS is parsed from all active logs, and fights against several mobs at once count as one.
- New Mob Card overlay, boss fight progress on the DPS overlay, and aggro and range alerts.
- CH Chain auto-press settings moved to Advanced, with a fixed timed interval.
- New Session, Spell Stats and Achievements tabs, plus Special keys on the Key Ring tab.
- New alerts for blocked buffs, resists, failed feign death, root and mez breaks, and quiet logs.
- Pet stats under their owner, rampage targets, and character flags.
- Faction standings, guild ranks, raid groups, idle raiders and total played time.
- Week view, plat per hour, next AA timer, XP by zone and recipe skill-ups.
- Learned boss timers, and your resist rates on the Mob Card.
- Output files are read as soon as EQ writes them, plus a redesigned Discord picture and fixes to Install update.

### 1.0.51 to 1.0.60

- Class personas: skills, faction, level, spellbook, missing spells and focus effects for each persona, and switching one changes class and level everywhere.
- One Inventory / Profile window per character, with an in-game style inventory, bank and shared bank.
- Skills with their caps, tradeskill success rates and faction for each character.
- Tells tab and optional Tells overlay for unanswered tells.
- Drop Table from your logs, shareable as files or pooled online with other players.
- New Experience, Log Search and Maps tabs.
- Songs track themselves, charm break alarm, and debuff counters that end when cured.
- Damage shields are their own attack type, and avoidance is broken down by type.
- Levels, classes, pets and characters are kept per server, and windows open where you left them.
- Color wheel for every swatch, tables that fit their contents, and Title Case tab names.
- Fixes for a loading crash, a color swatch crash and Install update with a read-only folder.

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
