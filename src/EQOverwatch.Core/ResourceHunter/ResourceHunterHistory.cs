using System.Globalization;

namespace EQOverwatch.Core.ResourceHunter;

/// <summary>A bonus's share of a zone's days: "Loot, 12 days, 8.3%".</summary>
public sealed record ResourceShare(ResourceBonus Bonus, int Days, double Percent);

// The kept history of every zone's bonus: each day the websites and other players reported, plus your logs. Nothing is removed.
public sealed partial class ResourceHunterStore
{
    /// <summary>The first day the history is filled from (the tracking websites start in 2026).</summary>
    public const string HistoryStart = "2026-05-01";

    // Keeps a reported list in the day's history: a known bonus replaces an unknown one, never the other way. Call under the gate.
    private void Record(string server, string day, IEnumerable<ResourceZoneBonus> zones)
    {
        string s = server.ToLowerInvariant();
        Dictionary<string, string> bonuses = Child(Child(_file.Days, s), day);
        Dictionary<string, string> names = Child(_file.Names, s);
        Dictionary<string, int[]> levels = Child(_file.Levels, s);
        foreach (ResourceZoneBonus z in zones)
        {
            string key = ResourceHunterText.ZoneKey(z.Zone);
            if (key.Length == 0)
            {
                continue;
            }

            names.TryAdd(key, z.Zone);
            if (z.MaxLevel > 0)
            {
                levels[key] = new[] { z.MinLevel, z.MaxLevel };
            }

            if (z.Bonus != ResourceBonus.Unknown || !bonuses.ContainsKey(key))
            {
                bonuses[key] = ResourceHunterText.Key(z.Bonus);
            }
        }
    }

    private static TValue Child<TValue>(Dictionary<string, TValue> parent, string key) where TValue : new()
    {
        if (!parent.TryGetValue(key, out TValue? child))
        {
            child = new TValue();
            parent[key] = child;
        }

        return child;
    }

    /// <summary>Keeps a past day's whole website list and marks the day as read (saved every few days and by <see cref="Flush"/>).</summary>
    public void RecordWebsiteDay(string server, string day, IReadOnlyList<ResourceZoneBonus> zones, bool save)
    {
        lock (_gate)
        {
            Record(server, day, zones);
            List<string> fetched = Child(_file.Fetched, server.ToLowerInvariant());
            if (!fetched.Contains(day))
            {
                fetched.Add(day);
            }

            if (save)
            {
                Save();
            }
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>Writes the history to disk now.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            Save();
        }
    }

    /// <summary>The past days (from <see cref="HistoryStart"/> to the day before <paramref name="nowUtc"/>) not yet read from the website, oldest first.</summary>
    public IReadOnlyList<string> MissingDays(string server, DateTime nowUtc)
    {
        DateTime first = DateTime.ParseExact(HistoryStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime today = DateTime.ParseExact(ResourceHunterText.Day(nowUtc), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            HashSet<string> fetched = _file.Fetched.TryGetValue(server.ToLowerInvariant(), out List<string>? list) ? list.ToHashSet() : new HashSet<string>();
            var missing = new List<string>();
            for (DateTime d = first; d < today; d = d.AddDays(1))
            {
                string day = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!fetched.Contains(day))
                {
                    missing.Add(day);
                }
            }

            return missing;
        }
    }

