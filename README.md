![EQ Overwatch](docs/EQOverwatch-logo.jpg)

# EQ Overwatch

A Windows companion for EverQuest that reads your game logs and gives you timers, alerts and damage meters on screen, with no
changes to the game. It runs alongside EverQuest and works for as many of your characters as you want to monitor at once.

> EverQuest is a trademark of Daybreak Game Company. EQ Overwatch is an independent tool and is not affiliated with or endorsed by Daybreak.

## What it does

- **Custom triggers.** GINA-style triggers: match a line in your log, then show text, play a sound, speak it, or start a timer.
- **Raid triggers.** Recast timers for boss abilities, with a spoken announcement when the timer starts and another when 10 seconds
  remain. Pick the ability from your spell file or type the log text yourself, and edit saved triggers in place.
- **Spells tab.** Pick the buffs and debuffs you care about and choose which overlay each one appears in. Bars start from your own
  casts (and your monitored characters' landings) using the messages in your spell file. Songs need no list: every song your
  characters sing goes to the Songs overlay on its own, and a checkbox adds every other bard's songs too.
- **Charm watch.** Your characters' charms get a countdown bar, and a red banner and a spoken "Charm broke" when they break.
- **Experience.** XP and AA points per hour for each character this session, kills, levels gained and the time to the next level.
- **Resists.** Mob History shows each mob's resist % for magic, fire, cold, poison and disease, from your characters' spells, and
  the raid can pool their numbers by pasting a short line into raid or guild chat.
- **Log search.** Search every character's log for chat lines, by channel, character and date.
- **Character data.** Buttons on the Characters tab open a character's inventory, spellbook, missing spells and key ring, read from the
  files EverQuest's `/outputfile` command writes. Run the command in the game, then click the button.
- **AAs & Discs.** Track disciplines and AAs: an "active" bar for how long it lasts and a "cooldown" bar until it can be used again,
  for everyone in the raid or only your own characters, each in the overlay you choose.
- **CH chain.** Complete Heal chain bars from the raid-say position tags (for example `5-5-5`), with a late-cast alarm.
- **DPS & encounter history.** Live damage meter and a single newest-first list of fights, live and imported. Opening the app in the
  middle of a fight picks the fight up from your log. Copy the top 12 as chat text, or everyone as a picture for Discord. Every
  attacker shows a class (WAR, CLR, ...), and a pet's damage counts under its owner.
- **Players & pets.** The app remembers the players it has verified and which pet belongs to whom, per server, and keeps them between
  runs. Classes and levels are read from `/who`. To link a pet, target it and type `/pet leader`.
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

### 1.0.54

- **The app remembers where its windows were.** The main window opens where you last left it, at the same size, and maximized if
  it was maximized when you closed it. The Colors and Character data windows do the same. The spot is saved a second after you
  stop moving or resizing a window, and again when you close it, so it's kept even if the app doesn't close normally. A window
  that was minimized reopens at its normal size. If the saved spot is on a monitor that's no longer connected, Windows moves the
  window back onto a screen.

### 1.0.53

- **Levels and classes are kept per server.** A `/who` or a level-up on one server no longer changes a character with the same name
  on another server. Each character's level and class on the Characters tab are the most recent ones seen in a log on that
  character's own server. The Experience tab, song lengths and the Class column on fights look them up on the right server too.
- **Pets are kept per server.** A pet is linked to its owner only on the server where `/pet leader` was seen, so a pet with the same
  name on another server is not merged into the wrong player's DPS row. The Players & Pets tab has a Server column in both lists,
  and Forget removes only that server's entry.
- **Additional characters have a server.** Pick the server next to the name when you add one. The Server column in the list is a
  drop-down, so you can move a character to another server later. The same name can be listed once per server. Buffs landing on an
  additional character only count in logs on that character's server.
- **Your saved data is moved over.** Players and pets saved before this version had no server. Each is filed under the server of
  your watched log with that name, otherwise the server most of your logs are on. Additional characters go on the server they were
  last seen on. The next `/who` on each server corrects anything that ended up wrong.
- **Experience by mob.** The Experience tab has a new table showing what each mob gave each character at each level: kills,
  average % of a level per kill, total % and when it was last killed. Pick the character and level at the top. Each experience
  line is matched to the kill line from the same second. A gain with no kill line beside it shows as "(unknown mob)" rather than
  being guessed. The kill that causes a level-up counts at the level the character was before it. The table is saved between runs.
  The first time, each watched log's history is read from the start, which takes about a second per log. "Scan log history"
  reads them all again.
- **Debuff counters.** A debuff bar shows how many counters the spell puts on its target, for example "3 disease" or
  "2 disease, 4 poison", taken from the spell file. The Spells tab has a Counters column. EQ doesn't log partial cures, so the
  number shown is the spell's starting count.
- **Cured debuffs end their bar.** When "Ream is cured of Spreading Crud by Stackprime." (or "You are cured of ...") appears, that
  debuff's bar turns green and reads "cured by Stackprime" for 5 seconds, then goes away. Otherwise a debuff bar runs until its time
  is up or it fades, as before.
- **"Everyone in the raid" bars show for everyone.** AA and discipline bars for other raid members (for example "Quickshotz:
  Trueshot Discipline") were made but hidden, because overlays only showed bars for your own logged-in characters. A bar about
  anyone without a log on your PC, including buffs on your additional characters, now shows while any of your characters is
  logged in. Bars for your own characters still show only while that character is logged in, and nothing shows when none are.

### 1.0.52

- **Spells & Songs is now Spells.** The tab is for buffs and debuffs only. Every song your characters sing ("You begin singing ...")
  goes to the Songs overlay on its own, with the song's own duration from your spell file (at your character's level when it is
  known), so songs no longer need to be added. Songs that were on the list are removed when you update. Songs also count on the
  Uptime tab.
- **Songs from every bard.** A "Show songs from all bards" checkbox at the top of the Spells tab adds other players' songs that your
  logged-in characters see, as "Trick: Warsong of the Vah Shir". Off by default. When several of your logs see the same song there is
  only one bar.
- **Charm watch.** When one of your characters charms a mob, a bar such as "Ream: Charm, a rock golem" counts down on the Debuffs
  overlay for the charm's longest duration. When the charm wears off, the bar turns into a red "CHARM BROKE" banner for 10 seconds and
  the app says "Charm broke" (using the trigger voice and the Mute switch). Only your own charms: the game only tells the caster. It
  can be switched off in Settings.
- **Experience tab.** For each character: level, XP gained this session, XP per hour, time to the next level, AA points gained and per
  hour, kills that gave experience and when the last gain was, plus totals for the session. The session starts when the app opens;
  Reset session starts it again. Where a character is in its level comes from the last level-up near the end of its log. A kill whose
  line shows no percentage (at the level cap, or with AA experience on) counts as a kill but adds no XP.
- **Resists in Mob History.** New Magic, Fire, Cold, Poison and Disease columns show each mob's resist %: resisted spells out of every
  spell of that type that landed or was resisted. Only your characters' own spells on mobs count, live and from logs you read in (a
  log read twice is counted once). Clicking a mob shows its numbers by resist type, including the rarer types.
