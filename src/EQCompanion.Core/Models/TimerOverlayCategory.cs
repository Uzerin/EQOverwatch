namespace EQCompanion.Core.Models;

/// <summary>Which of the five timer overlays a countdown or alert is shown in.</summary>
public enum TimerOverlayCategory
{
    Buffs,
    Debuffs,
    Songs,
    RaidTriggers,
    ChChain,
}

public static class TimerOverlayCategoryExtensions
{
    public static IReadOnlyList<TimerOverlayCategory> All { get; } = new[]
    {
        TimerOverlayCategory.Buffs,
        TimerOverlayCategory.Debuffs,
        TimerOverlayCategory.Songs,
        TimerOverlayCategory.RaidTriggers,
        TimerOverlayCategory.ChChain,
    };

    /// <summary>"Buffs", "Debuffs", "Songs", "Raid Triggers" or "CH Chain".</summary>
    public static string DisplayName(this TimerOverlayCategory category) => category switch
    {
        TimerOverlayCategory.Buffs => "Buffs",
        TimerOverlayCategory.Debuffs => "Debuffs",
        TimerOverlayCategory.Songs => "Songs",
        TimerOverlayCategory.RaidTriggers => "Raid Triggers",
        TimerOverlayCategory.ChChain => "CH Chain",
        _ => category.ToString(),
    };
}
