using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.Core.Settings;

/// <summary>Everything persisted to <c>settings.json</c>.</summary>
public sealed partial class AppSettings
{
    public const double DefaultEncounterTimeoutSeconds = 8;
    public const double MinEncounterTimeoutSeconds = 1;
    public const double MaxEncounterTimeoutSeconds = 120;

    public int Version { get; set; } = 2;
    public List<WatchedLogSettings> WatchedLogs { get; set; } = new();
    public List<TriggerRule> Triggers { get; set; } = new();
    public double EncounterTimeoutSeconds { get; set; } = DefaultEncounterTimeoutSeconds;
    public bool IsAudioMuted { get; set; }

    /// <summary>Speak incoming tells aloud (on by default).</summary>
    public bool ReadTellsAloud { get; set; } = true;

    /// <summary>Show every bard's songs on the Songs overlay (off: only songs your characters sing).</summary>
    public bool ShowAllBardSongs { get; set; }

    /// <summary>Bars and a break alarm for your characters' charms (on by default).</summary>
    public bool CharmWatchEnabled { get; set; } = true;

    /// <summary>
    /// The zones where the game does not tick buffs, so buff and debuff bars stop while a character is in one. Null means the built-in list;
    /// an empty list means no zone stops them.
    /// </summary>
    public List<string>? NonCombatZones { get; set; }

    /// <summary>Each character's duration focus choices and AA percentage (Focus Effects window). Characters not listed have none.</summary>
    public List<DurationProfileSetting>? DurationProfiles { get; set; }

    /// <summary>How visible every overlay is (10-100 percent) while no combat has been seen for a few seconds. 100 means no fading.</summary>
    public int OverlayIdlePercent { get; set; } = 100;

    /// <summary>Seconds without a combat line before the DPS overlay goes away; 0 keeps it up.</summary>
    public int DpsHideSeconds { get; set; } = OverlayFade.DefaultDpsHideSeconds;

    /// <summary>Hex color per attack type name (see <see cref="AttackType"/>); missing types use the default.</summary>
    public Dictionary<string, string>? AttackColors { get; set; }

    /// <summary>Timer and text overlays you made yourself (Settings, Overlays); triggers can show in them.</summary>
    public List<CustomOverlaySetting> CustomOverlays { get; set; } = new();

