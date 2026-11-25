![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A free Windows companion for EverQuest. It reads your game logs and puts timers, alerts, damage meters and raid tools on screen, for
as many characters as you play, without touching the game.

**[Download the latest version](https://github.com/Uzerin/EQOverwatch/releases/latest)** · **[Join us on Discord](https://discord.gg/cDk3xTdkAW)**

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

**Timers and alerts**
- GINA-style triggers: text, sounds, speech and timers from any log line, shareable in chat.
- Buff, debuff and song bars for all your characters, lengthened by your focus items and learned AAs, paused in safe zones and while logged out.
- Raid boss ability timers, CH chain bars with a late-cast alarm, and AA and discipline cooldowns.
- Charm break, aggro and range alerts for your boxes, and tells read aloud.

**Damage and raids**
- Live DPS meter from all your characters' logs, combined parses of fights you pick, a fight graph, tanking, healing, deaths, spell casts and boss abilities.
- One-click Discord picture of any fight, or automatic posts when a boss dies.
- Mob History with kill times, mob levels and resists shared by the raid.
- Loot, money and raid attendance tracking.

**Leveling and TLP servers**
- Leveling: the best zones for your level from your logs and other players', with XP, AA and plat per hour.
- Resource Hunter: every zone's bonus today, its history and how big the bonus is, shared between players.
- TLP: each server's rules, unlock dates, level caps and server bonuses, with reminders before an unlock.
- Experience and AA per hour of active play, Legacy bonus, challenge achievements and learned AAs per character.

**Your characters**
- Inventory, worn gear, spellbook and missing spells for every character and class persona.
- Skills with their caps, tradeskill success rates and faction for each character.
- Drop Table: drop rates by mob and item for each server, pooled with other players and with servers whose loot is normal.
- Unanswered tells across all your characters, and a search of every character's chat history.
- Maps: the Overwatch map pack and your own labels on the in-game maps.

**Built for boxers**
- Movable overlays that only show for characters who are logged in, plus overlays of your own; minimize or close any of them.
- Sync between several PCs, backups, and saved overlay layouts.
- An installer that needs no admin rights, and updates from inside the app.

## Install

1. Download `EQOverwatch-Setup-<version>.exe` from the [latest release](https://github.com/Uzerin/EQOverwatch/releases/latest) and run it.
   It installs for your Windows user only, so it needs no admin rights. Prefer no installer? Unzip `EQOverwatch-<version>-win-x64.zip`
   anywhere and run `EQOverwatch.exe` instead.
2. The first time it opens, pick your EverQuest folder (the app lists the ones it finds) and choose what to share.
3. On **Characters**, tick **Enable Triggers** for the characters to monitor.
4. Type `/log on` in game so EverQuest writes logs.

Settings are saved in `%LocalAppData%\EQOverwatch`. When a new version is out, a bar at the top offers **Install update**, which
downloads it, checks it and restarts the app; your settings are kept. Uninstall from Windows' Installed apps.

**Player count:** once a day the app sends a random id and its version number, so we know how many people use it. No names and no IP
addresses are kept. Untick **Count me in the anonymous player count** on Settings to stop it.

## Tips

- Run `/who all` to fill in classes and levels; target a pet and type `/pet leader` to count its damage with its owner.
- Run `/outputfile inventory`, `spellbook` and `missingspells` in game to fill in character data.

## Changelog

### 1.0.107

- Pick several fights in Encounter DPS (Ctrl or Shift) for one combined parse, timed by time in combat.

### 1.0.106

- Simpler Focus Effects tab (type, focus, description), and mana cost foci show their full range.

### 1.0.105

- Focus Effects fills in each focus from the items that fire in your log or that you wear.
- New healing columns (HPS, total, effective, overheals, casts, biggest), and tables keep their sort when they refresh.

### 1.0.104

- Thinner, blank window top with small Discord-style buttons.

### 1.0.103

- Windows installer, a welcome window that finds your EverQuest folder, and dark window tops.

### 1.0.102

- Focus Effects has a focus per type (healing, damage, haste, mana, range and more) with what it gives at your level, and items seen firing one.

### 1.0.101

- Focus Effects lists the learned AAs that lengthen spells; the AA percent box is gone.

### 1.0.91 to 1.0.100

- TLP tab with each server's rules, unlock schedule and bonuses, unlock reminders and challenge achievements per character.
- Resource Hunter tab: tables by bonus, zone history, daily bonus sizes shared between players, alerts and a Discord picture.
- AAs tab per character, and learned AAs lengthen your timers.
- Your own overlays, and minimize or close any overlay.
- Drop Table pooled across servers with normal loot; Experience rates by active play time.
- Fight graph: boss health estimate, burns, a replay slider and a damage-taken line per character with heal marks.
- Tabs grouped under Loot, Raid, Progress and Logs.

### 1.0.81 to 1.0.90

- Tabs that stay put, a Triggers group, and an Install/Update Overwatch Maps button that protects maps you edited.
- Fight graph death lines, boss cast ticks and a damage-taken graph; Damage Taken gets Last 20 Seconds and Tank Timeline.
- Loot filed under the right server, without doubles; stacks and left-on-corpse items counted.
- Personal best DPS notices, rolls and linkdead on the Tells overlay, follow and pet buff alerts.

### 1.0.71 to 1.0.80

- Leveling tab: best zones per 5-level band, AA mode, time to level and plat/hour, shared anonymously.
- Server First, class benchmarks, shared mob stats (regen, HP, levels, resists) and floating combat text.
- Chat tab, account names, tell notifications and level history.

### 1.0.61 to 1.0.70

- DPS from all active logs; fights against several mobs count as one.
- Mob Card overlay, Session, Spell Stats and Achievements tabs, and alerts for resists, blocked buffs and broken mez or root.
- Faction standings, guild ranks, raid groups, played time, week view and learned boss timers.

### 1.0.51 to 1.0.60

- Class personas with their own skills, faction, spellbook and focus effects.
- A profile window per character with an in-game style inventory, bank and shared bank.
- Tells tab and overlay, Drop Table, Experience, Log Search and Maps tabs.

### 1.0.41 to 1.0.50

- Overlays that follow who is logged in and fade out of combat; DPS graph and the Discord picture redesign.
- Install update from inside the app, Discord auto-post when a boss dies, and buff bars that survive restarts.

### 1.0.1 to 1.0.40

- Triggers, timers, DPS, Mob History, loot, attendance, money, sync, backups and overlay layouts.
- Character data from `/outputfile`, Players & Pets, Spell Casts and tells read aloud.

## Building from source

Requires the .NET 8 SDK. The app itself is Windows-only (WPF); the core library and its tests run anywhere.

```powershell
dotnet test tests/EQOverwatch.Core.Tests     # any OS
dotnet test                                  # whole solution, Windows
dotnet run --project src/EQOverwatch.App     # Windows
```

- `src/EQOverwatch.Core`: the engine (log reading, triggers, timers, DPS, sharing).
- `src/EQOverwatch.App`: the WPF app (view models, overlays, themes).
- `tests/`: unit tests for both.

See `RELEASING.md` for how a version is published.
