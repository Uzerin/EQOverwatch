![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A free Windows companion for EverQuest. It reads your game logs and puts timers, alerts, damage meters and raid tools on screen, for
as many characters as you play, without touching the game.

**[Download the latest version](https://github.com/Uzerin/EQOverwatch/releases/latest)** · **[Join us on Discord](https://discord.gg/cDk3xTdkAW)**

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

**Timers and alerts**
- GINA-style triggers: text, sounds, speech and timers from any log line, shareable in chat.
- Buff, debuff and song bars for all your characters, paused in safe zones and while logged out.
- Raid boss ability timers, CH chain bars with a late-cast alarm, and AA and discipline cooldowns.
- Charm break, aggro and range alerts for your boxes, and tells read aloud.

**Damage and raids**
- Live DPS meter from all your characters' logs, with a fight graph, tanking, healing, deaths, spell casts and boss abilities.
- One-click Discord picture of any fight, or automatic posts when a boss dies.
- Mob History with kill times, mob levels and resists shared by the raid.
- Loot, money and raid attendance tracking.

**Leveling and TLP servers**
- Leveling: the best zones for your level from your logs and other players', with XP, AA and plat per hour.
- Resource Hunter: every zone's bonus today, its history and how big the bonus is, shared between players.
- TLP: each server's rules, unlock dates, level caps and server bonuses, with reminders before an unlock.
- Experience and AA per hour, Legacy bonus and challenge achievements per character.

**Your characters**
- Inventory, worn gear, spellbook and missing spells for every character and class persona.
- Skills with their caps, tradeskill success rates and faction for each character.
- Drop Table: drop rates by mob and item for each server, pooled with other players and with servers whose loot is normal.
- Unanswered tells across all your characters, and a search of every character's chat history.
- Maps: the Overwatch map pack and your own labels on the in-game maps.

**Built for boxers**
- Movable overlays that only show for characters who are logged in.
- Sync between several PCs, backups, and saved overlay layouts.
- Updates itself from inside the app.

## Install

1. Download `EQOverwatch-<version>-win-x64.zip` from the [latest release](https://github.com/Uzerin/EQOverwatch/releases/latest) and unzip it anywhere.
2. Run `EQOverwatch.exe` (nothing else to install).
3. On **Settings**, choose your EverQuest folder.
4. On **Characters**, tick **Enable Triggers** for the characters to monitor.
5. Type `/log on` in game so EverQuest writes logs.

Settings are saved in `%LocalAppData%\EQOverwatch`. When a new version is out, a bar at the top offers **Install update**, which
downloads it, checks it and restarts the app; your settings are kept.

**Player count:** once a day the app sends a random id and its version number, so we know how many people use it. No names and no IP
addresses are kept. Untick **Count me in the anonymous player count** on Settings to stop it.

## Tips

- Run `/who all` to fill in classes and levels; target a pet and type `/pet leader` to count its damage with its owner.
- Run `/outputfile inventory`, `spellbook` and `missingspells` in game to fill in character data.

## Changelog

### 1.0.99

- Teek and Tormax get their own TLP tabs, TLP tables fit their contents, reverse damage shield heals leave the Heals text, and the Drop Table pools servers with normal loot.

### 1.0.98

- Shared Resource Hunter bonus sizes, bonus alerts, unlock reminders, Challenges tab per character and a Resource Hunter picture for Discord.

### 1.0.97

- Resource Hunter bonus size is measured per day.

### 1.0.96

- TLP tab with each server's rules, unlock schedule, level caps and bonuses; expansions tick by unlock date.

### 1.0.95

- Resource Hunter has its own tab, with tables by bonus, a day picker and each zone's bonus history.

### 1.0.94

- Heals on the tank are small green ticks on the damage-taken graph.

### 1.0.93

- The damage-taken graph has a line per character taking melee damage.

### 1.0.92

- Fight graph: boss health estimate, burns and a replay slider; the Maps button shows when a newer map pack is out.

### 1.0.91

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
