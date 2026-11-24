using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Session;

/// <summary>One stretch of a character's day in one zone. <see cref="Zone"/> is empty before the first zone change of the day.</summary>
public sealed record ZoneStretch(string Zone, DateTime Start, DateTime End)
{
    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
}

/// <summary>A character's day so far: time played, experience, money, kills and skill-ups.</summary>
public sealed record SessionSummary(
    string Character, double PlayedSeconds, double XpPercent, long Copper, int Kills, int SkillUps, IReadOnlyList<ZoneStretch> Zones,
    IReadOnlyDictionary<string, (double Xp, int Gains)>? ZoneXp = null, IReadOnlyDictionary<string, long>? ZoneCopper = null)
{
    public double Platinum => Copper / 1000.0;
}

/// <summary>One spell a character cast today: casts begun, fizzles, interrupts and "did not take hold" blocks.</summary>
public sealed record SpellDayRow(string Character, string Spell, int Casts, int Fizzles, int Interrupts, int Blocked, string BlockedBy)
{
    public double FizzlePercent => Casts == 0 ? 0 : 100.0 * Fizzles / Casts;

    public double InterruptPercent => Casts == 0 ? 0 : 100.0 * Interrupts / Casts;
}

/// <summary>One of a character's spells on one mob today: landed and resisted.</summary>
public sealed record ResistDayRow(string Character, string Spell, string Mob, int Landed, int Resisted)
{
    public double ResistPercent => Landed + Resisted == 0 ? 0 : 100.0 * Resisted / (Landed + Resisted);
}

/// <summary>
/// Adds up one character's day from its log lines (oldest first, local log times). Time played counts the gaps between lines up to
/// <see cref="OfflineGap"/>; a longer gap is time away. Kills are mobs the log saw slain. Money is coin looted, split or sold for.
/// </summary>
public sealed partial class DayAccumulator
{
    /// <summary>A gap between lines longer than this is time away, not play.</summary>
    public static readonly TimeSpan OfflineGap = TimeSpan.FromMinutes(5);

    private const string YouBeginCasting = "You begin casting ";
    private static readonly TimeSpan LandingGrace = TimeSpan.FromSeconds(3);

