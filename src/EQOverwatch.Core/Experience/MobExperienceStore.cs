using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Experience;

/// <summary>One row of the Experience by mob table: what one mob gave one character at one level.</summary>
/// <param name="Level">The character's level when the kills happened, or 0 when it was not known.</param>
/// <param name="Mob">The mob's name, or empty for gains with no kill line next to them.</param>
public sealed record MobExperienceRow(int Level, string Mob, int Kills, double TotalPercent, DateTime LastKillUtc)
{
    /// <summary>Percent of a level per kill (gains with no percent count as 0%).</summary>
    public double AveragePercent => Kills == 0 ? 0 : TotalPercent / Kills;
}

/// <summary>A character that has experience by mob, with the server of its log.</summary>
public sealed record MobExperienceCharacter(string Character, string Server);

/// <summary>Saved form of one row.</summary>
public sealed class MobExperienceEntry
{
    public string Server { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Mob { get; set; } = string.Empty;
    public int Kills { get; set; }
    public double Percent { get; set; }
    public DateTime LastUtc { get; set; }
}

/// <summary>Saved form of the whole store.</summary>
public sealed class MobExperienceFile
{
    public List<MobExperienceEntry> Entries { get; set; } = new();

    /// <summary>"server|character" of every log whose history has been read.</summary>
    public List<string> Scanned { get; set; } = new();
}

/// <summary>
/// Experience by mob, per character, server and character level. Each experience line ("You gain party experience! (7.800%)") is matched
/// with the kill line from the same second a few lines away ("Orc pawn was slain by Jannder!", "You have slain orc pawn!"); a kill line
/// after the experience line is preferred, then one just before it. A gain with no kill line beside it is kept as an unknown mob rather
/// than guessed. Live lines are counted as they arrive, and <see cref="ScanLog"/> rebuilds a character from its whole log.
/// </summary>
public sealed partial class MobExperienceStore : ITriggerLineSink
{
    /// <summary>How many lines apart an experience line and its kill line may be.</summary>
    public const int LineWindow = 8;

    private static readonly TimeSpan LiveTolerance = TimeSpan.FromSeconds(1.5);