    /// <summary>The days anything is known for on a server, newest first.</summary>
    public IReadOnlyList<string> KnownDays(string server)
    {
        string s = server.ToLowerInvariant();
        lock (_gate)
        {
            IEnumerable<string> days = _file.Entries.Where(e => e.Server == s).Select(e => e.Day);
            if (_file.Days.TryGetValue(s, out var byDay))
            {
                days = days.Concat(byDay.Where(d => d.Value.Count > 0).Select(d => d.Key));
            }

            return days.Distinct().OrderByDescending(d => d, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Every zone known on <paramref name="server"/> for one Pacific day: your logs first, then (today) other players and the website,
    /// then the kept history. Each zone appears once, with the level range when known.
    /// </summary>
    public IReadOnlyList<ResourceZoneBonus> OnDay(string server, string day)
    {
        string s = server.ToLowerInvariant();
        var byZone = new Dictionary<string, ResourceZoneBonus>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (Entry e in _file.Entries.Where(e => e.Server == s && e.Day == day))
            {
                if (ResourceHunterText.FromKey(e.Bonus) is { } bonus)
                {
                    byZone.TryAdd(ResourceHunterText.ZoneKey(e.Zone), new ResourceZoneBonus(e.Zone, bonus, ResourceBonusSource.Logs));
                }
            }

            foreach (ResourceBonusSource source in new[] { ResourceBonusSource.Shared, ResourceBonusSource.Website })
            {
                if (_reported.TryGetValue((s, source), out var list) && list.Day == day)
                {
                    foreach (ResourceZoneBonus z in list.Zones)
                    {
                        string key = ResourceHunterText.ZoneKey(z.Zone);
                        if (byZone.TryGetValue(key, out ResourceZoneBonus? had) && had.Bonus == ResourceBonus.Unknown && z.Bonus != ResourceBonus.Unknown)
                        {
                            byZone[key] = z;
                        }
                        else
                        {
                            byZone.TryAdd(key, z);
                        }
                    }
                }
            }

            if (_file.Days.TryGetValue(s, out var days) && days.TryGetValue(day, out var kept))
            {
                foreach ((string key, string bonusKey) in kept)
                {
                    if (ResourceHunterText.FromKey(bonusKey) is { } bonus && !byZone.ContainsKey(key))
                    {
                        byZone[key] = new ResourceZoneBonus(NameOf(s, key), bonus, ResourceBonusSource.Website);
                    }
                }
            }

            Dictionary<string, int[]>? levels = _file.Levels.GetValueOrDefault(s);
            foreach (string key in byZone.Keys.ToList())
            {
                if (levels?.GetValueOrDefault(key) is [int min, int max] && byZone[key].MaxLevel == 0)
                {
                    byZone[key] = byZone[key] with { MinLevel = min, MaxLevel = max };
                }
            }
        }

        return byZone.Values.OrderBy(z => z.Bonus == ResourceBonus.None).ThenBy(z => z.Bonus).ThenBy(z => z.Zone, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string NameOf(string server, string key) =>
        _file.Names.TryGetValue(server, out var names) && names.TryGetValue(key, out string? name) ? name : key;

    /// <summary>A zone's bonus on a Pacific day from every source (your logs first), or null when nothing is known.</summary>
    public ResourceBonus? BonusOnDay(string server, string day, string zone)
    {
        lock (_gate)
        {
            return BonusOn(server.ToLowerInvariant(), day, ResourceHunterText.ZoneKey(zone));
        }
    }

    // A zone's bonus on a day from every source (your logs first), or null when nothing is known. Call under the gate.
    private ResourceBonus? BonusOn(string server, string day, string key)
    {
        foreach (Entry e in _file.Entries)
        {
            if (e.Server == server && e.Day == day && ResourceHunterText.ZoneKey(e.Zone) == key && ResourceHunterText.FromKey(e.Bonus) is { } logged)
            {
                return logged;
            }
        }

        return _file.Days.TryGetValue(server, out var days) && days.TryGetValue(day, out var kept) && kept.TryGetValue(key, out string? bonusKey)
            ? ResourceHunterText.FromKey(bonusKey)
            : null;
    }

    /// <summary>
    /// How often a zone had each bonus from the first day anything is known on the server through today; days nothing is known for count as
    /// <see cref="ResourceBonus.Unknown"/>. Most days first. Also returns the first day and the number of days.
    /// </summary>
    public (IReadOnlyList<ResourceShare> Shares, string From, int Days) Breakdown(string server, string zone, DateTime nowUtc)
    {
        string s = server.ToLowerInvariant();
        string key = ResourceHunterText.ZoneKey(zone);
        string todayText = ResourceHunterText.Day(nowUtc);
        IReadOnlyList<string> known = KnownDays(server);
        if (known.Count == 0)
        {
            return (Array.Empty<ResourceShare>(), todayText, 0);
        }

        DateTime first = DateTime.ParseExact(known[^1], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime today = DateTime.ParseExact(todayText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var counts = new Dictionary<ResourceBonus, int>();
        int total = 0;
        lock (_gate)
        {
            for (DateTime d = first; d <= today; d = d.AddDays(1))
            {
                string day = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ResourceBonus bonus = BonusOn(s, day, key) ?? ResourceBonus.Unknown;
                counts[bonus] = counts.GetValueOrDefault(bonus) + 1;
                total++;
            }
        }

        var shares = counts.Select(c => new ResourceShare(c.Key, c.Value, 100.0 * c.Value / total))
            .OrderByDescending(c => c.Days).ThenBy(c => c.Bonus).ToList();
        return (shares, known[^1], total);
    }
}
