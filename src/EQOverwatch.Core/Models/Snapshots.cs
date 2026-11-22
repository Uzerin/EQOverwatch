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
    int Deaths = 0,
    int MaxNonCritHit = -1,
    IReadOnlyList<AttackSlice>? Attacks = null,
    ValueList<PetLine>? Pets = null)
{
    /// <summary>The pets whose damage is counted in this row (empty when none).</summary>
    public IReadOnlyList<PetLine> PetRows => Pets ?? (IReadOnlyList<PetLine>)Array.Empty<PetLine>();

    /// <summary>The name, followed by " (Deaths: 2)" when the player died during the fight.</summary>
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;

    /// <summary>The largest hit that was not a critical hit: the figure shown as "Max Hit". Falls back to <see cref="MaxHit"/> when it was not recorded.</summary>
    public int MaxHitNoCrit => MaxNonCritHit >= 0 ? MaxNonCritHit : MaxHit;
}

/// <summary>A pet's own figures inside its owner's row: damage, hits, misses and deaths.</summary>
public sealed record PetLine(string Name, long Damage, int Hits, int Misses, int Deaths)
{
    /// <summary>Hits / (hits + misses) in percent.</summary>
    public double HitPercent => Hits + Misses == 0 ? 0 : 100.0 * Hits / (Hits + Misses);
}

/// <summary>Damage one player or pet took from the fight's target.</summary>
public sealed record DamageTakenSnapshot(
    string Name,
    long TotalDamage,
    double PercentOfTotal,
    int Hits,
    int MaxHit,
    int Deaths = 0,
    int Avoided = 0,
    int Dodges = 0,
    int Parries = 0,
    int Blocks = 0,
    int Ripostes = 0,
    int Missed = 0,
    int OtherAvoids = 0,
    long ShieldDamage = 0,
    int Rampages = 0,
    long RampageDamage = 0,
    int DotTicks = 0,
    long DotDamage = 0)
{
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;

    /// <summary>Attacks the target made on this player that landed or failed (damage shield damage is not an attack).</summary>
    public int Attacks => Hits + Avoided;

    private double Share(int count) => Attacks > 0 ? 100.0 * count / Attacks : 0.0;

    /// <summary>Share of the target's attacks on this player that did not land, in percent.</summary>
    public double AvoidPercent => Share(Avoided);

    public double DodgePercent => Share(Dodges);
    public double ParryPercent => Share(Parries);
    public double BlockPercent => Share(Blocks);
    public double RipostePercent => Share(Ripostes);

    /// <summary>Plain misses ("..., but misses!").</summary>
    public double MissPercent => Share(Missed);

    /// <summary>Absorbed by a rune or ward, invulnerable and the like.</summary>
    public double OtherAvoidPercent => Share(OtherAvoids);
}

/// <summary>Damage the fight's target did with a damage shield: how much, how many hits, and when (seconds into the fight) it started and last hurt anyone.</summary>
public sealed record DamageShieldSummary(long Total, int Hits, double FirstSeconds, double LastSeconds)
{
    /// <summary>How long the shield kept hurting people, first hit to last.</summary>
    public double ActiveSeconds => Math.Max(0.0, LastSeconds - FirstSeconds);
}

/// <summary>A spell a boss started casting, at log-clock time.</summary>
public sealed record BossCastSnapshot(DateTime Time, string Spell);

/// <summary>A spell a player (or pet) began casting during a fight; <see cref="Failed"/> when the log showed it fizzle or be interrupted.</summary>
public sealed record PlayerCastSnapshot(DateTime Time, string Caster, string Spell, bool Failed = false);

/// <summary>
/// A player or pet that died during a fight; <see cref="KilledBy"/> is empty when the log did not say. <see cref="KillingBlow"/> is the
/// killer's last hit on the victim ("175 punch", "2,400 Judgment"), or empty when the log did not show it.
/// </summary>
/// <summary>A hit on a player or pet (<see cref="IsHeal"/> false) or a heal it received, for the "last seconds" view.</summary>
/// <summary>
/// A hit on a player or pet, a heal that landed on it (<see cref="IsHeal"/>), or a heal someone began casting then (<see cref="IsCast"/>,
/// no amount; the log does not say who it was for).
/// </summary>
public sealed record RecentEventSnapshot(DateTime Time, string Source, string What, int Amount, bool IsHeal, bool IsCast = false);

/// <summary>A turn the fight's target spent meleeing one player, in seconds from the fight's start.</summary>
public sealed record TankSpanSnapshot(string Name, double Start, double End, int Swings);

