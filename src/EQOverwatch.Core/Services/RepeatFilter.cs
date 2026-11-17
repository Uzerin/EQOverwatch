namespace EQOverwatch.Core.Services;

/// <summary>
/// Counts a line seen in several characters' logs once. Two logs on one PC give the same hit the same second, or one second apart
/// when the lines land either side of a second; anything within <see cref="Window"/> matches. A line from one log that matches a
/// counted line from another log (that this log has not matched yet) is a repeat; lines from the same log never match each other,
/// since two identical hits in one log are two hits. Each counted line can be matched once per other log, nearest time first.
/// </summary>
public sealed class RepeatFilter
{
    /// <summary>How far apart the same line's timestamps may be in two logs.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(10);
    private const int CleanEvery = 4096;

    private sealed class Seen
    {
        public required DateTime Time { get; init; }

        public required string Source { get; init; }

        /// <summary>The other logs that have matched this line.</summary>
        public List<string>? MatchedBy { get; set; }
    }

    private readonly Dictionary<string, List<Seen>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private int _sinceClean;
    private DateTime _newest;

    /// <summary>
    /// True when this line (<paramref name="key"/> at <paramref name="time"/>, from <paramref name="source"/>'s log) was already counted
    /// from another log. With no source it is always counted.
    /// </summary>
    public bool IsRepeat(string key, DateTime time, string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        if (time > _newest)
        {
            _newest = time;
        }

        if (++_sinceClean >= CleanEvery)
        {
            _sinceClean = 0;
            Clean();
        }

        if (!_seen.TryGetValue(key, out List<Seen>? list))
        {
            _seen[key] = list = new List<Seen>();
        }
        else
        {
            list.RemoveAll(s => (time - s.Time).Duration() > KeepFor && s.Time < time);
        }

        Seen? best = null;
        TimeSpan bestGap = TimeSpan.MaxValue;
        foreach (Seen s in list)
        {
            if (s.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                || (s.MatchedBy is { } by && by.Contains(source, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            TimeSpan gap = (time - s.Time).Duration();
            if (gap <= Window && (gap < bestGap || (gap == bestGap && best is not null && s.Time < best.Time)))
            {
                best = s;
                bestGap = gap;
            }
        }

        if (best is not null)
        {
            (best.MatchedBy ??= new List<string>()).Add(source);
            return true;
        }

        list.Add(new Seen { Time = time, Source = source });
        return false;
    }

    public void Clear()
    {
        _seen.Clear();
        _sinceClean = 0;
        _newest = default;
    }

    private void Clean()
    {
        foreach (string key in _seen.Keys.ToList())
        {
            List<Seen> list = _seen[key];
            list.RemoveAll(s => _newest - s.Time > KeepFor);
            if (list.Count == 0)
            {
                _seen.Remove(key);
            }
        }
    }
}