    public OverlaySettings BuffsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Buffs);
    public OverlaySettings DebuffsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Debuffs);
    public OverlaySettings SongsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Songs);
    public OverlaySettings RaidTriggersOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.RaidTriggers);
    public OverlaySettings ChChainOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.ChChain);
    public OverlaySettings DpsOverlay { get; set; } = OverlaySettings.DefaultDps;

    /// <summary>The unanswered-tells overlay (hidden until switched on).</summary>
    public OverlaySettings TellsOverlay { get; set; } = OverlaySettings.DefaultTells;

    /// <summary>Days tells are kept on the Tells tab; 0 keeps them for good.</summary>
    public int TellKeepDays { get; set; } = 30;

    /// <summary>Recast countdowns for raid boss abilities.</summary>
    public List<RaidBossTimerRule> RaidBossTimers { get; set; } = new();

    public ThemeConfig Theme { get; set; } = new();

    /// <summary>The saved placement of one of the five timer overlays.</summary>
    public OverlaySettings GetTimerOverlay(TimerOverlayCategory category) => category switch
    {
        TimerOverlayCategory.Buffs => BuffsOverlay,
        TimerOverlayCategory.Debuffs => DebuffsOverlay,
        TimerOverlayCategory.Songs => SongsOverlay,
        TimerOverlayCategory.ChChain => ChChainOverlay,
        _ => RaidTriggersOverlay,
    };

    /// <summary>The EverQuest folder (the one that contains <c>Logs</c> and <c>spells_us.txt</c>); empty when not chosen yet.</summary>
    public string EqFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Characters whose buffs should be tracked even though they are not played on this PC (a second PC, groupmates).
    /// They count as players for DPS and as valid targets for "cast on other" buff timers.
    /// </summary>
    public List<string> AdditionalMonitoredCharacters { get; set; } = new();

    /// <summary>
    /// The same characters, each with the server it is on. Settings saved before servers were kept have only
    /// <see cref="AdditionalMonitoredCharacters"/>; this list wins when it has entries.
    /// </summary>
    public List<AdditionalCharacterSetting> AdditionalCharacterServers { get; set; } = new();

    /// <summary>Where the main, Colors and Character data windows were when last moved or closed, by window name.</summary>
    public Dictionary<string, WindowPlacementSetting> WindowPlacements { get; set; } = new();

    /// <summary>How many days of history the Primary DPS log is imported for; 0 means all history.</summary>
    public int HistoricalDaysLimit { get; set; } = DefaultHistoricalDaysLimit;

    /// <summary>The tab that was showing when the app closed, so it opens on the same one next time.</summary>
    public int LastTabIndex { get; set; }

    /// <summary>The Triggers tab's last sub-tab (Custom, Raid, Spells, AAs &amp; Discs).</summary>
    public int LastTriggersTab { get; set; }

    /// <summary>The Loot tab's last sub-tab (Loot Log, Drop Table, Items &amp; Scrolls).</summary>
    public int LastLootTab { get; set; }

    /// <summary>The Raid tab's last sub-tab (Attendance, Mob History, Players &amp; Pets).</summary>
    public int LastRaidTab { get; set; }

    /// <summary>The Progress tab's last sub-tab (Experience, Session, Leveling, Spell Stats).</summary>
    public int LastProgressTab { get; set; }

    /// <summary>The Logs tab's last sub-tab (Chat, Log Search).</summary>
    public int LastLogsTab { get; set; }

    /// <summary>
    /// Which tab arrangement <see cref="LastTabIndex"/> was saved with: 0 = the original 20 tabs, 1 = with the Triggers group,
    /// 2 = with the Loot, Raid, Progress and Logs groups too.
    /// </summary>
    public int TabLayout { get; set; }

    /// <summary>The current tab arrangement; see <see cref="TabLayout"/>.</summary>
    public const int CurrentTabLayout = 2;

    /// <summary>
    /// Turns a tab index saved with an older arrangement into the current main tab and, when the old tab is now a sub-tab, its
    /// position in that group (-1 when the saved sub-tab choices still apply).
    /// </summary>
    public static (int MainTab, int SubTab) MigrateTabIndex(int index, int layout)
    {
        index = Math.Max(0, index);
        if (layout >= CurrentTabLayout)
        {
            return (index, -1);
        }

        int triggersSub = -1;
        if (layout < 1)
        {
            (index, triggersSub) = index switch
            {
                0 => (0, -1),
                <= 4 => (1, index - 1),
                _ => (index - 3, -1),
            };
        }

        // Layout 1: Characters, Triggers, Encounter DPS, Loot, Drop Table, Attendance, Items & Scrolls, Mob History, Experience,
        // Session, Leveling, Spell Stats, Maps, Log Search, Chat, Players & Pets, Settings.
        return index switch
        {
            0 => (0, -1),
            1 => (1, triggersSub),
            2 => (2, -1),
            3 => (3, 0),
            4 => (3, 1),
            5 => (4, 0),
            6 => (3, 2),
            7 => (4, 1),
            8 => (5, 0),
            9 => (5, 1),
            10 => (5, 2),
            11 => (5, 3),
            12 => (6, -1),
            13 => (7, 1),
            14 => (7, 0),
            15 => (4, 2),
            _ => (8, -1),
        };
    }

    /// <summary>Saved overlay layouts, each remembered for the monitor setup it was made on.</summary>
    public List<OverlayProfile> OverlayProfiles { get; set; } = new();

    /// <summary>When true, a saved overlay layout made for the current monitor setup is applied when the monitors change.</summary>
    public bool AutoSwitchOverlayProfiles { get; set; }

    /// <summary>Multi-PC character sync: "Off", "Host" (this is the main PC) or "Send" (send this PC's files to the main PC).</summary>
    public string SyncMode { get; set; } = "Off";

    /// <summary>The pairing code shared by the main PC and the PCs that send to it.</summary>
    public string SyncCode { get; set; } = string.Empty;

    /// <summary>This PC's name as shown on the main PC.</summary>
    public string SyncPcName { get; set; } = string.Empty;

    /// <summary>The main PC's address, only needed when it cannot be found automatically.</summary>
    public string SyncHostAddress { get; set; } = string.Empty;

    public const int DefaultHistoricalDaysLimit = 7;

    /// <summary>The choices offered for <see cref="HistoricalDaysLimit"/> (0 = all history).</summary>
    public static IReadOnlyList<int> HistoricalDaysChoices { get; } = new[] { 1, 3, 7, 14, 30, 90, 0 };

    /// <summary>
    /// The earliest log time to import for <paramref name="days"/> days of history ending at <paramref name="now"/>
    /// (log timestamps are in the PC's local time), or null for all history (0 or less).
    /// </summary>
    public static DateTime? GetHistoricalStart(int days, DateTime now) => days <= 0 ? null : now.AddDays(-days);

    /// <summary>"1 Day", "7 Days", "All History".</summary>
    public static string DescribeHistoricalDays(int days) => days <= 0 ? "All History" : days == 1 ? "1 Day" : $"{days} Days";

    /// <summary>Complete Heal chain tags, cues and alarm timing.</summary>
    public ChChainSettings ChChain { get; set; } = new();

    /// <summary>Starts cooldown bars for class abilities such as Divine Aura.</summary>
    public bool IsCooldownTrackerEnabled { get; set; } = true;

    /// <summary>Ask GitHub for a newer version when the app opens.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Hours between checks for a newer version while the app stays open. 0 checks only when the app opens.</summary>
    public int UpdateCheckHours { get; set; } = 4;

    /// <summary>Rank leveling zones with what other players shared online (on unless switched off).</summary>
    /// <summary>How the Custom triggers list is grouped: "Auto" (expansion, then zone) or "Folders" (your own folders).</summary>
    public string TriggerGrouping { get; set; } = "Auto";

    /// <summary>Interface size in percent (50 to 150); overlays are not affected.</summary>
    public double UiScalePercent { get; set; } = 100;

    /// <summary>
    /// Faster start: the DPS meter keeps only its newest fights, so after a full read the start time of the oldest fight it kept is
    /// remembered and the next start reads the logs from there. Null until the first read; forgotten when the logs change.
    /// </summary>
    public DateTime? BackfillFromLocal { get; set; }

    /// <summary>The logs (and history setting) <see cref="BackfillFromLocal"/> was worked out for.</summary>
    public string BackfillLogsKey { get; set; } = string.Empty;

    /// <summary>True once the encounter list was rebuilt from every log (done once, so every raid boss fight is in it).</summary>
    public bool EncounterListRebuilt { get; set; }

    /// <summary>The app version whose buff bars are up: after an update the bars are started again from the logs once.</summary>
    public string BuffBarsVersion { get; set; } = string.Empty;

    /// <summary>The newest crash.log entry already shown (so each error is reported once).</summary>
    public DateTime? LastCrashSeenUtc { get; set; }

    /// <summary>Smaller text and tighter table rows.</summary>
    public bool CompactMode { get; set; }

    /// <summary>Minimizing hides the window to the system tray.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool UseSharedLeveling { get; set; } = true;

    /// <summary>Upload this PC's leveling stretches, with no names (on unless switched off).</summary>
    public bool ShareMyLeveling { get; set; } = true;

    /// <summary>
    /// 1 once the sharing switches were turned on by default (v1.0.78 turned them all on once, for every install). Missing (0) in older
    /// files.
    /// </summary>
    public int SharingDefaultsVersion { get; set; }

    public const int CurrentSharingDefaults = 1;

    /// <summary>
    /// Share qualifying raid fights for Server First, WITH character names and guilds. Off by default; the app warns before turning it on.
    /// The one-time "sharing on" of 1.0.78 does not touch it.
    /// </summary>
    public bool ShareServerFirsts { get; set; }

    /// <summary>This install's random id for shared leveling (never a character name).</summary>
    public string LevelingId { get; set; } = string.Empty;

    /// <summary>The secret that lets only this install add leveling under its id.</summary>
    public string LevelingKey { get; set; } = string.Empty;

    /// <summary>The Leveling tab's expansions per server, when changed by hand (expansion numbers; missing: found from zones seen).</summary>
    public Dictionary<string, List<int>> LevelingExpansions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Use everyone's shared mob stats (regen/HP, levels, resists, best kills) when your own logs have none.</summary>
    public bool UseSharedMobStats { get; set; } = true;

    /// <summary>Share this PC's kills, /con levels and resist counts once a day (no names).</summary>
    public bool ShareMyMobStats { get; set; } = true;

    /// <summary>Per server: the newest kill already shared for mob stats (Unix seconds).</summary>
    public Dictionary<string, long> MobStatsSharedThrough { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Leveling tab ranks group zones (false: solo).</summary>
    public bool LevelingGroup { get; set; } = true;

    /// <summary>Add the drops other players shared online to the Drop Table (on unless switched off).</summary>
    public bool UseSharedDrops { get; set; } = true;

    /// <summary>On a server with normal loot, add the shared drops of the other servers with normal loot (on unless switched off).</summary>
    public bool PoolNormalLootDrops { get; set; } = true;

    /// <summary>Upload this PC's drops to the online shared table (on unless switched off).</summary>
    public bool ShareMyDrops { get; set; } = true;

    /// <summary>This install's random id in the online shared table (made on the first upload; never a character name).</summary>
    public string SharedDropsId { get; set; } = string.Empty;

    /// <summary>The secret that lets only this install replace or remove its online file.</summary>
    public string SharedDropsKey { get; set; } = string.Empty;

    /// <summary>Send the anonymous player count once a day (a random id and the app version, nothing else).</summary>
    public bool CountMeIn { get; set; } = true;

    /// <summary>The random id the player count uses (not the shared drops id; never a character name).</summary>
    public string PlayerCountId { get; set; } = string.Empty;

    /// <summary>The UTC day ("2026-11-16") this install was last counted.</summary>
    public string PlayerCountDay { get; set; } = string.Empty;

    /// <summary>
    /// True once the one-time load of every log's full history into Mob History was done or declined (asked on the first open of
    /// 1.0.63, or of a new install once it has logs).
    /// </summary>
    public bool AllLogsLoadOffered { get; set; }

    /// <summary>Speak and show "X is being attacked by Y" when a mob attacks one of your characters that is not a tank.</summary>
    public bool AggroAlert { get; set; } = true;

    /// <summary>Speak "X out of range" / "X can't see the target" when one of your characters can't reach its target in a fight.</summary>
    public bool RangeAlert { get; set; } = true;

    /// <summary>Show and say "MENDWELL STUNNED!" when one of your characters is stunned during a fight.</summary>
    public bool StunAlert { get; set; } = true;

    /// <summary>Show and say "OODLES LOST CONTROL!" when one of your characters is charmed, feared or mind controlled.</summary>
    public bool LostControlAlert { get; set; } = true;

    /// <summary>Where the stun banner shows.</summary>
    public AlertOverlay StunAlertOverlay { get; set; } = AlertOverlay.FloatingText;

    /// <summary>Where the lost-control banner shows.</summary>
    public AlertOverlay LostControlAlertOverlay { get; set; } = AlertOverlay.FloatingText;

    /// <summary>Say when one of your characters is out of food or drink, and show a banner when it is low.</summary>
    public bool FoodAlert { get; set; } = true;

    /// <summary>Show how far along a fight is on the DPS overlay, from the recorded kills of the same mob.</summary>
    public bool ShowFightProgress { get; set; } = true;

    /// <summary>Mobs ticked as raid bosses in the encounter list (by the player, or by the server's kill announcement).</summary>
    public List<string>? RaidBosses { get; set; }

    /// <summary>Mobs the player unticked: a kill announcement never ticks them again.</summary>
    public List<string>? RaidBossesUnticked { get; set; }

    /// <summary>The zone each raid trigger's boss was fought in (boss to zone), for grouping the Raid triggers list.</summary>
    public Dictionary<string, string>? BossZones { get; set; }

    /// <summary>False for zones saved before 1.0.127, some of which were wrong (the zone of an earlier fight): those are found again once.</summary>
    public bool BossZonesChecked { get; set; }

    /// <summary>Discord webhooks (address, raid key, share automatically) for raid boss kills, posted loot and unlooted items.</summary>
    public string DiscordKillsUrl { get; set; } = string.Empty;

    public string DiscordKillsKey { get; set; } = string.Empty;

    public bool DiscordKillsAuto { get; set; }

    public string DiscordLootUrl { get; set; } = string.Empty;

    public string DiscordLootKey { get; set; } = string.Empty;

    public bool DiscordLootAuto { get; set; }

    public string DiscordUnlootedUrl { get; set; } = string.Empty;

    public string DiscordUnlootedKey { get; set; } = string.Empty;

    public bool DiscordUnlootedAuto { get; set; }

    /// <summary>Say "Ream, loot Head of the Inquisitor" when an item given to one of your characters is not looted within a minute.</summary>
    public bool UnlootedAlert { get; set; }

    /// <summary>Save each raid boss kill's parse picture to Pictures\EQ Overwatch.</summary>
    public bool SaveKillPictures { get; set; }

    /// <summary>Loot lists linked to raid chat after raid boss kills (newest last, the last 200).</summary>
    public List<PostedLootEntry>? PostedLoot { get; set; }

    /// <summary>Speak when one of your character's buffs did not take hold.</summary>
    public bool TakeHoldAlert { get; set; } = true;

    /// <summary>Speak when the same mob resisted one of your spells 3 times in a row.</summary>
    public bool ResistStreakAlert { get; set; } = true;

    /// <summary>Speak when one of your characters' feign death failed.</summary>
    public bool FeignDeathAlert { get; set; } = true;

    /// <summary>Speak when your root or mez wears off or breaks.</summary>
    public bool RootMezAlert { get; set; } = true;

    /// <summary>Speak when a character's log has been quiet for 2 minutes during a fight.</summary>
    public bool QuietLogAlert { get; set; } = true;

    /// <summary>Counts down to boss abilities learned from past kills.</summary>
    public bool LearnedBossTimers { get; set; } = true;

    /// <summary>Opens the app minimized when you sign in to Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Where the mob card overlay goes; it shows when you /con a mob the app knows something about.</summary>
    public OverlaySettings MobCardOverlay { get; set; } = OverlaySettings.DefaultMobCard;

    public OverlaySettings FctDamageOverlay { get; set; } = OverlaySettings.DefaultFctDamage;

    public OverlaySettings FctHealsOverlay { get; set; } = OverlaySettings.DefaultFctHeals;


    /// <summary>Text size of each floating combat text overlay's numbers.</summary>
    public double FctDamageTextSize { get; set; } = 20;

    public double FctHealsTextSize { get; set; } = 20;

    /// <summary>Hits within 0.3 s from one character on one target show as one number ("3,412 x4").</summary>
    public bool FctMergeHits { get; set; }

    /// <summary>Misses and resists show as small gray words ("Dodge", "Resisted").</summary>
    public bool FctShowMisses { get; set; } = true;

    /// <summary>The floating text's font family and weight ("Segoe UI", "SemiBold").</summary>
    public string FctFont { get; set; } = "Segoe UI";

    public string FctWeight { get; set; } = "SemiBold";

    /// <summary>The account name typed for each character, keyed "name|server" (lower case).</summary>
    public Dictionary<string, string>? CharacterAccounts { get; set; }

    /// <summary>Each named account's color (#RRGGBB), keyed "server|account" (lower case).</summary>
    public Dictionary<string, string>? AccountColors { get; set; }

    /// <summary>A Windows notification when a tell arrives while EverQuest is not the active window.</summary>
    public bool TellToast { get; set; } = true;

    /// <summary>A Windows notification when one of your characters gains a level while EverQuest is not the active window.</summary>
    public bool LevelUpToast { get; set; } = true;

    /// <summary>Speak "X stopped following" when one of your characters stops auto-following.</summary>
    public bool FollowAlert { get; set; } = true;

    /// <summary>Speak "pet X faded" when a buff on one of your pets wears off.</summary>
    public bool PetBuffAlert { get; set; }

    /// <summary>Show /random rolls and players gone linkdead on the Tells overlay for 30 seconds.</summary>
    public bool TellsOverlayNotices { get; set; } = true;

    /// <summary>"Karnor's Castle: AA bonus" on the Tells overlay when one of your characters enters a Resource Hunter bonus zone.</summary>
    public bool ResourceHunterNotices { get; set; } = true;

    /// <summary>The last day an unlock reminder was shown, per server ("frostreaver" → "2026-11-22").</summary>
    public Dictionary<string, string>? UnlockReminders { get; set; }

    /// <summary>Fill in Resource Hunter zones your logs have not seen from the server's tracking website (Frostreaver, Fangbreaker).</summary>
    public bool UseResourceWebsites { get; set; } = true;

    /// <summary>Share the Resource Hunter bonuses your logs find (zone, bonus and day only) and use what other players shared.</summary>
    public bool ShareResourceBonuses { get; set; } = true;

    /// <summary>A Windows notification when one of your characters beats its best DPS, overall or against a mob.</summary>
    public bool PersonalBestToast { get; set; } = true;

    /// <summary>Floating combat text colors by kind (Melee, Ranged, Spell, DamageShield, Heal, Taken) as #RRGGBB; missing ones use the defaults.</summary>
    public Dictionary<string, string>? FloatingColors { get; set; }

    /// <summary>A version ("v1.0.12") the user chose to skip: it is not announced again (a newer one still is).</summary>
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    /// <summary>Path to <c>spells_us.txt</c>; empty when none has been chosen.</summary>
    public string SpellsFilePath { get; set; } = string.Empty;

    /// <summary>Caster level used to calculate buff durations (1-125).</summary>
    public int DefaultCasterLevel { get; set; } = DefaultCasterLevelValue;

    /// <summary>The spells the buff tracker should show timers for.</summary>
    public HashSet<int> TrackedSpellIds { get; set; } = new();

    /// <summary>
    /// The Buff &amp; Song Tracker list, each spell with its own overlay. This is what is used at run time. <see cref="TrackedSpellIds"/>
    /// is still written (the enabled ids) and read, so settings from older versions upgrade: an id with no entry here is added with
    /// the overlay the spell file suggests.
    /// </summary>
    public List<TrackedSpellSetting> TrackedSpells { get; set; } = new();

    /// <summary>Verified players (with classes) and pets, learned from /who and /pet leader lines.</summary>
    public RosterSettings Roster { get; set; } = new();

    /// <summary>The AAs and disciplines on the "AAs &amp; Discs" tab.</summary>
    public List<TrackedAbilitySetting> TrackedAbilities { get; set; } = new();

    public const int DefaultCasterLevelValue = 60;
    public const int MinCasterLevel = 1;
    public const int MaxCasterLevel = 125;
}