/// <summary>
/// A player's or pet's last <see cref="CombatEncounter.RecentSeconds"/> seconds in a fight, up to <see cref="End"/>: their death when
/// <see cref="Died"/>, else the fight's end.
/// </summary>
public sealed record LastSecondsSnapshot(string Name, DateTime End, bool Died, ValueList<RecentEventSnapshot> Events)
{
    public long Taken => Events.Where(e => !e.IsHeal).Sum(e => (long)e.Amount);

    public long Healed => Events.Where(e => e.IsHeal && !e.IsCast).Sum(e => (long)e.Amount);
}

public sealed record DeathSnapshot(DateTime Time, string Name, string KilledBy, string KillingBlow = "");

/// <summary>What one healer healed during a fight. Overheal is what the spells could have healed beyond what landed.</summary>
public sealed record HealingSnapshot(
    string Name,
    long Effective,
    long Overheal,
    double OverhealPercent,
    int Heals,
    int MaxHeal,
    double Hps,
    string TopSpell,
    long CritEffective = 0,
    int CritHeals = 0)
{
    /// <summary>Critical heals' share of the effective healing, in percent.</summary>
    public double CritHealPercent => Effective > 0 ? 100.0 * CritEffective / Effective : 0.0;
}

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
    IReadOnlyList<PlayerCastSnapshot>? PlayerCasts = null,
    DpsTimeline? Timeline = null,
    DamageShieldSummary? DamageShield = null)
{
    /// <summary>Every spell the raid began casting during the fight, in log order.</summary>
    public IReadOnlyList<PlayerCastSnapshot> PlayerCastRows => PlayerCasts ?? Array.Empty<PlayerCastSnapshot>();

    /// <summary>Who died during the fight and when, in order.</summary>
    public IReadOnlyList<DeathSnapshot> DeathRows => DeathLog ?? Array.Empty<DeathSnapshot>();

    /// <summary>The spells the target started casting during the fight, in order.</summary>
    public IReadOnlyList<BossCastSnapshot> CastRows => BossCasts ?? Array.Empty<BossCastSnapshot>();

    /// <summary>Healers, most effective healing first.</summary>
    public IReadOnlyList<HealingSnapshot> HealingRows => Healing ?? Array.Empty<HealingSnapshot>();

    /// <summary>Each player's last seconds of hits and heals (before their death, or before the fight's end).</summary>
    public IReadOnlyList<LastSecondsSnapshot> LastSeconds { get; init; } = Array.Empty<LastSecondsSnapshot>();

    /// <summary>Who the target meleed, turn by turn (the tank timeline).</summary>
    public IReadOnlyList<TankSpanSnapshot> TankSpans { get; init; } = Array.Empty<TankSpanSnapshot>();

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
    /// <summary>The server of the log that recorded the fight (empty when unknown).</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>"Regen ~5.7/s · HP ~10.3k" for the Discord picture (empty when there is no estimate), set just before drawing.</summary>
    public string RegenText { get; init; } = string.Empty;

    /// <summary>Like <see cref="WithClasses(Func{string, string})"/>, with each class looked up on the fight's <see cref="Server"/>.</summary>
    public EncounterSnapshot WithClassesOnServer(Func<string, string, string> classOnServer)
    {
        ArgumentNullException.ThrowIfNull(classOnServer);
        string server = Server;
        return WithClasses(name => classOnServer(name, server));
    }

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
                p.MaxCritHit, p.CritHitCount, p.CritRatePercent, Deaths: encounter.DeathsOf(p.Name), MaxNonCritHit: p.MaxNonCritHit, Attacks: p.AttackSlices(),
                Pets: PetsOf(p, encounter)))
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        long takenTotal = encounter.TotalDamageTaken;
        DamageTakenSnapshot[] taken = encounter.TakenStats
            .Select(t => new DamageTakenSnapshot(
                t.Name, t.TotalDamage, takenTotal > 0 ? 100.0 * t.TotalDamage / takenTotal : 0.0, t.Hits, t.MaxHit, encounter.DeathsOf(t.Name), t.Avoided,
                t.Dodges, t.Parries, t.Blocks, t.Ripostes, t.Misses, t.OtherAvoids, t.ShieldDamage, t.Rampages, t.RampageDamage, t.DotTicks, t.DotDamage))
            .OrderByDescending(t => t.TotalDamage)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        double seconds = Math.Max(1.0, encounter.DurationSeconds);
        HealingSnapshot[] healing = encounter.Healers
            .Select(h => new HealingSnapshot(
                h.Name, h.Effective, h.Overheal,
                h.Effective + h.Overheal > 0 ? 100.0 * h.Overheal / (h.Effective + h.Overheal) : 0.0,
                h.Heals, h.MaxHeal, h.Effective / seconds, h.TopSpell, h.CritEffective, h.CritHeals))
            .OrderByDescending(h => h.Effective)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ToArray();

        return new EncounterSnapshot(
            encounter.Id, encounter.TargetName, encounter.StartTime, encounter.EndTime,
            encounter.DurationSeconds, encounter.TotalDamage, encounter.IsActive,
            Array.AsReadOnly(participants), Array.AsReadOnly(taken),
            Array.AsReadOnly(encounter.Casts.ToArray()), Array.AsReadOnly(healing),
            Array.AsReadOnly(encounter.DeathLog.ToArray()), encounter.Killed,
            Array.AsReadOnly(encounter.PlayerCasts.ToArray()), encounter.BuildTimeline(), encounter.ShieldSummary())
        {
            Server = encounter.Server,
            LastSeconds = encounter.LastSeconds(),
            TankSpans = encounter.TankSpans.ToArray(),
        };
    }

    private static ValueList<PetLine>? PetsOf(ParticipantStats row, CombatEncounter encounter)
    {
        PetLine[] pets = row.Pets().Select(p => new PetLine(p.Pet, p.Damage, p.Hits, p.Misses, encounter.DeathsOf(p.Pet))).ToArray();
        return pets.Length == 0 ? null : new ValueList<PetLine>(pets);
    }

    /// <summary>
    /// Several mobs fought at once as one live fight: the totals of all of them, named after the mob taking the most damage plus how
    /// many others ("Blood of Ssraeshza +8"). Heals and spell casts are filed under every mob in progress, so they are counted once.
    /// The id is the negative of the lowest mob's, so it never clashes with a mob's own row.
    /// </summary>
    private static ValueList<PetLine>? CombinePets(IEnumerable<ParticipantSnapshot> rows, Func<string, int> deathsOf)
    {
        PetLine[] pets = rows.SelectMany(r => r.PetRows)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => new PetLine(g.Key, g.Sum(p => p.Damage), g.Sum(p => p.Hits), g.Sum(p => p.Misses), deathsOf(g.Key)))
            .OrderByDescending(p => p.Damage)
            .ToArray();
        return pets.Length == 0 ? null : new ValueList<PetLine>(pets);
    }

    public static EncounterSnapshot Combine(IReadOnlyList<EncounterSnapshot> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 1)
        {
            return parts[0];
        }

        EncounterSnapshot[] ordered = parts.OrderByDescending(p => p.TotalDamage).ThenBy(p => p.Id).ToArray();
        DateTime start = parts.Min(p => p.StartTime);
        DateTime end = parts.Max(p => p.EndTime);
        double duration = Math.Max(0.0, (end - start).TotalSeconds);
        double seconds = Math.Max(1.0, duration);
        long total = parts.Sum(p => p.TotalDamage);

        DeathSnapshot[] deaths = parts.SelectMany(p => p.DeathRows)
            .GroupBy(d => (d.Time, d.Name))
            .Select(g => g.OrderByDescending(d => d.KillingBlow.Length).First())
            .OrderBy(d => d.Time)
            .ToArray();
        int DeathsOf(string name) => deaths.Count(d => d.Name == name);

        ParticipantSnapshot[] participants = parts.SelectMany(p => p.Participants)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                long damage = g.Sum(p => p.TotalDamage);
                int hits = g.Sum(p => p.Hits);
                int misses = g.Sum(p => p.Misses);
                int crits = g.Sum(p => p.CritHitCount);
                double active = Math.Min(seconds, g.Sum(p => p.Dps > 0 ? p.TotalDamage / p.Dps : 0.0));
                var slices = g.SelectMany(p => p.Attacks ?? Array.Empty<AttackSlice>())
                    .GroupBy(a => a.Type)
                    .Select(a => new AttackSlice(a.Key, a.Sum(x => x.Normal), a.Sum(x => x.Crit)))
                    .OrderByDescending(a => a.Total).ThenBy(a => a.Type)
                    .ToList();
                return new ParticipantSnapshot(
                    g.Key, damage, total > 0 ? 100.0 * damage / total : 0.0, active > 0 ? damage / active : damage / seconds, damage / seconds,
                    g.Max(p => p.MaxHit), hits, misses, hits + misses == 0 ? 0.0 : (double)hits / (hits + misses),
                    g.Max(p => p.MaxCritHit), crits, hits == 0 ? 0.0 : 100.0 * crits / hits,
                    g.Select(p => p.Class).FirstOrDefault(c => c.Length > 0) ?? string.Empty,
                    DeathsOf(g.Key), g.Max(p => p.MaxNonCritHit), new AttackSliceList(slices), CombinePets(g, DeathsOf));
            })
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        long takenTotal = parts.Sum(p => p.TotalDamageTaken);
        DamageTakenSnapshot[] taken = parts.SelectMany(p => p.TakenRows)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                long damage = g.Sum(t => t.TotalDamage);
                return new DamageTakenSnapshot(
                    g.Key, damage, takenTotal > 0 ? 100.0 * damage / takenTotal : 0.0, g.Sum(t => t.Hits), g.Max(t => t.MaxHit), DeathsOf(g.Key),
                    g.Sum(t => t.Avoided), g.Sum(t => t.Dodges), g.Sum(t => t.Parries), g.Sum(t => t.Blocks), g.Sum(t => t.Ripostes),
                    g.Sum(t => t.Missed), g.Sum(t => t.OtherAvoids), g.Sum(t => t.ShieldDamage), g.Sum(t => t.Rampages), g.Sum(t => t.RampageDamage),
                    g.Sum(t => t.DotTicks), g.Sum(t => t.DotDamage));
            })
            .OrderByDescending(t => t.TotalDamage)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        // Heals went to every mob in progress: each healer's figures from the mob that was fought longest are the whole fight's.
        HealingSnapshot[] healing = parts.SelectMany(p => p.HealingRows)
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Effective).First() is { } h ? h with { Hps = h.Effective / seconds } : null!)
            .OrderByDescending(h => h.Effective)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ToArray();

        PlayerCastSnapshot[] casts = parts.SelectMany(p => p.PlayerCastRows).Distinct().OrderBy(c => c.Time).ToArray();
        BossCastSnapshot[] bossCasts = parts.SelectMany(p => p.CastRows).OrderBy(c => c.Time).ToArray();

        return new EncounterSnapshot(
            -parts.Min(p => p.Id), $"{ordered[0].TargetName} +{parts.Count - 1}", start, end, duration, total, parts.Any(p => p.IsActive),
            Array.AsReadOnly(participants), Array.AsReadOnly(taken), Array.AsReadOnly(bossCasts), Array.AsReadOnly(healing),
            Array.AsReadOnly(deaths), Killed: false, Array.AsReadOnly(casts), ordered[0].Timeline, null)
        {
            Server = ordered[0].Server,
            TankSpans = ordered[0].TankSpans,
            LastSeconds = parts.SelectMany(p => p.LastSeconds).GroupBy(w => w.Name, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(w => w.Died).ThenByDescending(w => w.End).First()).ToList(),
        };
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
    TimerOverlayCategory Category = TimerOverlayCategory.RaidTriggers,
    DateTime? PausedUtc = null,
    bool Hidden = false)
{
    /// <summary>A short note shown beside the bar's text, such as a debuff's counters ("3 disease").</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>True for the short green bar that replaces a debuff once a "cured of" line has removed it.</summary>
    public bool Cured { get; init; }

    /// <summary>The moment the bar's clock reads: <paramref name="nowUtc"/>, or the instant it was paused at while it is paused.</summary>
    private DateTime ClockAt(DateTime nowUtc) => PausedUtc is { } paused && paused < nowUtc ? paused : nowUtc;

    /// <summary>True while the bar's clock is stopped (the character is in a safe zone or logged out).</summary>
    public bool IsPaused => PausedUtc is not null;

    public bool IsExpired(DateTime nowUtc) => ClockAt(nowUtc) >= ExpiresUtc;

    public double RemainingSeconds(DateTime nowUtc) => Math.Max(0.0, (ExpiresUtc - ClockAt(nowUtc)).TotalSeconds);

    /// <summary>1.0 when just started, 0.0 when expired. Drives the bar width.</summary>
    public double FractionRemaining(DateTime nowUtc) =>
        DurationSeconds > 0 ? Math.Clamp(RemainingSeconds(nowUtc) / DurationSeconds, 0.0, 1.0) : 0.0;
}

public sealed record TriggerSnapshot(long Version, IReadOnlyList<ActiveTimerSnapshot> Timers)
{
    public static readonly TriggerSnapshot Empty = new(0, Array.AsReadOnly(Array.Empty<ActiveTimerSnapshot>()));

    /// <summary>Timers still running at <paramref name="nowUtc"/>. The engine only prunes when a trigger fires, so filter here. Hidden bars (a logged-out character's) are left out.</summary>
    public IReadOnlyList<ActiveTimerSnapshot> GetActive(DateTime nowUtc) =>
        Timers.Where(t => !t.Hidden && !t.IsExpired(nowUtc)).ToArray();

    /// <summary>Running timers of one overlay category (what that category's overlay window shows).</summary>
    public IReadOnlyList<ActiveTimerSnapshot> GetActive(DateTime nowUtc, TimerOverlayCategory category) =>
        Timers.Where(t => t.Category == category && !t.Hidden && !t.IsExpired(nowUtc)).ToArray();
}
