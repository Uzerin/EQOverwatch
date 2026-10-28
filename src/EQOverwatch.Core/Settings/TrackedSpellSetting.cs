using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Settings;

/// <summary>
/// One spell on the Buff &amp; Song Tracker list: which spell, whether it is switched on, and which overlay its timer bar appears in.
/// The overlay is the user's choice; it is never derived from the spell file's class columns.
/// </summary>
public sealed class TrackedSpellSetting
{
    public const int MaxNameLength = 120;

    public int SpellId { get; set; }

    /// <summary>Kept so the row can still be shown before (or without) the spell file being loaded.</summary>
    public string SpellName { get; set; } = string.Empty;

    public TimerOverlayCategory Overlay { get; set; } = TimerOverlayCategory.Buffs;

    public bool IsEnabled { get; set; } = true;
}