public sealed class DurationProfileSetting
{
    public string CharacterName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;

    /// <summary>The class persona ("Cleric") the choices are for; empty for choices saved before personas, which count for any.</summary>
    public string Persona { get; set; } = string.Empty;

    /// <summary>Spell id of the chosen beneficial duration focus, or null for None.</summary>
    public int? BeneficialFocusId { get; set; }

    public int? DetrimentalFocusId { get; set; }

    /// <summary>The total spell-duration percentage shown in the AA window, 0-100 (no longer used: learned AAs count by themselves).</summary>
    public int AaPercent { get; set; }

    /// <summary>The other focus kinds picked (kind name to spell id): healing, damage, spell haste, mana cost, range, hate, reagent cost.</summary>
    public Dictionary<string, int>? Foci { get; set; }

    /// <summary>The item each focus kind was taken from (kind name to item name), for foci the app set from an identified item.</summary>
    public Dictionary<string, string>? FromItems { get; set; }

    /// <summary>"Item|Kind" pairs already applied once; a focus picked by hand afterwards stays until another item is identified.</summary>
    public List<string>? AppliedItems { get; set; }
}

public sealed class WatchedLogSettings
{
    public string FilePath { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public bool IsTriggerEnabled { get; set; } = true;

    /// <summary>The Tank box as chosen on the Characters tab; null follows the class (warriors, paladins and shadow knights).</summary>
    public bool? IsTank { get; set; }

    /// <summary>Complete Heal chain position (1-12), or 0 for none.</summary>
    public int ChChainPosition { get; set; }

    /// <summary>Retired: hidden from the Characters list with triggers off, its data kept.</summary>
    public bool IsRetired { get; set; }
}

/// <summary>A named layout of all six overlays, with a description of the monitor setup it was saved on.</summary>
public sealed class OverlayProfile
{
    public string Name { get; set; } = string.Empty;
    public string DisplaySignature { get; set; } = string.Empty;
    public OverlaySettings Buffs { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Buffs);
    public OverlaySettings Debuffs { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Debuffs);
    public OverlaySettings Songs { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Songs);
    public OverlaySettings RaidTriggers { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.RaidTriggers);
    public OverlaySettings ChChain { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.ChChain);
    public OverlaySettings Dps { get; set; } = OverlaySettings.DefaultDps;

