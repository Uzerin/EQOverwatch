using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Services;

/// <summary>One boss spell in a fight: how often it was cast and when.</summary>
/// <param name="Offsets">Seconds from the start of the fight to each cast.</param>
/// <param name="AverageGapSeconds">Mean time between casts, or null when it was cast once.</param>
public sealed record BossAbilityRow(string Spell, int Casts, double FirstOffsetSeconds, double? AverageGapSeconds, IReadOnlyList<double> Offsets)
{
    /// <summary>"0:05, 0:35, 1:05"</summary>
    public string TimesText => string.Join(", ", Offsets.Select(Format));

    /// <summary>"every 30s", or empty for a spell cast once.</summary>
    public string EveryText => AverageGapSeconds is { } gap ? $"every {Math.Round(gap):0}s" : string.Empty;

    private static string Format(double seconds)
    {
        long total = (long)Math.Max(0, Math.Round(seconds));
        return $"{total / 60}:{total % 60:00}";
    }
}

/// <summary>Turns the casts recorded in a fight into one row per spell, in the order the spells were first cast.</summary>
public static class BossAbilityTimeline
{
    public static IReadOnlyList<BossAbilityRow> Build(EncounterSnapshot encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        var rows = new List<BossAbilityRow>();
        foreach (IGrouping<string, BossCastSnapshot> group in encounter.CastRows
                     .OrderBy(c => c.Time)
                     .GroupBy(c => c.Spell, StringComparer.OrdinalIgnoreCase))
        {
            double[] offsets = group.Select(c => (c.Time - encounter.StartTime).TotalSeconds).ToArray();
            double? gap = offsets.Length > 1 ? (offsets[^1] - offsets[0]) / (offsets.Length - 1) : null;
            rows.Add(new BossAbilityRow(group.Key, offsets.Length, offsets[0], gap, offsets));
        }

        return rows.OrderBy(r => r.FirstOffsetSeconds).ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
