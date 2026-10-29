using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.App.Presentation;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One line of the encounter browser.</summary>
public sealed partial class EncounterRowViewModel : ObservableObject
{
    public EncounterRowViewModel(int id, bool isImported = false)
    {
        Id = id;
        IsImported = isImported;
    }

    /// <summary>Id within its tracker (the live and the imported tracker number their fights separately).</summary>
    public int Id { get; }

    /// <summary>True for a fight loaded with "Import log file…"; false for one seen live or caught up from the Primary DPS log.</summary>
    public bool IsImported { get; }

    /// <summary>Unique across both trackers.</summary>
    public long Key => ((IsImported ? 1L : 0L) << 32) | (uint)Id;

    /// <summary>The snapshot this row currently shows (replaced on every update).</summary>
    public EncounterSnapshot? Snapshot { get; private set; }

    [ObservableProperty]
    private string _targetName = string.Empty;

    [ObservableProperty]
    private string _startText = string.Empty;

    [ObservableProperty]
    private DateTime _startTime;

    [ObservableProperty]
    private string _durationText = "00:00";

    /// <summary>Numeric duration, used for sorting the column.</summary>
    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private long _totalDamage;

    [ObservableProperty]
    private double _groupDps;

    [ObservableProperty]
    private bool _isActive;

    public void Update(EncounterSnapshot snapshot)
    {
        Snapshot = snapshot;
        TargetName = snapshot.TargetName;
        StartTime = snapshot.StartTime;
        StartText = snapshot.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        DurationSeconds = snapshot.DurationSeconds;
        DurationText = OverlayFormat.Duration(snapshot.DurationSeconds);
        TotalDamage = snapshot.TotalDamage;
        GroupDps = snapshot.TotalDamage / Math.Max(1.0, snapshot.DurationSeconds);
        IsActive = snapshot.IsActive;
    }
}

/// <summary>One row of the participant breakdown grid. Numeric properties so the grid sorts and formats them natively.</summary>
public sealed partial class ParticipantRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _rank;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>"WAR", "Pet" or empty when the class is not known.</summary>
    [ObservableProperty]
    private string _class = string.Empty;

    [ObservableProperty]
    private long _totalDamage;

    [ObservableProperty]
    private double _damagePercent;

    [ObservableProperty]
    private double _scaledDps;

    [ObservableProperty]
    private double _dps;

    [ObservableProperty]
    private int _maxHit;

    [ObservableProperty]
    private int _hits;

    [ObservableProperty]
    private int _misses;

    [ObservableProperty]
    private double _hitRatePercent;

    /// <summary>Largest hit that was announced as a critical hit or blast.</summary>
    [ObservableProperty]
    private int _maxCritHit;

    /// <summary>Critical hits as a percentage of all hits.</summary>
    [ObservableProperty]
    private double _critRatePercent;

    /// <summary>Times the player died during the fight.</summary>
    [ObservableProperty]
    private int _deaths;

    /// <summary>Damage relative to the top attacker (0 to 1): the length of the bar.</summary>
    [ObservableProperty]
    private double _barFraction;

    /// <summary>The name, followed by " (Deaths: 2)" when the player died during the fight.</summary>
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnDeathsChanged(int value) => OnPropertyChanged(nameof(DisplayName));

    public void Update(int rank, ParticipantSnapshot participant, long topDamage = 0)
    {
        Rank = rank;
        Name = participant.Name;
        Deaths = participant.Deaths;
        BarFraction = topDamage > 0 ? (double)participant.TotalDamage / topDamage : 0.0;
        Class = participant.Class;
        TotalDamage = participant.TotalDamage;
        DamagePercent = participant.PercentOfGroupDamage;
        ScaledDps = participant.ScaledDps;
        Dps = participant.Dps;
        MaxHit = participant.MaxHit;
        Hits = participant.Hits;
        Misses = participant.Misses;
        HitRatePercent = participant.HitRate * 100.0;
        MaxCritHit = participant.MaxCritHit;
        CritRatePercent = participant.CritRatePercent;
    }
}

/// <summary>One row of the damage-taken grid.</summary>
public sealed partial class DamageTakenRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _rank;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _deaths;

    [ObservableProperty]
    private long _totalDamage;

    [ObservableProperty]
    private double _percentOfTotal;

    [ObservableProperty]
    private int _hits;

    [ObservableProperty]
    private int _maxHit;

    /// <summary>Damage relative to the player who took the most (0 to 1): the length of the bar.</summary>
    [ObservableProperty]
    private double _barFraction;

    /// <summary>The name, followed by " (Deaths: 2)" when the player died during the fight.</summary>
    public string DisplayName => Deaths > 0 ? $"{Name} (Deaths: {Deaths})" : Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnDeathsChanged(int value) => OnPropertyChanged(nameof(DisplayName));

    public void Update(int rank, DamageTakenSnapshot taken, long topDamage)
    {
        Rank = rank;
        Name = taken.Name;
        Deaths = taken.Deaths;
        TotalDamage = taken.TotalDamage;
        PercentOfTotal = taken.PercentOfTotal;
        Hits = taken.Hits;
        MaxHit = taken.MaxHit;
        BarFraction = topDamage > 0 ? (double)taken.TotalDamage / topDamage : 0.0;
    }
}