    /// <summary>Null in layouts saved before the Tells overlay existed: applying one leaves the Tells overlay as it is.</summary>
    public OverlaySettings? Tells { get; set; }

    /// <summary>Null in layouts saved before the mob card existed: applying one leaves the mob card as it is.</summary>
    public OverlaySettings? MobCard { get; set; }

    public OverlaySettings? FctDamage { get; set; }

    public OverlaySettings? FctHeals { get; set; }
}

/// <summary>
/// A window's normal (not maximized) bounds in screen pixels, and whether it was maximized. Windows itself puts a window that would be off
/// every screen back on one, so a monitor that has been unplugged is handled when the placement is applied.
/// </summary>
public sealed record WindowPlacementSetting
{
    /// <summary>The window names saved: the main window, the Colors window and the Character data window.</summary>
    public const string Main = "Main";
    public const string Colors = "Colors";
    public const string CharacterData = "CharacterData";

    /// <summary>Smaller than this is not a real window size, so it is not restored.</summary>
    public const int MinSize = 100;

    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public bool IsMaximized { get; init; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>True when the bounds are a usable window size (not empty, inverted or absurdly large).</summary>
    public bool IsUsable => Width >= MinSize && Height >= MinSize && Width <= 32000 && Height <= 32000
        && Math.Abs((long)Left) <= 64000 && Math.Abs((long)Top) <= 64000;
}

/// <summary>Bounds, visibility and lock state of one overlay window (device-independent units).</summary>
/// <summary>An overlay you made: its id (kept when renamed), name and placement.</summary>
public sealed class CustomOverlaySetting
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public OverlaySettings Placement { get; set; } = OverlaySettings.DefaultTimer;
}

public sealed record OverlaySettings
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsLocked { get; init; }

