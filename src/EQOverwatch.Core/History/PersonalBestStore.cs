using System.Text.Json;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.History;

/// <summary>A character beat its best DPS: overall (<see cref="Overall"/>) or against <see cref="Mob"/>.</summary>
public sealed record PersonalBest(string Character, string Mob, double Dps, double Previous, bool Overall);

/// <summary>
/// Each of your characters' best DPS, overall and against each mob, per server. A fight counts when it lasted at least
/// <see cref="MinSeconds"/>. The first fight a character has on record sets its best quietly; only beating a best is reported.
/// </summary>
public sealed class PersonalBestStore
{
    public const double MinSeconds = 20;

    private sealed class Best
    {
        public double Dps { get; set; }
        public string Fight { get; set; } = string.Empty;
    }

    private sealed class SavedFile
    {
        public Dictionary<string, Best> Overall { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Best> ByMob { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _gate = new();
    private string? _path;
    private SavedFile _file = new();

    /// <summary>Where the bests are kept (<c>personalbests.json</c>); reads what is there.</summary>
    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _path = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<SavedFile>(File.ReadAllText(path)) is { } saved)
                {
                    _file = new SavedFile
                    {
                        Overall = new Dictionary<string, Best>(saved.Overall ?? new(), StringComparer.OrdinalIgnoreCase),
                        ByMob = new Dictionary<string, Best>(saved.ByMob ?? new(), StringComparer.OrdinalIgnoreCase),
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _file = new SavedFile();
            }
        }
    }

    /// <summary>The key that marks one character's row in one fight.</summary>
    public static string FightKey(EncounterSnapshot fight, string character) =>
        $"{fight.Server}|{fight.TargetName}|{fight.StartTime.Ticks}|{character}".ToLowerInvariant();

    /// <summary>
    /// Checks a finished fight. <paramref name="isOwn"/> says which participants are your characters. Returns the bests it beat: an overall
    /// best, or else a best against that mob (one per character).
    /// </summary>
    public IReadOnlyList<PersonalBest> Check(EncounterSnapshot fight, Func<string, bool> isOwn)
    {
        var beaten = new List<PersonalBest>();
        if (fight.IsActive || fight.DurationSeconds < MinSeconds || string.IsNullOrEmpty(fight.TargetName))
        {
            return beaten;
        }

        bool changed = false;
        lock (_gate)
        {
            foreach (ParticipantSnapshot p in fight.Participants.Where(p => p.Dps > 0 && isOwn(p.Name)))
            {
                string who = $"{fight.Server}|{p.Name}".ToLowerInvariant();
                string key = FightKey(fight, p.Name);
                bool overall = Beat(_file.Overall, who, p.Dps, key, out double before);
                bool mob = Beat(_file.ByMob, who + "|" + fight.TargetName.ToLowerInvariant(), p.Dps, key, out double mobBefore);
                changed |= overall || mob || before < 0 || mobBefore < 0;
                if (overall)
                {
                    beaten.Add(new PersonalBest(p.Name, fight.TargetName, p.Dps, before, true));
                }
                else if (mob)
                {
                    beaten.Add(new PersonalBest(p.Name, fight.TargetName, p.Dps, mobBefore, false));
                }
            }

            if (changed)
            {
                Save();
            }
        }

        return beaten;
    }

    // True when dps beats a best on record; a first record (before = -1) is kept quietly.
    private static bool Beat(Dictionary<string, Best> bests, string key, double dps, string fight, out double before)
    {
        if (!bests.TryGetValue(key, out Best? best))
        {
            bests[key] = new Best { Dps = dps, Fight = fight };
            before = -1;
            return false;
        }

        before = best.Dps;
        if (dps <= best.Dps + 0.5 || best.Fight == fight)
        {
            return false;
        }

        best.Dps = dps;
        best.Fight = fight;
        return true;
    }

    /// <summary>True when the fight holds one of the character's current bests (overall or against that mob).</summary>
    public bool HoldsBest(EncounterSnapshot fight, string character)
    {
        string key = FightKey(fight, character);
        lock (_gate)
        {
            return _file.Overall.Values.Any(b => b.Fight == key) || _file.ByMob.Values.Any(b => b.Fight == key);
        }
    }

    /// <summary>The fight keys that hold a best, for marking rows.</summary>
    public IReadOnlySet<string> BestFights()
    {
        lock (_gate)
        {
            return _file.Overall.Values.Concat(_file.ByMob.Values).Select(b => b.Fight).ToHashSet(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_file));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // saved again next time
        }
    }
}
