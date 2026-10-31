using System.Collections.ObjectModel;

namespace EQOverwatch.Core.Models;

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
    string Class = "",
    int Deaths = 0)
{
    /// <summary>The name, followed by " (Deaths: 2)" when the player died during the fight.</summary>
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;
}

/// <summary>Damage one player or pet took from the fight's target.</summary>
public sealed record DamageTakenSnapshot(
    string Name,
    long TotalDamage,
    double PercentOfTotal,
    int Hits,
    int MaxHit,
    int Deaths = 0)
{
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;
}

/// <summary>A spell a boss started casting, at log-clock time.</summary>
public sealed record BossCastSnapshot(DateTime Time, string Spell);

/// <summary>A spell a player (or pet) began casting during a fight; <see cref="Failed"/> when the log showed it fizzle or be interrupted.</summary>
public sealed record PlayerCastSnapshot(DateTime Time, string Caster, string Spell, bool Failed = false);

/// <summary>A player or pet that died during a fight; <see cref="KilledBy"/> is empty when the log did not say.</summary>
public sealed record DeathSnapshot(DateTime Time, string Name, string KilledBy);

/// <summary>What one healer healed during a fight. Overheal is what the spells could have healed beyond what landed.</summary>
public sealed record HealingSnapshot(
    string Name,
    long Effective,
    long Overheal,
    double OverhealPercent,
    int Heals,
    int MaxHeal,
    double Hps,
    string TopSpell);

public sealed record EncounterSnapshot(
    int Id,
    string TargetName,
    DateTime StartTime,
    DateTime EndTime,
    double DurationSeconds,
    long TotalDamage,
    bool IsActive,
    IReadOnlyList<ParticipantSnapshot> Participants,
    IReadOnlyList<DamageTakenSnapshot>? DamageTaken = null,
    IReadOnlyList<BossCastSnapshot>? BossCasts = null,
    IReadOnlyList<HealingSnapshot>? Healing = null,
    IReadOnlyList<DeathSnapshot>? DeathLog = null,
    bool Killed = false,
    IReadOnlyList<PlayerCastSnapshot>? PlayerCasts = null)
{
    /// <summary>Every spell the raid began casting during the fight, in log order.</summary>
    public IReadOnlyList<PlayerCastSnapshot> PlayerCastRows => PlayerCasts ?? Array.Empty<PlayerCastSnapshot>();

    /// <summary>Who died during the fight and when, in order.</summary>
    public IReadOnlyList<DeathSnapshot> DeathRows => DeathLog ?? Array.Empty<DeathSnapshot>();

    /// <summary>The spells the target started casting during the fight, in order.</summary>
    public IReadOnlyList<BossCastSnapshot> CastRows => BossCasts ?? Array.Empty<BossCastSnapshot>();

    /// <summary>Healers, most effective healing first.</summary>
    public IReadOnlyList<HealingSnapshot> HealingRows => Healing ?? Array.Empty<HealingSnapshot>();

    /// <summary>Who the target hurt, most damage first (empty when nothing was recorded).</summary>
    public IReadOnlyList<DamageTakenSnapshot> TakenRows => DamageTaken ?? Array.Empty<DamageTakenSnapshot>();

    /// <summary>Total damage the target dealt to players and pets.</summary>
    public long TotalDamageTaken => TakenRows.Sum(t => t.TotalDamage);

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
                p.MaxCritHit, p.CritHitCount, p.CritRatePercent, Deaths: encounter.DeathsOf(p.Name)))
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        long takenTotal = encounter.TotalDamageTaken;
        DamageTakenSnapshot[] taken = encounter.TakenStats
            .Select(t => new DamageTakenSnapshot(
                t.Name, t.TotalDamage, takenTotal > 0 ? 100.0 * t.TotalDamage / takenTotal : 0.0, t.Hits, t.MaxHit, encounter.DeathsOf(t.Name)))
            .OrderByDescending(t => t.TotalDamage)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        double seconds = Math.Max(1.0, encounter.DurationSeconds);
        HealingSnapshot[] healing = encounter.Healers
            .Select(h => new HealingSnapshot(
                h.Name, h.Effective, h.Overheal,
                h.Effective + h.Overheal > 0 ? 100.0 * h.Overheal / (h.Effective + h.Overheal) : 0.0,
                h.Heals, h.MaxHeal, h.Effective / seconds, h.TopSpell))
            .OrderByDescending(h => h.Effective)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ToArray();

        return new EncounterSnapshot(
            encounter.Id, encounter.TargetName, encounter.StartTime, encounter.EndTime,
            encounter.DurationSeconds, encounter.TotalDamage, encounter.IsActive,
            Array.AsReadOnly(participants), Array.AsReadOnly(taken),
            Array.AsReadOnly(encounter.Casts.ToArray()), Array.AsReadOnly(healing),
            Array.AsReadOnly(encounter.DeathLog.ToArray()), encounter.Killed,
            Array.AsReadOnly(encounter.PlayerCasts.ToArray()));
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
