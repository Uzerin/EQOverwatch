using System.Globalization;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Progress;

namespace EQOverwatch.Core.Leveling;

/// <summary>
/// A stretch of leveling: one character, one zone, one level, solo or in a group. <see cref="Xp"/> is percent of a level with the legacy
/// bonus taken out; <see cref="Seconds"/> is the time spent earning it (gaps between gains, each counted up to five minutes);
/// <see cref="Start"/> is the first gain in Unix seconds (UTC). No character names: these are what is shared.
/// </summary>
public sealed record LevelingStretch(string Zone, int Level, bool Group, double Seconds, double Xp, int Kills, long Start, long Copper = 0, int Aa = 0);

/// <summary>A level the character reached (Unix seconds, UTC), for the level history chart.</summary>
public sealed record LevelEvent(long Time, int Level);

/// <summary>The scanner's place in one log, so the next scan carries on from it.</summary>
public sealed record LevelingScanState(long Offset, string? Zone, int Level, int LegacyBonus, long TaintedUntil = 0);

/// <summary>
/// Reads a character's log into <see cref="LevelingStretch"/>es. Only experience lines that show a percentage count, and only once the
/// character's level is known (from "Welcome to level N!"). Raid experience is left out. Power leveling is left out too: a burst is more
/// than <see cref="BurstGains"/> gains within <see cref="BurstWindow"/>, or more than <see cref="LongBurstGains"/> within
/// <see cref="LongBurstWindow"/> (a real group, mercenaries included, never gets there). Every gain from <see cref="BurstMargin"/>
/// before a burst to <see cref="BurstMargin"/> after it is dropped (and its time), across level-ups, and a stretch whose experience was
/// mostly such gains is dropped whole. How much a single kill gives does not matter (Resource Hunter bonuses can be large).
/// </summary>
public sealed partial class LevelingScanner
{
    private const char CarriageReturn = (char)13;

    public const int BurstGains = 8;
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(30);

    public const int LongBurstGains = 12;
    public static readonly TimeSpan LongBurstWindow = TimeSpan.FromSeconds(60);

    /// <summary>Gains this close before or after a burst are part of the power leveling too.</summary>
    public static readonly TimeSpan BurstMargin = TimeSpan.FromMinutes(5);

    /// <summary>A gap between gains counts as leveling time up to this long (longer: away, or between pulls for a long time).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);

    /// <summary>A gap this long ends the stretch.</summary>
    public static readonly TimeSpan BreakGap = TimeSpan.FromMinutes(30);

    /// <summary>Stretches shorter than this (after bursts are dropped) are left out.</summary>
    public static readonly TimeSpan MinTime = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"^You gain(?:ed)? (?:(?<kind>party|raid) )?experience(?: \(with a bonus\))?!(?: \((?<p>\d+(?:\.\d+)?)%\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex XpLine();

    [GeneratedRegex(@"^You have gained (?:a level|\d+ levels)! Welcome to level (?<level>\d+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelLine();

    [GeneratedRegex(@"^You have lost a level! Welcome back to level (?<level>\d+)!?$", RegexOptions.CultureInvariant)]
    private static partial Regex LostLevelLine();

    private readonly record struct Gain(DateTime Time, double Xp, bool Group);

    private const string AbilityPoint = "You have gained an ability point!";

    private readonly List<Gain> _gains = new();
    private readonly List<DateTime> _recent = new(); // every gain in the last minute, solo and group, for spotting bursts
    private readonly List<(DateTime From, DateTime To)> _tainted = new();
    private readonly List<Pending> _pending = new();
    private DateTime _taintedUntil;

    // A stretch's gains wait until BurstMargin has passed after its last gain, in case a burst follows.
    private sealed record Pending(string Zone, int Level, List<Gain> Gains, long SoloCopper, long GroupCopper, int SoloAa, int GroupAa);
    private readonly List<LevelingStretch> _done = new();
    private readonly List<LevelEvent> _levels = new();
    private long _soloCopper;
    private long _groupCopper;
    private int _soloAa;
    private int _groupAa;
    private string? _zone;
    private int _level;
    private int _legacy;

    public LevelingScanner(LevelingScanState? state = null)
    {
        _zone = state?.Zone;
        _level = state?.Level ?? 0;
        _legacy = state?.LegacyBonus ?? 0;
        _taintedUntil = state is { TaintedUntil: > 0 } ? DateTimeOffset.FromUnixTimeSeconds(state.TaintedUntil).LocalDateTime : DateTime.MinValue;
    }

    /// <summary>The end of the last power-leveling margin (Unix seconds), carried into the next scan of the log.</summary>
    public long TaintedUntil
    {
        get
        {
            DateTime until = _tainted.Count > 0 ? _tainted.Max(t => t.To) : DateTime.MinValue;
            if (_taintedUntil > until)
            {
                until = _taintedUntil;
            }

            return until == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Local)).ToUnixTimeSeconds();
        }
    }

    public string? Zone => _zone;
    public int Level => _level;
    public int LegacyBonus => _legacy;

