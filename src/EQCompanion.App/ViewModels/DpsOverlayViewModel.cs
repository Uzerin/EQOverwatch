using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQCompanion.App.Presentation;
using EQCompanion.Core.Models;

namespace EQCompanion.App.ViewModels;

/// <summary>One ranked participant row.</summary>
public sealed partial class DpsRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _rankName = string.Empty;

    [ObservableProperty]
    private string _sdpsText = string.Empty;

    [ObservableProperty]
    private string _totalText = string.Empty;

    [ObservableProperty]
    private string _percentText = string.Empty;

    /// <summary>Damage relative to the top participant (0..1); drives the background bar.</summary>
    [ObservableProperty]
    private double _fill;

    public void Update(int rank, ParticipantSnapshot participant, long topDamage)
    {
        RankName = $"{rank}. {participant.Name}";
        SdpsText = OverlayFormat.Compact((long)Math.Round(participant.ScaledDps));
        TotalText = OverlayFormat.Compact(participant.TotalDamage);
        PercentText = participant.PercentOfGroupDamage.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        Fill = topDamage > 0 ? (double)participant.TotalDamage / topDamage : 0.0;
    }
}

/// <summary>
/// Drives the mini DPS overlay from a DPS tracker's snapshot. Shows the most recently active encounter, or the latest
/// completed one (dimmed via <see cref="IsLive"/>) when nothing is active.
/// </summary>
public sealed partial class DpsOverlayViewModel : ObservableObject
{
    public const string IdleTitle = "Waiting for combat…";
    public const int MaxRows = 8;

    private readonly Func<DpsSnapshot> _source;
    private long _lastVersion = -1;

    public DpsOverlayViewModel(Func<DpsSnapshot> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    [ObservableProperty]
    private string _targetName = IdleTitle;

    [ObservableProperty]
    private string _durationText = "00:00";

    [ObservableProperty]
    private string _groupDpsText = "0";

    /// <summary>True while the displayed encounter is still active.</summary>
    [ObservableProperty]
    private bool _isLive;

    public ObservableCollection<DpsRowViewModel> Rows { get; } = new();

    /// <summary>The encounter the overlay is showing right now, or null while it waits for combat.</summary>
    public EncounterSnapshot? DisplayedEncounter { get; private set; }

    /// <summary>The user clicked "Chat" in the overlay's title strip: copy this encounter's top attackers for EverQuest chat.</summary>
    public event Action<EncounterSnapshot>? CopyChatRequested;

    /// <summary>The user clicked "Img" in the overlay's title strip: copy this encounter as a Discord picture.</summary>
    public event Action<EncounterSnapshot>? CopyImageRequested;

    [RelayCommand]
    private void CopyChat()
    {
        if (DisplayedEncounter is { } encounter)
        {
            CopyChatRequested?.Invoke(encounter);
        }
    }

    [RelayCommand]
    private void CopyImage()
    {
        if (DisplayedEncounter is { } encounter)
        {
            CopyImageRequested?.Invoke(encounter);
        }
    }

    /// <summary>Call from the overlay's 250ms dispatcher timer. Does nothing when the snapshot has not changed.</summary>
    public void Tick()
    {
        DpsSnapshot snapshot = _source();
        if (snapshot.Version == _lastVersion)
        {
            return;
        }

        _lastVersion = snapshot.Version;

        EncounterSnapshot? encounter = snapshot.Active
            .OrderByDescending(e => e.EndTime)
            .ThenByDescending(e => e.Id)
            .FirstOrDefault()
            ?? (snapshot.Completed.Count > 0 ? snapshot.Completed[^1] : null);

        DisplayedEncounter = encounter;

        if (encounter is null)
        {
            TargetName = IdleTitle;
            DurationText = OverlayFormat.Duration(0);
            GroupDpsText = "0";
            IsLive = false;
            Rows.Clear();
            return;
        }

        TargetName = encounter.TargetName;
        DurationText = OverlayFormat.Duration(encounter.DurationSeconds);
        GroupDpsText = OverlayFormat.Compact((long)Math.Round(encounter.TotalDamage / Math.Max(1.0, encounter.DurationSeconds)));
        IsLive = encounter.IsActive;

        ParticipantSnapshot[] top = encounter.Participants
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToArray();

        long topDamage = top.Length > 0 ? top[0].TotalDamage : 0;

        for (int i = 0; i < top.Length; i++)
        {
            if (i >= Rows.Count)
            {
                Rows.Add(new DpsRowViewModel());
            }

            Rows[i].Update(i + 1, top[i], topDamage);
        }

        while (Rows.Count > top.Length)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}