- **Raid resist sharing.** Share resists copies a short line such as `{EQO:R1:Xin Thall Centien:M180-3:F22-6}` for the selected mob to
  paste into /rs or /gu; Share all copies one line for the mobs you cast on most recently (click again for the next line). Everyone
  running Overwatch picks the line up from raid or guild chat. The Mine / Raid switch shows your numbers alone or added to everyone's
  shares. Each player's newest share replaces their older one, shares older than 90 days are dropped, and with three or more
  contributors anyone more than 30 points from the middle value for a type is left out of that type and shown in orange.
- **Log search tab.** Search the chat in every character's log: every word has to be in the line and a phrase in quotes stays
  together. Pick the channels (tells, guild, raid, group, say, OOC/shout/auction, chat channels or everything else), one character or
  all, and the last 7, 30 or 90 days or all time. Up to 1,000 lines are listed, newest first; double-click a line to copy it.
- **Color swatch crash fixed.** Opening the Settings tab or the Colors window could close the app (the color wheel's brightness slider
  reacted before the control was ready). It no longer does.

### 1.0.51

- **Damage shields.** Thorns, flames and frost lines ("is pierced by ...'s thorns", "is burned by YOUR flames", "is tormented by ...'s frost") are now their own attack type, "Damage shield", with its own color in the Colors window and its own segment in the damage bars. A player's damage shield counts in their total, SDPS and the group DPS, and shows in the DPS-over-time graph. A pet's damage shield counts with its owner when pet damage is merged.
- **Mob damage shields are damage taken.** When the fight's target hurts a player with its own damage shield, that counts as damage taken (but not as an attack on the player, so it does not change their avoidance percentages). The Damage taken tab adds a line saying how much the shield did, in how many hits, and from when to when into the fight, so you can see how long it took to dispel.
- **Tagged misses are counted.** Miss lines ending in a tag such as "(Riposte)", "(Flurry)" or "(Rampage)" were being skipped (about 1.3% of misses in a real log). They now count as misses and avoidance, which also corrects players' Misses and Hit Rate.
- **Avoidance percentages.** The Avoided count is gone. The Damage taken tab shows Avoid %, Dodge %, Parry %, Block %, Riposte %, Miss % (plain misses) and Other % (runes, wards, invulnerable) for each tank, to 0.01%, as a share of all the target's attacks on them. The Discord picture's tanking table shows the overall Avoid %.
- **Color wheel.** Clicking a color swatch (the attack colors in the Colors window and the theme colors in Settings) opens a color wheel with a brightness slider. Typing a #RRGGBB code still works.

