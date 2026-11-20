namespace EQOverwatch.Core.Experience;

/// <summary>
/// Notices a server bonus experience event from your own kills: when the last kills (each against the usual experience for the same mob
/// at the same level) are well above the usual, bonus experience looks active; when they drop back, it has ended.
/// </summary>
public sealed class BonusXpDetector
{
    /// <summary>The kills looked at (the latest ones within <see cref="Window"/>).</summary>
    public const int KillsNeeded = 4;

    /// <summary>A kill only counts when its usual is from at least this many earlier kills.</summary>
    public const int UsualKillsNeeded = 3;

    /// <summary>The median ratio that starts it (40% more than usual).</summary>
    public const double StartRatio = 1.4;

    /// <summary>The median ratio under which it ends.</summary>
    public const double EndRatio = 1.15;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly List<(double Ratio, DateTime Time)> _kills = new();

    /// <summary>True while bonus experience looks active.</summary>
    public bool Active { get; private set; }

    /// <summary>The median ratio of the last kills while active (1.5 = 50% more experience), otherwise 0.</summary>
    public double Boost { get; private set; }

    /// <summary>Raised when <see cref="Active"/> changes: true when it starts, false when it ends.</summary>
    public event Action<bool>? Changed;

    public void Observe(double ratio, int usualKills, DateTime time)
    {
        if (usualKills < UsualKillsNeeded || double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0)
        {
            return;
        }

        bool? changed = null;
        lock (_gate)
        {
            _kills.Add((ratio, time));
            _kills.RemoveAll(k => time - k.Time > Window);
            if (_kills.Count > KillsNeeded)
            {
                _kills.RemoveRange(0, _kills.Count - KillsNeeded);
            }

            if (_kills.Count < KillsNeeded)
            {
                return;
            }

            double[] sorted = _kills.Select(k => k.Ratio).OrderBy(r => r).ToArray();
            double median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            if (!Active && median >= StartRatio)
            {
                Active = true;
                changed = true;
            }
            else if (Active && median < EndRatio)
            {
                Active = false;
                changed = false;
            }

            Boost = Active ? median : 0;
        }

        if (changed is { } now)
        {
            Changed?.Invoke(now);
        }
    }
}
