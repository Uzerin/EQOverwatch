using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Services;

/// <summary>The overlay windows.</summary>
public enum OverlayKind
{
    Buffs,
    Debuffs,
    Songs,
    RaidTriggers,
    ChChain,
    Dps,
    Tells,
    MobCard,
    FctDamage,
    FctHeals,
}

public static class OverlayKindExtensions
{
    public static IReadOnlyList<OverlayKind> All { get; } = Enum.GetValues<OverlayKind>();

    /// <summary>The timer category an overlay shows, or null for the DPS overlay.</summary>
    public static TimerOverlayCategory? Category(this OverlayKind kind) => kind switch
    {
        OverlayKind.Buffs => TimerOverlayCategory.Buffs,
        OverlayKind.Debuffs => TimerOverlayCategory.Debuffs,
        OverlayKind.Songs => TimerOverlayCategory.Songs,
        OverlayKind.RaidTriggers => TimerOverlayCategory.RaidTriggers,
        OverlayKind.ChChain => TimerOverlayCategory.ChChain,
        _ => null,
    };

    public static OverlayKind FromCategory(TimerOverlayCategory category) => category switch
    {
        TimerOverlayCategory.Buffs => OverlayKind.Buffs,
        TimerOverlayCategory.Debuffs => OverlayKind.Debuffs,
        TimerOverlayCategory.Songs => OverlayKind.Songs,
        TimerOverlayCategory.ChChain => OverlayKind.ChChain,
        _ => OverlayKind.RaidTriggers,
    };

    /// <summary>The window title and drag-strip text: "Buffs", "Debuffs", "Songs", "Raid Triggers", "CH Chain", "DPS" or "Tells".</summary>
    public static string Title(this OverlayKind kind) =>
        kind.Category()?.DisplayName() ?? kind switch
        {
            OverlayKind.Tells => "Tells",
            OverlayKind.MobCard => "Mob Card",
            OverlayKind.FctDamage => "Damage",
            OverlayKind.FctHeals => "Heals",
            _ => "DPS",
        };

    /// <summary>The floating combat text overlays.</summary>
    public static bool IsFloatingText(this OverlayKind kind) => kind is OverlayKind.FctDamage or OverlayKind.FctHeals;

    /// <summary>Where an overlay goes when nothing is saved for it.</summary>
    public static EQOverwatch.Core.Settings.OverlaySettings Defaults(this OverlayKind kind) =>
        kind.Category() is { } category ? EQOverwatch.Core.Settings.OverlaySettings.ForCategory(category)
        : kind == OverlayKind.Tells ? EQOverwatch.Core.Settings.OverlaySettings.DefaultTells
        : kind == OverlayKind.MobCard ? EQOverwatch.Core.Settings.OverlaySettings.DefaultMobCard
        : kind == OverlayKind.FctDamage ? EQOverwatch.Core.Settings.OverlaySettings.DefaultFctDamage
        : kind == OverlayKind.FctHeals ? EQOverwatch.Core.Settings.OverlaySettings.DefaultFctHeals
        : EQOverwatch.Core.Settings.OverlaySettings.DefaultDps;
}

/// <summary>Applies a color palette to the running application (main window and every overlay).</summary>
public interface IThemeService
{
    void Apply(ThemePalette palette);
}

/// <summary>Mute switch and lifetime of the audio/TTS player.</summary>
public interface IAudioControl : IDisposable
{
    /// <summary>False mutes audio and TTS.</summary>
    bool IsEnabled { get; set; }
}

/// <summary>File pickers, so view-model logic can be tested without showing dialogs. Each returns null when cancelled.</summary>
public interface IDialogService
{
    string[]? PickLogFiles();
    string? PickImportLogFile();
    string? PickWavFile();

    /// <summary>Picks <c>spells_us.txt</c>.</summary>
    string? PickSpellsFile();

    /// <summary>Picks the EverQuest game folder (or its Logs folder).</summary>
    string? PickEqFolder();

