using System.Text.Json;

namespace EQOverwatch.Core.Settings;

/// <summary>
/// How the player arranged the app: divider positions, table columns (width, order, shown, sort, row height), collapsed groups,
/// tab order and hidden tabs, per-tab text size and the start-up tab. Each value is a short text under a key the app makes up; the
/// whole set can be saved under a name (a layout preset) and put back, or cleared (Reset layout). Kept in its own file
/// (layout.json) so the main settings stay small.
/// </summary>
public sealed class UiLayoutStore
{
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, string>> _presets = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    /// <summary>Raised after a preset was applied or the layout was reset, so the window can put every element back.</summary>
    public event Action? Replaced;

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    /// <summary>Sets a value (null or empty removes it) and saves.</summary>
    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (!_values.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (_values.TryGetValue(key, out string? old) && old == value)
                {
                    return;
                }

                _values[key] = value;
            }
        }

        Save();
    }

    /// <summary>The names of the saved layout presets, A to Z.</summary>
    public IReadOnlyList<string> PresetNames
    {
        get
        {
            lock (_gate)
            {
                return _presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>Saves the current layout under <paramref name="name"/> (replacing a preset of that name).</summary>
    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _presets[name] = new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }

        Save();
    }

    /// <summary>Makes the named preset the current layout. False when there is no such preset.</summary>
    public bool ApplyPreset(string name)
    {
        lock (_gate)
        {
            if (!_presets.TryGetValue(name.Trim(), out Dictionary<string, string>? preset))
            {
                return false;
            }

            _values = new Dictionary<string, string>(preset, StringComparer.Ordinal);
        }

        Save();
        Replaced?.Invoke();
        return true;
    }

    public void DeletePreset(string name)
    {
        bool removed;
        lock (_gate)
        {
            removed = _presets.Remove(name.Trim());
        }

        if (removed)
        {
            Save();
        }
    }

    /// <summary>Forgets the whole current layout (presets are kept).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Save();
        Replaced?.Invoke();
    }

    /// <summary>Reads the layout file; a missing or unreadable file means the built-in layout.</summary>
    public void Load(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(path));
            lock (_gate)
            {
                _values = new Dictionary<string, string>(file?.Values ?? new(), StringComparer.Ordinal);
                _presets = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, Dictionary<string, string> values) in file?.Presets ?? new())
                {
                    _presets[name] = new Dictionary<string, string>(values, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file leaves the built-in layout.
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        LayoutFile file;
        lock (_gate)
        {
            file = new LayoutFile
            {
                Values = new Dictionary<string, string>(_values, StringComparer.Ordinal),
                Presets = _presets.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase),
            };
        }

        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved this time; the next change tries again.
        }
    }

    private sealed class LayoutFile
    {
        public Dictionary<string, string> Values { get; set; } = new();

        public Dictionary<string, Dictionary<string, string>> Presets { get; set; } = new();
    }
}
