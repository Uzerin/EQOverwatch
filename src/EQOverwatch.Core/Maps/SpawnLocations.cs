using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace EQOverwatch.Core.Maps;

/// <summary>A spot a mob can spawn at, in /loc order (Y, X, Z), how often it takes the spot (percent, 0 when not known)
/// and, for named mobs, the other mobs that spawn there (its placeholders).</summary>
public sealed record SpawnSpot(int Y, int X, int Z, int Chance = 0, IReadOnlyList<string>? SharesWith = null)
{
    /// <summary>"a chromadrac, a wurm".</summary>
    public string SharesWithText => SharesWith is { Count: > 0 } others ? string.Join(", ", others) : string.Empty;

    public string ChanceText => Chance > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Chance}%") : string.Empty;

    /// <summary>Same place (the placeholders and chance are not compared).</summary>
    public bool SamePlace(SpawnSpot other) => Y == other.Y && X == other.X && Z == other.Z;

    /// <summary>"1768, -3849, -160", as /loc prints it.</summary>
    public string LocText => string.Create(CultureInfo.InvariantCulture, $"{Y}, {X}, {Z}");

    /// <summary>"/waypoint 1768 -3849 -160", to paste in game.</summary>
    public string Waypoint => string.Create(CultureInfo.InvariantCulture, $"/waypoint {Y} {X} {Z}");
}

/// <summary>A mob of a zone: its levels, respawn time and spawn spots (null when it spawns at random spots).</summary>
public sealed record SpawnMob(string Name, int MinLevel, int MaxLevel, int RespawnSeconds, IReadOnlyList<SpawnSpot>? Spots)
{
    /// <summary>Named mobs start with a capital letter ("Eldrig the Old", not "a wurm").</summary>
    public bool IsNamed => Name.Length > 0 && char.IsUpper(Name[0])
        && !Name.StartsWith("A ", StringComparison.Ordinal) && !Name.StartsWith("An ", StringComparison.Ordinal) && !Name.StartsWith("The ", StringComparison.Ordinal);

    public string LevelText => MinLevel <= 0 ? string.Empty
        : MaxLevel > MinLevel ? string.Create(CultureInfo.InvariantCulture, $"{MinLevel}-{MaxLevel}")
        : MinLevel.ToString(CultureInfo.InvariantCulture);

    public string RespawnText => FormatRespawn(RespawnSeconds);

    /// <summary>"6:40", "1:12:00", "3 days".</summary>
    public static string FormatRespawn(int seconds)
    {
        if (seconds <= 0)
        {
            return string.Empty;
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1 && span.Hours == 0 && span.Minutes == 0)
        {
            int days = (int)span.TotalDays;
            return days == 1 ? "1 day" : string.Create(CultureInfo.InvariantCulture, $"{days} days");
        }

        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }
}

/// <summary>A zone's mobs. Outdoor zones spawn most mobs at random spots; their named mobs keep fixed spots.</summary>
public sealed record SpawnZone(string ShortName, string Name, bool IsOutdoor, IReadOnlyList<SpawnMob> Mobs);

/// <summary>The possible spawn locations of each zone's mobs, shipped with the app.</summary>
public static class SpawnLocations
{
    private static readonly Lazy<IReadOnlyList<SpawnZone>> Loaded = new(() =>
    {
        using Stream? stream = typeof(SpawnLocations).Assembly.GetManifestResourceStream("EQOverwatch.Core.Maps.SpawnLocations.json.gz");
        return stream is null ? Array.Empty<SpawnZone>() : Read(stream);
    });

    /// <summary>Every zone, by name.</summary>
    public static IReadOnlyList<SpawnZone> Zones => Loaded.Value;

