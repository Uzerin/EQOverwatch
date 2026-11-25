using System.Globalization;

namespace EQOverwatch.Core.Spells;

/// <summary>The kinds of item focus effect, by the spell effect that gives them.</summary>
public enum FocusKind
{
    BuffDuration,
    DetrimentalDuration,
    Healing,
    Damage,
    SpellHaste,
    ManaCost,
    Range,
    Hate,
    ReagentCost,
}

/// <summary>
/// One focus effect read from the spell file: its kind, its size (<see cref="Percent"/>, and <see cref="MaxPercent"/> when it rolls between
/// two values), and its limits: the highest spell level it fully works on and how much it loses per level above that, the lowest spell
/// level, beneficial or detrimental spells only, a minimum duration, effects a spell must or must not have, and combat skills left out.
/// </summary>
public sealed record ItemFocus(
    int SpellId,
    string Name,
    FocusKind Kind,
    int Percent,
    int MaxPercent,
    int MaxLevel,
    int PercentPerLevelOver,
    int MinLevel,
    FocusAppliesTo AppliesTo,
    int MinDurationTicks,
    bool ExcludesCombatSkills,
    int EffectLimits)
{
    /// <summary>The focus size for a spell of <paramref name="spellLevel"/>: full up to its max level, then less for each level above.</summary>
    public double PercentAt(int spellLevel)
    {
        if (spellLevel <= 0 || MaxLevel <= 0 || MaxLevel >= 255 || spellLevel <= MaxLevel)
        {
            return spellLevel > 0 && MinLevel > 0 && spellLevel < MinLevel ? 0 : Top;
        }

        return Math.Max(0, Top * (1 - (spellLevel - MaxLevel) * PercentPerLevelOver / 100.0));
    }

    private int Top => Math.Max(Percent, MaxPercent);

    /// <summary>"+15%", or "+10–20%" for a focus that rolls between two values; mana and reagent cost read as savings.</summary>
    public string SizeText
    {
        get
        {
            string size = MaxPercent > Percent
                ? string.Create(CultureInfo.InvariantCulture, $"{Percent}–{MaxPercent}%")
                : string.Create(CultureInfo.InvariantCulture, $"{Percent}%");
            return Kind switch
            {
                FocusKind.ManaCost => "-" + size + " mana",
                FocusKind.ReagentCost => size + " chance to keep reagents",
                FocusKind.SpellHaste => "-" + size + " cast time",
                FocusKind.Range => "+" + size + " range",
                FocusKind.Hate => (Percent < 0 ? string.Empty : "+") + size + " hate",
                FocusKind.Healing => "+" + size + " healing",
                FocusKind.Damage => "+" + size + " damage",
                _ => "+" + size + " duration",
            };
        }
    }

    /// <summary>"Extended Range III (+15% range, to level 60)", for the drop-downs.</summary>
    public string Label => MaxLevel is > 0 and < 255 ? $"{Name} ({SizeText}, to level {MaxLevel})" : $"{Name} ({SizeText})";

    /// <summary>
    /// What the focus gives a character of <paramref name="level"/>: "+15% range on spells up to level 60; your level 65 spells get
    /// +11.3%", with its other limits after.
    /// </summary>
    public string BenefitAt(int level)
    {
        var parts = new List<string>();
        string scope = AppliesTo switch
        {
            FocusAppliesTo.Beneficial => "beneficial spells",
            FocusAppliesTo.Detrimental => "detrimental spells",
            _ => "spells",
        };
        parts.Add(MaxLevel is > 0 and < 255 ? $"{SizeText} on {scope} up to level {MaxLevel}" : $"{SizeText} on {scope} of any level");
        if (MaxLevel is > 0 and < 255 && level > MaxLevel)
        {
            double at = PercentAt(level);
            parts.Add(at <= 0
                ? $"nothing on your level {level} spells"
                : string.Create(CultureInfo.CurrentCulture, $"your level {level} spells get {at:0.#}%"));
        }

        if (MinLevel > 0)
        {
            parts.Add($"spells from level {MinLevel}");
        }

        if (MinDurationTicks > 0)
        {
            parts.Add($"spells lasting {MinDurationTicks * 6} s or more");
        }

        if (ExcludesCombatSkills)
        {
            parts.Add("not combat abilities");
        }

        if (EffectLimits > 0)
        {
            parts.Add("only some spell effects");
        }

        return string.Join("; ", parts);
    }

    /// <summary>The focus kinds a spell row has, each with its limits (one row can hold several).</summary>
    public static IReadOnlyList<ItemFocus> FromEffects(int spellId, string name, IReadOnlyList<SpellEffects.Effect> effects)
    {
        int maxLevel = 0, perLevel = 0, minLevel = 0, minTicks = 0, effectLimits = 0;
        bool noCombat = false;
        FocusAppliesTo applies = FocusAppliesTo.Any;
        foreach (SpellEffects.Effect e in effects)
        {
            switch (e.Spa)
            {
                case 134: maxLevel = e.Base; perLevel = e.Limit; break;
                case 142: minLevel = e.Base; break;
                case 138: applies = e.Base == 1 ? FocusAppliesTo.Beneficial : FocusAppliesTo.Detrimental; break;
                case 140: minTicks = e.Base; break;
                case 137: effectLimits++; break;
                case 311: noCombat = true; break;
            }
        }

        var list = new List<ItemFocus>();
        foreach (SpellEffects.Effect e in effects)
        {
            FocusKind? kind = e.Spa switch
            {
                124 => FocusKind.Damage,
                125 => FocusKind.Healing,
                127 => FocusKind.SpellHaste,
                128 => applies == FocusAppliesTo.Detrimental ? FocusKind.DetrimentalDuration : FocusKind.BuffDuration,
                129 => FocusKind.Range,
                130 => FocusKind.Hate,
                131 => FocusKind.ReagentCost,
                132 => FocusKind.ManaCost,
                _ => null,
            };
            if (kind is not { } k || e.Base == 0 || list.Any(f => f.Kind == k))
            {
                continue;
            }

            int max = k is FocusKind.Damage or FocusKind.Healing or FocusKind.ManaCost && e.Limit > e.Base ? e.Limit : 0; // these roll between base and limit
            list.Add(new ItemFocus(spellId, name, k, e.Base, max, maxLevel, perLevel, minLevel, applies, minTicks, noCombat, effectLimits));

            // A duration focus with no beneficial/detrimental limit works on both.
            if (k == FocusKind.BuffDuration && applies == FocusAppliesTo.Any)
            {
                list.Add(list[^1] with { Kind = FocusKind.DetrimentalDuration });
            }
        }

        return list;
    }
}

