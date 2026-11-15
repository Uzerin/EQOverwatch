using System.Globalization;

namespace EQOverwatch.Core.Maps;

/// <summary>One zone: its number, full name, short name (map file name) and expansion number (0 when it has none).</summary>
public sealed record ZoneInfo(int Id, string Name, string ShortName, int Expansion);

/// <summary>
/// Zone names and expansions, from EQ's own <c>Resources\ZoneNames.txt</c> (<c>number^Full Name^...^expansion^expansion</c>) joined
/// with <see cref="ZoneShortNames"/>.
/// </summary>
public sealed class ZoneDirectory
{
    /// <summary>The expansions in release order; expansion number n is entry n - 1.</summary>
    public static IReadOnlyList<string> ExpansionNames { get; } = new[]
    {
        "Classic", "The Ruins of Kunark", "The Scars of Velious", "The Shadows of Luclin", "The Planes of Power", "The Legacy of Ykesha",
        "Lost Dungeons of Norrath", "Gates of Discord", "Omens of War", "Dragons of Norrath", "Depths of Darkhollow", "Prophecy of Ro",
        "The Serpent's Spine", "The Buried Sea", "Secrets of Faydwer", "Seeds of Destruction", "Underfoot", "House of Thule",
        "Veil of Alaris", "Rain of Fear", "Call of the Forsaken", "The Darkened Sea", "The Broken Mirror", "Empires of Kunark",
        "Ring of Scale", "The Burning Lands", "Torment of Velious", "Claws of Veeshan", "Terror of Luclin", "Night of Shadows",
        "Laurion's Song", "The Outer Brood", "Shattering of Ro",
    };

    private readonly Dictionary<string, ZoneInfo> _byShortName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ZoneInfo>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public static ZoneDirectory Empty { get; } = new(Array.Empty<ZoneInfo>());

    public ZoneDirectory(IEnumerable<ZoneInfo> zones)
    {
        foreach (ZoneInfo zone in zones.OrderBy(z => z.Id))
        {
            _byShortName.TryAdd(zone.ShortName, zone); // a short name used by several numbers keeps its first one
            if (!_byName.TryGetValue(Key(zone.Name), out List<ZoneInfo>? list))
            {
                _byName[Key(zone.Name)] = list = new List<ZoneInfo>();
            }

            if (!list.Any(z => string.Equals(z.ShortName, zone.ShortName, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(zone);
            }
        }
    }

    /// <summary>Reads <c>Resources\ZoneNames.txt</c> from the EQ folder; empty when it is missing.</summary>
    public static ZoneDirectory Load(string? eqFolder)
    {
        if (string.IsNullOrWhiteSpace(eqFolder))
        {
            return Empty;
        }

        string path = Path.Combine(eqFolder, "Resources", "ZoneNames.txt");
        try
        {
            return File.Exists(path) ? Parse(File.ReadLines(path)) : Empty;
        }
        catch (IOException)
        {
            return Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public static ZoneDirectory Parse(IEnumerable<string> lines)
    {
        var zones = new List<ZoneInfo>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('^');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                || !ZoneShortNames.ById.TryGetValue(id, out string? shortName))
            {
                continue;
            }

            int expansion = parts.Length > 4 && int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) ? e : 0;
            zones.Add(new ZoneInfo(id, parts[1].Trim(), shortName, expansion));
        }

        return new ZoneDirectory(zones);
    }

    public static string ExpansionName(int expansion) =>
        expansion >= 1 && expansion <= ExpansionNames.Count ? ExpansionNames[expansion - 1]
        : expansion > ExpansionNames.Count ? string.Create(CultureInfo.InvariantCulture, $"Expansion {expansion}")
        : "Other";

    /// <summary>The zone with this short name, or null.</summary>
    public ZoneInfo? ByShortName(string shortName) => _byShortName.GetValueOrDefault(shortName);

    /// <summary>
    /// The zones with this full name ("You have entered Velketor's Labyrinth." can be the Velious zone or the Torment of Velious
    /// one), oldest first. A leading "The" is ignored.
    /// </summary>
    public IReadOnlyList<ZoneInfo> ByName(string name) =>
        _byName.TryGetValue(Key(name), out List<ZoneInfo>? list) ? list : Array.Empty<ZoneInfo>();

    /// <summary>Every expansion number some zone belongs to, in release order (0, for none, last).</summary>
    public IEnumerable<int> Expansions => _byShortName.Values.Select(z => z.Expansion).Distinct();

    private static string Key(string name)
    {
        string text = name.Trim().TrimEnd('.').Trim();
        if (text.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].TrimStart();
        }

        return text;
    }
}