    [GeneratedRegex(@"^You gain(?:ed)? (?:(?:party|raid) )?experience(?: \(with a bonus\))?!(?: \((?<p>\d+(?:\.\d+)?)%\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex XpRegex();

    [GeneratedRegex(@"^(?:You have slain (?<mob>.+)|(?<mob>.+?) (?:was|has been) slain by .+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex SlainRegex();

    [GeneratedRegex(@"(?<n>\d+) (?<coin>platinum|gold|silver|copper)", RegexOptions.CultureInvariant)]
    private static partial Regex CoinRegex();

    [GeneratedRegex(@"^Your (?<spell>.+?) spell (?<what>fizzles!|is interrupted\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex FailRegex();

    [GeneratedRegex(@"^Your (?<spell>.+?) spell did not take hold(?: on .+?)?\. \(Blocked by (?<by>.+?)\.\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TakeHoldRegex();

    [GeneratedRegex(@"^(?<mob>.+?) resisted your (?<spell>.+?)!$", RegexOptions.CultureInvariant)]
    private static partial Regex ResistRegex();

    private sealed class SpellCount
    {
        public int Casts;
        public int Fizzles;
        public int Interrupts;
        public int Blocked;
        public string BlockedBy = string.Empty;
    }

    private readonly Func<string, SpellInfo?> _findSpell;
    private readonly Dictionary<string, SpellCount> _spells = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Spell, string Mob), (int Landed, int Resisted)> _resists = new();
    private readonly List<ZoneStretch> _zones = new();
    private readonly Dictionary<string, (double Xp, int Gains)> _zoneXp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _zoneCopper = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastLine;
    private string _zone = string.Empty;
    private DateTime _stretchStart;
    private SpellInfo? _pending;
    private DateTime _pendingUntil;

    public DayAccumulator(string character, Func<string, SpellInfo?>? findSpell = null)
    {
        Character = character;
        _findSpell = findSpell ?? (_ => null);
    }

    public string Character { get; }

    public double PlayedSeconds { get; private set; }

    public double XpPercent { get; private set; }

    public long Copper { get; private set; }

    public int Kills { get; private set; }

    public int SkillUps { get; private set; }

    /// <summary>Applies one log line (its message, after the timestamp).</summary>
    public void Line(string message, DateTime time)
    {
        Clock(time);
        if (message.Length < 8)
        {
            return;
        }

        if (message.StartsWith("You have entered ", StringComparison.Ordinal) && message[^1] == '.'
            && !message.StartsWith("You have entered an area", StringComparison.Ordinal))
        {
            string zone = message["You have entered ".Length..^1];
            CloseStretch(time);
            _zone = zone;
            _stretchStart = time;
            return;
        }

        if (message[^1] == '!' && message.Contains(" slain ", StringComparison.Ordinal) && SlainRegex().Match(message) is { Success: true } slain)
        {
            if (!Services.PlayerNameHeuristic.CouldBePlayer(slain.Groups["mob"].Value.Trim()))
            {
                Kills++; // a mob died where this character could see it (players and pets dying are not kills)
            }

            return;
        }

        if (message.StartsWith("You gain", StringComparison.Ordinal) && XpRegex().Match(message) is { Success: true } xp)
        {
            if (xp.Groups["p"].Success && double.TryParse(xp.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            {
                XpPercent += p;
                (double zx, int zg) = _zoneXp.GetValueOrDefault(_zone);
                _zoneXp[_zone] = (zx + p, zg + 1);
            }

            return;
        }

        if (message.StartsWith("You have become better at ", StringComparison.Ordinal))
        {
            SkillUps++;
            return;
        }

        if (IsMoneyLine(message))
        {
            long copper = CopperIn(message);
            Copper += copper;
            _zoneCopper[_zone] = _zoneCopper.GetValueOrDefault(_zone) + copper;
            return;
        }

        if ((message.StartsWith(YouBeginCasting, StringComparison.Ordinal) || message.StartsWith("You begin singing ", StringComparison.Ordinal)) && message[^1] == '.')
        {
            string spell = message[YouBeginCasting.Length..^1]; // both prefixes are the same length
            Count(spell).Casts++;
            _pending = _findSpell(spell) is { } info && info.CastOnOtherSuffix.Length > 0 && info.Category == Models.TimerOverlayCategory.Debuffs ? info : null; // only spells a mob can resist
            _pendingUntil = time.AddMilliseconds(_pending?.CastTimeMs ?? 0) + LandingGrace;
            return;
        }

        if (message.StartsWith("Your ", StringComparison.Ordinal))
        {
            if (FailRegex().Match(message) is { Success: true } fail)
            {
                SpellCount count = Count(fail.Groups["spell"].Value);
                if (fail.Groups["what"].Value[0] == 'f')
                {
                    count.Fizzles++;
                }
                else
                {
                    count.Interrupts++;
                }

                _pending = null;
                return;
            }

            if (message[^1] == ')' && TakeHoldRegex().Match(message) is { Success: true } hold)
            {
                SpellCount count = Count(hold.Groups["spell"].Value);
                count.Blocked++;
                count.BlockedBy = hold.Groups["by"].Value;
                return;
            }
        }

        if (message[^1] == '!' && message.Contains(" resisted your ", StringComparison.Ordinal) && ResistRegex().Match(message) is { Success: true } resist)
        {
            var key = (resist.Groups["spell"].Value, resist.Groups["mob"].Value.Trim());
            (int landed, int resisted) = _resists.GetValueOrDefault(key);
            _resists[key] = (landed, resisted + 1);
            _pending = null;
            return;
        }

        if (_pending is { } pending && message[^1] == '.' && time <= _pendingUntil)
        {
            string suffix = pending.CastOnOtherSuffix;
            if (message.Length > suffix.Length && message.EndsWith(suffix, StringComparison.Ordinal))
            {
                string mob = message[..^suffix.Length].Trim();
                if (mob.Length > 0 && !mob.Equals(Character, StringComparison.OrdinalIgnoreCase))
                {
                    var key = (pending.Name, mob);
                    (int landed, int resisted) = _resists.GetValueOrDefault(key);
                    _resists[key] = (landed + 1, resisted);
                }

                _pending = null;
            }
        }
    }

    private void Clock(DateTime time)
    {
        if (_lastLine == default)
        {
            _lastLine = time;
            _stretchStart = time;
            return;
        }

        TimeSpan gap = time - _lastLine;
        if (gap < TimeSpan.Zero)
        {
            return;
        }

        if (gap <= OfflineGap)
        {
            PlayedSeconds += gap.TotalSeconds;
        }
        else
        {
            CloseStretch(_lastLine);
            _stretchStart = time;
        }

        _lastLine = time;
    }

    private void CloseStretch(DateTime end)
    {
        if (_stretchStart != default && end > _stretchStart)
        {
            _zones.Add(new ZoneStretch(_zone, _stretchStart, end));
        }

        _stretchStart = end;
    }

    private SpellCount Count(string spell)
    {
        if (!_spells.TryGetValue(spell, out SpellCount? count))
        {
            _spells[spell] = count = new SpellCount();
        }

        return count;
    }

    /// <summary>True for a line about coin you got: looted, your split, or a sale to a merchant.</summary>
    public static bool IsMoneyLine(string message) =>
        (message.StartsWith("You receive ", StringComparison.Ordinal) || message.StartsWith("Alive group members received ", StringComparison.Ordinal))
        && (message.Contains(" platinum", StringComparison.Ordinal) || message.Contains(" gold", StringComparison.Ordinal)
            || message.Contains(" silver", StringComparison.Ordinal) || message.Contains(" copper", StringComparison.Ordinal))
        && (message.Contains(" from ", StringComparison.Ordinal) || message.Contains(" split", StringComparison.Ordinal));

    /// <summary>The coin a line names, in copper (1 platinum = 1000).</summary>
    public static long CopperIn(string message)
    {
        long total = 0;
        foreach (Match m in CoinRegex().Matches(message))
        {
            if (!long.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            {
                continue;
            }

            total += m.Groups["coin"].Value switch
            {
                "platinum" => n * 1000,
                "gold" => n * 100,
                "silver" => n * 10,
                _ => n,
            };
        }

        return total;
    }

    /// <summary>The day so far; <paramref name="now"/> (local) ends the current zone stretch while the character is still playing.</summary>
    public SessionSummary Summary(DateTime now)
    {
        var zones = new List<ZoneStretch>(_zones);
        if (_stretchStart != default && _lastLine > _stretchStart)
        {
            DateTime end = now - _lastLine <= OfflineGap && now > _lastLine ? now : _lastLine;
            zones.Add(new ZoneStretch(_zone, _stretchStart, end));
        }

        // A few seconds before the first "You have entered" at log-in is not worth a block of its own.
        zones.RemoveAll(z => z.Seconds < 30 && zones.Count > 1);
        return new SessionSummary(Character, PlayedSeconds, XpPercent, Copper, Kills, SkillUps, zones, new Dictionary<string, (double, int)>(_zoneXp),
            new Dictionary<string, long>(_zoneCopper));
    }

    public IReadOnlyList<SpellDayRow> Spells() =>
        _spells.Select(p => new SpellDayRow(Character, p.Key, p.Value.Casts, p.Value.Fizzles, p.Value.Interrupts, p.Value.Blocked, p.Value.BlockedBy))
            .OrderByDescending(r => r.Casts).ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ResistDayRow> Resists() =>
        _resists.Select(p => new ResistDayRow(Character, p.Key.Spell, p.Key.Mob, p.Value.Landed, p.Value.Resisted))
            .OrderByDescending(r => r.Resisted).ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Mob, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Reads the last <see cref="WeekDays"/> days of each log (from local midnight six days ago) into one <see cref="DayAccumulator"/> per
/// day, then only the new lines on each <see cref="Update"/>. A new day drops the oldest.
/// </summary>
public sealed class DayScanner
{
    public const int WeekDays = 7;

    private sealed class LogState
    {
        public required string Character;
        public DateTime Start;
        public long Offset;
        public Dictionary<DateTime, DayAccumulator> Days = new();
    }

    private readonly Dictionary<string, LogState> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, SpellInfo?> _findSpell;
    private readonly object _gate = new();
    private DateTime _today;

    public DayScanner(Func<string, SpellInfo?>? findSpell = null) => _findSpell = findSpell ?? (_ => null);

    /// <summary>Reads what each log added since the last call (the last seven days only). Logs no longer listed are dropped.</summary>
    public void Update(IEnumerable<(string Path, string Character)> logs, DateTime nowLocal, CancellationToken cancellation = default)
    {
        DateTime today = nowLocal.Date;
        DateTime start = today.AddDays(-(WeekDays - 1));
        lock (_gate)
        {
            _today = today;
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, string character) in logs)
            {
                listed.Add(path);
                if (!_logs.TryGetValue(path, out LogState? state))
                {
                    _logs[path] = state = new LogState { Character = character, Start = start, Offset = -1 };
                }
                else if (state.Start != start)
                {
                    state.Start = start; // a new day: the oldest day goes, the rest is kept
                    foreach (DateTime old in state.Days.Keys.Where(d => d < start).ToList())
                    {
                        state.Days.Remove(old);
                    }
                }

                Read(path, state, cancellation);
            }

            foreach (string gone in _logs.Keys.Where(k => !listed.Contains(k)).ToList())
            {
                _logs.Remove(gone);
            }
        }
    }

    private void Read(string path, LogState state, CancellationToken cancellation)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (state.Offset < 0)
            {
                state.Offset = LogSeek.FindOffset(stream, state.Start);
            }

            if (stream.Length < state.Offset)
            {
                state.Offset = 0; // the log was cut down or replaced
            }

            stream.Position = state.Offset;
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            var sb = new StringBuilder();
            long consumed = state.Offset;
            long lineStart = state.Offset;
            int c;
            int count = 0;
            DayAccumulator? acc = null;
            DateTime accDay = default;
            while ((c = reader.Read()) >= 0)
            {
                consumed++; // Latin-1: one byte per character
                if (c != '\n')
                {
                    sb.Append((char)c);
                    continue;
                }

                if ((++count & 0x3FFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                string line = sb.ToString().TrimEnd('\r');
                sb.Clear();
                lineStart = consumed;
                if (line.Length <= LogLineFormat.MessageOffset || !LogLineFormat.TryParseTimestamp(line, out DateTime stamp) || stamp < state.Start)
                {
                    continue;
                }

                if (acc is null || stamp.Date != accDay)
                {
                    accDay = stamp.Date;
                    if (!state.Days.TryGetValue(accDay, out acc))
                    {
                        state.Days[accDay] = acc = new DayAccumulator(state.Character, _findSpell);
                    }
                }

                acc.Line(line[LogLineFormat.MessageOffset..], stamp);
            }

            state.Offset = lineStart; // a line still being written is read next time
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // read again next time
        }
    }

    private static bool Played(SessionSummary s) => s.PlayedSeconds > 0 || s.Kills > 0 || s.Copper > 0 || s.SkillUps > 0 || s.XpPercent > 0;

    private IEnumerable<DayAccumulator> Today() =>
        _logs.Values.Select(s => s.Days.GetValueOrDefault(_today)).OfType<DayAccumulator>();

    public IReadOnlyList<SessionSummary> Sessions(DateTime nowLocal)
    {
        lock (_gate)
        {
            return Today().Select(a => a.Summary(nowLocal))
                .Where(Played)
                .GroupBy(s => s.Character, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.PlayedSeconds).First())
                .OrderByDescending(s => s.PlayedSeconds)
                .ToList();
        }
    }

    /// <summary>Each character's numbers for each of the last seven days they played, newest day first.</summary>
    public IReadOnlyList<(DateTime Day, SessionSummary Summary)> Week(DateTime nowLocal)
    {
        lock (_gate)
        {
            return _logs.Values
                .SelectMany(s => s.Days.Select(d => (Day: d.Key, Summary: d.Value.Summary(d.Key == nowLocal.Date ? nowLocal : d.Key.AddDays(1)))))
                .Where(d => Played(d.Summary))
                .GroupBy(d => (d.Day, d.Summary.Character.ToUpperInvariant()))
                .Select(g => g.OrderByDescending(d => d.Summary.PlayedSeconds).First())
                .OrderByDescending(d => d.Day).ThenByDescending(d => d.Summary.PlayedSeconds)
                .ToList();
        }
    }

    /// <summary>
    /// Experience per zone over the last seven days, all characters together: the XP % gained, how many gains, and the hours played there
    /// (character-hours), for zones that gave experience. Best XP per hour first.
    /// </summary>
    public IReadOnlyList<(string Zone, double Xp, int Gains, double Hours)> ZoneXp(DateTime nowLocal)
    {
        var totals = new Dictionary<string, (double Xp, int Gains, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach ((DateTime _, SessionSummary s) in Week(nowLocal))
        {
            foreach (ZoneStretch z in s.Zones.Where(z => z.Zone.Length > 0))
            {
                (double x, int g, double sec) = totals.GetValueOrDefault(z.Zone);
                totals[z.Zone] = (x, g, sec + z.Seconds);
            }

            foreach ((string zone, (double xp, int gains)) in s.ZoneXp ?? new Dictionary<string, (double, int)>())
            {
                if (zone.Length == 0)
                {
                    continue;
                }

                (double x, int g, double sec) = totals.GetValueOrDefault(zone);
                totals[zone] = (x + xp, g + gains, sec);
            }
        }

        return totals.Where(t => t.Value.Xp > 0 && t.Value.Seconds >= 60)
            .Select(t => (t.Key, t.Value.Xp, t.Value.Gains, t.Value.Seconds / 3600))
            .OrderByDescending(t => t.Item2 / t.Item4)
            .ToList();
    }

    /// <summary>
    /// Platinum looted per zone over the last seven days, all characters together, with the hours played there (character-hours).
    /// Most platinum first.
    /// </summary>
    public IReadOnlyList<(string Zone, double Platinum, double Hours)> ZonePlat(DateTime nowLocal)
    {
        var totals = new Dictionary<string, (long Copper, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach ((DateTime _, SessionSummary s) in Week(nowLocal))
        {
            foreach (ZoneStretch z in s.Zones.Where(z => z.Zone.Length > 0))
            {
                (long c, double sec) = totals.GetValueOrDefault(z.Zone);
                totals[z.Zone] = (c, sec + z.Seconds);
            }

            foreach ((string zone, long copper) in s.ZoneCopper ?? new Dictionary<string, long>())
            {
                if (zone.Length > 0)
                {
                    (long c, double sec) = totals.GetValueOrDefault(zone);
                    totals[zone] = (c + copper, sec);
                }
            }
        }

        return totals.Where(t => t.Value.Copper > 0)
            .Select(t => (t.Key, t.Value.Copper / 1000.0, t.Value.Seconds / 3600))
            .OrderByDescending(t => t.Item2)
            .ToList();
    }

    public IReadOnlyList<SpellDayRow> Spells()
    {
        lock (_gate)
        {
            return Today().SelectMany(a => a.Spells()).ToList();
        }
    }

    public IReadOnlyList<ResistDayRow> Resists()
    {
        lock (_gate)
        {
            return Today().SelectMany(a => a.Resists()).ToList();
        }
    }

    /// <summary>Landed and resisted per spell and mob over the last seven days, all characters together (character left empty).</summary>
    public IReadOnlyList<ResistDayRow> WeekResists()
    {
        lock (_gate)
        {
            return _logs.Values.SelectMany(s => s.Days.Values).SelectMany(a => a.Resists())
                .GroupBy(r => (r.Spell.ToUpperInvariant(), r.Mob.ToUpperInvariant()))
                .Select(g => new ResistDayRow(string.Empty, g.First().Spell, g.First().Mob, g.Sum(r => r.Landed), g.Sum(r => r.Resisted)))
                .ToList();
        }
    }
}
