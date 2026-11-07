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

        return new DpsTimeline(bucket, Cut(_group), new ValueList<TimelineSeries>(series));
    }
}
