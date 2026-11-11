using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Experience;

/// <summary>One character's experience this session, as the Experience tab shows it.</summary>
/// <param name="Level">The character's level, or 0 when not known.</param>
/// <param name="XpPercent">Percent of a level gained this session (only lines that show a percentage add to it).</param>
/// <param name="XpPerHour">Percent of a level per hour this session.</param>
/// <param name="ProgressPercent">How far into the current level the character is, or null when no level-up has been seen to count from.</param>
/// <param name="NextLevelIn">Time to the next level at this session's rate, or null when it cannot be worked out.</param>
public sealed record ExperienceRow(
    string Character, int Level, double XpPercent, double XpPerHour, double? ProgressPercent, TimeSpan? NextLevelIn,
    int AaGained, double AaPerHour, int Kills, int PartyKills, int RaidKills, int SoloKills, int LevelsGained, DateTime LastGainUtc);

/// <summary>
/// Adds up the experience lines of every character's live log since the app opened (or since <see cref="Reset"/>):
/// "You gain experience! (2.6%)", "You gain party experience (with a bonus)! (5.2%)", "You gained raid experience!" (each one a kill;
/// the percentage, when shown, is of a level), "You have gained a level! Welcome to level 61!" and "You have gained an ability point!".
/// </summary>
public sealed partial class ExperienceTracker : ITriggerLineSink
{
    private sealed class Stats
    {
        public int Level;
        public double Xp;
        public double? Progress;
        public int Aa;
        public int Kills;
        public int Party;
        public int Raid;
        public int Solo;
        public int Levels;
        public DateTime LastGainUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Stats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Level, double? Progress)> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _servers = new(StringComparer.OrdinalIgnoreCase); // character -> server of its log
    private DateTime _sessionStartUtc;
    private long _version;

    public ExperienceTracker(DateTime sessionStartUtc) => _sessionStartUtc = sessionStartUtc;

    public DateTime SessionStartUtc
    {
        get
        {
            lock (_gate)
            {
                return _sessionStartUtc;
            }
        }
    }

    /// <summary>Changes whenever a line is counted.</summary>
    public long Version => Interlocked.Read(ref _version);