    /// <summary>Shrunk to its title strip (<see cref="Height"/> stays the full height).</summary>
    public bool IsMinimized { get; init; }

    /// <summary>A generic default timer-overlay placement.</summary>
    public static readonly OverlaySettings DefaultTimer = new() { Left = 100, Top = 100, Width = 320, Height = 240 };

    public static readonly OverlaySettings DefaultDps = new() { Left = 700, Top = 340, Width = 300, Height = 230 };

    public static readonly OverlaySettings DefaultMobCard = new() { Left = 1020, Top = 240, Width = 340, Height = 150, IsVisible = true };

    public static readonly OverlaySettings DefaultFctDamage = new() { Left = 900, Top = 300, Width = 300, Height = 260, IsVisible = true };

    public static readonly OverlaySettings DefaultFctHeals = new() { Left = 580, Top = 300, Width = 300, Height = 260, IsVisible = true };

    public static readonly OverlaySettings DefaultTells = new() { Left = 1020, Top = 60, Width = 320, Height = 160, IsVisible = false };

    private static readonly OverlaySettings DefaultBuffs = new() { Left = 60, Top = 60, Width = 300, Height = 260 };
    private static readonly OverlaySettings DefaultDebuffs = new() { Left = 380, Top = 60, Width = 300, Height = 260 };
    private static readonly OverlaySettings DefaultSongs = new() { Left = 700, Top = 60, Width = 300, Height = 200 };
    private static readonly OverlaySettings DefaultRaidTriggers = new() { Left = 60, Top = 340, Width = 320, Height = 240 };
    private static readonly OverlaySettings DefaultChChain = new() { Left = 380, Top = 340, Width = 320, Height = 220, IsVisible = false };

