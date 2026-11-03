using System.Text.Json;

namespace EQOverwatch.Core.History;

/// <summary>The lowest and highest level the logs have shown for one mob.</summary>
public sealed record MobLevels(string Mob, int Lowest, int Highest);

/// <summary>
/// Remembers the level range of every mob seen on a <c>/consider</c> line, in a JSON file. It is shared by every character and every
/// log, so a mob keeps its range whichever log is loaded. A level only widens the range: a lower one moves Lowest down and a higher
/// one moves Highest up.
/// </summary>
public sealed class MobLevelStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MobLevels> _levels = new(StringComparer.OrdinalIgnoreCase);
    private string? _filePath;

    public event Action? Changed;

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path))
                {
                    List<MobLevels>? saved = JsonSerializer.Deserialize<List<MobLevels>>(File.ReadAllText(path));
                    foreach (MobLevels entry in saved ?? new List<MobLevels>())
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Mob) && entry.Lowest > 0 && entry.Highest >= entry.Lowest)
                        {
                            _levels[entry.Mob] = entry;
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

    /// <summary>Notes that <paramref name="mob"/> was seen at <paramref name="level"/>. Returns true when the range changed.</summary>
    public bool Observe(string mob, int level)
    {
        if (string.IsNullOrWhiteSpace(mob) || level <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_levels.TryGetValue(mob, out MobLevels? known))
            {
                if (level >= known.Lowest && level <= known.Highest)
                {
                    return false;
                }

                _levels[mob] = known with { Lowest = Math.Min(known.Lowest, level), Highest = Math.Max(known.Highest, level) };
            }
            else
            {
                _levels[mob] = new MobLevels(mob, level, level);
            }

            Save();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>The range for <paramref name="mob"/> (names compared without regard to capitals), or null when no consider line has shown it.</summary>
    public MobLevels? Find(string mob)
    {
        lock (_gate)
        {
            return _levels.TryGetValue(mob, out MobLevels? found) ? found : null;
        }
    }

    public IReadOnlyList<MobLevels> All()
    {
        lock (_gate)
        {
            return _levels.Values.ToList();
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

            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_levels.Values.OrderBy(l => l.Mob, StringComparer.OrdinalIgnoreCase).ToList()));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; the next change tries again
        }
    }
}
