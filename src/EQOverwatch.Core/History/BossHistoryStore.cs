using System.Globalization;
using System.Text.Json;
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
public sealed record BossFight(string Boss, DateTime Start, double Seconds, long Damage, int Players, int Deaths, bool Killed);

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

    /// <summary>Adds the finished fights in <paramref name="snapshots"/>. Returns how many were new.</summary>
    public int Record(IEnumerable<EncounterSnapshot> snapshots)
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
                    s.Participants.Count, s.DeathRows.Count, s.Killed);
                string key = Key(fight);
                if (_fights.TryGetValue(key, out BossFight? existing) && existing.Killed == fight.Killed)
                {
                    continue;
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
                    kills.Count == 0 ? 0 : kills.Max(f => f.Seconds > 0 ? f.Damage / f.Seconds : 0),
                    g.Max(f => f.Players),
                    g.Max(f => f.Start));
            })
            .OrderByDescending(s => s.LastFought)
            .ToList();
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

    private static string Key(BossFight fight) => $"{fight.Boss}|{fight.Start:O}";

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