    /// <summary>The first-run placement of a timer overlay: the six are laid out side by side so they do not stack.</summary>
    public static OverlaySettings ForCategory(TimerOverlayCategory category) => category switch
    {
        TimerOverlayCategory.Buffs => DefaultBuffs,
        TimerOverlayCategory.Debuffs => DefaultDebuffs,
        TimerOverlayCategory.Songs => DefaultSongs,
        TimerOverlayCategory.ChChain => DefaultChChain,
        _ => DefaultRaidTriggers,
    };
}

/// <summary>
/// The user-facing, persisted form of a trigger. One rule maps to one or two engine <see cref="TriggerDefinition"/>s
/// (see <see cref="ToDefinitions"/>): a text/audio alert and an optional countdown timer.
/// </summary>
public sealed class TriggerRule
{
    public const string DefaultTemplate = "${Character}: ${Trigger}";

    /// <summary>How long a text alert stays registered in the engine. The overlay flashes it for at most 4 seconds.</summary>
    public const double AlertDurationSeconds = 4.0;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New trigger";

    /// <summary>Regex when <see cref="UseRegex"/>; otherwise literal text that may appear anywhere in the message.</summary>
    public string Pattern { get; set; } = string.Empty;

    public bool UseRegex { get; set; } = true;
    public bool IsEnabled { get; set; } = true;

