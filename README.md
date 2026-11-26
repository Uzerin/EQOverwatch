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
- Unanswered tells and DZ timers across all your characters, and a search of every character's chat history.
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

**Player count:** once a day the app sends a random id, its version number and the names of the servers you played on, so we know
how many people use it and on which servers (shown at the top of the app and on the TLP tab). No character names and no IP addresses
are kept. Untick **Count me in the anonymous player count** on Settings to stop it.

## Tips

- Run `/who all` to fill in classes and levels; target a pet and type `/pet leader` to count its damage with its owner.
- Run `/outputfile inventory`, `spellbook` and `missingspells` in game to fill in character data. Right-click a character's profile
  window and pick **Add Overwatch Data socials** (while that character is logged out) to get two socials that run them all.
- To post raid kills and loot to your Discord, paste a channel webhook on Settings, then say its raid key in /rsay.

## Changelog

### 1.0.122

- The Discord picture is 16:9, and its DPS, Healing and Tanking tables each show the top 25.

### 1.0.121

- Mob HP and regen estimates use the kills with the most damage (the top 5 or the top half).

### 1.0.120

- Reload buff bars (Triggers › Spells) starts the buff and debuff bars again from the last 3 hours of your logs; it also runs once after each update.

### 1.0.119

- Every raid boss fight stays in the encounter list, plus the newest 500 other fights; the first start reads all logs once to rebuild it.
- Buff bars show only the character, buff and time left.

### 1.0.118

- Raid triggers are grouped by expansion and zone, with on/off for each group.
- Buffs your characters cast keep their focus and AA bonuses when another of your boxes sees them land.

### 1.0.117

- Loot lists linked from the loot window ("Item | Item | Item") are read from raid chat.
- Class and level from guild and raid roster dumps fill the Class column and overlay labels.

### 1.0.116

- Settings split into General, Overlays, Alerts and Sharing; Default Caster Level removed (your highest character's level is used).
- Raid boss kill posts are the parse picture alone; raid keys expire on raid changes, logins, quiet logs and after 6 hours without a boss kill.
- Loot unlock timer after raid boss kills, unlooted items on the Tells overlay, a Posted Loot tab, an Unlooted item box alert and saving kill pictures.

### 1.0.115

- Encounter list shows Raid Boss, Name, Zone and Kill Time; the Raid Boss tick covers every fight of that mob and ticks itself on the server's kill announcement.
- Discord webhooks on Settings for raid boss kills, posted loot and unlooted items, each with its own raid key and Share automatically; say the key in /rsay and the raid's Overwatch players send to it.
- Send to Discord buttons on Encounter DPS and Loot.

### 1.0.114

- Fixed a crash when floating combat text merged several hits into one number.

### 1.0.113

- Mob Card shows regen and HP again and counts the kills once; the kill-time estimate stays on the DPS overlay.
- The Experience tab scrolls.

### 1.0.112

- "Add Overwatch Data socials" on the profile window's right-click menu.
- Ding history under the playtime chart.
- Mob Card: last killed and kill count, and "Max hit".

### 1.0.111

- Players today at the top of the app, and players per server on the TLP tab.
- Guild tab, a playtime chart on Experience, and Ctrl+F finds spells and zones.
- A compact Discord picture, a "Problems lately" list on Settings and a copy button after an error.
- Faster start, fewer settings writes, and a damaged settings file is restored from the last good copy.

### 1.0.101 to 1.0.110

- Windows installer, welcome window, dark window tops and minimize to the tray.
- Focus Effects filled in from your items, with a focus picker per type.
- Encounter DPS: combined parses of picked fights, date and mob filters, new healing columns and sorts that stay put.
- DZ timers on the Tells overlay, Ctrl+F search, trigger folders and retired characters.
- Compact mode and interface size, releases posted to Discord.











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
