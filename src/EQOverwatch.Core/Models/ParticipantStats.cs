using CommunityToolkit.Mvvm.ComponentModel;

namespace EQOverwatch.Core.Models;

/// <summary>
/// Per-player breakdown inside one <see cref="CombatEncounter"/>.
/// <para>
/// <b>DPS</b> = TotalDamage / the player's own active window (first to last action, minimum 1s).
/// <b>Scaled DPS</b> = TotalDamage / the whole encounter's duration (minimum 1s).
/// </para>
/// Hot-path mutation does not raise change notifications; <see cref="Recalculate"/> raises one for all properties.
/// </summary>
public sealed class ParticipantStats : ObservableObject
{
    public ParticipantStats(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public long TotalDamage { get; private set; }
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int MaxHit { get; private set; }

    /// <summary>Largest hit that was announced as a critical hit or blast.</summary>
    public int MaxCritHit { get; private set; }

    public int CritHitCount { get; private set; }

    /// <summary>CritHitCount / Hits x 100 (0 when there are no hits).</summary>
    public double CritRatePercent => Hits == 0 ? 0.0 : CritHitCount / (double)Hits * 100.0;
    public DateTime FirstActionTime { get; private set; }
    public DateTime LastActionTime { get; private set; }

    public double PercentOfGroupDamage { get; private set; }
    public double ScaledDps { get; private set; }

    public double ActiveSeconds => FirstActionTime == default
        ? 1.0
        : Math.Max(1.0, (LastActionTime - FirstActionTime).TotalSeconds);

    public double Dps => TotalDamage / ActiveSeconds;

    /// <summary>Hits / (Hits + Misses), 0..1. Zero when nothing has been attempted.</summary>
    public double HitRate => Hits + Misses == 0 ? 0.0 : (double)Hits / (Hits + Misses);

    public double MissRate => Hits + Misses == 0 ? 0.0 : (double)Misses / (Hits + Misses);

    internal void RecordHit(DateTime timestamp, int amount, bool isCritical = false)
    {
        Hits++;
        TotalDamage += amount;
        if (amount > MaxHit)
        {
            MaxHit = amount;
        }

        if (isCritical)
        {
            CritHitCount++;
            if (amount > MaxCritHit)
            {
                MaxCritHit = amount;
            }
        }

        Touch(timestamp);
    }

    internal void RecordMiss(DateTime timestamp)
    {
        Misses++;
        Touch(timestamp);
    }

    internal void Recalculate(long groupTotalDamage, double encounterSeconds)
    {
        PercentOfGroupDamage = groupTotalDamage > 0 ? 100.0 * TotalDamage / groupTotalDamage : 0.0;
        ScaledDps = encounterSeconds > 0 ? TotalDamage / encounterSeconds : 0.0;
        OnPropertyChanged(string.Empty);
    }

    private void Touch(DateTime timestamp)
    {
        if (FirstActionTime == default || timestamp < FirstActionTime)
        {
            FirstActionTime = timestamp;
        }

        if (timestamp > LastActionTime)
        {
            LastActionTime = timestamp;
        }
    }
}

/// <summary>Damage one player or pet took from a fight's target.</summary>
internal sealed class DamageTakenStats
{
    public DamageTakenStats(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public long TotalDamage { get; private set; }
    public int Hits { get; private set; }
    public int MaxHit { get; private set; }

    public void Record(int amount)
    {
        Hits++;
        TotalDamage += amount;
        if (amount > MaxHit)
        {
            MaxHit = amount;
        }
    }
}