    /// <summary>Greater than zero adds a countdown bar of this length.</summary>
    public double TimerDurationSeconds { get; set; }

    public string TimerLabelTemplate { get; set; } = DefaultTemplate;

    /// <summary>Non-empty shows a flash alert.</summary>
    public string AlertTextTemplate { get; set; } = DefaultTemplate;

    /// <summary>Non-empty speaks this text (TTS).</summary>
    public string AudioText { get; set; } = string.Empty;

    /// <summary>Non-empty plays this .wav file.</summary>
    public string AudioFilePath { get; set; } = string.Empty;

    /// <summary>Which timer overlay shows this rule's alert and countdown (default: Raid Triggers).</summary>
    public TimerOverlayCategory OverlayCategory { get; set; } = TimerOverlayCategory.RaidTriggers;

    /// <summary>The id of one of your own overlays that shows this rule instead (null: the <see cref="OverlayCategory"/> overlay).</summary>
    public string? CustomOverlayId { get; set; }

    /// <summary>Your own folder for this trigger ("General" when none), used when triggers are grouped by folder.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The zone the trigger is for (empty for any zone), used when triggers are grouped by expansion and zone.</summary>
    public string Zone { get; set; } = string.Empty;

    public TriggerRule Clone() => (TriggerRule)MemberwiseClone();