    /// <summary>Levels reached in what was read (and forgets them).</summary>
    public IReadOnlyList<LevelEvent> TakeLevels()
    {
        var levels = _levels.ToList();
        _levels.Clear();
        return levels;
    }

    private bool LastGainWasGroup => _gains.Count > 0 && _gains[^1].Group;

    /// <summary>Reads one line's message at its log time.</summary>
    public void Feed(DateTime time, ReadOnlySpan<char> message)
    {
        if (message.Length < 12)
        {
            return;
        }

        if (_pending.Count > 0 && time - _pending[0].Gains[^1].Time > BurstMargin)
        {
            FinishPending(time);
        }

        if (message[0] == 'Y' && message.StartsWith("You gain", StringComparison.Ordinal))
        {
            Match xp = XpLine().Match(message.ToString());
            if (!xp.Success || xp.Groups["kind"].Value == "raid" || _level <= 0 || string.IsNullOrEmpty(_zone))
            {
                return;
            }

            if (_gains.Count > 0 && time - _gains[^1].Time >= BreakGap)
            {
                Flush();
            }

            // No percentage: at the level cap, where kills give AA experience only. The kill and its time still count (for AA mode).
            double percent = xp.Groups["p"].Success ? double.Parse(xp.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
            _gains.Add(new Gain(time, percent / (1 + (_legacy / 100.0)), xp.Groups["kind"].Value == "party"));
            NoteBurst(time);
            return;
        }

        if (message.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            if (ZoneTracker.ParseZone(message) is { } zone && !zone.Equals(_zone, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                _zone = zone;
            }

            return;
        }

        if (message.StartsWith("You have gained ", StringComparison.Ordinal) || message.StartsWith("You have lost a level", StringComparison.Ordinal))
        {
            Match level = LevelLine().Match(message.ToString());
            if (!level.Success)
            {
                level = LostLevelLine().Match(message.ToString());
            }

            if (level.Success && int.TryParse(level.Groups["level"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n != _level)
            {
                Flush();
                _level = n;
                _levels.Add(new LevelEvent(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local)).ToUnixTimeSeconds(), n));
            }
            else if (message.StartsWith(AbilityPoint, StringComparison.Ordinal) && _gains.Count > 0)
            {
                if (LastGainWasGroup)
                {
                    _groupAa++;
                }
                else
                {
                    _soloAa++;
                }
            }

            return;
        }

        if ((message[0] == 'Y' || message[0] == 'A') && _gains.Count > 0)
        {
            string text = message.ToString();
            if (Session.DayAccumulator.IsMoneyLine(text))
            {
                long copper = Session.DayAccumulator.CopperIn(text);
                if (LastGainWasGroup)
                {
                    _groupCopper += copper;
                }
                else
                {
                    _soloCopper += copper;
                }

                return;
            }
        }

        if (message.StartsWith("Your legacy", StringComparison.Ordinal) && ProgressStore.LegacyBonus(message.ToString()) is { } bonus)
        {
            _legacy = bonus;
        }
    }

    /// <summary>Ends the stretch in progress and returns every stretch found so far (and forgets them).</summary>
    public IReadOnlyList<LevelingStretch> Finish()
    {
        Flush();
        FinishPending(DateTime.MaxValue);
        var found = _done.ToList();
        _done.Clear();
        return found;
    }

    private void Flush()
    {
        if (_gains.Count > 0 && _zone is not null && _level > 0)
        {
            _pending.Add(new Pending(_zone, _level, _gains.ToList(), _soloCopper, _groupCopper, _soloAa, _groupAa));
        }

        _gains.Clear();
        _soloCopper = _groupCopper = 0;
        _soloAa = _groupAa = 0;
    }

    // Notes a burst ending at this gain: more than BurstGains in BurstWindow, or LongBurstGains in LongBurstWindow.
    private void NoteBurst(DateTime time)
    {
        _recent.Add(time);
        _recent.RemoveAll(t => time - t > LongBurstWindow);
        int inShort = _recent.Count(t => time - t <= BurstWindow);
        if (inShort > BurstGains || _recent.Count > LongBurstGains)
        {
            DateTime from = inShort > BurstGains ? _recent.First(t => time - t <= BurstWindow) : _recent[0];
            _tainted.Add((from - BurstMargin, time + BurstMargin));
        }
    }

    private bool IsTainted(DateTime time) => time <= _taintedUntil || _tainted.Any(t => time >= t.From && time <= t.To);

    // Turns the stretches whose margin has passed (all of them at the end) into stretches.
    private void FinishPending(DateTime now)
    {
        while (_pending.Count > 0 && (now == DateTime.MaxValue || now - _pending[0].Gains[^1].Time > BurstMargin))
        {
            Pending p = _pending[0];
            _pending.RemoveAt(0);
            foreach (bool group in new[] { false, true })
            {
                List<Gain> gains = p.Gains.Where(g => g.Group == group).ToList();
                if (Stretch(gains, group, p.Zone, p.Level) is { } stretch)
                {
                    _done.Add(stretch with { Copper = group ? p.GroupCopper : p.SoloCopper, Aa = group ? p.GroupAa : p.SoloAa });
                }
            }
        }

        if (_pending.Count == 0 && now != DateTime.MaxValue)
        {
            // Margins nothing still waiting can fall in: before the stretch in progress, and long enough ago.
            DateTime oldest = _gains.Count > 0 && _gains[0].Time < now - BurstMargin ? _gains[0].Time : now - BurstMargin;
            _tainted.RemoveAll(t => t.To < oldest);
        }
    }

    /// <summary>One stretch from gains of one kind, with power leveling taken out; null when too little is left.</summary>
    private LevelingStretch? Stretch(List<Gain> gains, bool group, string zone, int level)
    {
        if (gains.Count < 2)
        {
            return null;
        }

        bool[] burst = gains.Select(g => IsTainted(g.Time)).ToArray();
        double total = gains.Sum(g => g.Xp);
        double burstXp = gains.Where((_, i) => burst[i]).Sum(g => g.Xp);
        bool mostlyBursts = total > 0 ? burstXp > total / 2 : burst.Count(b => b) > gains.Count / 2;
        if (mostlyBursts)
        {
            return null; // mostly power leveling
        }

        double seconds = 0;
        double xp = 0;
        int kills = 0;
        for (int i = 0; i < gains.Count; i++)
        {
            if (burst[i])
            {
                continue;
            }

            xp += gains[i].Xp;
            kills++;
            if (i > 0 && !burst[i - 1])
            {
                seconds += Math.Min(MaxGap.TotalSeconds, (gains[i].Time - gains[i - 1].Time).TotalSeconds);
            }
        }

        if (seconds < MinTime.TotalSeconds || kills < 2)
        {
            return null;
        }

        long start = new DateTimeOffset(DateTime.SpecifyKind(gains[0].Time, DateTimeKind.Local)).ToUnixTimeSeconds();
        return new LevelingStretch(zone, level, group, Math.Round(seconds, 1), Math.Round(xp, 4), kills, start);
    }

    /// <summary>
    /// True for each gain in power leveling: in a burst (more than <see cref="BurstGains"/> within <see cref="BurstWindow"/>, or more than
    /// <see cref="LongBurstGains"/> within <see cref="LongBurstWindow"/>) or within <see cref="BurstMargin"/> of one.
    /// </summary>
    public static bool[] Bursts(IReadOnlyList<DateTime> times)
    {
        var scanner = new LevelingScanner();
        foreach (DateTime t in times)
        {
            scanner.NoteBurst(t);
        }

        return times.Select(scanner.IsTainted).ToArray();
    }

    /// <summary>
    /// Reads a log from <paramref name="state"/>'s offset to the end (shared, so EQ can keep writing). Returns the stretches found and
    /// where to carry on next time. A line cut off at the end is read next time.
    /// </summary>
    public static (IReadOnlyList<LevelingStretch> Stretches, LevelingScanState State) ScanLog(string path, LevelingScanState? state)
    {
        (IReadOnlyList<LevelingStretch> stretches, LevelingScanState next, _) = ScanLogWithLevels(path, state);
        return (stretches, next);
    }

    /// <summary>The same, with the levels reached in what was read.</summary>
    public static (IReadOnlyList<LevelingStretch> Stretches, LevelingScanState State, IReadOnlyList<LevelEvent> Levels) ScanLogWithLevels(string path, LevelingScanState? state)
    {
        var scanner = new LevelingScanner(state);
        long offset = state?.Offset ?? 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (offset > stream.Length)
        {
            offset = 0; // the log was cut or replaced: read it again
            scanner = new LevelingScanner();
        }

        stream.Position = offset;
        var buffer = new byte[1 << 20];
        var carry = new List<byte>();
        long lineStart = offset;
        long position = offset;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int from = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                string line;
                if (carry.Count > 0)
                {
                    carry.AddRange(new ArraySegment<byte>(buffer, from, i - from));
                    line = System.Text.Encoding.UTF8.GetString(carry.ToArray());
                    carry.Clear();
                }
                else
                {
                    line = System.Text.Encoding.UTF8.GetString(buffer, from, i - from);
                }

                position += (i - from) + 1;
                from = i + 1;
                lineStart = position;
                Read(scanner, line.TrimEnd(CarriageReturn));
            }

            if (from < read)
            {
                carry.AddRange(new ArraySegment<byte>(buffer, from, read - from));
                position += read - from;
            }
        }

        // a last line without its newline is half-written: it is read next time
        IReadOnlyList<LevelingStretch> found = scanner.Finish();
        return (found, new LevelingScanState(lineStart, scanner.Zone, scanner.Level, scanner.LegacyBonus, scanner.TaintedUntil), scanner.TakeLevels());
    }

    private static void Read(LevelingScanner scanner, string line)
    {
        if (LogLineFormat.TryParseTimestamp(line, out DateTime time))
        {
            int close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0)
            {
                scanner.Feed(time, line.AsSpan(close + 2));
            }
        }
    }
}