/// <summary>What each focus kind is called on the Focus Effects tab, and which item message (eqstr_us.txt) shows it firing.</summary>
public static class FocusKinds
{
    public static IReadOnlyList<FocusKind> All { get; } = Enum.GetValues<FocusKind>();

    public static string Title(this FocusKind kind) => kind switch
    {
        FocusKind.BuffDuration => "Buff duration",
        FocusKind.DetrimentalDuration => "Detrimental duration",
        FocusKind.Healing => "Healing",
        FocusKind.Damage => "Damage",
        FocusKind.SpellHaste => "Spell haste",
        FocusKind.ManaCost => "Mana cost",
        FocusKind.Range => "Range",
        FocusKind.Hate => "Hate",
        _ => "Reagent cost",
    };

    /// <summary>
    /// The confirmed "Your item ..." message of each kind on live servers ("feels alive with power" fired on heals with Improved Healing II;
    /// "flickers with a pale light" with a mana-cost focus). Unconfirmed messages are left out until a player confirms them.
    /// </summary>
    public static FocusKind? FromItemMessage(string ending) => ending switch
    {
        "feels alive with power." => FocusKind.Healing,
        "flickers with a pale light." => FocusKind.ManaCost,
        _ => null,
    };

    /// <summary>The endings of every item focus message the game has, recorded whether or not their kind is confirmed yet.</summary>
    public static IReadOnlyList<string> ItemMessageEndings { get; } = new[]
    {
        "flickers with a pale light.", "pulses with light as your vision sharpens.", "feeds you with power.", "seems drained of power.",
        "feels alive with power.", "sparkles.", "grows dim.", "begins to shine.", "shimmers briefly.", "glows blue.",
    };
}
