using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace EQOverwatch.Core.Maps;

/// <summary>A spot a mob can spawn at, in /loc order (Y, X, Z).</summary>
public sealed record SpawnSpot(int Y, int X, int Z)
{
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

    /// <summary>Reads the gzipped list: {"zones":[{"s":short,"n":name,"o":outdoor,"m":[[name,min,max,respawn,[[y,x,z],...]|null],...]}]}.</summary>
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
                    spots = mob[4].EnumerateArray().Select(s => new SpawnSpot(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32())).ToList();
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
