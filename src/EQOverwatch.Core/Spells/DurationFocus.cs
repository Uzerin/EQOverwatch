using System.Globalization;

namespace EQOverwatch.Core.Spells;

/// <summary>Which spells a duration focus works on.</summary>
public enum FocusAppliesTo
{
    Any,
    Beneficial,
    Detrimental,
}

/// <summary>
/// A spell-duration focus effect read from the spell file (effect 128): a percent bonus up to a spell level, shrinking by
/// <see cref="PercentPerLevelOver"/> percent of itself for each level above it.
/// </summary>
public sealed record DurationFocus(
    int SpellId,
    string Name,
    int Percent,
    int MaxLevel,
    int PercentPerLevelOver,
    FocusAppliesTo AppliesTo,
    int MinDurationTicks,
    IReadOnlyList<int> ExcludedEffects)
{
    /// <summary>"Extended Enhancement II (+15%, to level 44)".</summary>
    public string Label => MaxLevel > 0 && MaxLevel < 255
        ? $"{Name} (+{Percent}%, to level {MaxLevel})"
        : $"{Name} (+{Percent}%)";
}

/// <summary>One character's duration choices: the two focus spells and the AA percentage. Null ids mean None.</summary>
public sealed record DurationProfile(int? BeneficialFocusId, int? DetrimentalFocusId, int AaPercent)
{
    public static readonly DurationProfile None = new(null, null, 0);

    public bool IsEmpty => BeneficialFocusId is null && DetrimentalFocusId is null && AaPercent <= 0;
}

/// <summary>Reads the slot list at the end of a split-layout spell row: <c>slot|spa|base|limit|max|calc</c> joined by <c>$</c>.</summary>
public static class SpellEffects
{
    public readonly record struct Effect(int Spa, int Base, int Limit);

    public static List<Effect> Parse(ReadOnlySpan<char> field)
    {
        var result = new List<Effect>();
        while (!field.IsEmpty)
        {
            int dollar = field.IndexOf('$');
            ReadOnlySpan<char> slot = dollar < 0 ? field : field[..dollar];
            field = dollar < 0 ? default : field[(dollar + 1)..];

            // slot | spa | base | limit | ...
            var parts = new int[4];
            int count = 0;
            ReadOnlySpan<char> rest = slot;
            while (count < 4 && !rest.IsEmpty)
            {
                int bar = rest.IndexOf('|');
                ReadOnlySpan<char> piece = bar < 0 ? rest : rest[..bar];
                rest = bar < 0 ? default : rest[(bar + 1)..];
                if (!int.TryParse(piece, NumberStyles.Integer, CultureInfo.InvariantCulture, out parts[count]))
                {
                    parts[count] = 0;
                }

                count++;
            }

            if (count >= 3)
            {
                result.Add(new Effect(parts[1], parts[2], count >= 4 ? parts[3] : 0));
            }
        }

        return result;
    }

    /// <summary>Builds a focus from a row's effects, or null when it has no duration effect (128).</summary>
    public static DurationFocus? ToFocus(int spellId, string name, IReadOnlyList<Effect> effects)
    {
        int percent = 0, maxLevel = 0, perLevel = 0, minTicks = 0;
        bool found = false;
        FocusAppliesTo applies = FocusAppliesTo.Any;
        var excluded = new List<int>();

        foreach (Effect e in effects)
        {
            switch (e.Spa)
            {
                case 128:
                    found = true;
                    percent = e.Base;
                    break;
                case 134:
                    maxLevel = e.Base;
                    perLevel = e.Limit;
                    break;
                case 138:
                    applies = e.Base == 1 ? FocusAppliesTo.Beneficial : FocusAppliesTo.Detrimental;
                    break;
                case 140:
                    minTicks = e.Base;
                    break;
                case 137 when e.Base < 0:
                    excluded.Add(-e.Base);
                    break;
            }
        }

        return found && percent > 0 ? new DurationFocus(spellId, name, percent, maxLevel, perLevel, applies, minTicks, excluded) : null;
    }
}

/// <summary>Works out how long one of your own buffs, debuffs or DoTs lasts with your focus and AA bonuses.</summary>
public static class DurationCalculator
{
    private const int SecondsPerTick = 6;

    /// <summary>The spell's level for the class at <paramref name="classIndex"/> (0 = Warrior), or 0 when unknown or not castable.</summary>
    public static int LevelFor(SpellInfo spell, int classIndex)
    {
        IReadOnlyList<int>? levels = spell.ClassLevels;
        if (levels is null || classIndex < 0 || classIndex >= levels.Count)
        {
            return 0;
        }

        int level = levels[classIndex];
        return level is > 0 and < 255 ? level : 0;
    }

    /// <summary>Bonus percent for this spell: the chosen focus (reduced above its level cap, and only if it applies) plus the AA percent.</summary>
    public static double BonusPercent(SpellInfo spell, int classIndex, DurationProfile profile, SpellDatabase database)
    {
        if (spell.Category == Models.TimerOverlayCategory.Songs)
        {
            return 0;
        }

        bool detrimental = spell.Category == Models.TimerOverlayCategory.Debuffs;
        int? focusId = detrimental ? profile.DetrimentalFocusId : profile.BeneficialFocusId;
        double focusPercent = 0;
        if (focusId is int id && database.FindFocus(id) is { } focus && FocusApplies(focus, spell, detrimental))
        {
            focusPercent = focus.Percent;
            int level = LevelFor(spell, classIndex);
            if (focus.MaxLevel > 0 && level > focus.MaxLevel)
            {
                focusPercent *= Math.Max(0, 1 - (level - focus.MaxLevel) * focus.PercentPerLevelOver / 100.0);
            }
        }

        return focusPercent + Math.Clamp(profile.AaPercent, 0, 100);
    }

    private static bool FocusApplies(DurationFocus focus, SpellInfo spell, bool detrimental)
    {
        if (focus.AppliesTo == FocusAppliesTo.Beneficial && detrimental)
        {
            return false;
        }

        if (focus.AppliesTo == FocusAppliesTo.Detrimental && !detrimental)
        {
            return false;
        }

        if (spell.BaseDurationSeconds / SecondsPerTick < focus.MinDurationTicks)
        {
            return false;
        }

        if (spell.EffectIds is { } effects)
        {
            foreach (int excluded in focus.ExcludedEffects)
            {
                if (effects.Contains(excluded))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Duration in seconds, rounded down to whole ticks. Songs and an empty profile return the base duration.</summary>
    public static int Seconds(SpellInfo spell, int classIndex, DurationProfile? profile, SpellDatabase database)
    {
        if (profile is null || profile.IsEmpty)
        {
            return spell.BaseDurationSeconds;
        }

        double percent = BonusPercent(spell, classIndex, profile, database);
        if (percent <= 0)
        {
            return spell.BaseDurationSeconds;
        }

        int baseTicks = spell.BaseDurationSeconds / SecondsPerTick;
        int ticks = (int)Math.Floor(baseTicks * (1 + percent / 100.0) + 1e-9);
        return Math.Max(spell.BaseDurationSeconds, ticks * SecondsPerTick);
    }
}
