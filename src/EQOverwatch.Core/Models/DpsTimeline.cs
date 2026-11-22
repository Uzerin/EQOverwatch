namespace EQOverwatch.Core.Models;

/// <summary>A read-only list whose equality is by content, so snapshots holding one still compare equal.</summary>
public sealed class ValueList<T> : System.Collections.ObjectModel.ReadOnlyCollection<T>
{
    public ValueList(IList<T> items)
        : base(items)
    {
    }

    public override bool Equals(object? obj) => obj is ValueList<T> other && this.SequenceEqual(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (T item in this)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A moment marked on the graph: a death (the name) or a spell the fight's target cast.</summary>
public sealed record TimelineMark(double Seconds, string Label);

/// <summary>One line on the DPS-over-time graph: a player's damage per second in each time bucket.</summary>
public sealed record TimelineSeries(string Name, ValueList<double> Dps);

/// <summary>
/// Damage per second across a fight, in equal time buckets: the whole group's line plus the top damage dealers'.
/// </summary>
public sealed record DpsTimeline(double BucketSeconds, ValueList<double> Group, ValueList<TimelineSeries> Players)
{
    /// <summary>The most player lines drawn.</summary>
    public const int MaxPlayers = 5;

    /// <summary>Number of buckets.</summary>
    public int Count => Group.Count;

    /// <summary>The fight's length in seconds (0 when unknown: the buckets' span is used).</summary>
    public double DurationSeconds { get; init; }

    /// <summary>The seconds the time axis runs over.</summary>
    public double Span => DurationSeconds > 0 ? DurationSeconds : Count * BucketSeconds;

    /// <summary>Damage the raid took per second in each bucket (the incoming graph), or null when none was recorded.</summary>
    public ValueList<double>? Incoming { get; init; }

    /// <summary>Healing landed per second in each bucket (drawn with the incoming graph), or null when none was recorded.</summary>
    public ValueList<double>? Healing { get; init; }

    /// <summary>Every attacker's damage in each bucket (not per second), for replaying the fight's table at any point.</summary>
    public ValueList<TimelineSeries>? Damage { get; init; }

    /// <summary>The boss's estimated health left (0 to 1) at the end of each bucket, or null when its hit points are unknown.</summary>
    public ValueList<double>? BossHp { get; init; }

    /// <summary>
    /// The boss's health through the fight from its estimated <paramref name="hitPoints"/> and <paramref name="regenPerSecond"/>:
    /// hit points, less the damage dealt so far, plus what it regenerated.
    /// </summary>
    public DpsTimeline WithBossHp(long hitPoints, double regenPerSecond)
    {
        if (hitPoints <= 0 || Count == 0)
        {
            return this with { BossHp = null };
        }

        var values = new double[Count];
        double dealt = 0;
        for (int i = 0; i < Count; i++)
        {
            double end = Math.Min(Span, (i + 1) * BucketSeconds);
            double length = Math.Max(0, end - (i * BucketSeconds));
            dealt += Group[i] * length;
            values[i] = Math.Clamp((hitPoints - dealt + (regenPerSecond * end)) / hitPoints, 0, 1);
        }

        return this with { BossHp = new ValueList<double>(values) };
    }

    /// <summary>The buckets where the group's DPS ran at least <paramref name="factor"/> times the fight's average (burn phases).</summary>
    public IReadOnlyList<(int From, int To)> Burns(double factor = 1.5)
    {
        var spans = new List<(int, int)>();
        double total = 0, seconds = 0;
        for (int i = 0; i < Count; i++)
        {
            double length = Math.Max(0, Math.Min(Span, (i + 1) * BucketSeconds) - (i * BucketSeconds));
            total += Group[i] * length;
            seconds += length;
        }

        double average = seconds > 0 ? total / seconds : 0;
        if (average <= 0)
        {
            return spans;
        }

        int start = -1;
        for (int i = 0; i <= Count; i++)
        {
            bool hot = i < Count && Group[i] >= average * factor;
            if (hot && start < 0)
            {
                start = i;
            }
            else if (!hot && start >= 0)
            {
                spans.Add((start, i - 1));
                start = -1;
            }
        }

        return spans;
    }

    /// <summary>Each attacker's damage and DPS from the start of the fight to <paramref name="seconds"/> in, highest first.</summary>
    public IReadOnlyList<(string Name, long Damage, double Dps)> Standings(double seconds)
    {
        if (Damage is not { Count: > 0 } damage || Count == 0)
        {
            return Array.Empty<(string, long, double)>();
        }

        seconds = Math.Clamp(seconds, 0, Span);
        int whole = (int)Math.Floor(seconds / BucketSeconds);
        double part = (seconds / BucketSeconds) - whole;
        var rows = new List<(string, long, double)>(damage.Count);
        foreach (TimelineSeries series in damage)
        {
            double sum = 0;
            for (int i = 0; i < whole && i < series.Dps.Count; i++)
            {
                sum += series.Dps[i];
            }

            if (whole < series.Dps.Count)
            {
                sum += series.Dps[whole] * part;
            }

            long total = (long)Math.Round(sum);
            if (total > 0)
            {
                rows.Add((series.Name, total, seconds > 0 ? total / seconds : 0));
            }
        }

        return rows.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>When someone died, with their name.</summary>
    public ValueList<TimelineMark>? Deaths { get; init; }

    /// <summary>When the fight's target began casting, with the spell.</summary>
    public ValueList<TimelineMark>? Casts { get; init; }

    /// <summary>The highest value on any line (what the vertical axis must reach).</summary>
    public double Peak => Math.Max(Group.Count == 0 ? 0.0 : Group.Max(), Players.Count == 0 ? 0.0 : Players.Max(p => p.Dps.Count == 0 ? 0.0 : p.Dps.Max()));
}

/// <summary>Accumulates damage per second of the fight, then cuts it into a <see cref="DpsTimeline"/>.</summary>
internal sealed class TimelineRecorder
{
    private const int MaxSeconds = 4 * 3600;
    private const int TargetBuckets = 60;
    private readonly Dictionary<string, List<long>> _byPlayer = new(StringComparer.Ordinal);
    private readonly List<long> _group = new();
    private readonly List<long> _incoming = new();
    private readonly List<long> _healing = new();

    /// <summary>Healing landed, for the line drawn with the incoming graph.</summary>
    public void RecordHealing(int secondsIn, int amount)
    {
        if (secondsIn is >= 0 and < MaxSeconds)
        {
            Add(_healing, secondsIn, amount);
        }
    }

    /// <summary>Damage a player or pet took, for the incoming graph.</summary>
    public void RecordIncoming(int secondsIn, int amount)
    {
        if (secondsIn is >= 0 and < MaxSeconds)
        {
            Add(_incoming, secondsIn, amount);
        }
    }

    public void Record(string player, int secondsIn, int amount)
    {
        if (secondsIn < 0)
        {
            secondsIn = 0;
        }

        if (secondsIn >= MaxSeconds)
        {
            return;
        }

        Add(_group, secondsIn, amount);
        if (!_byPlayer.TryGetValue(player, out List<long>? list))
        {
            list = new List<long>();
            _byPlayer.Add(player, list);
        }

        Add(list, secondsIn, amount);
    }

    private static void Add(List<long> list, int index, int amount)
    {
        while (list.Count <= index)
        {
            list.Add(0);
        }

        list[index] += amount;
    }

    /// <summary>The timeline, or null when the fight was too short or too quiet to draw.</summary>
    public DpsTimeline? Build(double durationSeconds, IReadOnlyList<string> topPlayers)
    {
        int total = (int)Math.Max(Math.Ceiling(durationSeconds), _group.Count);
        if (total < 6 || _group.Count == 0)
        {
            return null;
        }

        int bucket = Math.Max(2, (int)Math.Ceiling(total / (double)TargetBuckets));
        int count = (int)Math.Ceiling(total / (double)bucket);

        ValueList<double> Sum(List<long> source)
        {
            var values = new double[count];
            for (int s = 0; s < source.Count && s < total; s++)
            {
                values[s / bucket] += source[s];
            }

            return new ValueList<double>(values);
        }

        ValueList<double> Cut(List<long> source)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                int from = i * bucket;
                int to = Math.Min(total, from + bucket);
                long sum = 0;
                for (int s = from; s < to && s < source.Count; s++)
                {
                    sum += source[s];
                }

                values[i] = to > from ? sum / (double)(to - from) : 0.0;
            }

            return new ValueList<double>(values);
        }

        var series = new List<TimelineSeries>();
        foreach (string name in topPlayers.Take(DpsTimeline.MaxPlayers))
        {
            if (_byPlayer.TryGetValue(name, out List<long>? list))
            {
                series.Add(new TimelineSeries(name, Cut(list)));
            }
        }

        return new DpsTimeline(bucket, Cut(_group), new ValueList<TimelineSeries>(series))
        {
            Incoming = _incoming.Count == 0 ? null : Cut(_incoming),
            Healing = _healing.Count == 0 ? null : Cut(_healing),
            Damage = new ValueList<TimelineSeries>(_byPlayer.Select(p => new TimelineSeries(p.Key, Sum(p.Value))).ToArray()),
        };
    }
}
