using System.Collections.ObjectModel;

namespace EQCompanion.Core.Models;

// Immutable snapshots published from the pipeline's consumer thread. The UI reads the latest one on a timer tick
// (a single volatile reference read) and never needs a lock. Collections are read-only wrappers over private arrays.

public sealed record ParticipantSnapshot(
    string Name,
    long TotalDamage,
    double PercentOfGroupDamage,
    double Dps,
    double ScaledDps,
    int MaxHit,
    int Hits,
    int Misses,
    double HitRate,
    int MaxCritHit = 0,
    int CritHitCount = 0,
    double CritRatePercent = 0.0,
    string Class = "");

public sealed record EncounterSnapshot(
    int Id,
    string TargetName,
    DateTime StartTime,
    DateTime EndTime,
    double DurationSeconds,
    long TotalDamage,
    bool IsActive,
    IReadOnlyList<ParticipantSnapshot> Participants)
{
    public string DisplayName =>
        $"{TargetName} — {StartTime:HH:mm:ss} ({DurationSeconds:0}s, {TotalDamage:N0} dmg){(IsActive ? " • live" : string.Empty)}";

    /// <summary>
    /// A copy whose participants carry their class label ("WAR", "Pet", or empty), looked up now. Classes are not stored in the
    /// frozen fight, so one learned after the fight ended (a later /who) still shows.
    /// </summary>
    public EncounterSnapshot WithClasses(Func<string, string> classOf)
    {
        ArgumentNullException.ThrowIfNull(classOf);
        return this with
        {
            Participants = Array.AsReadOnly(Participants.Select(p => p with { Class = classOf(p.Name) }).ToArray()),
        };
    }

    /// <summary>Freezes an encounter. Participants are ordered by damage, highest first.</summary>
    public static EncounterSnapshot From(CombatEncounter encounter)
    {
        encounter.Recalculate();

        ParticipantSnapshot[] participants = encounter.ParticipantStats
            .Select(p => new ParticipantSnapshot(
                p.Name, p.TotalDamage, p.PercentOfGroupDamage, p.Dps, p.ScaledDps, p.MaxHit, p.Hits, p.Misses, p.HitRate,
                p.MaxCritHit, p.CritHitCount, p.CritRatePercent))
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        return new EncounterSnapshot(
            encounter.Id, encounter.TargetName, encounter.StartTime, encounter.EndTime,
            encounter.DurationSeconds, encounter.TotalDamage, encounter.IsActive,
            Array.AsReadOnly(participants));
    }
}

/// <summary>Everything a DPS tracker knows at one instant. <see cref="Version"/> increases on every publish.</summary>
public sealed record DpsSnapshot(
    long Version,
    IReadOnlyList<EncounterSnapshot> Completed,
    IReadOnlyList<EncounterSnapshot> Active)
{
    public static readonly DpsSnapshot Empty = new(
        0,
        Array.AsReadOnly(Array.Empty<EncounterSnapshot>()),
        Array.AsReadOnly(Array.Empty<EncounterSnapshot>()));

    /// <summary>Active encounters first, then completed ones (oldest first).</summary>
    public IEnumerable<EncounterSnapshot> All => Active.Concat(Completed);

    public EncounterSnapshot? FindById(int id) => All.FirstOrDefault(e => e.Id == id);
}

public enum TimerKind : byte
{
    /// <summary>Text overlay alert that disappears after its duration.</summary>
    TextAlert,
    /// <summary>Countdown bar.</summary>
    Countdown,
}

/// <summary>
/// A running alert or countdown. All instants are wall-clock UTC taken from when the line was read, so the UI computes
/// remaining time against its own clock with <see cref="RemainingSeconds"/>.
/// </summary>
public sealed record ActiveTimerSnapshot(
    long Id,
    Guid TriggerId,
    string TriggerName,
    string CharacterName,
    TimerKind Kind,
    string Text,
    DateTime StartedUtc,
    DateTime ExpiresUtc,
    double DurationSeconds,
    TimerOverlayCategory Category = TimerOverlayCategory.RaidTriggers)
{
    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresUtc;

    public double RemainingSeconds(DateTime nowUtc) => Math.Max(0.0, (ExpiresUtc - nowUtc).TotalSeconds);

    /// <summary>1.0 when just started, 0.0 when expired. Drives the bar width.</summary>
    public double FractionRemaining(DateTime nowUtc) =>
        DurationSeconds > 0 ? Math.Clamp(RemainingSeconds(nowUtc) / DurationSeconds, 0.0, 1.0) : 0.0;
}

public sealed record TriggerSnapshot(long Version, IReadOnlyList<ActiveTimerSnapshot> Timers)
{
    public static readonly TriggerSnapshot Empty = new(0, Array.AsReadOnly(Array.Empty<ActiveTimerSnapshot>()));

    /// <summary>Timers still running at <paramref name="nowUtc"/>. The engine only prunes when a trigger fires, so filter here.</summary>
    public IReadOnlyList<ActiveTimerSnapshot> GetActive(DateTime nowUtc) =>
        Timers.Where(t => !t.IsExpired(nowUtc)).ToArray();

    /// <summary>Running timers of one overlay category (what that category's overlay window shows).</summary>
    public IReadOnlyList<ActiveTimerSnapshot> GetActive(DateTime nowUtc, TimerOverlayCategory category) =>
        Timers.Where(t => t.Category == category && !t.IsExpired(nowUtc)).ToArray();
}
