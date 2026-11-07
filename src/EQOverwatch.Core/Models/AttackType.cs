using EQOverwatch.Core.Theming;

namespace EQOverwatch.Core.Models;

/// <summary>The kinds of attack the damage pictures tell apart. Every kicking verb counts as <see cref="Kick"/>.</summary>
public enum AttackType
{
    Hit,
    Slash,
    Pierce,
    Crush,
    HandToHand,
    Kick,
    Bash,
    Backstab,
    Frenzy,
    Archery,
    OtherMelee,
    Spell,

    /// <summary>Everything a pet does, whatever the verb or spell.</summary>
    Pet,
}

/// <summary>Damage one player did with one kind of attack: the regular hits and the critical hits, in points.</summary>
public sealed record AttackSlice(AttackType Type, long Normal, long Crit)
{
    public long Total => Normal + Crit;
}

/// <summary>
/// A player's attack slices. Two lists with the same slices are equal, so snapshots of the same fight compare equal
/// (a plain array would compare by reference and break record equality).
/// </summary>
public sealed class AttackSliceList : System.Collections.ObjectModel.ReadOnlyCollection<AttackSlice>
{
    public AttackSliceList(IList<AttackSlice> slices)
        : base(slices)
    {
    }

    public override bool Equals(object? obj) => obj is AttackSliceList other && this.SequenceEqual(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (AttackSlice slice in this)
        {
            hash.Add(slice);
        }

        return hash.ToHashCode();
    }
}

public static class AttackTypes
{
    public static IReadOnlyList<AttackType> All { get; } = Enum.GetValues<AttackType>();

    public static string Label(AttackType type) => type switch
    {
        AttackType.HandToHand => "Hand-to-hand",
        AttackType.OtherMelee => "Other melee",
        AttackType.Spell => "Spells & procs",
        AttackType.Pet => "Pet",
        AttackType.Archery => "Archery",
        _ => type.ToString(),
    };

    /// <summary>The attack a damage line was made with, from its kind and melee verb ("slash", "kicks", "frenzy on"...).</summary>
    public static AttackType Classify(DamageKind kind, string? verb)
    {
        if (kind is DamageKind.NonMelee or DamageKind.DamageOverTime)
        {
            return AttackType.Spell;
        }

        string v = (verb ?? string.Empty).Trim().ToLowerInvariant();
        if (v.Contains("kick", StringComparison.Ordinal))
        {
            return AttackType.Kick;
        }

        return v switch
        {
            "hit" or "hits" => AttackType.Hit,
            "slash" or "slashes" => AttackType.Slash,
            "pierce" or "pierces" => AttackType.Pierce,
            "crush" or "crushes" => AttackType.Crush,
            "punch" or "punches" => AttackType.HandToHand,
            "bash" or "bashes" => AttackType.Bash,
            "backstab" or "backstabs" => AttackType.Backstab,
            "frenzy on" or "frenzies on" or "frenzy" => AttackType.Frenzy,
            "shoot" or "shoots" => AttackType.Archery,
            "" => AttackType.Hit,
            _ => AttackType.OtherMelee,
        };
    }

    public static string DefaultColor(AttackType type) => type switch
    {
        AttackType.Hit => "#9AA5B1",
        AttackType.Slash => "#3B82F6",
        AttackType.Pierce => "#14B8A6",
        AttackType.Crush => "#8B5CF6",
        AttackType.HandToHand => "#EC4899",
        AttackType.Kick => "#F97316",
        AttackType.Bash => "#A16207",
        AttackType.Backstab => "#DC2626",
        AttackType.Frenzy => "#EAB308",
        AttackType.Archery => "#22C55E",
        AttackType.OtherMelee => "#78716C",
        AttackType.Pet => "#BEF264",
        _ => "#06B6D4", // spells and procs
    };
}

/// <summary>The color of every attack type: the user's choices, falling back to the defaults. A crit is a lighter shade of its attack's color.</summary>
public sealed class AttackColorScheme
{
    /// <summary>How far toward white a crit shade is.</summary>
    public const double CritLightening = 0.45;

    private static volatile AttackColorScheme _current = new();

    /// <summary>The settings key of the healing color (attack types are keyed by their enum name).</summary>
    public const string HealingKey = "Healing";

    public const string DefaultHealingColor = "#5CC98A";

    private readonly Dictionary<AttackType, string> _colors = new();
    private readonly string _healing;

    /// <summary>The colors the encounter pictures use right now (the app keeps this in step with the settings).</summary>
    public static AttackColorScheme Current
    {
        get => _current;
        set => _current = value ?? new AttackColorScheme();
    }

    public AttackColorScheme(IReadOnlyDictionary<string, string>? chosen = null)
    {
        foreach (AttackType type in AttackTypes.All)
        {
            string hex = AttackTypes.DefaultColor(type);
            if (chosen is not null
                && chosen.TryGetValue(type.ToString(), out string? value)
                && ThemeColors.TryNormalize(value, out string normalized))
            {
                hex = normalized;
            }

            _colors[type] = hex;
        }

        _healing = chosen is not null && chosen.TryGetValue(HealingKey, out string? healing) && ThemeColors.TryNormalize(healing, out string h)
            ? h
            : DefaultHealingColor;
    }

    /// <summary>The healing color.</summary>
    public string Healing => _healing;

    /// <summary>The lighter shade for critical heals.</summary>
    public string HealingCrit => Lighter(_healing);

    private static string Lighter(string hex) =>
        ThemeColors.TryParse(hex, out Rgba color) ? ThemeColors.ToHex(ThemeColors.Lighten(color, CritLightening)) : hex;

    public string Normal(AttackType type) => _colors[type];

    public string Crit(AttackType type) =>
        ThemeColors.TryParse(_colors[type], out Rgba color)
            ? ThemeColors.ToHex(ThemeColors.Lighten(color, CritLightening))
            : _colors[type];
}

/// <summary>When overlays fade and when the DPS overlay goes away, from how long ago the last combat line was seen.</summary>
public static class OverlayFade
{
    /// <summary>Seconds after the last combat line before the other overlays count as out of combat.</summary>
    public const int CombatGraceSeconds = 10;

    public const int DefaultDpsHideSeconds = 30;

    /// <summary>True when a combat line was seen within <see cref="CombatGraceSeconds"/> seconds.</summary>
    public static bool IsInCombat(DateTime? lastCombatUtc, DateTime nowUtc) =>
        lastCombatUtc is { } last && (nowUtc - last).TotalSeconds <= CombatGraceSeconds;

    public const int MinIdlePercent = 10;

    /// <summary>
    /// The opacity (0 to 1) an overlay should have. Unlocked overlays are always fully shown, so they can be placed.
    /// <paramref name="dpsHideSeconds"/> of 0 means the DPS overlay never hides.
    /// </summary>
    public static double Opacity(
        DateTime? lastCombatUtc, DateTime nowUtc, int idlePercent, int dpsHideSeconds, bool isDpsOverlay, bool unlocked)
    {
        if (unlocked)
        {
            return 1.0;
        }

        double idle = lastCombatUtc is { } last ? Math.Max(0, (nowUtc - last).TotalSeconds) : double.PositiveInfinity;

        if (isDpsOverlay && dpsHideSeconds > 0 && idle > dpsHideSeconds)
        {
            return 0.0;
        }

        return idle > CombatGraceSeconds ? Math.Clamp(idlePercent, MinIdlePercent, 100) / 100.0 : 1.0;
    }
}