    /// <summary>The regex pattern the engine will use.</summary>
    public string GetEffectivePattern() => UseRegex ? Pattern ?? string.Empty : Regex.Escape(Pattern ?? string.Empty);

    /// <summary>
    /// Alert definition (keeps <see cref="Id"/>): text overlay if <see cref="AlertTextTemplate"/> is set, audio if
    /// <see cref="AudioText"/> or <see cref="AudioFilePath"/> is set. Timer definition (derived id): a countdown if
    /// <see cref="TimerDurationSeconds"/> &gt; 0. A rule with none of the three still shows a default alert.
    /// </summary>
    public IReadOnlyList<TriggerDefinition> ToDefinitions()
    {
        string pattern = GetEffectivePattern();
        bool hasAlert = !string.IsNullOrWhiteSpace(AlertTextTemplate);
        bool hasAudio = !string.IsNullOrWhiteSpace(AudioText) || !string.IsNullOrWhiteSpace(AudioFilePath);
        bool hasTimer = TimerDurationSeconds > 0;

        var definitions = new List<TriggerDefinition>(2);
        var alertActions = TriggerActions.None;

        if (hasAlert || (!hasAudio && !hasTimer))
        {
            alertActions |= TriggerActions.TextOverlay;
        }

        if (hasAudio)
        {
            alertActions |= TriggerActions.Audio;
        }

        if (alertActions != TriggerActions.None)
        {
            definitions.Add(new TriggerDefinition
            {
                Id = Id,
                Name = Name,
                Pattern = pattern,
                IsEnabled = IsEnabled,
                OverlayCategory = OverlayCategory,
                Actions = alertActions,
                DurationSeconds = AlertDurationSeconds,
                DisplayText = hasAlert ? AlertTextTemplate : DefaultTemplate,
                AudioText = string.IsNullOrWhiteSpace(AudioText) ? null : AudioText,
                AudioFilePath = string.IsNullOrWhiteSpace(AudioFilePath) ? null : AudioFilePath,
            });
        }

        if (hasTimer)
        {
            definitions.Add(new TriggerDefinition
            {
                Id = DeriveId(Id, "timer"),
                Name = Name,
                Pattern = pattern,
                IsEnabled = IsEnabled,
                OverlayCategory = OverlayCategory,
                Actions = TriggerActions.CountdownTimer,
                DurationSeconds = TimerDurationSeconds,
                DisplayText = string.IsNullOrWhiteSpace(TimerLabelTemplate) ? DefaultTemplate : TimerLabelTemplate,
            });
        }

        return definitions;
    }

    private static Guid DeriveId(Guid id, string purpose) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{id:N}:{purpose}")));
}

/// <summary>An additional character to monitor and the server it is on (empty for any server).</summary>
public sealed class AdditionalCharacterSetting
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
}

/// <summary>One loot list linked to raid chat: the boss, who linked it, the items ("Thorny Chain Helm x2 · ...") and when (UTC).</summary>
public sealed record PostedLootEntry(string Mob, string Poster, string Items, DateTime Utc);
