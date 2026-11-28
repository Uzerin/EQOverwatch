using System.Globalization;
using System.Text.Json;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.History;

/// <summary>One of your characters' DPS in one raid boss fight.</summary>
public sealed record RaidDpsEntry(string Character, string Boss, DateTime Start, double Dps, string? Server = null);

/// <summary>One week of a character's raid boss fights: the Monday it starts, the average DPS and how many fights.</summary>
public sealed record RaidDpsWeek(DateTime WeekStart, double Dps, int Fights);

/// <summary>
/// A character's raid boss DPS week by week (only weeks with fights), with the last 4 weeks' average against the 4 weeks before
/// (0 when there were no fights then).
/// </summary>
public sealed record RaidDpsTrend(IReadOnlyList<RaidDpsWeek> Weeks, double RecentDps, double EarlierDps, int Fights)
{
    public const int WeeksShown = 12;
    public const int CompareWeeks = 4;

    /// <summary>
    /// The weekly averages of <paramref name="character"/>'s fights in the <see cref="WeeksShown"/> weeks up to <paramref name="today"/>;
    /// null when fewer than 2 weeks had fights.
    /// </summary>
    public static RaidDpsTrend? For(IEnumerable<RaidDpsEntry> entries, string character, DateTime today, string? server = null)
    {
        DateTime thisWeek = WeekOf(today);
        DateTime first = thisWeek.AddDays(-7 * (WeeksShown - 1));
        List<RaidDpsEntry> mine = entries
            .Where(e => e.Character.Equals(character, StringComparison.OrdinalIgnoreCase) && e.Dps > 0 && e.Start >= first
                        && (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(e.Server) || e.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        List<RaidDpsWeek> weeks = mine
            .GroupBy(e => WeekOf(e.Start))
            .OrderBy(g => g.Key)
            .Select(g => new RaidDpsWeek(g.Key, g.Average(e => e.Dps), g.Count()))
            .ToList();
        if (weeks.Count < 2)
        {
            return null;
        }

        DateTime recentFrom = thisWeek.AddDays(-7 * (CompareWeeks - 1));
        DateTime earlierFrom = recentFrom.AddDays(-7 * CompareWeeks);
        double Average(IEnumerable<RaidDpsEntry> list) => list.Any() ? list.Average(e => e.Dps) : 0;
        return new RaidDpsTrend(
            weeks,
            Average(mine.Where(e => e.Start >= recentFrom)),
            Average(mine.Where(e => e.Start >= earlierFrom && e.Start < recentFrom)),
            mine.Count);
    }

    /// <summary>The Monday that starts the week of <paramref name="time"/>.</summary>
    public static DateTime WeekOf(DateTime time)
    {
        int back = ((int)time.DayOfWeek + 6) % 7;
        return time.Date.AddDays(-back);
    }

    /// <summary>"Last 4 weeks 780 DPS against 676 the 4 weeks before (+15%) · 53 raid boss fights".</summary>
    public string Describe(CultureInfo culture)
    {
        string fights = $"{Fights.ToString("N0", culture)} raid boss fight{(Fights == 1 ? string.Empty : "s")}";
        if (RecentDps <= 0)
        {
            return $"No raid boss fights in the last {CompareWeeks} weeks · {fights}";
        }

        string recent = $"Last {CompareWeeks} weeks {RecentDps.ToString("N0", culture)} DPS";
        if (EarlierDps <= 0)
        {
            return $"{recent} · {fights}";
        }

        double change = (RecentDps - EarlierDps) / EarlierDps * 100;
        string sign = change >= 0 ? "+" : "−";
        return $"{recent} against {EarlierDps.ToString("N0", culture)} the {CompareWeeks} weeks before ({sign}{Math.Abs(change).ToString("0", culture)}%) · {fights}";
    }
}

/// <summary>
/// Your characters' DPS in each raid boss fight, kept on disk so the profile can show weeks the app no longer loads. Also remembers which
/// recorded boss fights were already read back from the logs for it.
/// </summary>
public sealed class RaidDpsStore
{
    /// <summary>Entries for the same character and boss starting closer together than this are the same fight.</summary>
    public const double SameFightSeconds = 90;

    public const int MaxEntries = 20000;

    private readonly object _gate = new();
    private readonly List<RaidDpsEntry> _entries = new();
    private readonly HashSet<string> _readBack = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;
    private bool _dirty;

    private sealed class SavedFile
    {
        public List<RaidDpsEntry> Entries { get; set; } = new();
        public List<string> ReadBack { get; set; } = new();
    }

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _path = path;
            _entries.Clear();
            _readBack.Clear();
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<SavedFile>(File.ReadAllText(path)) is { } saved)
                {
                    _entries.AddRange(saved.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Character) && !string.IsNullOrWhiteSpace(e.Boss)));
                    _readBack.UnionWith(saved.ReadBack);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged file starts the history again.
            }
        }
    }

    public IReadOnlyList<RaidDpsEntry> Entries()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    /// <summary>
    /// Adds the DPS of each of <paramref name="isOwn"/>'s characters in the finished fights against a boss <paramref name="isRaidBoss"/>
    /// accepts. A fight already on record for the character is replaced (a fuller read of it). Returns how many entries changed.
    /// </summary>
    public int Record(IEnumerable<EncounterSnapshot> fights, Func<string, bool> isRaidBoss, Func<string, bool> isOwn)
    {
        int changed = 0;
        lock (_gate)
        {
            foreach (EncounterSnapshot fight in fights)
            {
                if (fight.IsActive || fight.CombinedCount > 1 || !isRaidBoss(fight.TargetName))
                {
                    continue;
                }

                foreach (ParticipantSnapshot player in fight.Participants)
                {
                    if (player.Dps <= 0 || !isOwn(player.Name))
                    {
                        continue;
                    }

                    var entry = new RaidDpsEntry(player.Name, fight.TargetName.Trim(), fight.StartTime, Math.Round(player.Dps, 1),
                        string.IsNullOrWhiteSpace(fight.Server) ? null : fight.Server);
                    int same = _entries.FindIndex(e => e.Character.Equals(entry.Character, StringComparison.OrdinalIgnoreCase)
                                                       && e.Boss.Equals(entry.Boss, StringComparison.OrdinalIgnoreCase)
                                                       && Math.Abs((e.Start - entry.Start).TotalSeconds) < SameFightSeconds);
                    if (same >= 0)
                    {
                        if (_entries[same] == entry)
                        {
                            continue;
                        }

                        _entries[same] = entry;
                    }
                    else
                    {
                        _entries.Add(entry);
                    }

                    changed++;
                }
            }

            if (changed > 0)
            {
                if (_entries.Count > MaxEntries)
                {
                    _entries.Sort((a, b) => a.Start.CompareTo(b.Start));
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                }

                _dirty = true;
            }
        }

        return changed;
    }

    /// <summary>The key a recorded boss fight is remembered by once it has been read back from the logs.</summary>
    public static string ReadBackKey(BossFight fight) =>
        fight.Boss.Trim() + "|" + fight.Start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public bool WasReadBack(BossFight fight)
    {
        lock (_gate)
        {
            return _readBack.Contains(ReadBackKey(fight));
        }
    }

    public void MarkReadBack(BossFight fight)
    {
        lock (_gate)
        {
            _dirty |= _readBack.Add(ReadBackKey(fight));
        }
    }

    /// <summary>Writes the file when something changed.</summary>
    public void Flush()
    {
        string? path;
        string json;
        lock (_gate)
        {
            if (!_dirty || _path is null)
            {
                return;
            }

            _dirty = false;
            path = _path;
            json = JsonSerializer.Serialize(new SavedFile { Entries = _entries.ToList(), ReadBack = _readBack.ToList() });
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }
}