### 1.0.50

- **Overlays follow who is logged in.** A character counts as logged in while a line has been written to its log in the last 60 seconds. Timer bars, alerts, buffs, debuffs and songs show only for characters that are logged in (a character's bars come back with its next log line), and with nobody logged in every locked overlay is hidden completely. The DPS overlay shows only while an active character is producing combat lines, so a logged-off character's fight no longer keeps it on screen. While a character is logged in but out of combat, the overlays use your out-of-combat transparency as before. Unlocked overlays always show so you can place them.

### 1.0.49

- **Full-width graph in the Discord picture.** The DPS over time graph now runs across the whole picture, directly under the mob's name and above the tables, and is a little taller.

### 1.0.48

- **DPS over time graph.** The Encounter DPS tab has a new Graph tab, and the Discord picture has a "DPS over time" section under the right-hand tables. It plots damage per second through the fight: the whole group as a thick line plus lines for the top five damage dealers. Fights shorter than a few seconds have no graph.
- **Tank avoidance.** Attacks the fight's target makes on a player or pet that miss (dodged, parried, blocked, riposted and the like) are counted. The tanking table in the picture and the Damage taken tab have new Avoided and Avoid % columns.
- **Overheal % in the Discord picture.** The Healing table has an Overheal % column (the Healing tab already had it).
- **Update checks while running.** The app looks for a newer version again every few hours while it stays open (Settings, Updates: default 4 hours, 0 = only when the app opens). A version you already saw or skipped does not bring the banner back. The Install update confirmation now shows the release notes.
- **Test cleanup fix.** A test that sometimes failed because a temporary settings file was still being written now waits and retries.

### 1.0.47

- **Misses and hit rate in the Discord picture.** The DPS table has two new columns after Hits: Misses (attacks that missed, including dodges, parries and the like) and Hit Rate (hits out of all attempts). A pet's hits and misses count with its owner when pet damage is merged.

### 1.0.46

- **Hits in the Discord picture.** The DPS table has a new Hits column after Total showing each player's number of damage hits.
- **Columns fit their content.** Every column in the picture (DPS, tanking, healing and deaths) is now as wide as its widest value, and the picture is as wide as its two halves need, so long names and big numbers are no longer cut off. Names are shortened with "..." only past a very long name.

### 1.0.45

- **Overlays fade when you are not fighting.** A new "Overlays out of combat (% visible)" setting on the Settings tab sets how see-through all overlays get once no combat line has been seen for 10 seconds (100 turns fading off, the lowest is 10). They are fully shown again on the next combat line, and always while they are unlocked so you can place them.
- **The DPS overlay goes away between fights.** It disappears when no combat line (hit, miss or death) has been seen for a number of seconds you choose (30 by default, 0 keeps it up) and returns with the next combat line. Only live lines count; reading old logs does not bring it back.
- **Attack colors in the Discord picture.** Each player's damage bar is now split into segments by attack type: slash, pierce, crush, hit, hand-to-hand, kick (all kicks together), bash, backstab, frenzy, archery, other melee, spells and procs (one color), and pets (everything a pet does, one color). Crits of an attack are drawn in a lighter shade of its color, so you can see how much of the damage was crits. A legend under the damage table lists only the attacks that appeared in the fight. The live DPS meter is unchanged.
- **Colors window.** The new Colors... button on the Encounter DPS tab opens a window with every color (the attack types, pets and healing), a Reset button and a preview of a sample fight drawn in your colors.
- **Healing crits.** Critical heals ("... by Complete Heal. (Critical)") are now counted; before, a heal with that tag at the end of the line was missed. The Healing table shows each healer's Crit % (in place of the heal count), and the healing bars are split into the healing color and a lighter shade for critical heals.
- **Install update button.** When a new version is announced, "Install update" downloads it, checks it against the SHA-256 checksum published with the release, replaces EQOverwatch.exe in the folder it runs from and restarts. It asks first, naming the folder, and is not offered during combat. Nothing is installed if the checksum is missing or does not match, and a failed swap keeps the old version. Releases from this version on carry the checksum file; older releases still open the release page.

### 1.0.44

- **Focus effects and AA duration.** A new "Focus Effects" button on each character's line on the Characters tab opens the character window on its Focus Effects tab. Pick a buff duration focus and a debuff/DoT duration focus from lists built from your `spells_us.txt` (None by default), and type the total spell-duration percentage shown in your AA window (Alt+V). The focus percentage and the AA percentage add together. Buff, debuff and DoT bars, and the uptime tab, then use the longer time, but only for spells that character casts itself (it must see "You begin casting"). Songs are never extended. Each focus counts only on spells up to its level, and its bonus shrinks for higher-level spells, skips spells shorter than the focus allows and spells with effects the focus excludes. Choices are saved per character. This needs the newer split `spells_us.txt` (with `spells_us_str.txt`).
- Tidied two build warnings.

### 1.0.43

- **DPS overlay shows the top 5 and your characters.** The overlay lists the five highest damage dealers, then any of your own characters that are doing damage, even when they are far down the ranking. Their rows keep their real rank, are drawn in gold and sit below a gap. A character already in the top 5 is not listed twice.
- **Discord auto-post.** A new "Discord auto-post" box on the Settings tab posts the picture from Copy Image for Discord, with a line such as "Praetorian Pius killed in 3:42", to one channel's webhook when a boss fight ends. It is off by default. Choose whether wipes are posted too, the fewest players and the shortest fight. Only fights that have just ended from your Primary DPS log are posted, so each fight goes out once and old logs never post. "Send test post" checks the address. The webhook address is hidden in the box, stays on this PC and is left out of backups.
- **Per-boss trend.** On the Mob History tab, the kills panel shows how the boss's clear times have gone: the latest kill against the first, the recent kills against the earlier ones with the change in raid DPS, and a row of bars with one bar per kill, oldest first.
- **Buff and debuff uptime.** A new Uptime tab in the encounter details shows how long each spell on your Spells & Songs list was up during the fight and what share of the fight that was. Debuffs landing on the mob you are fighting are counted too (they still get no timer bar).
- **Buff and debuff bars stop in safe zones.** While a character is in the Plane of Knowledge, Guild Lobby, Guild Hall, the Bazaar or the Nexus, its buff and debuff bars stop counting down (the game does not tick buffs there) and carry on when it leaves, with the time they stopped for given back. The zone list is on the Settings tab and can be edited. Songs are not stopped, and a character that dies loses its bars.
- **Buff and debuff bars follow logging out and in.** When a character camps out, its bars are hidden from the overlays and stopped. They return, with the time they had left, when the character logs in again. A camp counts as a logout when "prepare your camp" is not followed by "You abandon your preparations to camp" within 33 seconds.
- **Bars survive closing Overwatch.** The bars of every character are saved and put back at the next start. Bars that were running keep their end time (the game kept counting while the app was closed), and bars that were stopped keep their time left. The file is not part of backups.
- **Loot search.** The Loot tab's Filter is now a Search: every word has to be found in the item, the looter or the mob ("seru sword"), a phrase in quotes stays together, and a range box limits it to the last 7, 30 or 90 days. When the search finds a single item, the status line says how often it was looted and who got it last.

### 1.0.42

- **Tidier Auto-click box.** The checkbox and the toggle hotkey are now on the top line, and the click delay and auto-off time are on the second line.

### 1.0.41

- **Set the auto-click's auto-off time.** A new "Auto-off after (minutes)" box sits in the Auto-click box on the Settings tab, from 1 to 120 minutes (5 by default). The click switches itself off after that long with no chain cue for your position, and every cue starts the count again. The status line shows the time you set.

### 1.0.40

- **Auto-click on your CH turn.** An optional switch on the Settings tab. When another cleric says your position tag with the cue (the same cue as the late-cast alarm), the app presses the left mouse button once where the pointer is, after a delay you set, so you can park the pointer on your Complete Heal button. It does nothing inside the game client, and it clicks only while EverQuest (`eqgame.exe`) is the window in front. It is always off when the app starts, a global hotkey (Ctrl+Alt+F9 unless you change it) switches it on and off with a spoken "Auto click on" or "Auto click off", it makes at most ten clicks a minute, and it switches itself off after a quiet time you set (five minutes by default) with no cue. Check your server's rules on automation before using it.

### 1.0.39

- **Players & Pets has its own tab.** The verified players and pets moved out of Settings into a tab of their own, with the two lists side by side and filling the window, so they are easy to scroll. The pet damage switch moved with them.
- **Columns fit their contents.** The columns in both tables are as wide as the names in them.
- **Search the players.** A box above the players list narrows it to names that contain what you type.
- **Forgetting.** Players no longer have a Forget button. Each pet has a Forget button, and Forget all pets clears the pet list. Both ask first, and a pet is learned again the next time a `/pet leader` line names it.

### 1.0.37

- **Tells are read aloud.** When someone sends a tell to a character you are watching, such as `Catiua tells you, 'I SEE YOUUUUUU'`, Overwatch says "Catiua tells Stackprimeiiv: I SEE YOUUUUUU", naming the character it was sent to. It uses the same voice, volume and Mute audio switch as trigger audio. Turn it off with Read tells aloud in the Tells box on the Settings tab. It works for every character log you watch, speaks only new tells (not ones from history you read in), cuts a message off at 200 characters, and skips an exact repeat of the same tell within two seconds.

### 1.0.36

- **Max Hit is the biggest non-crit hit.** On the DPS parse, in the Discord picture and in the exports, Max Hit leaves out critical hits. Max Crit still shows the biggest crit.
- **The Discord picture of a fight has two halves.** The DPS table is on the left. Tanking, Healing and Deaths are stacked on the right, and every character is still listed. Deaths show who died, when in the fight, and who killed them, or "No deaths".
- **Boss History is now Mob History.** The tab and its "Boss" column are renamed, and the two tables share the width evenly.
- **Highest Level and Lowest Level.** Two columns next to each mob show the highest and lowest level your `/consider` lines have shown for it, such as `(Lvl: 62)`. The mob names and levels are remembered across every log. A mob you have never considered shows a dash.
- **Mob History follows the Primary DPS character.** Kills, attempts, times and DPS come from that character's log, so reading every character's log builds each one's own record. Each saved fight keeps its character and server in the background; the tab never shows them. Fights saved by older versions count for every character until that log is read again.
- **The kills list is fastest first.** The list beside the table, Copy list and the Discord picture of every kill run from the fastest clear time to the slowest.

### 1.0.35

- **Boss History shows each boss's kills.** Click a boss and its kills appear beside the table, newest first, with the start time, clear time, DPS, players, deaths and damage of each. The fastest kill and the best DPS are highlighted. A line under the list sums up the attempts that did not end in a kill. Copy list puts the kills on the clipboard as text, and Copy Image for Discord makes a picture of every kill of that boss.

### 1.0.34

- **Deleted output files are forgotten.** When EQ Overwatch starts it now drops the saved inventory, spellbook and missing spells whose `/outputfile` file is no longer in your EverQuest folder, so deleted files stop showing up in Items & Scrolls. A character with nothing left disappears, and a server's shared bank goes when no inventory for that server remains. Nothing is dropped when the EverQuest folder is not set or cannot be read, and characters sent from another PC are not touched.
- **Forget button.** The Characters tab has a Forget button that clears what is saved for one character (after asking). It comes back if the files are read again.

### 1.0.33

- **Spell Casts tab.** In the fight details (next to Healing), every spell the raid began casting during a fight: a By player list in the usual layout (each player with their spells, casts, casts per minute, first and last cast, fizzles), a By spell list with how often each spell was cast and by how many players (handy for Slow, Weaken and the like), and a Timeline with a line for each spell and a tick for every cast. A Spell box narrows all three to spells containing that text, and Copy puts the breakdown on the clipboard. Other players' casts only appear in your log when they are in range of you.
- **Discord picture.** The picture now carries three sections, top to bottom: damage, tanking (damage taken) and healing, with every character in each.

### 1.0.32

- **Boss names ignore capital letters.** A mob such as "a Praetorian Pius" is the same fight whether the log prints "A Praetorian Pius" or "a Praetorian Pius", so its kill is counted. In Boss History, a fight that an older version saved in pieces is replaced by the whole fight the next time you read that log, and a shorter read never undoes a kill already recorded.

### 1.0.29

- **Boss kills are counted.** Kill lines such as "A Praetorian Pius was slain by Pecos!" are now recognised (they were missed before), so Boss History shows kills, kill times and best DPS. Fights already saved as attempts are corrected the next time you read that log. A boss with no kill yet shows your best attempt's DPS. Player deaths in the same wording now show on the Deaths tab too.
- **Loot shows again.** Loot and money saved before servers were recorded now take their character's server instead of being hidden by the Server box, and reading a log fills in the server on entries that were missing it.

### 1.0.27

- **One Server box.** A Server box in the top bar filters the Characters, Loot, Attendance and Items & Scrolls tabs. It starts on your Primary DPS character's server and follows it when you change the Primary DPS; pick another server, or All servers, to look at those. Loot and money are now saved with the server they came from (older entries show under All servers only).
- **More over the PC sync.** Loot, money and raid roster records now travel to the main PC along with the character files, without doubling up drops that both PCs saw.
- **Boss History tab.** For each boss: kills, attempts, fastest and average kill, best DPS and when you last fought it. Fights are saved as they finish, so the history keeps growing after old log days drop out of the loaded window.
- **Backup & restore.** Settings has Back up… and Restore… buttons for your settings, triggers, loot, attendance, characters and boss history in one zip file. The pairing code is not included. A restore takes effect the next time the app starts.
- **Overlay layouts.** Save the position, size, shown and locked state of the six overlays under a name and switch between layouts in Settings. An optional setting (off by default) applies the newest layout made for your monitor setup when the monitors change.
- The spell search boxes no longer lose the first letter you type.

### 1.0.26

- **Several PCs.** Settings has a new "Several PCs" section. Set your main PC to receive, and your other PCs to send, with a pairing code typed into Overwatch. The other PCs find the main PC on your home network and send their `/outputfile` files whenever they change, so every character shows up in Items & Scrolls. Everything is encrypted with the code.
- **Items & Scrolls server.** A Server box at the top starts on your Primary DPS character's server; pick another server, or All servers, to search those instead. It applies to the item search, the scrolls and the shopping list.
- **Game commands.** Buttons on Items & Scrolls copy `/outputfile inventory`, `/outputfile spellbook` and `/outputfile missingspells` so you can paste them into the game.
- **Boss spell damage.** Lines like "a boss hit you for 497 points of magic damage by Judgment." now count as damage taken, with the spell name.

### 1.0.25

- **Money in the Loot tab.** Your characters' coin splits are read from the log and shown per mob and per character (platinum, gold, silver, copper). Each character is counted separately. The Loot tab also gets a By character view.
- **Deaths tab.** A tab in Encounter DPS lists who died in the selected fight, how far in, and what killed them.
- **Attendance totals.** Pick All raids or the last 30, 60 or 90 days, and Copy totals to paste into a spreadsheet or Discord.
- **Timer packs.** Copy pack puts every saved raid timer for the boss in the Boss box on the clipboard; Import pack reads them from the clipboard and offers each one for you to accept.
- The window reopens on the tab you were last using.

### 1.0.24

- **Boss abilities.** A new tab beside Damage taken lists each spell a boss cast, how many times, the usual interval and when into the fight.
- **Recommended raid triggers.** A Recommended button next to the Boss dropdown in Raid Triggers offers spells cast at a regular interval in your loaded fights, ready to add.
- **Loot tab.** Loot line by line, grouped by mob, or summed up, with who won the roll. Copy it for Discord.
- **Attendance tab.** Reads the raid window's Dump files (`RaidRoster_...`) and shows who was there, plus attendance over all raids.
- **Healing.** A Healing tab in Encounter DPS with effective healing, HPS and overheal.
- **Items & Scrolls tab.** Search items on every character, see scrolls you carry that a character needs, and a shopping list of missing spells nobody has a scroll for. Old character files are marked.
- Damage from a boss's corpse no longer counts as damage taken.
- Boss timers and the buff tracker accept both "begins casting X." and the older "begins to cast a spell. X" wording.
- Attempts that fail because the target is INVULNERABLE are no longer counted as misses.
- Thorns (damage shield) damage is counted.

### 1.0.23
- Encounter DPS tab: the fight list takes the left half; damage dealt is on the top right and a new damage-taken table (what the boss did to each player, with a bar) is on the bottom right.
- A player who died in a fight shows `(Deaths: #)` after the name, in both tables and in the Discord picture.
- Copy Image for Discord now puts the damage dealt and the damage taken in one picture.
- The fight list only shows fights the Primary DPS character saw: choosing another Primary DPS character clears the old character's fights before loading the new one's.
- Spellbook and Missing spells lists are sorted from the highest level to the lowest.

### 1.0.22
- The columns in the character data windows and on the Characters tab fit their contents, so long scroll lists no longer wrap.

### 1.0.21
- The scrolls listed on the Missing spells tab come only from characters on the same server.

### 1.0.20
- The Missing spells tab lists the scrolls your other characters on the same server carry, not just the one you are looking at. A spell Reamiiv is
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
