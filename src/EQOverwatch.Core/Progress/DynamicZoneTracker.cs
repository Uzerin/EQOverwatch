using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Progress;

/// <summary>A dynamic zone (expedition) one or more of your characters are in, and when it closes.</summary>
public sealed record DynamicZoneTimer(string Zone, DateTime ClosesUtc, IReadOnlyList<string> Members)
{
    public TimeSpan Remaining(DateTime nowUtc) => ClosesUtc > nowUtc ? ClosesUtc - nowUtc : TimeSpan.Zero;
}

/// <summary>
/// Your characters' dynamic zones: "Ream has been added to Vex Thal." in Ream's own log starts a six-hour timer for Vex Thal, and
/// "Ream has been removed from Vex Thal." takes Ream off it. Boxes in the same zone share one timer, timed from the first to join. A
/// timer ends when it runs out or nobody is left in it. Kept in a file, because the zone keeps running while the app is closed.
/// </summary>
public sealed partial class DynamicZoneTracker : ITriggerLineSink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _zones = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;
    private long _version;

    private sealed class Entry
    {
        public DateTime StartUtc { get; set; }

        public List<string> Members { get; set; } = new();
    }

    public long Version => Interlocked.Read(ref _version);

    [GeneratedRegex(@"^(?<who>[A-Za-z]+) has been added to (?<zone>.+)\.$")]
    private static partial Regex AddedRegex();

    [GeneratedRegex(@"^(?<who>[A-Za-z]+) has been removed from (?<zone>.+)\.$")]
    private static partial Regex RemovedRegex();

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 20 || !message.Contains(" has been ", StringComparison.Ordinal))
        {
            return;
        }

        string line = message.ToString();
        if (AddedRegex().Match(line) is { Success: true } added && IsOwn(added, characterName))
        {
            Join(added.Groups["zone"].Value, characterName, receivedUtc);
        }
        else if (RemovedRegex().Match(line) is { Success: true } removed && IsOwn(removed, characterName))
        {
            Leave(removed.Groups["zone"].Value, characterName, receivedUtc);
        }
    }

    /// <summary>Only the log owner's own line counts, and never a key ring, corpse list or inventory.</summary>
    private static bool IsOwn(Match match, string characterName) =>
        match.Groups["who"].Value.Equals(characterName, StringComparison.OrdinalIgnoreCase)
        && !match.Groups["zone"].Value.StartsWith("your ", StringComparison.OrdinalIgnoreCase)
        && !match.Groups["zone"].Value.StartsWith("the ", StringComparison.Ordinal);

    public void Join(string zone, string character, DateTime utc)
    {
        lock (_gate)
        {
            Prune(utc);
            if (!_zones.TryGetValue(zone, out Entry? entry))
            {
                _zones[zone] = entry = new Entry { StartUtc = utc };
            }

            if (!entry.Members.Contains(character, StringComparer.OrdinalIgnoreCase))
            {
                entry.Members.Add(character);
            }
        }

        Changed();
    }

    public void Leave(string zone, string character, DateTime utc)
    {
        lock (_gate)
        {
            if (_zones.TryGetValue(zone, out Entry? entry))
            {
                entry.Members.RemoveAll(m => m.Equals(character, StringComparison.OrdinalIgnoreCase));
                if (entry.Members.Count == 0)
                {
                    _zones.Remove(zone);
                }
            }

            Prune(utc);
        }

        Changed();
    }

    /// <summary>The open zones, the one closing soonest first.</summary>
    public IReadOnlyList<DynamicZoneTimer> Current(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _zones
                .Where(z => z.Value.StartUtc + Lifetime > nowUtc)
                .Select(z => new DynamicZoneTimer(z.Key, z.Value.StartUtc + Lifetime, z.Value.Members.ToList()))
                .OrderBy(z => z.ClosesUtc)
                .ToList();
        }
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (string zone in _zones.Where(z => z.Value.StartUtc + Lifetime <= nowUtc).Select(z => z.Key).ToList())
        {
            _zones.Remove(zone);
        }
    }

    public void SetStoragePath(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } saved)
            {
                lock (_gate)
                {
                    _zones.Clear();
                    foreach (KeyValuePair<string, Entry> pair in saved)
                    {
                        _zones[pair.Key] = pair.Value;
                    }

                    Prune(DateTime.UtcNow);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file only loses the open timers.
        }
    }

    private void Changed()
    {
        Interlocked.Increment(ref _version);
        if (_path is null)
        {
            return;
        }

        try
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(_zones);
            }

            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved again on the next change.
        }
    }
}
