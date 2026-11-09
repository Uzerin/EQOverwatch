using System.Text.Json;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Resists;

/// <summary>How many of one resist type's spells landed on a mob and how many were resisted.</summary>
public readonly record struct ResistCount(int Landed, int Resisted)
{
    public int Total => Landed + Resisted;

    /// <summary>Resisted as a percentage of every landed or resisted spell; 0 when there were none.</summary>
    public double Percent => Total == 0 ? 0 : Resisted * 100.0 / Total;

    public static ResistCount operator +(ResistCount a, ResistCount b) => new(a.Landed + b.Landed, a.Resisted + b.Resisted);
}

/// <summary>One of your characters' spells on a mob: one resist type's counts.</summary>
public sealed record OwnResistEntry(string Mob, string Caster, int Type, int Landed, int Resisted, DateTime LastUtc);

/// <summary>Another player's shared numbers for one mob (their newest share).</summary>
public sealed record ResistShare(string Mob, string Player, DateTime SharedUtc, Dictionary<int, ResistCount> Counts);

/// <summary>A stretch of one character's log (log clock) whose resist lines have already been counted.</summary>
public sealed record CoveredSpan(string Character, DateTime From, DateTime To);

/// <summary>One contributor to a mob's raid figure: who, when, their numbers per type, and the types left out as outliers.</summary>
public sealed record ResistContributor(string Player, DateTime? SharedUtc, IReadOnlyDictionary<int, ResistCount> Counts, IReadOnlyCollection<int> LeftOut, bool IsYou);

/// <summary>A mob's combined resist figures per type and who they came from.</summary>
public sealed record MobResistReport(string Mob, IReadOnlyDictionary<int, ResistCount> ByType, IReadOnlyList<ResistContributor> Contributors);

/// <summary>
/// Resist counts per mob and resist type: your characters' own (from their logs, live and read in) and the numbers other players shared
/// in raid or guild chat. Saved to a JSON file. Thread-safe. Changes are written by <see cref="Flush"/>.
/// <para>
/// The raid figure for a type adds up you and everyone who shared. When at least <see cref="MinContributorsForOutliers"/> contributors have
/// numbers for a type, anyone whose resist % for it is more than <see cref="OutlierPoints"/> points from the median is left out of that type.
/// </para>
/// </summary>
public sealed class MobResistStore
{
    public const int MinContributorsForOutliers = 3;
    public const double OutlierPoints = 30;

    /// <summary>Shares older than this are dropped.</summary>
    public static readonly TimeSpan ShareLifetime = TimeSpan.FromDays(90);

    private sealed class Saved
    {
        public List<OwnResistEntry> Own { get; set; } = new();
        public List<SavedShare> Shares { get; set; } = new();
        public List<CoveredSpan> Covered { get; set; } = new();
    }

    private sealed class SavedShare
    {
        public string Mob { get; set; } = string.Empty;
        public string Player { get; set; } = string.Empty;
        public DateTime SharedUtc { get; set; }
        public Dictionary<string, int[]> Counts { get; set; } = new();
    }

    private readonly object _gate = new();

    // mob → caster → type → counts
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, ResistCount>>> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mobNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _ownLast = new(StringComparer.OrdinalIgnoreCase);

    // mob → player → share
    private readonly Dictionary<string, Dictionary<string, ResistShare>> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(DateTime From, DateTime To)>> _covered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _liveSpan = new(StringComparer.OrdinalIgnoreCase);
    private string? _filePath;
    private bool _dirty;
    private long _version;

    public event Action? Changed;