    [GeneratedRegex(@"^You gain(?:ed)? (?:(?<kind>party|raid) )?experience(?: \(with a bonus\))?!(?: \((?<p>\d+(?:\.\d+)?)%\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex XpLine();

    [GeneratedRegex(@"^You have gained (?:a level|\d+ levels)! Welcome to level (?<level>\d+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelLine();

    private const string AbilityPoint = "You have gained an ability point!";

    /// <summary>Starts a new session: everything counted so far is dropped. Levels and progress into a level are kept.</summary>
    public void Reset(DateTime nowUtc)
    {
        lock (_gate)
        {
            foreach ((string name, Stats stats) in _stats)
            {
                _known[name] = (stats.Level, stats.Progress);
            }

            _stats.Clear();
            _sessionStartUtc = nowUtc;
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>What a log said about a character before this session: its level and how far into it (null when not known).</summary>
    public void SetStartingPoint(string character, int level, double? progressPercent)
    {
        if (string.IsNullOrWhiteSpace(character) || level <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_stats.TryGetValue(character, out Stats? stats))
            {
                if (stats.Level == 0)
                {
                    stats.Level = level;
                    stats.Progress = progressPercent is null || stats.Progress is null ? progressPercent : progressPercent + stats.Progress;
                }
            }
            else
            {
                _known[character] = (level, progressPercent);
            }
        }

        Interlocked.Increment(ref _version);
    }

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!string.IsNullOrEmpty(serverName) && !string.IsNullOrEmpty(characterName))
        {
            lock (_gate)
            {
                if (!_servers.TryGetValue(characterName, out string? known) || !known.Equals(serverName, StringComparison.Ordinal))
                {
                    _servers[characterName] = serverName;
                }
            }
        }

        OnLine(characterName, message, receivedUtc);
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 20 || !message.StartsWith("You ", StringComparison.Ordinal))
        {
            return;
        }

        if (message.StartsWith(AbilityPoint, StringComparison.Ordinal))
        {
            Update(characterName, s =>
            {
                s.Aa++;
                return true;
            }, receivedUtc);
            return;
        }

        if (message[^1] == '!' && message.Contains("Welcome to level ", StringComparison.Ordinal))
        {
            Match level = LevelLine().Match(message.ToString());
            if (level.Success && int.TryParse(level.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int newLevel))
            {
                Update(characterName, s =>
                {
                    s.Levels += s.Level > 0 && newLevel > s.Level ? newLevel - s.Level : 1;
                    s.Level = newLevel;
                    s.Progress = 0;
                    return true;
                }, receivedUtc);
            }

            return;
        }

        if (!message.Contains("experience", StringComparison.Ordinal))
        {
            return;
        }

        Match xp = XpLine().Match(message.ToString());
        if (!xp.Success)
        {
            return;
        }

        double percent = xp.Groups["p"].Success
            ? double.Parse(xp.Groups["p"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
            : 0;
        string kind = xp.Groups["kind"].Value;
        Update(characterName, s =>
        {
            s.Kills++;
            if (kind == "party")
            {
                s.Party++;
            }
            else if (kind == "raid")
            {
                s.Raid++;
            }
            else
            {
                s.Solo++;
            }

            s.Xp += percent;
            if (s.Progress is not null)
            {
                s.Progress = Math.Min(100, s.Progress.Value + percent);
            }

            return true;
        }, receivedUtc);
    }

    private void Update(string character, Func<Stats, bool> change, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        lock (_gate)
        {
            if (!_stats.TryGetValue(character, out Stats? stats))
            {
                stats = new Stats();
                if (_known.Remove(character, out (int Level, double? Progress) known))
                {
                    stats.Level = known.Level;
                    stats.Progress = known.Progress;
                }

                _stats[character] = stats;
            }

            if (change(stats))
            {
                stats.LastGainUtc = nowUtc;
            }
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>One row per character that gained anything this session, in name order.</summary>
    public IReadOnlyList<ExperienceRow> Rows(DateTime nowUtc, Func<string, int>? levelFallback = null) =>
        Rows(nowUtc, levelFallback is null ? null : (name, _) => levelFallback(name));

    /// <summary>The same, with the level fallback looked up on the server of each character's log.</summary>
    public IReadOnlyList<ExperienceRow> Rows(DateTime nowUtc, Func<string, string, int>? levelOnServer)
    {
        lock (_gate)
        {
            double hours = Math.Max((nowUtc - _sessionStartUtc).TotalHours, 1.0 / 60);
            return _stats
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    Stats s = p.Value;
                    double perHour = s.Xp / hours;
                    TimeSpan? next = s.Progress is { } progress && perHour > 0.0001
                        ? TimeSpan.FromHours(Math.Max(0, 100 - progress) / perHour)
                        : null;
                    int level = s.Level > 0 ? s.Level : levelOnServer?.Invoke(p.Key, _servers.GetValueOrDefault(p.Key, string.Empty)) ?? 0;
                    return new ExperienceRow(
                        p.Key, level, s.Xp, perHour, s.Progress, next, s.Aa, s.Aa / hours, s.Kills, s.Party, s.Raid, s.Solo, s.Levels, s.LastGainUtc);
                })
                .ToList();
        }
    }

    /// <summary>
    /// Reads the end of a log (at most <paramref name="maxBytes"/>) for the last level-up and adds up the experience percentages after it.
    /// Returns null when that part of the log has no level-up.
    /// </summary>
    public static (int Level, double Progress)? ScanStartingPoint(string path, long maxBytes = 32L * 1024 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            if (start > 0)
            {
                reader.ReadLine(); // part of a line
            }

            int level = 0;
            double progress = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                ReadOnlySpan<char> message = line.AsSpan(LogLineFormat.MessageOffset);
                if (!message.StartsWith("You ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (message.Contains("Welcome to level ", StringComparison.Ordinal))
                {
                    Match match = LevelLine().Match(message.ToString());
                    if (match.Success && int.TryParse(match.Groups["level"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int newLevel))
                    {
                        level = newLevel;
                        progress = 0;
                    }
                }
                else if (level > 0 && message.Contains("experience", StringComparison.Ordinal))
                {
                    Match xp = XpLine().Match(message.ToString());
                    if (xp.Success && xp.Groups["p"].Success)
                    {
                        progress = Math.Min(100, progress + double.Parse(xp.Groups["p"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
                    }
                }
            }

            return level > 0 ? (level, progress) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
