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
    internal void AddDeath(string name) => _deaths[name] = _deaths.GetValueOrDefault(name) + 1;

    /// <summary>True when <paramref name="name"/> dealt or took damage in this fight.</summary>
    internal bool Involves(string name) => _byName.ContainsKey(name) || _takenByName.ContainsKey(name);

    /// <summary>Times <paramref name="name"/> died during the fight.</summary>
    public int DeathsOf(string name) => _deaths.GetValueOrDefault(name);

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