    /// <summary>Changes whenever a count or share changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void SetStoragePath(string path, DateTime nowUtc)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved)
                {
                    foreach (OwnResistEntry entry in saved.Own)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Mob) && !string.IsNullOrWhiteSpace(entry.Caster) && SpellResist.IsResistable(entry.Type))
                        {
                            AddOwnLocked(entry.Mob, entry.Caster, entry.Type, new ResistCount(Math.Max(0, entry.Landed), Math.Max(0, entry.Resisted)), entry.LastUtc);
                        }
                    }

                    foreach (SavedShare share in saved.Shares)
                    {
                        var counts = new Dictionary<int, ResistCount>();
                        foreach ((string key, int[] pair) in share.Counts)
                        {
                            if (int.TryParse(key, out int type) && SpellResist.IsResistable(type) && pair is { Length: 2 })
                            {
                                counts[type] = new ResistCount(Math.Max(0, pair[0]), Math.Max(0, pair[1]));
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(share.Mob) && !string.IsNullOrWhiteSpace(share.Player) && nowUtc - share.SharedUtc <= ShareLifetime)
                        {
                            PutShareLocked(new ResistShare(share.Mob, share.Player, share.SharedUtc, counts));
                        }
                    }

                    foreach (CoveredSpan span in saved.Covered)
                    {
                        AddCoveredLocked(span.Character, span.From, span.To);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Bump();
    }

    /// <summary>Counts one of your characters' spells landing on (or being resisted by) a mob.</summary>
    public void AddOwn(string mob, string caster, int type, bool resisted, DateTime whenUtc)
    {
        if (string.IsNullOrWhiteSpace(mob) || string.IsNullOrWhiteSpace(caster) || !SpellResist.IsResistable(type))
        {
            return;
        }

        lock (_gate)
        {
            AddOwnLocked(mob.Trim(), caster.Trim(), type, resisted ? new ResistCount(0, 1) : new ResistCount(1, 0), whenUtc);
            _dirty = true;
        }

        Bump();
    }

    /// <summary>Keeps a player's newest share for a mob (an older one from the same player is replaced; one older than what is kept is ignored).</summary>
    public bool ApplyShare(ResistShare share)
    {
        if (string.IsNullOrWhiteSpace(share.Mob) || string.IsNullOrWhiteSpace(share.Player))
        {
            return false;
        }

        lock (_gate)
        {
            if (_shares.TryGetValue(share.Mob, out Dictionary<string, ResistShare>? byPlayer)
                && byPlayer.TryGetValue(share.Player, out ResistShare? known) && known.SharedUtc > share.SharedUtc)
            {
                return false;
            }

            PutShareLocked(share);
            _dirty = true;
        }

        Bump();
        return true;
    }

    /// <summary>True when <paramref name="logTime"/> of <paramref name="character"/>'s log has already been counted.</summary>
    public bool IsCovered(string character, DateTime logTime)
    {
        lock (_gate)
        {
            return _covered.TryGetValue(character, out List<(DateTime From, DateTime To)>? spans)
                && spans.Any(s => logTime >= s.From && logTime <= s.To);
        }
    }

    /// <summary>Marks a stretch of a character's log as counted (after reading it in).</summary>
    public void AddCovered(string character, DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(character) || to < from)
        {
            return;
        }

        lock (_gate)
        {
            AddCoveredLocked(character, from, to);
            _liveSpan.Remove(character);
            _dirty = true;
        }
    }

    /// <summary>Notes that a live line at <paramref name="logTime"/> of the character's log was seen, so reading that log later skips it.</summary>
    public void TouchLive(string character, DateTime logTime)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        lock (_gate)
        {
            if (!_covered.TryGetValue(character, out List<(DateTime From, DateTime To)>? spans))
            {
                spans = new List<(DateTime From, DateTime To)>();
                _covered[character] = spans;
            }

            if (_liveSpan.TryGetValue(character, out int index) && index < spans.Count && logTime >= spans[index].From)
            {
                if (logTime > spans[index].To)
                {
                    spans[index] = (spans[index].From, logTime);
                    _dirty = true;
                }

                return;
            }

            spans.Add((logTime, logTime));
            _liveSpan[character] = spans.Count - 1;
            _dirty = true;
        }
    }

    /// <summary>Every mob with your own numbers or a share, by name.</summary>
    public IReadOnlyList<string> Mobs()
    {
        lock (_gate)
        {
            return _mobNames.Values.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Mobs with your own numbers, the most recently updated first.</summary>
    public IReadOnlyList<string> MobsByRecentOwn()
    {
        lock (_gate)
        {
            return _ownLast.OrderByDescending(p => p.Value).Select(p => _mobNames.GetValueOrDefault(p.Key, p.Key)).ToList();
        }
    }

    /// <summary>Your characters' numbers for a mob, per type.</summary>
    public IReadOnlyDictionary<int, ResistCount> Own(string mob)
    {
        lock (_gate)
        {
            return OwnLocked(mob);
        }
    }

    /// <summary>
    /// The mob's figures. Mine: your characters only. Raid: you plus every share, with outliers left out per type.
    /// </summary>
    public MobResistReport Report(string mob, bool raid)
    {
        lock (_gate)
        {
            Dictionary<int, ResistCount> own = OwnLocked(mob);
            string name = _mobNames.GetValueOrDefault(mob, mob);
            if (!raid)
            {
                var you = new List<ResistContributor>();
                if (own.Count > 0)
                {
                    you.Add(new ResistContributor("Your characters", null, own, Array.Empty<int>(), true));
                }

                return new MobResistReport(name, own, you);
            }

            var people = new List<(string Player, DateTime? When, Dictionary<int, ResistCount> Counts, bool IsYou)>();
            if (own.Count > 0)
            {
                people.Add(("Your characters", null, own, true));
            }

            if (_shares.TryGetValue(mob, out Dictionary<string, ResistShare>? byPlayer))
            {
                foreach (ResistShare share in byPlayer.Values.OrderByDescending(s => s.SharedUtc))
                {
                    people.Add((share.Player, share.SharedUtc, share.Counts, false));
                }
            }

            var leftOut = people.ToDictionary(p => p.Player, _ => new List<int>(), StringComparer.OrdinalIgnoreCase);
            var totals = new Dictionary<int, ResistCount>();
            foreach (int type in SpellResist.All)
            {
                var withType = people.Where(p => p.Counts.TryGetValue(type, out ResistCount c) && c.Total > 0).ToList();
                if (withType.Count == 0)
                {
                    continue;
                }

                double median = withType.Count >= MinContributorsForOutliers ? Median(withType.Select(p => p.Counts[type].Percent)) : double.NaN;
                ResistCount sum = default;
                foreach (var person in withType)
                {
                    ResistCount count = person.Counts[type];
                    if (!double.IsNaN(median) && Math.Abs(count.Percent - median) > OutlierPoints)
                    {
                        leftOut[person.Player].Add(type);
                        continue;
                    }

                    sum += count;
                }

                if (sum.Total > 0)
                {
                    totals[type] = sum;
                }
            }

            return new MobResistReport(
                name, totals,
                people.Select(p => new ResistContributor(p.Player, p.When, p.Counts, leftOut[p.Player], p.IsYou)).ToList());
        }
    }

    /// <summary>How many players shared numbers for the mob.</summary>
    public int ShareCount(string mob)
    {
        lock (_gate)
        {
            return _shares.TryGetValue(mob, out Dictionary<string, ResistShare>? byPlayer) ? byPlayer.Count : 0;
        }
    }

    /// <summary>Drops every count and share.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _own.Clear();
            _ownLast.Clear();
            _shares.Clear();
            _mobNames.Clear();
            _covered.Clear();
            _liveSpan.Clear();
            _dirty = true;
        }

        Bump();
        Flush();
    }

    /// <summary>Writes the file if anything changed since the last write.</summary>
    public void Flush()
    {
        string? path;
        Saved saved;
        lock (_gate)
        {
            if (!_dirty || _filePath is null)
            {
                return;
            }

            _dirty = false;
            path = _filePath;
            saved = new Saved
            {
                Own = _own.SelectMany(m => m.Value.SelectMany(c => c.Value.Select(t =>
                        new OwnResistEntry(_mobNames.GetValueOrDefault(m.Key, m.Key), c.Key, t.Key, t.Value.Landed, t.Value.Resisted, _ownLast.GetValueOrDefault(m.Key)))))
                    .ToList(),
                Shares = _shares.Values.SelectMany(p => p.Values).Select(s => new SavedShare
                {
                    Mob = s.Mob,
                    Player = s.Player,
                    SharedUtc = s.SharedUtc,
                    Counts = s.Counts.ToDictionary(c => c.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), c => new[] { c.Value.Landed, c.Value.Resisted }),
                }).ToList(),
                Covered = _covered.SelectMany(c => c.Value.Select(s => new CoveredSpan(c.Key, s.From, s.To))).ToList(),
            };
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true; // try again next time
            }
        }
    }

    private void AddOwnLocked(string mob, string caster, int type, ResistCount add, DateTime whenUtc)
    {
        _mobNames.TryAdd(mob, mob);
        if (!_own.TryGetValue(mob, out Dictionary<string, Dictionary<int, ResistCount>>? byCaster))
        {
            byCaster = new Dictionary<string, Dictionary<int, ResistCount>>(StringComparer.OrdinalIgnoreCase);
            _own[mob] = byCaster;
        }

        if (!byCaster.TryGetValue(caster, out Dictionary<int, ResistCount>? byType))
        {
            byType = new Dictionary<int, ResistCount>();
            byCaster[caster] = byType;
        }

        byType[type] = byType.GetValueOrDefault(type) + add;
        if (whenUtc > _ownLast.GetValueOrDefault(mob))
        {
            _ownLast[mob] = whenUtc;
        }
    }

    private void PutShareLocked(ResistShare share)
    {
        _mobNames.TryAdd(share.Mob, share.Mob);
        if (!_shares.TryGetValue(share.Mob, out Dictionary<string, ResistShare>? byPlayer))
        {
            byPlayer = new Dictionary<string, ResistShare>(StringComparer.OrdinalIgnoreCase);
            _shares[share.Mob] = byPlayer;
        }

        byPlayer[share.Player] = share;
    }

    private void AddCoveredLocked(string character, DateTime from, DateTime to)
    {
        if (!_covered.TryGetValue(character, out List<(DateTime From, DateTime To)>? spans))
        {
            spans = new List<(DateTime From, DateTime To)>();
            _covered[character] = spans;
        }

        spans.Add((from, to));
    }

    private Dictionary<int, ResistCount> OwnLocked(string mob)
    {
        var totals = new Dictionary<int, ResistCount>();
        if (_own.TryGetValue(mob, out Dictionary<string, Dictionary<int, ResistCount>>? byCaster))
        {
            foreach (Dictionary<int, ResistCount> byType in byCaster.Values)
            {
                foreach ((int type, ResistCount count) in byType)
                {
                    totals[type] = totals.GetValueOrDefault(type) + count;
                }
            }
        }

        return totals;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private void Bump()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }
}
