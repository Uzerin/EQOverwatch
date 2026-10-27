using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EQCompanion.Core.Models;
using EQCompanion.Core.Theming;

namespace EQCompanion.Core.Settings;

/// <summary>Everything persisted to <c>settings.json</c>.</summary>
public sealed class AppSettings
{
    public const double DefaultEncounterTimeoutSeconds = 8;
    public const double MinEncounterTimeoutSeconds = 1;
    public const double MaxEncounterTimeoutSeconds = 120;

    public int Version { get; set; } = 2;
    public List<WatchedLogSettings> WatchedLogs { get; set; } = new();
    public List<TriggerRule> Triggers { get; set; } = new();
    public double EncounterTimeoutSeconds { get; set; } = DefaultEncounterTimeoutSeconds;
    public bool IsAudioMuted { get; set; }
    public OverlaySettings BuffsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Buffs);
    public OverlaySettings DebuffsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Debuffs);
    public OverlaySettings SongsOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.Songs);
    public OverlaySettings RaidTriggersOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.RaidTriggers);
    public OverlaySettings ChChainOverlay { get; set; } = OverlaySettings.ForCategory(TimerOverlayCategory.ChChain);
    public OverlaySettings DpsOverlay { get; set; } = OverlaySettings.DefaultDps;

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

    /// <summary>How many days of history the Primary DPS log is imported for; 0 means all history.</summary>
    public int HistoricalDaysLimit { get; set; } = DefaultHistoricalDaysLimit;

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

    /// <summary>The AAs and disciplines on the "AAs &amp; Discs" tab.</summary>
    public List<TrackedAbilitySetting> TrackedAbilities { get; set; } = new();

    public const int DefaultCasterLevelValue = 60;
    public const int MinCasterLevel = 1;
    public const int MaxCasterLevel = 125;
}

public sealed class WatchedLogSettings
{
    public string FilePath { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public bool IsTriggerEnabled { get; set; } = true;
    public bool IsPrimaryDps { get; set; }

    /// <summary>Complete Heal chain position (1-12), or 0 for none.</summary>
    public int ChChainPosition { get; set; }
}

/// <summary>Bounds, visibility and lock state of one overlay window (device-independent units).</summary>
public sealed record OverlaySettings
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsLocked { get; init; }

    /// <summary>A generic default timer-overlay placement.</summary>
    public static readonly OverlaySettings DefaultTimer = new() { Left = 100, Top = 100, Width = 320, Height = 240 };

    public static readonly OverlaySettings DefaultDps = new() { Left = 700, Top = 340, Width = 300, Height = 230 };

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