    [GeneratedRegex(@"^You gain(?:ed)? (?:(?:party|raid) )?experience(?: \(with a bonus\))?!(?: \((?<p>\d+(?:\.\d+)?)%\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex XpLine();

    [GeneratedRegex(@"^(?:You have slain (?<mob>.+)|(?<mob>.+?) (?:was|has been) slain by .+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex KillLine();

    [GeneratedRegex(@"^You have gained (?:a level|(?<count>\d+) levels)! Welcome to level (?<level>\d+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelLine();

    [GeneratedRegex(@"^\[(?<level>\d+) [^\]]*\] (?<name>[A-Za-z]+)(?: |$)", RegexOptions.CultureInvariant)]
    private static partial Regex WhoLine();

    private sealed record Kill(string Mob, DateTime Time, long Line)
    {
        public bool Claimed { get; set; }
    }

    private sealed record Gain(double Percent, int Level, DateTime Time, long Line, Kill? Before);

    /// <summary>Matches experience lines to kill lines for one log. Not thread-safe on its own.</summary>
    private sealed class Matcher
    {
        private readonly TimeSpan _tolerance;
        private readonly Action<Gain, string> _record;
        private Gain? _gain;
        private Kill? _kill;
        private long _line;

        public Matcher(TimeSpan tolerance, Action<Gain, string> record)
        {
            _tolerance = tolerance;
            _record = record;
        }

        public void Expire(DateTime time)
        {
            if (_gain is not null && (_line - _gain.Line > LineWindow || (time - _gain.Time).Duration() > _tolerance))
            {
                Resolve();
            }
        }

        /// <summary>Counts a line that is neither a gain nor a kill.</summary>
        public void Other(DateTime time)
        {
            _line++;
            Expire(time);
        }

        public void OnKill(string mob, DateTime time)
        {
            _line++;
            Expire(time);
            var kill = new Kill(mob, time, _line);
            if (_gain is not null)
            {
                kill.Claimed = true;
                _record(_gain, mob);
                _gain = null;
            }

            _kill = kill;
        }

        private int _levelUpFrom;
        private long _levelUpLine;
        private DateTime _levelUpTime;

        /// <summary>
        /// A level-up line. EQ writes it just before the experience line of the kill that caused it, so that next gain is counted at
        /// <paramref name="previousLevel"/>, the level the character had when it made the kill.
        /// </summary>
        public void LevelUp(int previousLevel, DateTime time)
        {
            _line++;
            Expire(time);
            _levelUpFrom = previousLevel;
            _levelUpLine = _line;
            _levelUpTime = time;
        }

        public void OnGain(double percent, int level, DateTime time)
        {
            _line++;
            Expire(time);
            if (_gain is not null)
            {
                Resolve();
            }

            if (_levelUpFrom > 0 && _line - _levelUpLine <= LineWindow && (time - _levelUpTime).Duration() <= _tolerance)
            {
                level = _levelUpFrom;
            }

            _levelUpFrom = 0;

            Kill? before = _kill is { Claimed: false } k && _line - k.Line <= LineWindow && (time - k.Time).Duration() <= _tolerance ? k : null;
            _gain = new Gain(percent, level, time, _line, before);
        }

        /// <summary>Settles a waiting gain: the unclaimed kill just before it, or an unknown mob.</summary>
        public void Resolve()
        {
            if (_gain is not { } gain)
            {
                return;
            }

            _gain = null;
            if (gain.Before is { Claimed: false } before)
            {
                before.Claimed = true;
                _record(gain, before.Mob);
            }
            else
            {
                _record(gain, string.Empty);
            }
        }
    }

    private sealed class Row
    {
        public string Display = string.Empty;
        public int Kills;
        public double Percent;
        public DateTime LastUtc;
    }

    private sealed record LiveRecord(int Level, string Mob, double Percent, DateTime Utc);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<(int Level, string Mob), Row>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Matcher> _matchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<LiveRecord>> _liveDuringScan = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, string, int>? _levelProvider;
    private string? _filePath;
    private bool _dirty;
    private bool _recorded;
    private long _version;

    /// <summary>Changes whenever a row changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Where a live gain looks up the character's level: (name, server) to level, 0 when not known.</summary>
    public void SetLevelProvider(Func<string, string, int> levelProvider) => _levelProvider = levelProvider;

    private static string Key(string character, string server) => $"{server}|{character}";

    /// <summary>The stored name of a mob: one that starts with a lower-case letter wins, as EQ writes it mid-sentence.</summary>
    private static string BetterName(string known, string seen)
    {
        if (seen.StartsWith("A ", StringComparison.Ordinal) || seen.StartsWith("An ", StringComparison.Ordinal))
        {
            seen = char.ToLowerInvariant(seen[0]) + seen[1..];
        }

        return known.Length == 0 || (char.IsUpper(known[0]) && seen.Length > 0 && char.IsLower(seen[0])) ? seen : known;
    }

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<MobExperienceFile>(File.ReadAllText(path)) is { } saved)
                {
                    foreach (MobExperienceEntry entry in saved.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Character) || entry.Kills <= 0)
                        {
                            continue;
                        }

                        Row row = RowFor(Key(entry.Character, entry.Server ?? string.Empty), entry.Level, entry.Mob ?? string.Empty);
                        row.Kills += entry.Kills;
                        row.Percent += entry.Percent;
                        row.LastUtc = entry.LastUtc > row.LastUtc ? entry.LastUtc : row.LastUtc;
                    }

                    foreach (string key in saved.Scanned)
                    {
                        _scanned.Add(key);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Interlocked.Increment(ref _version);
    }

    private Row RowFor(string key, int level, string mob)
    {
        if (!_rows.TryGetValue(key, out Dictionary<(int, string), Row>? rows))
        {
            rows = new Dictionary<(int, string), Row>();
            _rows[key] = rows;
        }

        (int, string) id = (level, mob.ToLowerInvariant());
        if (!rows.TryGetValue(id, out Row? row))
        {
            row = new Row();
            rows[id] = row;
        }

        row.Display = BetterName(row.Display, mob);
        return row;
    }

    private void AddLocked(string key, int level, string mob, double percent, DateTime utc)
    {
        Row row = RowFor(key, level, mob);
        row.Kills++;
        row.Percent += percent;
        if (utc > row.LastUtc)
        {
            row.LastUtc = utc;
        }

        _dirty = true;
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        string server = serverName ?? string.Empty;
        string key = Key(characterName, server);
        Line kind = Classify(message, out double percent, out string mob);
        bool changed;
        lock (_gate)
        {
            _recorded = false;
            if (!_matchers.TryGetValue(key, out Matcher? matcher))
            {
                matcher = new Matcher(LiveTolerance, (gain, killed) =>
                {
                    AddLocked(key, gain.Level, killed, gain.Percent, gain.Time);
                    if (_liveDuringScan.TryGetValue(key, out List<LiveRecord>? live))
                    {
                        live.Add(new LiveRecord(gain.Level, killed, gain.Percent, gain.Time));
                    }

                    _recorded = true;
                });
                _matchers[key] = matcher;
            }

            switch (kind)
            {
                case Line.LevelUp:
                    matcher.LevelUp((int)percent, receivedUtc);
                    break;
                case Line.Gain:
                    matcher.OnGain(percent, _levelProvider?.Invoke(characterName, server) ?? 0, receivedUtc);
                    break;
                case Line.Kill:
                    matcher.OnKill(mob, receivedUtc);
                    break;
                default:
                    matcher.Other(receivedUtc);
                    break;
            }

            changed = _recorded;
        }

        if (changed)
        {
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Settles gains still waiting for a kill line once their time has passed (a quiet log has no next line to do it).</summary>
    public void ResolveExpired(DateTime nowUtc)
    {
        bool changed;
        lock (_gate)
        {
            _recorded = false;
            foreach (Matcher matcher in _matchers.Values)
            {
                matcher.Expire(nowUtc);
            }

            changed = _recorded;
        }

        if (changed)
        {
            Interlocked.Increment(ref _version);
        }
    }

    private enum Line
    {
        Other,
        Gain,
        Kill,
        LevelUp,
    }

    private static Line Classify(ReadOnlySpan<char> message, out double percent, out string mob)
    {
        percent = 0;
        mob = string.Empty;
        if (message.Length < 12 || message[^1] != '!' && message[^1] != ')')
        {
            return Line.Other;
        }

        if (message[^1] == '!' && message.StartsWith("You have gained ", StringComparison.Ordinal) && message.Contains("Welcome to level ", StringComparison.Ordinal))
        {
            // The level before the level-up, passed back in percent.
            Match level = LevelLine().Match(message.ToString());
            if (level.Success && int.TryParse(level.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int newLevel))
            {
                int gained = level.Groups["count"].Success && int.TryParse(level.Groups["count"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 1;
                percent = Math.Max(0, newLevel - gained);
                return Line.LevelUp;
            }

            return Line.Other;
        }

        if (message.StartsWith("You gain", StringComparison.Ordinal) && message.Contains("experience", StringComparison.Ordinal))
        {
            Match xp = XpLine().Match(message.ToString());
            if (!xp.Success)
            {
                return Line.Other;
            }

            if (xp.Groups["p"].Success)
            {
                percent = double.Parse(xp.Groups["p"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            }

            return Line.Gain;
        }

        if (message[^1] == '!' && message.Contains(" slain ", StringComparison.Ordinal))
        {
            Match kill = KillLine().Match(message.ToString());
            if (kill.Success)
            {
                mob = kill.Groups["mob"].Value.Trim();
                return mob.Length == 0 ? Line.Other : Line.Kill;
            }
        }

        return Line.Other;
    }

    /// <summary>Characters with any rows, in name order.</summary>
    public IReadOnlyList<MobExperienceCharacter> Characters()
    {
        lock (_gate)
        {
            return _rows
                .Where(p => p.Value.Count > 0)
                .Select(p =>
                {
                    int bar = p.Key.IndexOf('|');
                    return new MobExperienceCharacter(p.Key[(bar + 1)..], p.Key[..bar]);
                })
                .OrderBy(c => c.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Server, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>One character's rows: highest level first, then most experience per kill.</summary>
    public IReadOnlyList<MobExperienceRow> Rows(string character, string server)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(Key(character, server), out Dictionary<(int, string), Row>? rows))
            {
                return Array.Empty<MobExperienceRow>();
            }

            return rows
                .Select(p => new MobExperienceRow(p.Key.Item1, p.Value.Display, p.Value.Kills, p.Value.Percent, p.Value.LastUtc))
                .OrderByDescending(r => r.Level)
                .ThenByDescending(r => r.AveragePercent)
                .ThenBy(r => r.Mob, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>True once the history of this character's log has been read.</summary>
    public bool IsScanned(string character, string server)
    {
        lock (_gate)
        {
            return _scanned.Contains(Key(character, server));
        }
    }

    /// <summary>
    /// Rebuilds one character from its whole log, replacing what was kept for it. Gains that arrive live while the log is read are
    /// kept when they are newer than the last line read. <paramref name="currentLevel"/> is the character's level now, used only for a
    /// log with no level-up or /who of the character at all (so its level never changed in that log).
    /// </summary>
    public bool ScanLog(string path, string character, string server, int currentLevel, CancellationToken cancellation = default)
    {
        string key = Key(character, server);
        lock (_gate)
        {
            _liveDuringScan[key] = new List<LiveRecord>();
        }

        var found = new List<LiveRecord>();
        DateTime lastLineUtc = DateTime.MinValue;
        bool ok = false;
        try
        {
            ok = ReadHistory(path, character, currentLevel, found, out lastLineUtc, cancellation);
        }
        finally
        {
            lock (_gate)
            {
                _liveDuringScan.Remove(key, out List<LiveRecord>? live);
                if (ok)
                {
                    _rows.Remove(key);
                    foreach (LiveRecord record in found)
                    {
                        AddLocked(key, record.Level, record.Mob, record.Percent, record.Utc);
                    }

                    foreach (LiveRecord record in live ?? new List<LiveRecord>())
                    {
                        if (record.Utc > lastLineUtc.AddSeconds(1))
                        {
                            AddLocked(key, record.Level, record.Mob, record.Percent, record.Utc);
                        }
                    }

                    _scanned.Add(key);
                    _dirty = true;
                }
            }
        }

        if (ok)
        {
            Interlocked.Increment(ref _version);
            Flush();
        }

        return ok;
    }

    private static bool ReadHistory(
        string path, string character, int currentLevel, List<LiveRecord> found, out DateTime lastLineUtc, CancellationToken cancellation)
    {
        lastLineUtc = DateTime.MinValue;
        int level = 0;
        var waiting = new List<int>(); // indexes in found of gains made before any level was known
        int firstLevel = 0;              // the level those gains were made at, once a level-up or /who has shown it
        var matcher = new Matcher(TimeSpan.Zero, (gain, mob) =>
        {
            int gainLevel = gain.Level > 0 ? gain.Level : firstLevel;
            if (gainLevel == 0)
            {
                waiting.Add(found.Count);
            }

            found.Add(new LiveRecord(gainLevel, mob, gain.Percent, DateTime.SpecifyKind(gain.Time, DateTimeKind.Local).ToUniversalTime()));
        });

        void LevelKnown(int levelBefore)
        {
            if (firstLevel > 0 || levelBefore <= 0)
            {
                return;
            }

            firstLevel = levelBefore;
            foreach (int index in waiting)
            {
                found[index] = found[index] with { Level = levelBefore };
            }

            waiting.Clear();
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            DateTime time = DateTime.MinValue;
            long count = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((++count & 0xFFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                ReadOnlySpan<char> message = line.AsSpan(LogLineFormat.MessageOffset);
                bool who = message.Length > 4 && message[0] == '[' && char.IsAsciiDigit(message[1]);
                double value = 0;
                string mob = string.Empty;
                Line kind = who ? Line.Other : Classify(message, out value, out mob);
                if (kind == Line.Other && !who)
                {
                    matcher.Other(time);
                    continue;
                }

                if (LogLineFormat.TryParseTimestamp(line, out DateTime stamp))
                {
                    time = stamp;
                }

                switch (kind)
                {
                    case Line.LevelUp:
                        int before = (int)value;
                        LevelKnown(before);
                        matcher.LevelUp(before, time);
                        level = NewLevel(message);
                        break;
                    case Line.Gain:
                        matcher.OnGain(value, level, time);
                        break;
                    case Line.Kill:
                        matcher.OnKill(mob, time);
                        break;
                    default:
                        matcher.Other(time);
                        Match match = WhoLine().Match(message.ToString());
                        if (match.Success && match.Groups["name"].Value.Equals(character, StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(match.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int whoLevel) && whoLevel > 0)
                        {
                            LevelKnown(whoLevel);
                            level = whoLevel;
                        }

                        break;
                }
            }

            matcher.Resolve();
            if (currentLevel > 0)
            {
                LevelKnown(currentLevel);
            }

            if (time != DateTime.MinValue)
            {
                lastLineUtc = DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime();
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The level a level-up line welcomes the character to.</summary>
    private static int NewLevel(ReadOnlySpan<char> message)
    {
        Match match = LevelLine().Match(message.ToString());
        return match.Success && int.TryParse(match.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int level) ? level : 0;
    }

    /// <summary>Forgets every row (and which logs were read).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rows.Clear();
            _scanned.Clear();
            _dirty = true;
        }

        Interlocked.Increment(ref _version);
        Flush();
    }

    /// <summary>Writes the file when anything changed since the last write.</summary>
    public void Flush()
    {
        string json;
        string? path;
        lock (_gate)
        {
            if (!_dirty || _filePath is null)
            {
                return;
            }

            _dirty = false;
            path = _filePath;
            var file = new MobExperienceFile { Scanned = _scanned.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() };
            foreach ((string key, Dictionary<(int Level, string Mob), Row> rows) in _rows)
            {
                int bar = key.IndexOf('|');
                foreach (((int level, string _), Row row) in rows)
                {
                    file.Entries.Add(new MobExperienceEntry
                    {
                        Server = key[..bar],
                        Character = key[(bar + 1)..],
                        Level = level,
                        Mob = row.Display,
                        Kills = row.Kills,
                        Percent = Math.Round(row.Percent, 3),
                        LastUtc = row.LastUtc,
                    });
                }
            }

            json = JsonSerializer.Serialize(file);
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }
}
