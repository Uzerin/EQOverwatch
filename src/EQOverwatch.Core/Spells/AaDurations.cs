using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQOverwatch.Core.Spells;

/// <summary>
/// One duration bonus of an alternate ability rank: whole seconds (<see cref="Seconds"/>) or a percent (<see cref="Percent"/>) added to the
/// spells it is limited to. Every limit that is set must hold.
/// </summary>
public sealed class AaDurationBonus
{
    [JsonPropertyName("s")] public int Seconds { get; init; }

    [JsonPropertyName("p")] public int Percent { get; init; }

    /// <summary>Spell groups (the spell file's group column).</summary>
    [JsonPropertyName("g")] public int[]? Groups { get; init; }

    /// <summary>Spell lines (the spell file's line column).</summary>
    [JsonPropertyName("l")] public int[]? Lines { get; init; }

    /// <summary>Single spells by id.</summary>
    [JsonPropertyName("sp")] public int[]? Spells { get; init; }

    /// <summary>Classes (0 = Warrior) of which one must be able to use the spell.</summary>
    [JsonPropertyName("c")] public int[]? Classes { get; init; }

    /// <summary>Effects of which the spell must have one.</summary>
    [JsonPropertyName("ie")] public int[]? WithEffects { get; init; }

    /// <summary>Effects the spell must not have.</summary>
    [JsonPropertyName("xe")] public int[]? WithoutEffects { get; init; }

    /// <summary>1 beneficial only, 0 detrimental only, null either.</summary>
    [JsonPropertyName("ty")] public int? Type { get; init; }

    [JsonPropertyName("mx")] public int? MaxLevel { get; init; }

    [JsonPropertyName("mn")] public int? MinLevel { get; init; }

    /// <summary>The spell's base duration must be at least this many seconds.</summary>
    [JsonPropertyName("md")] public int? MinDurationSeconds { get; init; }

    /// <summary>The ability that gives this bonus and its rank ("Improved Thief's Eyes 7"), filled in when looked up.</summary>
    [JsonIgnore] public string Source { get; set; } = string.Empty;

    /// <summary>True when this bonus works on <paramref name="spell"/> (<paramref name="level"/>: the spell's level for the caster's class, 0 when unknown).</summary>
    public bool AppliesTo(SpellInfo spell, int level)
    {
        bool limitedToSpells = Groups is not null || Lines is not null || Spells is not null;
        if (limitedToSpells
            && !(Groups?.Contains(spell.SpellGroup) == true && spell.SpellGroup != 0)
            && !(Lines?.Contains(spell.SpellLine) == true && spell.SpellLine != 0)
            && Spells?.Contains(spell.SpellId) != true)
        {
            return false;
        }

        if (Classes is not null && (spell.ClassLevels is not { } levels || !Classes.Any(c => c < levels.Count && levels[c] is > 0 and < 255)))
        {
            return false;
        }

        IReadOnlyList<int> effects = spell.EffectIds ?? Array.Empty<int>();
        if (WithEffects is not null && !WithEffects.Any(effects.Contains))
        {
            return false;
        }

        if (WithoutEffects is not null && WithoutEffects.Any(effects.Contains))
        {
            return false;
        }

        bool detrimental = spell.Category == Models.TimerOverlayCategory.Debuffs;
        if ((Type == 1 && detrimental) || (Type == 0 && !detrimental))
        {
            return false;
        }

        if (level > 0 && ((MaxLevel is int max && level > max) || (MinLevel is int min && level < min)))
        {
            return false;
        }

        return MinDurationSeconds is not int minSeconds || spell.BaseDurationSeconds >= minSeconds;
    }
}

/// <summary>
/// The alternate abilities that lengthen spells and combat abilities, with each rank's bonus. The game files do not give these values,
/// so the table ships with the app (Spells/AaDurations.json).
/// </summary>
public static class AaDurations
{
    private sealed class Entry
    {
        [JsonPropertyName("n")] public string Name { get; init; } = string.Empty;

        [JsonPropertyName("r")] public List<Rank> Ranks { get; init; } = new();
    }

    private sealed class Rank
    {
        [JsonPropertyName("rank")] public int Number { get; init; }

        [JsonPropertyName("lv")] public int Level { get; init; }

        [JsonPropertyName("b")] public List<AaDurationBonus> Bonuses { get; init; } = new();
    }

    private static readonly Lazy<Dictionary<string, Entry>> Table = new(Load);

    /// <summary>The names of the abilities in the table.</summary>
    public static IReadOnlyCollection<string> Names => Table.Value.Keys;

    /// <summary>The bonuses of the ranks a character has (ability name to rank); an ability not in the table adds nothing.</summary>
    public static IReadOnlyList<AaDurationBonus> For(IReadOnlyDictionary<string, int> ranks)
    {
        var list = new List<AaDurationBonus>();
        foreach ((string name, int rank) in ranks)
        {
            if (rank < 1 || !Table.Value.TryGetValue(name, out Entry? entry))
            {
                continue;
            }

            // A rank above the table's highest uses the highest.
            Rank? row = entry.Ranks.Where(r => r.Number <= rank).OrderByDescending(r => r.Number).FirstOrDefault();
            if (row is null)
            {
                continue;
            }

            foreach (AaDurationBonus bonus in row.Bonuses)
            {
                bonus.Source = $"{entry.Name} {row.Number}";
                list.Add(bonus);
            }
        }

        return list;
    }

    private static Dictionary<string, Entry> Load()
    {
        using Stream? stream = typeof(AaDurations).Assembly.GetManifestResourceStream("EQOverwatch.Core.Spells.AaDurations.json");
        List<Entry> entries = stream is null ? new() : JsonSerializer.Deserialize<List<Entry>>(stream) ?? new();
        return entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>An alternate ability's name and description from the game's string file (dbstr_us.txt), for picking one by name.</summary>
public sealed record AaInfo(string Name, string Description);

public static class AaCatalog
{
    /// <summary>Every ability name in the EverQuest folder's dbstr_us.txt (A to Z, once each), or none when the file is missing.</summary>
    public static IReadOnlyList<AaInfo> Load(string? eqFolder)
    {
        string path = string.IsNullOrEmpty(eqFolder) ? string.Empty : Path.Combine(eqFolder, "dbstr_us.txt");
        if (!File.Exists(path))
        {
            return Array.Empty<AaInfo>();
        }

        var names = new Dictionary<string, string>(); // id to name
        var descriptions = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(path, System.Text.Encoding.Latin1))
        {
            int a = line.IndexOf('^');
            int b = a < 0 ? -1 : line.IndexOf('^', a + 1);
            int c = b < 0 ? -1 : line.IndexOf('^', b + 1);
            if (c < 0)
            {
                continue;
            }

            ReadOnlySpan<char> type = line.AsSpan(a + 1, b - a - 1);
            if (type is "1")
            {
                names[line[..a]] = line[(b + 1)..c].Trim();
            }
            else if (type is "4")
            {
                descriptions[line[..a]] = Clean(line[(b + 1)..c]);
            }
        }

        return names.Where(n => n.Value.Length > 0)
            .GroupBy(n => n.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AaInfo(g.Key, g.Select(n => descriptions.GetValueOrDefault(n.Key)).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? string.Empty))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Drops the game's markup ("&lt;br&gt;", colour tags) and its number placeholders ("#1", "*#2+B").</summary>
    private static string Clean(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\\?>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<c [^>]*\\?>|</c\\?>", string.Empty);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*?#\d(\+B)?", "…");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }
}
