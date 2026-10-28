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