    /// <summary>Asks where to save a backup zip; null when cancelled.</summary>
    string? PickBackupSavePath(string suggestedName) => null;

    /// <summary>Asks which backup zip to restore; null when cancelled.</summary>
    string? PickBackupFile() => null;

    /// <summary>Asks where to save a drops file; null when cancelled.</summary>
    string? PickDropsExportPath(string suggestedName) => null;

    /// <summary>Asks which drops file to import; null when cancelled.</summary>
    string? PickDropsImportFile() => null;

    /// <summary>Asks a yes/no question.</summary>
    bool Confirm(string title, string message) => false;

    /// <summary>Asks whether to save changes before they would be lost: Save, Don't save or Cancel.</summary>
    UnsavedChoice AskUnsavedChanges(string title, string message) => UnsavedChoice.Discard;

    /// <summary>
    /// Asks what to do with map files the player edited (keep, overwrite or merge each); null when cancelled, which keeps them all.
    /// </summary>
    IReadOnlyDictionary<string, EQOverwatch.Core.Maps.MapEditChoice>? ChooseEditedMaps(IReadOnlyList<string> fileNames) => null;
}

/// <summary>The answer to "save your changes first?".</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Opens a character's data window and shows plain messages, so view-model logic can be tested without windows.</summary>
public interface ICharacterDataPresenter
{
    /// <summary>Shows the window for the character, or brings the existing one forward on the requested tab.</summary>
    void Show(EQOverwatch.App.ViewModels.CharacterDataViewModel viewModel);

    void ShowMessage(string title, string message);
}

/// <summary>The Windows clipboard, so view-model logic can be tested without touching it.</summary>
public interface IClipboardService
{
    /// <summary>Replaces the clipboard with plain text.</summary>
    void SetText(string text);

    /// <summary>The plain text on the clipboard, or null when there is none or it cannot be read.</summary>
    string? GetText() => null;

    /// <summary>
    /// Renders the top attackers of <paramref name="encounter"/> as a dark-themed picture, with <paramref name="footer"/> along the
    /// bottom edge, and puts the picture on the clipboard.
    /// </summary>
    void SetEncounterImage(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount);

    /// <summary>Puts a picture of every kill of a boss on the clipboard, in the active theme with <paramref name="footer"/> along the bottom.</summary>
    void SetBossKillsImage(EQOverwatch.Core.History.BossKillReport report, ThemePalette palette, string footer)
    {
    }

    /// <summary>Puts a picture of the day's Resource Hunter tables on the clipboard, in the active theme with <paramref name="footer"/> along the bottom.</summary>
    void SetResourceBonusImage(string title, IReadOnlyList<EQOverwatch.App.ViewModels.ResourcePairView> pairs, ThemePalette palette, string footer)
    {
    }
}

/// <summary>The overlay windows as seen by the main view model.</summary>
public partial interface IOverlayHost
{
    /// <summary>Raised when an overlay is moved, resized, shown/hidden or locked/unlocked.</summary>
    event EventHandler? PlacementChanged;

    /// <summary>Current bounds, visibility and lock state.</summary>
    OverlaySettings Capture(OverlayKind kind);

    /// <summary>Restores saved state, clamping the bounds onto the visible screen area.</summary>
    void Apply(OverlayKind kind, OverlaySettings settings);

    /// <summary>Locks (click-through) or unlocks every overlay.</summary>
    void SetLocked(bool locked);

    void SetVisible(OverlayKind kind, bool visible);

    /// <summary>Sets how opaque an overlay is (0 to 1) without changing whether it counts as shown.</summary>
    void SetOpacity(OverlayKind kind, double opacity)
    {
    }

    /// <summary>Closes every window for good (application shutdown).</summary>
    void CloseAll();

    /// <summary>A short description of the monitor setup (positions and sizes), so a saved layout can be matched to it.</summary>
    string DisplaySignature => string.Empty;
}
