using System.Text.Json;

namespace EQOverwatch.Core.History;

/// <summary>The lowest and highest level the logs have shown for one mob, in one zone (<see cref="Zone"/> null: zone not known).</summary>
public sealed record MobLevels(string Mob, int Lowest, int Highest, string? Zone = null);

/// <summary>
/// Remembers the level range of every mob seen on a <c>/consider</c> line, per zone, in a JSON file. It is shared by every character and
/// every log. The same mob name can have different levels in different zones, so each zone keeps its own range; ranges saved before
/// zones were kept (and lines seen before a log entered a zone) have no zone. A level only widens a range.
/// </summary>
public sealed class MobLevelStore
{
    private readonly object _gate = new();

    /// <summary>Mob name, then zone ("" for no zone), to its range.</summary>
    private readonly Dictionary<string, Dictionary<string, MobLevels>> _levels = new(StringComparer.OrdinalIgnoreCase);
    private string? _filePath;

    public event Action? Changed;

    /// <summary>True once the ranges have been rebuilt by zone from the logs (saved with the file).</summary>
    public bool ZonesRebuilt { get; private set; }

    private sealed record SavedFile(int Version, bool ZonesRebuilt, List<MobLevels> Levels);

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    List<MobLevels>? saved;
                    if (json.TrimStart().StartsWith('['))
                    {
                        saved = JsonSerializer.Deserialize<List<MobLevels>>(json); // older files: a plain list without zones
                    }
                    else
                    {
                        SavedFile? file = JsonSerializer.Deserialize<SavedFile>(json);
                        saved = file?.Levels;
                        ZonesRebuilt = file?.ZonesRebuilt ?? false;
                    }

                    foreach (MobLevels entry in saved ?? new List<MobLevels>())
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Mob) && entry.Lowest > 0 && entry.Highest >= entry.Lowest)
                        {
                            Zones(entry.Mob)[ZoneKey(entry.Zone)] = entry with { Zone = NullIfEmpty(entry.Zone) };
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Notes that <paramref name="mob"/> was seen at <paramref name="level"/> in <paramref name="zone"/>. Returns true when a range changed.</summary>
    public bool Observe(string mob, int level, string? zone = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = ObserveLocked(mob, level, zone);
            if (changed)
            {
                Save();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Adds many sightings (a rebuild from the logs) with one save, and marks the store as rebuilt by zone.</summary>
    public void ObserveAll(IEnumerable<(string Mob, int Level, string? Zone)> sightings)
    {
        lock (_gate)
        {
            foreach ((string mob, int level, string? zone) in sightings)
            {
                ObserveLocked(mob, level, zone);
            }

            ZonesRebuilt = true;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Every /consider level in a log, with the zone the log was in at the time ("You have entered ..."); null before the first zone line.
    /// Reads the file shared, so EQ can keep writing it.
    /// </summary>
    public static IEnumerable<(string Mob, int Level, string? Zone)> ScanLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = new StreamReader(stream);
        string? zone = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int close = line.IndexOf("] ", StringComparison.Ordinal);
            if (line.Length == 0 || line[0] != '[' || close < 0)
            {
                continue;
            }

            ReadOnlySpan<char> message = line.AsSpan(close + 2).TrimEnd('\r');
            if (message.StartsWith("You have entered ", StringComparison.Ordinal))
            {
                zone = EQOverwatch.Core.Maps.ZoneTracker.ParseZone(message) ?? zone;
            }
            else if (EQOverwatch.Core.Parsing.ConLineParser.TryParse(message, out string mob, out int level))
            {
                yield return (mob, level, zone);
            }
        }
    }

    private bool ObserveLocked(string mob, int level, string? zone)
    {
        if (string.IsNullOrWhiteSpace(mob) || level <= 0)
        {
            return false;
        }

        Dictionary<string, MobLevels> zones = Zones(mob);
        string key = ZoneKey(zone);
        if (zones.TryGetValue(key, out MobLevels? known))
        {
            if (level >= known.Lowest && level <= known.Highest)
            {
                return false;
            }

            zones[key] = known with { Lowest = Math.Min(known.Lowest, level), Highest = Math.Max(known.Highest, level) };
        }
        else
        {
            zones[key] = new MobLevels(mob, level, level, NullIfEmpty(zone));
        }

        return true;
    }

    /// <summary>The range for <paramref name="mob"/> over every zone (names compared without regard to capitals), or null when no consider line has shown it.</summary>
    public MobLevels? Find(string mob)
    {
        lock (_gate)
        {
            return _levels.TryGetValue(mob, out Dictionary<string, MobLevels>? zones) ? Combined(zones.Values) : null;
        }
    }

    /// <summary>
    /// The range for <paramref name="mob"/> in <paramref name="zone"/>. When that zone has none yet (or the zone is not known), the range
    /// with no zone is used, then the range over every zone.
    /// </summary>
    public MobLevels? Find(string mob, string? zone)
    {
        lock (_gate)
        {
            if (!_levels.TryGetValue(mob, out Dictionary<string, MobLevels>? zones))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(zone) && zones.TryGetValue(ZoneKey(zone), out MobLevels? here))
            {
                return here;
            }

            return zones.TryGetValue(string.Empty, out MobLevels? unzoned) ? unzoned : Combined(zones.Values);
        }
    }

    /// <summary>Every range, one per mob and zone.</summary>
    public IReadOnlyList<MobLevels> All()
    {
        lock (_gate)
        {
            return _levels.Values.SelectMany(z => z.Values).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _levels.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    private Dictionary<string, MobLevels> Zones(string mob)
    {
        if (!_levels.TryGetValue(mob, out Dictionary<string, MobLevels>? zones))
        {
            zones = new Dictionary<string, MobLevels>(StringComparer.OrdinalIgnoreCase);
            _levels[mob] = zones;
        }

        return zones;
    }

    private static MobLevels? Combined(IEnumerable<MobLevels> ranges)
    {
        MobLevels? all = null;
        foreach (MobLevels r in ranges)
        {
            all = all is null ? r with { Zone = null } : all with { Lowest = Math.Min(all.Lowest, r.Lowest), Highest = Math.Max(all.Highest, r.Highest) };
        }

        return all;
    }

    private static string ZoneKey(string? zone) => zone?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string? zone) => string.IsNullOrWhiteSpace(zone) ? null : zone.Trim();

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<MobLevels> levels = _levels.Values.SelectMany(z => z.Values)
                .OrderBy(l => l.Mob, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Zone ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedFile(2, ZonesRebuilt, levels)));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; the next change tries again
        }
    }
}
