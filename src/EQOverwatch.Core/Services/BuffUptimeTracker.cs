using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Services;

/// <summary>How long one tracked spell was up on one target during a fight.</summary>
/// <param name="Target">Who the spell was on (a monitored character, or the mob being fought for a debuff).</param>
/// <param name="Spell">The spell's name.</param>
/// <param name="Seconds">Seconds of the fight the spell was up.</param>
/// <param name="Percent">That as a share of the fight (0 to 100).</param>
public sealed record SpellUptimeRow(string Target, string Spell, double Seconds, double Percent);

/// <summary>
/// Remembers when each tracked spell was up on each target (from <see cref="BuffTrackerService.Landed"/> and
/// <see cref="BuffTrackerService.Faded"/>), so a fight can be asked how much of it a buff or debuff covered. A spell is up from when it
/// lands until it fades or its base duration runs out; landing again while it is up extends it. Fights are compared in local time, the
/// time the log lines carry. Thread-safe.
/// </summary>
public sealed class BuffUptimeTracker
{
    private const int MaxWindows = 5000;

    private sealed record Window(string Target, string Spell, DateTime Start, DateTime End);

    private readonly object _gate = new();
    private readonly List<Window> _windows = new();
    private readonly Dictionary<(string Target, string Spell), Window> _open = new();

    public BuffUptimeTracker(BuffTrackerService buffs)
    {
        ArgumentNullException.ThrowIfNull(buffs);
        buffs.Landed += OnLanded;
        buffs.Faded += OnFaded;
    }

    /// <summary>A tracker with no source, for tests and callers that feed it by hand.</summary>
    public BuffUptimeTracker()
    {
    }

    public void OnLanded(string target, string spell, int durationSeconds, DateTime when)
    {
        if (durationSeconds <= 0 || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(spell))
        {
            return;
        }

        DateTime start = ToLocal(when);
        DateTime end = start.AddSeconds(durationSeconds);
        var key = (target.Trim().ToLowerInvariant(), spell.Trim().ToLowerInvariant());
        lock (_gate)
        {
            if (_open.TryGetValue(key, out Window? current) && current.End >= start)
            {
                Window extended = current with { End = end }; // landed again while up: one unbroken stretch
                Replace(current, extended);
                _open[key] = extended;
                return;
            }

            var window = new Window(target.Trim(), spell.Trim(), start, end);
            _windows.Add(window);
            _open[key] = window;
            if (_windows.Count > MaxWindows)
            {
                _windows.RemoveRange(0, _windows.Count - MaxWindows);
            }
        }
    }

    public void OnFaded(string target, string spell, DateTime when)
    {
        DateTime end = ToLocal(when);
        var key = (target.Trim().ToLowerInvariant(), spell.Trim().ToLowerInvariant());
        lock (_gate)
        {
            if (_open.TryGetValue(key, out Window? current))
            {
                if (end < current.End)
                {
                    Replace(current, current with { End = end < current.Start ? current.Start : end });
                }

                _open.Remove(key);
            }
        }
    }

    /// <summary>How long each tracked spell was up during <paramref name="fight"/>, longest first. Empty when none was.</summary>
    public IReadOnlyList<SpellUptimeRow> Uptime(EncounterSnapshot fight)
    {
        ArgumentNullException.ThrowIfNull(fight);
        double length = Math.Max(1.0, fight.DurationSeconds);
        DateTime from = fight.StartTime;
        DateTime to = fight.StartTime.AddSeconds(fight.DurationSeconds);

        List<Window> snapshot;
        lock (_gate)
        {
            snapshot = _windows.ToList();
        }

        return snapshot
            .Select(w => (Window: w, Seconds: Overlap(w, from, to)))
            .Where(x => x.Seconds > 0)
            .GroupBy(x => (x.Window.Target.ToLowerInvariant(), x.Window.Spell.ToLowerInvariant()))
            .Select(g =>
            {
                double seconds = Math.Min(length, g.Sum(x => x.Seconds));
                return new SpellUptimeRow(g.First().Window.Target, g.First().Window.Spell, seconds, seconds / length * 100.0);
            })
            .OrderByDescending(r => r.Seconds)
            .ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double Overlap(Window w, DateTime from, DateTime to)
    {
        DateTime start = w.Start > from ? w.Start : from;
        DateTime end = w.End < to ? w.End : to;
        return end > start ? (end - start).TotalSeconds : 0.0;
    }

    private void Replace(Window old, Window replacement)
    {
        int index = _windows.IndexOf(old);
        if (index >= 0)
        {
            _windows[index] = replacement;
        }
    }

    private static DateTime ToLocal(DateTime when) => when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when;
}
