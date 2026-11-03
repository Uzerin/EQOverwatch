using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.History;

/// <summary>One finished fight, kept for the boss history.</summary>
/// <param name="Boss">The mob's name.</param>
/// <param name="Start">Log time the fight began (together with the boss it identifies the fight).</param>
/// <param name="Seconds">How long the fight lasted.</param>
/// <param name="Damage">Total damage the raid dealt.</param>
/// <param name="Players">How many attackers took part.</param>
/// <param name="Deaths">How many deaths there were.</param>
/// <param name="Killed">True when the boss died.</param>
/// <param name="Character">The character whose log the fight came from. Kept in the background and never shown; null for fights saved by older versions.</param>
/// <param name="Server">The server of that character's log (kept with <paramref name="Character"/>).</param>
public sealed record BossFight(
    string Boss, DateTime Start, double Seconds, long Damage, int Players, int Deaths, bool Killed,
    string? Character = null, string? Server = null)
{
    /// <summary>The raid's damage per second over the fight.</summary>
    [JsonIgnore]
    public double Dps => Seconds > 0 ? Damage / Seconds : 0;
}

/// <summary>Every kill of one boss, fastest first, with the fights that ended without a kill and the figures the kills add up to.</summary>
public sealed record BossKillReport(
    string Boss, IReadOnlyList<BossFight> Kills, IReadOnlyList<BossFight> Attempts, double FastestSeconds, double AverageSeconds, double BestDps);

/// <summary>One row of the boss history: how a boss has gone.</summary>
public sealed record BossSummary(
    string Boss, int Kills, int Attempts, double FastestSeconds, double AverageSeconds, double BestDps, int MostPlayers, DateTime LastFought);

/// <summary>
/// Remembers every finished fight (kill or not) in a JSON file, so the boss history keeps growing after old log days drop out of
/// the loaded window. A fight is stored once, identified by boss and start time.
/// </summary>
public sealed class BossHistoryStore
{
    private const int MaxFights = 20_000;
    private const double MinSeconds = 5.0;
    private const double OverlapSlackSeconds = 8.0;

    private readonly object _gate = new();
    private readonly Dictionary<string, BossFight> _fights = new(StringComparer.OrdinalIgnoreCase);
    private string? _filePath;

