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

    /// <summary>Largest hit that was not a critical hit or blast (0 when every hit was a crit). This is the "Max Hit" the parse shows.</summary>
    public int MaxNonCritHit { get; private set; }

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

    private readonly long[] _attackNormal = new long[AttackTypes.All.Count];
    private readonly long[] _attackCrit = new long[AttackTypes.All.Count];

    /// <summary>Damage by attack type, regular and critical, for the types this player used (largest first).</summary>
    public IReadOnlyList<AttackSlice> AttackSlices()
    {
        var slices = new List<AttackSlice>();
        for (int i = 0; i < _attackNormal.Length; i++)
        {
            if (_attackNormal[i] + _attackCrit[i] > 0)
            {
                slices.Add(new AttackSlice((AttackType)i, _attackNormal[i], _attackCrit[i]));
            }
        }

        return new AttackSliceList(slices.OrderByDescending(a => a.Total).ThenBy(a => a.Type).ToList());
    }

    internal void RecordHit(DateTime timestamp, int amount, bool isCritical = false, AttackType attack = AttackType.Hit)
    {
        if (isCritical)
        {
            _attackCrit[(int)attack] += amount;
        }
        else
        {
            _attackNormal[(int)attack] += amount;
        }

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
        else if (amount > MaxNonCritHit)
        {
            MaxNonCritHit = amount;
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

/// <summary>What one healer healed during a fight.</summary>
internal sealed class HealStats
{
    private readonly Dictionary<string, long> _bySpell = new(StringComparer.Ordinal);

    public HealStats(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Points that actually landed.</summary>
    public long Effective { get; private set; }

    /// <summary>Points the spells could have healed beyond what landed.</summary>
    public long Overheal { get; private set; }

    public int Heals { get; private set; }
    public int MaxHeal { get; private set; }

    /// <summary>Points that landed from critical heals.</summary>
    public long CritEffective { get; private set; }

    public int CritHeals { get; private set; }

    public string TopSpell => _bySpell.Count == 0 ? string.Empty : _bySpell.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;

    public void Record(string spell, int actual, int possible, bool isCritical = false)
    {
        Heals++;
        Effective += actual;
        if (isCritical)
        {
            CritHeals++;
            CritEffective += actual;
        }

        if (possible > actual)
        {
            Overheal += possible - actual;
        }

        if (actual > MaxHeal)
        {
            MaxHeal = actual;
        }

        _bySpell[spell] = _bySpell.GetValueOrDefault(spell) + actual;
    }
}
