using CommunityToolkit.Mvvm.ComponentModel;

namespace EQOverwatch.Core.Models;

/// <summary>
/// One fight against one target. All times are log-clock times. While active, <see cref="EndTime"/> tracks the
/// most recent activity; <see cref="Close"/> freezes it. State transitions are driven by <c>DpsTrackerService</c> (Phase 2).
/// </summary>
public sealed class CombatEncounter : ObservableObject
{
    private readonly Dictionary<string, ParticipantStats> _byName = new(StringComparer.Ordinal);
    private readonly List<ParticipantStats> _participants = new();
    private readonly Dictionary<string, DamageTakenStats> _takenByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deaths = new(StringComparer.Ordinal);
    private readonly List<DeathSnapshot> _deathLog = new();
    private readonly List<BossCastSnapshot> _casts = new();
    private readonly Dictionary<string, HealStats> _healers = new(StringComparer.Ordinal);
    private const int MaxCasts = 2000;

    public CombatEncounter(int id, string targetName, DateTime startTime)
    {
        Id = id;
        TargetName = targetName;
        StartTime = startTime;
        EndTime = startTime;
    }

    public int Id { get; }
    public string TargetName { get; }
    public DateTime StartTime { get; }
    public DateTime EndTime { get; private set; }
    public bool IsActive { get; private set; } = true;
    public long TotalDamage { get; private set; }

    /// <summary>True when the log announced the target's death, so the fight ended in a kill rather than fading out.</summary>
    public bool Killed { get; internal set; }

    public double DurationSeconds => Math.Max(0.0, (EndTime - StartTime).TotalSeconds);

    /// <summary>Per-player rows, in first-seen order.</summary>
    public IReadOnlyList<ParticipantStats> ParticipantStats => _participants;

    /// <summary>Text for encounter pickers, e.g. "a frost giant — 11:45:01 (14s, 802 dmg)".</summary>
    public string DisplayName =>
        $"{TargetName} — {StartTime:HH:mm:ss} ({DurationSeconds:0}s, {TotalDamage:N0} dmg){(IsActive ? " • live" : string.Empty)}";

    internal void AddDamage(DateTime timestamp, string source, int amount, bool isCritical = false)
    {
        GetOrAdd(source).RecordHit(timestamp, amount, isCritical);
        TotalDamage += amount;
        Touch(timestamp);
    }

    /// <summary>Damage the fight's target dealt to <paramref name="victim"/> (a player or pet) during the fight.</summary>
    internal void AddDamageTaken(string victim, int amount)
    {
        if (!_takenByName.TryGetValue(victim, out DamageTakenStats? stats))
        {
            stats = new DamageTakenStats(victim);
            _takenByName.Add(victim, stats);
        }

        stats.Record(amount);
        TotalDamageTaken += amount;
    }

    /// <summary>A player or pet died during the fight.</summary>
    internal void AddDeath(string name, DateTime time = default, string killedBy = "")
    {
        _deaths[name] = _deaths.GetValueOrDefault(name) + 1;
        if (_deathLog.Count < 200)
        {
            _deathLog.Add(new DeathSnapshot(time, name, killedBy ?? string.Empty));
        }
    }

    /// <summary>Each death in order, with the log time and the killer when known.</summary>
    internal IReadOnlyList<DeathSnapshot> DeathLog => _deathLog;

    /// <summary>True when <paramref name="name"/> dealt or took damage in this fight.</summary>
    internal bool Involves(string name) => _byName.ContainsKey(name) || _takenByName.ContainsKey(name);

    /// <summary>Times <paramref name="name"/> died during the fight.</summary>
    public int DeathsOf(string name) => _deaths.GetValueOrDefault(name);

    /// <summary>A spell the fight's target started casting.</summary>
    internal void AddCast(DateTime timestamp, string spell)
    {
        if (_casts.Count < MaxCasts)
        {
            _casts.Add(new BossCastSnapshot(timestamp, spell));
        }
    }

    /// <summary>Spells the fight's target cast, in log order.</summary>
    internal IReadOnlyList<BossCastSnapshot> Casts => _casts;

    /// <summary>A heal landed during the fight. <paramref name="possible"/> is what the spell could have healed.</summary>
    internal void AddHeal(string healer, string spell, int actual, int possible)
    {
        if (!_healers.TryGetValue(healer, out HealStats? stats))
        {
            stats = new HealStats(healer);
            _healers.Add(healer, stats);
        }

        stats.Record(spell, actual, possible);
    }

    internal IReadOnlyCollection<HealStats> Healers => _healers.Values;

    /// <summary>Damage the target dealt to each player or pet, in first-seen order.</summary>
    internal IReadOnlyCollection<DamageTakenStats> TakenStats => _takenByName.Values;

    /// <summary>Total damage the target dealt to players and pets.</summary>
    public long TotalDamageTaken { get; private set; }

    internal void AddMiss(DateTime timestamp, string source)
    {
        GetOrAdd(source).RecordMiss(timestamp);
        Touch(timestamp);
    }

    internal void Close(DateTime endTime)
    {
        Touch(endTime);
        IsActive = false;
        Recalculate();
    }

    /// <summary>Refreshes %-of-group and scaled DPS on every participant and notifies the UI once.</summary>
    public void Recalculate()
    {
        double seconds = Math.Max(1.0, DurationSeconds);
        foreach (ParticipantStats participant in _participants)
        {
            participant.Recalculate(TotalDamage, seconds);
        }

        OnPropertyChanged(string.Empty);
    }

    private ParticipantStats GetOrAdd(string name)
    {
        if (!_byName.TryGetValue(name, out ParticipantStats? stats))
        {
            stats = new ParticipantStats(name);
            _byName.Add(name, stats);
            _participants.Add(stats);
        }

        return stats;
    }

    private void Touch(DateTime timestamp)
    {
        if (timestamp > EndTime)
        {
            EndTime = timestamp;
        }
    }
}
