using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using EQCompanion.Core.Theming;

namespace EQCompanion.App.Services;

/// <summary>The six overlay windows.</summary>
public enum OverlayKind
{
    Buffs,
    Debuffs,
    Songs,
    RaidTriggers,
    ChChain,
    Dps,
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

    /// <summary>The window title and drag-strip text: "Buffs", "Debuffs", "Songs", "Raid Triggers", "CH Chain" or "DPS".</summary>
    public static string Title(this OverlayKind kind) => kind.Category()?.DisplayName() ?? "DPS";
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
}

/// <summary>The Windows clipboard, so view-model logic can be tested without touching it.</summary>
public interface IClipboardService
{
    /// <summary>Replaces the clipboard with plain text.</summary>
    void SetText(string text);

    /// <summary>
    /// Renders the top attackers of <paramref name="encounter"/> as a dark-themed picture, with <paramref name="footer"/> along the
    /// bottom edge, and puts the picture on the clipboard.
    /// </summary>
    void SetEncounterImage(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount);
}

/// <summary>The overlay windows as seen by the main view model.</summary>
public interface IOverlayHost
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

    /// <summary>Closes every window for good (application shutdown).</summary>
    void CloseAll();
}
