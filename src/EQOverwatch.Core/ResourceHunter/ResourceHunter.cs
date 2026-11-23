using System.Globalization;
using System.Text;
using System.Text.Json;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.ResourceHunter;

/// <summary>The daily zone bonuses of the Resource Hunter ruleset (Frostreaver, Fangbreaker and later servers).</summary>
public enum ResourceBonus
{
    /// <summary>Reported as having no bonus today (only the websites say this; a log never does).</summary>
    None,
    Experience,
    Aa,
    Loot,
    Coin,
    RareSpawn,
    Respawn,
    Skill,
    Faction,

    /// <summary>Listed by a website but not confirmed by anyone today (never stored or shared).</summary>
    Unknown,
}

/// <summary>Where a zone's bonus for today came from: your own logs, other EQ Overwatch players, or the server's tracking website.</summary>
public enum ResourceBonusSource
{
    Logs,
    Shared,
    Website,
}

/// <summary>A zone's bonus on one Pacific day.</summary>
public sealed record ResourceZoneBonus(string Zone, ResourceBonus Bonus, ResourceBonusSource Source, int Reports = 0, int MinLevel = 0, int MaxLevel = 0);

/// <summary>The words of the Resource Hunter log lines, bonus names, zone matching and the Pacific day the bonuses change on.</summary>
public static class ResourceHunterText
{
    public const string LinePrefix = "This land is saturated with unstable magic, ";

    /// <summary>The start of the line everyone online gets when the day's bonuses are drawn (midnight Pacific).</summary>
    public const string ResetPrefix = "Fonts of unstable magic are erupting and receding";

    private static readonly (string Words, ResourceBonus Bonus)[] Lines =
    {
        ("improving the experiences of all who venture here", ResourceBonus.Experience),
        ("improving the experience tied to abilities", ResourceBonus.Aa),
        ("multiplying the loot", ResourceBonus.Loot),
        ("multiplying the coin", ResourceBonus.Coin),
        ("drawn many creatures of exceptional rarity", ResourceBonus.RareSpawn),
        ("drawn numerous mundane creatures", ResourceBonus.Respawn),
        ("improving the rate at which proficiency is gained", ResourceBonus.Skill),
        ("magnifying the recognition", ResourceBonus.Faction),
    };

    private static readonly (string Key, ResourceBonus Bonus)[] Keys =
    {
        ("none", ResourceBonus.None), ("experience", ResourceBonus.Experience), ("aa", ResourceBonus.Aa), ("loot", ResourceBonus.Loot),
        ("coin", ResourceBonus.Coin), ("rare", ResourceBonus.RareSpawn), ("respawn", ResourceBonus.Respawn), ("skill", ResourceBonus.Skill),
        ("tradeskill", ResourceBonus.Skill), ("faction", ResourceBonus.Faction), ("unknown", ResourceBonus.Unknown),
    };

    /// <summary>The bonus a "This land is saturated with unstable magic, ..." line hints at, or null for any other line.</summary>
    public static ResourceBonus? Parse(ReadOnlySpan<char> message)
    {
        if (!message.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = message[LinePrefix.Length..];
        foreach ((string words, ResourceBonus bonus) in Lines)
        {
            if (rest.Contains(words, StringComparison.Ordinal))
            {
                return bonus;
            }
        }

        return null;
    }

    /// <summary>"experience", "aa", "loot", "coin", "rare", "respawn", "skill", "faction" or "none" (what the EQ Overwatch server stores).</summary>
    public static string Key(ResourceBonus bonus) => Keys.First(k => k.Bonus == bonus).Key;

    /// <summary>The bonus for a key from the EQ Overwatch server or a tracking website ("tradeskill" is the websites' Skill), or null.</summary>
    public static ResourceBonus? FromKey(string? key) =>
        Keys.FirstOrDefault(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Bonus : null;

    public static string Label(ResourceBonus bonus) => bonus switch
    {
        ResourceBonus.None => "No bonus",
        ResourceBonus.Aa => "AA",
        ResourceBonus.RareSpawn => "Rare Spawn",
        ResourceBonus.Unknown => "Unknown",
        _ => bonus.ToString(),
    };

    /// <summary>A zone name reduced for matching: "The Akheva Ruins" and "Akheva Ruins", "Nagafen's Lair (SolB)" and "Nagafen's Lair".</summary>
    public static string ZoneKey(string zone)
    {
        string name = zone.Trim();
        int paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            name = name[..paren];
        }

        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[4..];
        }

        var key = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }

        return key.ToString();
    }

    private static readonly TimeZoneInfo Pacific = FindPacific();

    private static TimeZoneInfo FindPacific()
    {
        foreach (string id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }

    /// <summary>The Pacific day ("2026-11-20") a moment falls on: the bonuses change at midnight Pacific.</summary>
    public static string Day(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Pacific).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The website that tracks the server's bonuses ("https://frostreaver.zone"), or null: other servers use EQ Overwatch data only.</summary>
    public static string? WebsiteFor(string server) => server.ToLowerInvariant() switch
    {
        "frostreaver" => "https://frostreaver.zone",
        "fangbreaker" => "https://fangbreaker.zone",
        _ => null,
    };
}

/// <summary>A bonus your logs found, for sharing: zone (as the game names it), bonus and Pacific day.</summary>
public sealed record ResourceFinding(string Zone, ResourceBonus Bonus, string Day);

/// <summary>
/// The Resource Hunter bonuses: the ones your characters' logs show (live lines and, once, everything already in the logs), kept per
/// server and Pacific day (never removed), plus today's bonuses other players and the tracking websites report. Live lines raise
/// <see cref="Found"/> once per zone entry.
/// </summary>
public sealed partial class ResourceHunterStore : ITriggerLineSink
{
    private const int ChunkSize = 4 << 20;

    private sealed class Entry
    {
        public string Server { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public string Zone { get; set; } = string.Empty;
        public string Bonus { get; set; } = string.Empty;
        public bool Shared { get; set; }
    }

    private sealed class SavedFile
    {
        public Dictionary<string, long> Logs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> LogZones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Entry> Entries { get; set; } = new();

        /// <summary>Every zone's bonus key per server, Pacific day and zone key (websites and other players; your logs are in <see cref="Entries"/>).</summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Days { get; set; } = new();

        /// <summary>The zones' names as the websites write them, per server and zone key.</summary>
        public Dictionary<string, Dictionary<string, string>> Names { get; set; } = new();

        /// <summary>The zones' level ranges [min, max], per server and zone key.</summary>
        public Dictionary<string, Dictionary<string, int[]>> Levels { get; set; } = new();

        /// <summary>The past days whose whole list was read from the website, per server.</summary>
        public Dictionary<string, List<string>> Fetched { get; set; } = new();
    }

    private readonly object _gate = new();
    private string? _path;
    private SavedFile _file = new();
    private readonly Dictionary<string, (string Zone, bool Told)> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Server, ResourceBonusSource Source), (string Day, List<ResourceZoneBonus> Zones)> _reported = new();
    private Func<string, string, string?>? _zoneOf;
    private long _version;

    public ResourceHunterStore(string? path = null)
    {
        _path = path;
        Load();
    }

