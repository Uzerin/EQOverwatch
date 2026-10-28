using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Settings;

/// <summary>Whose use of an AA or discipline starts a timer.</summary>
public enum AbilityScope
{
    /// <summary>Anyone whose use shows up in a watched log: every raid member gets a bar of their own.</summary>
    Everyone = 0,

    /// <summary>Only the characters this app is monitoring.</summary>
    MyCharacters = 1,
}

/// <summary>
/// One AA or discipline on the "AAs &amp; Discs" tab. A use starts up to two bars for the person who used it: one for how long the
/// ability lasts (<see cref="ActiveSeconds"/>) and one for how long until it can be used again (<see cref="CooldownSeconds"/>).
/// A bar is only ever restarted by the same person using the same ability again; another character using it never resets it.
/// </summary>
public sealed class TrackedAbilitySetting
{
    public const int MaxNameLength = 120;
    public const int MaxSeconds = 86400;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The ability's name exactly as the log prints it ("Defensive Discipline").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The spell file row it was picked from, or 0 when it was typed in.</summary>
    public int SpellId { get; set; }

    /// <summary>How long the ability lasts. Zero means no "active" bar.</summary>
    public int ActiveSeconds { get; set; }

    /// <summary>How long until it can be used again. Zero means no "cooldown" bar.</summary>
    public int CooldownSeconds { get; set; }

    public AbilityScope Scope { get; set; } = AbilityScope.Everyone;

    public TimerOverlayCategory ActiveOverlay { get; set; } = TimerOverlayCategory.Buffs;

    public TimerOverlayCategory CooldownOverlay { get; set; } = TimerOverlayCategory.RaidTriggers;

    public bool IsEnabled { get; set; } = true;

    public TrackedAbilitySetting Clone() => (TrackedAbilitySetting)MemberwiseClone();
}
