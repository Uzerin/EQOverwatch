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
public sealed record LevelingScanState(long Offset, string? Zone, int Level, int LegacyBonus);

/// <summary>
/// Reads a character's log into <see cref="LevelingStretch"/>es. Only experience lines that show a percentage count, and only once the
/// character's level is known (from "Welcome to level N!"). Raid experience is left out. Power leveling is left out too: when more than
/// <see cref="BurstGains"/> gains come within <see cref="BurstWindow"/>, those gains (and their time) are dropped, and a stretch whose
/// experience was mostly such bursts is dropped whole.
/// </summary>
public sealed partial class LevelingScanner
{
    private const char CarriageReturn = (char)13;

    public const int BurstGains = 4;
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);

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
        var found = _done.ToList();
        _done.Clear();
        return found;
    }

    private void Flush()
    {
        if (_gains.Count > 0 && _zone is not null && _level > 0)
        {
            foreach (bool group in new[] { false, true })
            {
                List<Gain> gains = _gains.Where(g => g.Group == group).ToList();
                if (Stretch(gains, group) is { } stretch)
                {
                    _done.Add(stretch with { Copper = group ? _groupCopper : _soloCopper, Aa = group ? _groupAa : _soloAa });
                }
            }
        }

        _gains.Clear();
        _soloCopper = _groupCopper = 0;
        _soloAa = _groupAa = 0;
    }

    /// <summary>One stretch from gains of one kind, with power-leveling bursts taken out; null when too little is left.</summary>
    private LevelingStretch? Stretch(List<Gain> gains, bool group)
    {
        if (gains.Count < 2)
        {
            return null;
        }

        bool[] burst = Bursts(gains);
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
        return new LevelingStretch(_zone!, _level, group, Math.Round(seconds, 1), Math.Round(xp, 4), kills, start);
    }

    /// <summary>True for each gain that is part of more than <see cref="BurstGains"/> gains within <see cref="BurstWindow"/>.</summary>
    public static bool[] Bursts(IReadOnlyList<DateTime> times)
    {
        var burst = new bool[times.Count];
        int start = 0;
        for (int end = 0; end < times.Count; end++)
        {
            while (times[end] - times[start] >= BurstWindow)
            {
                start++;
            }

            if (end - start + 1 > BurstGains)
            {
                for (int i = start; i <= end; i++)
                {
                    burst[i] = true;
                }
            }
        }

        return burst;
    }

    private static bool[] Bursts(List<Gain> gains) => Bursts(gains.Select(g => g.Time).ToList());

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
        return (found, new LevelingScanState(lineStart, scanner.Zone, scanner.Level, scanner.LegacyBonus), scanner.TakeLevels());
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