    /// <summary>Where the bonuses are kept (<c>resourcehunter.json</c>); reads what is there.</summary>
    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _path = path;
            Load();
        }
    }

    /// <summary>Goes up whenever anything shown changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised on the tailing thread the first time a live line shows a zone's bonus after a character enters it: character, server, zone, bonus.</summary>
    public event Action<string, string, string, ResourceBonus>? Found;

    /// <summary>Raised on the tailing thread when a log shows the bonuses being drawn again (server); that day's old bonuses are forgotten first.</summary>
    public event Action<string>? Reset;

    /// <summary>Where a character is (character, server) when its log has had no zone line since the app started.</summary>
    public void SetZoneProvider(Func<string, string, string?> zoneOf) => _zoneOf = zoneOf;

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.StartsWith(ResourceHunterText.ResetPrefix, StringComparison.Ordinal) && serverName.Length > 0)
        {
            ForgetDay(serverName, ResourceHunterText.Day(receivedUtc));
            Reset?.Invoke(serverName);
            return;
        }

        string key = characterName + "|" + serverName;
        if (message.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            if (ZoneTracker.ParseZone(message) is { } entered)
            {
                lock (_gate)
                {
                    _live[key] = (entered, false);
                }
            }

            return;
        }

        if (ResourceHunterText.Parse(message) is not { } bonus || string.IsNullOrEmpty(serverName))
        {
            return;
        }

        string? zone;
        bool tell;
        lock (_gate)
        {
            if (_live.TryGetValue(key, out (string Zone, bool Told) where))
            {
                zone = where.Zone;
                tell = !where.Told;
            }
            else
            {
                zone = _zoneOf?.Invoke(characterName, serverName);
                tell = zone is not null;
            }

            if (zone is null)
            {
                return;
            }

            _live[key] = (zone, true);
            Add(serverName, ResourceHunterText.Day(receivedUtc), zone, bonus);
        }

        if (tell)
        {
            Found?.Invoke(characterName, serverName, zone, bonus);
        }
    }

    /// <summary>Reads what is new in each log (<c>Path</c>, <c>Server</c>) for bonus lines. The first read of a log reads all of it.</summary>
    public int Scan(IEnumerable<(string Path, string Server)> logs, DateTime nowUtc)
    {
        int found = 0;
        foreach ((string path, string server) in logs)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(server) || !File.Exists(path))
            {
                continue;
            }

            long offset;
            string? lastZone;
            lock (_gate)
            {
                offset = _file.Logs.GetValueOrDefault(path);
                lastZone = _file.LogZones.GetValueOrDefault(path);
            }

            try
            {
                (long next, string? zoneAtEnd, List<(string Day, string Zone, ResourceBonus Bonus)> lines) = ReadLog(path, offset, lastZone);
                lock (_gate)
                {
                    _file.Logs[path] = next;
                    if (zoneAtEnd is not null)
                    {
                        _file.LogZones[path] = zoneAtEnd;
                    }

                    foreach ((string day, string zone, ResourceBonus bonus) in lines)
                    {
                        if (Add(server, day, zone, bonus))
                        {
                            found++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // read again next time
            }
        }

        lock (_gate)
        {
            Save();
        }

        return found;
    }

    // The zone a bonus line is about is the zone of the last "You have entered" line before it.
    private static (long Next, string? Zone, List<(string Day, string Zone, ResourceBonus Bonus)> Lines) ReadLog(string path, long offset, string? zone)
    {
        var found = new List<(string, string, ResourceBonus)>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (offset > stream.Length)
        {
            offset = 0; // the log was cut or replaced
            zone = null;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[ChunkSize];
        int carried = 0;
        while (true)
        {
            int read = stream.Read(buffer, carried, buffer.Length - carried);
            if (read <= 0)
            {
                break;
            }

            int length = carried + read;
            int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', length - 1);
            if (lastNewline < 0)
            {
                if (length == buffer.Length)
                {
                    offset += length; // a line longer than the buffer: skip it
                    carried = 0;
                    continue;
                }

                carried = length;
                break;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            foreach (string raw in text.Split('\n'))
            {
                ReadOnlySpan<char> line = raw.AsSpan().TrimEnd('\r');
                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                ReadOnlySpan<char> message = line[LogLineFormat.MessageOffset..];
                if (message.StartsWith("You have entered ", StringComparison.Ordinal))
                {
                    zone = ZoneTracker.ParseZone(message) ?? zone;
                }
                else if (zone is not null && ResourceHunterText.Parse(message) is { } bonus && LogLineFormat.TryParseTimestamp(line, out DateTime stamp))
                {
                    DateTime utc = DateTime.SpecifyKind(stamp, DateTimeKind.Local).ToUniversalTime();
                    found.Add((ResourceHunterText.Day(utc), zone, bonus));
                }
            }

            offset += lastNewline + 1;
            carried = length - (lastNewline + 1);
            Array.Copy(buffer, lastNewline + 1, buffer, 0, carried);
        }

        return (offset, zone, found);
    }

    // Keeps a bonus your logs showed. A zone has one bonus a day: a later line for the same zone and day replaces it. Call under the gate.
    private bool Add(string server, string day, string zone, ResourceBonus bonus)
    {
        string s = server.ToLowerInvariant();
        string key = ResourceHunterText.ZoneKey(zone);
        string bonusKey = ResourceHunterText.Key(bonus);
        Entry? entry = _file.Entries.FirstOrDefault(e => e.Server == s && e.Day == day && ResourceHunterText.ZoneKey(e.Zone) == key);
        if (entry is not null && entry.Bonus == bonusKey)
        {
            return false;
        }

        if (entry is null)
        {
            _file.Entries.Add(new Entry { Server = s, Day = day, Zone = zone, Bonus = bonusKey });
        }
        else
        {
            entry.Bonus = bonusKey;
            entry.Shared = false;
        }

        Interlocked.Increment(ref _version);
        return true;
    }

    /// <summary>Today's bonuses reported by other players (<see cref="ResourceBonusSource.Shared"/>) or a website, replacing that source's earlier list.</summary>
    public void SetReported(string server, ResourceBonusSource source, string day, IEnumerable<ResourceZoneBonus> zones)
    {
        lock (_gate)
        {
            var list = zones.Select(z => z with { Source = source }).ToList();
            _reported[(server.ToLowerInvariant(), source)] = (day, list);
            Record(server, day, list);
            Save();
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Today's bonus for every zone anyone knows of on <paramref name="server"/>: your logs first, then other players, then the website.
    /// </summary>
    public IReadOnlyList<ResourceZoneBonus> Today(string server, DateTime nowUtc) => OnDay(server, ResourceHunterText.Day(nowUtc));

    /// <summary>Today's bonus for one zone, or null when nobody knows it.</summary>
    public ResourceZoneBonus? TodayIn(string server, string zone, DateTime nowUtc)
    {
        string key = ResourceHunterText.ZoneKey(zone);
        return Today(server, nowUtc).FirstOrDefault(z => z.Bonus != ResourceBonus.Unknown && ResourceHunterText.ZoneKey(z.Zone) == key);
    }

    /// <summary>The zones' level ranges today's website list gives (by <see cref="ResourceHunterText.ZoneKey"/>).</summary>
    public IReadOnlyDictionary<string, (int Min, int Max)> WebsiteLevels(string server, DateTime nowUtc)
    {
        var levels = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_reported.TryGetValue((server.ToLowerInvariant(), ResourceBonusSource.Website), out var list) && list.Day == ResourceHunterText.Day(nowUtc))
            {
                foreach (ResourceZoneBonus z in list.Zones.Where(z => z.MaxLevel > 0))
                {
                    levels.TryAdd(ResourceHunterText.ZoneKey(z.Zone), (z.MinLevel, z.MaxLevel));
                }
            }
        }

        return levels;
    }

    /// <summary>How often each bonus (not "unknown") was seen in a zone over the last <paramref name="days"/> days, most often first: every source.</summary>
    public IReadOnlyList<(ResourceBonus Bonus, int Days)> History(string server, string zone, DateTime nowUtc, int days = 30)
    {
        string key = ResourceHunterText.ZoneKey(zone);
        DateTime today = DateTime.ParseExact(ResourceHunterText.Day(nowUtc), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            return Enumerable.Range(0, days)
                .Select(i => BonusOn(server.ToLowerInvariant(), today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), key))
                .Where(b => b is not null && b != ResourceBonus.Unknown)
                .GroupBy(b => b!.Value)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(g => g.Item2)
                .ThenBy(g => g.Key)
                .ToList();
        }
    }

    /// <summary>The servers your logs have seen bonuses on.</summary>
    public IReadOnlyList<string> Servers()
    {
        lock (_gate)
        {
            return _file.Entries.Select(e => e.Server).Distinct().ToList();
        }
    }

    /// <summary>Today's bonuses your logs found on <paramref name="server"/> that have not been shared yet.</summary>
    public IReadOnlyList<ResourceFinding> Unshared(string server, DateTime nowUtc)
    {
        string s = server.ToLowerInvariant();
        string day = ResourceHunterText.Day(nowUtc);
        lock (_gate)
        {
            return _file.Entries.Where(e => e.Server == s && e.Day == day && !e.Shared && ResourceHunterText.FromKey(e.Bonus) is not null)
                .Select(e => new ResourceFinding(e.Zone, ResourceHunterText.FromKey(e.Bonus)!.Value, e.Day)).ToList();
        }
    }

    public void MarkShared(string server, IEnumerable<ResourceFinding> shared)
    {
        string s = server.ToLowerInvariant();
        lock (_gate)
        {
            foreach (ResourceFinding f in shared)
            {
                string key = ResourceHunterText.ZoneKey(f.Zone);
                foreach (Entry e in _file.Entries.Where(e => e.Server == s && e.Day == f.Day && ResourceHunterText.ZoneKey(e.Zone) == key
                             && e.Bonus == ResourceHunterText.Key(f.Bonus)))
                {
                    e.Shared = true;
                }
            }

            Save();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            SavedFile? saved = JsonSerializer.Deserialize<SavedFile>(File.ReadAllText(_path));
            if (saved is not null)
            {
                _file = new SavedFile
                {
                    Logs = new Dictionary<string, long>(saved.Logs ?? new(), StringComparer.OrdinalIgnoreCase),
                    LogZones = new Dictionary<string, string>(saved.LogZones ?? new(), StringComparer.OrdinalIgnoreCase),
                    Entries = saved.Entries ?? new(),
                    Days = saved.Days ?? new(),
                    Names = saved.Names ?? new(),
                    Levels = saved.Levels ?? new(),
                    Fetched = saved.Fetched ?? new(),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _file = new SavedFile(); // a damaged file starts empty; the logs are read again
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_file));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // saved again next time
        }
    }
}
