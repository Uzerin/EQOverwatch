using System.Text.Json;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.History;

/// <summary>What the learner knows of one boss ability: the usual time between casts and the usual first cast, in seconds.</summary>
public sealed record LearnedAbility(string Boss, string Spell, double? EverySeconds, double? FirstSeconds, int Fights);

/// <summary>
/// Learns each boss's ability timing from killed fights: the median time between casts of an ability and the median time of its first cast.
/// A boss counts once it has <see cref="MinKills"/> kills, and an ability once it was seen in <see cref="MinKills"/> of them. Saved as JSON.
/// </summary>
public sealed class BossAbilityLearner
{
    public const int MinKills = 2;
    private const int KeepPerAbility = 30;
    private const double MinFightSeconds = 20;

    public sealed class AbilityData
    {
        public int Fights { get; set; }
        public List<double> Gaps { get; set; } = new();
        public List<double> Firsts { get; set; } = new();
    }

    public sealed class BossData
    {
        public List<string> Kills { get; set; } = new();
        public Dictionary<string, AbilityData> Abilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _gate = new();
    private Dictionary<string, BossData> _bosses = new(StringComparer.OrdinalIgnoreCase);
    private string? _filePath;

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, BossData>>(File.ReadAllText(path)) is { } saved)
                {
                    _bosses = new Dictionary<string, BossData>(saved, StringComparer.OrdinalIgnoreCase);
                    foreach (BossData boss in _bosses.Values)
                    {
                        boss.Abilities = new Dictionary<string, AbilityData>(boss.Abilities, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // start again
            }
        }
    }

    /// <summary>Learns from the killed fights not seen before. Returns how many were added.</summary>
    public int Learn(IEnumerable<EncounterSnapshot> fights)
    {
        int added = 0;
        lock (_gate)
        {
            foreach (EncounterSnapshot fight in fights)
            {
                if (fight.IsActive || !fight.Killed || fight.DurationSeconds < MinFightSeconds || fight.CastRows.Count == 0)
                {
                    continue;
                }

                if (!_bosses.TryGetValue(fight.TargetName.Trim(), out BossData? boss))
                {
                    _bosses[fight.TargetName.Trim()] = boss = new BossData();
                }

                string key = fight.StartTime.ToUniversalTime().ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
                if (boss.Kills.Contains(key))
                {
                    continue;
                }

                boss.Kills.Add(key);
                foreach (IGrouping<string, BossCastSnapshot> spell in fight.CastRows.OrderBy(c => c.Time).GroupBy(c => c.Spell, StringComparer.OrdinalIgnoreCase))
                {
                    if (!boss.Abilities.TryGetValue(spell.Key, out AbilityData? data))
                    {
                        boss.Abilities[spell.Key] = data = new AbilityData();
                    }

                    data.Fights++;
                    DateTime[] times = spell.Select(c => c.Time).ToArray();
                    Add(data.Firsts, (times[0] - fight.StartTime).TotalSeconds);
                    for (int i = 1; i < times.Length; i++)
                    {
                        double gap = (times[i] - times[i - 1]).TotalSeconds;
                        if (gap >= 2)
                        {
                            Add(data.Gaps, gap); // casts a second or two apart are one cast seen twice
                        }
                    }
                }

                added++;
            }

            if (added > 0)
            {
                Save();
            }
        }

        return added;
    }

    private static void Add(List<double> list, double value)
    {
        if (value < 0)
        {
            return;
        }

        list.Add(value);
        if (list.Count > KeepPerAbility)
        {
            list.RemoveAt(0);
        }
    }

    /// <summary>The boss's learned abilities, or none until it has <see cref="MinKills"/> kills.</summary>
    public IReadOnlyList<LearnedAbility> For(string boss)
    {
        lock (_gate)
        {
            if (!_bosses.TryGetValue(boss.Trim(), out BossData? data) || data.Kills.Count < MinKills)
            {
                return Array.Empty<LearnedAbility>();
            }

            return data.Abilities
                .Where(a => a.Value.Fights >= MinKills)
                .Select(a => new LearnedAbility(boss, a.Key,
                    a.Value.Gaps.Count >= 2 ? Median(a.Value.Gaps) : null,
                    a.Value.Firsts.Count >= MinKills ? Median(a.Value.Firsts) : null,
                    a.Value.Fights))
                .Where(a => a.EverySeconds is not null || a.FirstSeconds is not null)
                .ToList();
        }
    }

    public static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

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
            File.WriteAllText(temp, JsonSerializer.Serialize(_bosses));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // saved next time
        }
    }
}