    public static SpawnZone? Find(string? shortName) =>
        shortName is null ? null : Zones.FirstOrDefault(z => z.ShortName.Equals(shortName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the gzipped list: {"zones":[{"s":short,"n":name,"o":outdoor,"m":[[name,min,max,respawn,[[y,x,z,chance,[others]],...]|null],...]}]}.</summary>
    public static IReadOnlyList<SpawnZone> Read(Stream gzipped)
    {
        using var gzip = new GZipStream(gzipped, CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(gzip);
        var zones = new List<SpawnZone>();
        foreach (JsonElement zone in document.RootElement.GetProperty("zones").EnumerateArray())
        {
            var mobs = new List<SpawnMob>();
            foreach (JsonElement mob in zone.GetProperty("m").EnumerateArray())
            {
                List<SpawnSpot>? spots = null;
                if (mob[4].ValueKind == JsonValueKind.Array)
                {
                    spots = mob[4].EnumerateArray().Select(s => new SpawnSpot(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(),
                        s.GetArrayLength() > 3 ? s[3].GetInt32() : 0,
                        s.GetArrayLength() > 4 ? s[4].EnumerateArray().Select(n => n.GetString() ?? string.Empty).ToList() : null)).ToList();
                }

                mobs.Add(new SpawnMob(mob[0].GetString() ?? string.Empty, mob[1].GetInt32(), mob[2].GetInt32(), mob[3].GetInt32(), spots));
            }

            zones.Add(new SpawnZone(zone.GetProperty("s").GetString() ?? string.Empty, zone.GetProperty("n").GetString() ?? string.Empty,
                zone.GetProperty("o").GetInt32() == 1, mobs));
        }

        return zones.OrderBy(z => z.Name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? z.Name[4..] : z.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>The spawn spots a player has ticked Verified, saved on this PC only (verifiedspawns.json).</summary>
public sealed class VerifiedSpawnStore
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    public void SetStoragePath(string path)
    {
        _path = path;
        _keys.Clear();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) is { } keys)
            {
                _keys.UnionWith(keys);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // start empty
        }
    }

    public static string Key(string zone, string mob, SpawnSpot spot) =>
        string.Create(CultureInfo.InvariantCulture, $"{zone}|{mob}|{spot.Y}|{spot.X}|{spot.Z}");

    public bool IsVerified(string zone, string mob, SpawnSpot spot) => _keys.Contains(Key(zone, mob, spot));

    public void Set(string zone, string mob, SpawnSpot spot, bool verified)
    {
        bool changed = verified ? _keys.Add(Key(zone, mob, spot)) : _keys.Remove(Key(zone, mob, spot));
        if (!changed || _path is null)
        {
            return;
        }

        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; saved with the next change
        }
    }
}

/// <summary>The player's own Named ticks, by zone and mob, saved on this PC only (namedspawns.json). Missing means "as known".</summary>
public sealed class NamedSpawnStore
{
    private readonly Dictionary<string, bool> _named = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    public void SetStoragePath(string path)
    {
        _path = path;
        _named.Clear();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) is { } named)
            {
                foreach ((string key, bool value) in named)
                {
                    _named[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // start empty
        }
    }

    private static string Key(string zone, string mob) => zone + "|" + mob;

    /// <summary>The player's tick, or the known answer when they have not changed it.</summary>
    public bool IsNamed(string zone, SpawnMob mob) => _named.TryGetValue(Key(zone, mob.Name), out bool named) ? named : mob.IsNamed;

    public void Set(string zone, SpawnMob mob, bool named)
    {
        string key = Key(zone, mob.Name);
        if (named == mob.IsNamed)
        {
            _named.Remove(key);
        }
        else
        {
            _named[key] = named;
        }

        if (_path is null)
        {
            return;
        }

        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_named));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; saved with the next change
        }
    }
}

/// <summary>EverQuest con colors: how a mob of one level looks to a player of another.</summary>
public enum ConColor
{
    None,
    Gray,
    Green,
    LightBlue,
    Blue,
    White,
    Yellow,
    Red,
}

public static class Cons
{
    // (highest player level, gray at or below this difference, green at or below, light blue at or below); 0 = no such band.
    private static readonly (int Level, int Gray, int Green, int LightBlue)[] Bands =
    {
        (8, -4, 0, 0), (9, -6, 0, -4), (13, -7, 0, -5), (15, -7, -5, 0), (17, -8, -6, 0), (21, -9, -7, -5), (25, -10, -8, -6),
        (29, -11, -9, -7), (31, -12, -9, -7), (33, -13, -10, -8), (37, -14, -11, -8), (40, -16, -12, -9), (42, -17, -13, -10),
        (44, -18, -13, -10), (48, -19, -14, -11), (49, -20, -15, -12), (53, -21, -16, -12), (55, -22, -17, -13), (60, -23, -18, -14),
        (65, -24, -19, -15), (70, -25, -20, -16), (int.MaxValue, -26, -21, -17),
    };

    public static ConColor For(int playerLevel, int mobLevel)
    {
        if (playerLevel <= 0 || mobLevel <= 0)
        {
            return ConColor.None;
        }

        int diff = mobLevel - playerLevel;
        if (diff == 0)
        {
            return ConColor.White;
        }

        if (diff > 0)
        {
            return diff <= 2 ? ConColor.Yellow : ConColor.Red;
        }

        (int _, int gray, int green, int lightBlue) = Bands.First(b => playerLevel <= b.Level);
        return diff <= gray ? ConColor.Gray
            : green != 0 && diff <= green ? ConColor.Green
            : lightBlue != 0 && diff <= lightBlue ? ConColor.LightBlue
            : ConColor.Blue;
    }
}