    public event Action? Changed;

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path))
                {
                    List<BossFight>? saved = JsonSerializer.Deserialize<List<BossFight>>(File.ReadAllText(path));
                    foreach (BossFight fight in saved ?? new List<BossFight>())
                    {
                        _fights[Key(fight)] = fight;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Adds the finished fights in <paramref name="snapshots"/>, tagged with the <paramref name="character"/> and <paramref name="server"/> of the
    /// log they came from (the tag is kept in the background). Returns how many were new.
    /// </summary>
    public int Record(IEnumerable<EncounterSnapshot> snapshots, string? character = null, string? server = null)
    {
        int added = 0;
        lock (_gate)
        {
            foreach (EncounterSnapshot s in snapshots)
            {
                if (s.IsActive || s.DurationSeconds < MinSeconds)
                {
                    continue;
                }

                var fight = new BossFight(
                    s.TargetName, s.StartTime, s.DurationSeconds, s.TotalDamage,
                    s.Participants.Count, s.DeathRows.Count, s.Killed,
                    string.IsNullOrWhiteSpace(character) ? null : character.Trim(),
                    string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(server) ? null : server.Trim());
                string key = Key(fight);
                if (_fights.TryGetValue(key, out BossFight? same) && same == fight)
                {
                    continue;
                }

                // The same boss in the same stretch of time (names compared without regard to capitals) is one fight. Older builds
                // could save a single fight in pieces, and a log read again should replace them rather than sit beside them.
                // Two characters' logs of the same fight are kept apart; a fight with no character (saved by an older build) is
                // replaced by the same fight from any character's log.
                double end = fight.Seconds + OverlapSlackSeconds;
                List<string> overlapping = _fights
                    .Where(f => f.Value.Boss.Equals(fight.Boss, StringComparison.OrdinalIgnoreCase)
                                && SameSource(f.Value, fight)
                                && f.Value.Start <= fight.Start.AddSeconds(end)
                                && f.Value.Start.AddSeconds(f.Value.Seconds + OverlapSlackSeconds) >= fight.Start)
                    .Select(f => f.Key)
                    .ToList();
                if (!fight.Killed && overlapping.Any(k => _fights[k].Killed))
                {
                    continue; // a partial read must not undo a kill already on record
                }

                foreach (string old in overlapping)
                {
                    _fights.Remove(old);
                }

                _fights[key] = fight;
                added++;
            }

            if (added > 0)
            {
                if (_fights.Count > MaxFights)
                {
                    foreach (string old in _fights.OrderBy(f => f.Value.Start).Take(_fights.Count - MaxFights).Select(f => f.Key).ToList())
                    {
                        _fights.Remove(old);
                    }
                }

                Save();
            }
        }

        if (added > 0)
        {
            Changed?.Invoke();
        }

        return added;
    }

    public IReadOnlyList<BossFight> Fights()
    {
        lock (_gate)
        {
            return _fights.Values.ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _fights.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>True when two fights may be the same fight: either has no character, or both came from the same character's log.</summary>
    private static bool SameSource(BossFight a, BossFight b) =>
        string.IsNullOrEmpty(a.Character) || string.IsNullOrEmpty(b.Character)
        || (a.Character.Equals(b.Character, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(a.Server) || string.IsNullOrEmpty(b.Server)
                || a.Server.Equals(b.Server, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The fights that belong to the log of <paramref name="character"/> on <paramref name="server"/>: the ones tagged with that character,
    /// plus fights with no character (saved by an older build), which count for every log until that log is read again. With no
    /// character, every fight.
    /// </summary>
    public static IReadOnlyList<BossFight> ForLog(IEnumerable<BossFight> fights, string? character, string? server)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return fights.ToList();
        }

        return fights
            .Where(f => string.IsNullOrEmpty(f.Character)
                        || (f.Character.Equals(character, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrEmpty(f.Server) || string.IsNullOrWhiteSpace(server)
                                || f.Server.Equals(server, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>
    /// One summary per mob for the mob history. The mobs come from every fight in <paramref name="all"/> (so the list is the same whichever
    /// log is loaded); the figures come only from <paramref name="loaded"/>, the fights of the loaded log. A mob the loaded log never
    /// fought is listed with no kills and no attempts. Mobs fought in the loaded log come first, most recent first.
    /// </summary>
    public static IReadOnlyList<BossSummary> SummarizeMobs(IEnumerable<BossFight> all, IEnumerable<BossFight> loaded, int minPlayers = 1)
    {
        List<BossSummary> own = Summarize(loaded, minPlayers).ToList();
        var seen = new HashSet<string>(own.Select(s => s.Boss), StringComparer.OrdinalIgnoreCase);
        IEnumerable<BossSummary> others = all
            .Where(f => f.Players >= minPlayers && !seen.Contains(f.Boss))
            .GroupBy(f => f.Boss, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BossSummary(g.Key, 0, 0, 0, 0, 0, 0, default))
            .OrderBy(s => s.Boss, StringComparer.OrdinalIgnoreCase);
        return own.Concat(others).ToList();
    }

    /// <summary>One summary per boss, for fights of at least <paramref name="minPlayers"/> attackers, most recently fought first.</summary>
    public static IReadOnlyList<BossSummary> Summarize(IEnumerable<BossFight> fights, int minPlayers = 1)
    {
        return fights
            .Where(f => f.Players >= minPlayers)
            .GroupBy(f => f.Boss, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                List<BossFight> kills = g.Where(f => f.Killed).ToList();
                return new BossSummary(
                    g.Key,
                    kills.Count,
                    g.Count() - kills.Count,
                    kills.Count == 0 ? 0 : kills.Min(f => f.Seconds),
                    kills.Count == 0 ? 0 : kills.Average(f => f.Seconds),
                    (kills.Count == 0 ? g.ToList() : kills).Max(f => f.Seconds > 0 ? f.Damage / f.Seconds : 0),
                    g.Max(f => f.Players),
                    g.Max(f => f.Start));
            })
            .OrderByDescending(s => s.LastFought)
            .ToList();
    }

    /// <summary>
    /// The kills of <paramref name="boss"/> (names compared without regard to capitals), fastest first, from fights of at least
    /// <paramref name="minPlayers"/> attackers; the other fights are its attempts.
    /// </summary>
    public static BossKillReport KillReport(IEnumerable<BossFight> fights, string boss, int minPlayers = 1)
    {
        List<BossFight> all = fights
            .Where(f => f.Players >= minPlayers && f.Boss.Equals(boss, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Start)
            .ToList();
        List<BossFight> kills = all.Where(f => f.Killed).OrderBy(f => f.Seconds).ThenByDescending(f => f.Start).ToList();
        return new BossKillReport(
            boss,
            kills,
            all.Where(f => !f.Killed).ToList(),
            kills.Count == 0 ? 0 : kills.Min(f => f.Seconds),
            kills.Count == 0 ? 0 : kills.Average(f => f.Seconds),
            kills.Count == 0 ? 0 : kills.Max(f => f.Dps));
    }

    /// <summary>"3:07" for 187 seconds; "—" for none.</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        int total = (int)Math.Round(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    private static string Key(BossFight fight) => $"{fight.Boss}|{fight.Start:O}|{fight.Character}";

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_fights.Values.OrderBy(f => f.Start).ToList()));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; the next change tries again
        }
    }
}
