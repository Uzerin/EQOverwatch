using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Presentation;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One ranked participant row.</summary>
public sealed partial class DpsRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _rankName = string.Empty;

    /// <summary>"WAR", "Pet" or empty.</summary>
    [ObservableProperty]
    private string _classText = string.Empty;

    [ObservableProperty]
    private string _sdpsText = string.Empty;

    [ObservableProperty]
    private string _totalText = string.Empty;

    [ObservableProperty]
    private string _percentText = string.Empty;

    /// <summary>Damage relative to the top participant (0..1); drives the background bar.</summary>
    [ObservableProperty]
    private double _fill;

    /// <summary>True for one of the user's own characters (drawn in gold).</summary>
    [ObservableProperty]
    private bool _isMine;

    /// <summary>True for the first of the user's characters listed below the top rows (draws a dashed line above it).</summary>
    [ObservableProperty]
    private bool _startsMineSection;

    public void Update(int rank, ParticipantSnapshot participant, long topDamage, bool isMine = false, bool startsMineSection = false)
    {
        IsMine = isMine;
        StartsMineSection = startsMineSection;
        RankName = $"{rank}. {participant.Name}";
        ClassText = participant.Class;
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
    /// <summary>How many of the highest damage dealers are always shown.</summary>
    public const int MaxRows = 5;

    private readonly Func<DpsSnapshot> _source;
    private long _lastVersion = -1;

    private readonly Func<string, string>? _classOf;

    /// <summary>When set, classes are looked up on the server of the log that recorded the fight (used instead of the name-only lookup).</summary>
    public Func<string, string, string>? ClassOnServer { get; init; }
    private readonly Func<string, bool>? _isMine;

    /// <param name="classOf">Looks up a name's class label ("WAR", "Pet", or empty); null shows no classes.</param>
    /// <param name="isMine">True for the user's own characters: they are shown (with their real rank) even when outside the top rows.</param>
    public DpsOverlayViewModel(Func<DpsSnapshot> source, Func<string, string>? classOf = null, Func<string, bool>? isMine = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _classOf = classOf;
        _isMine = isMine;
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

        if (encounter is not null && _classOf is not null)
        {
            encounter = ClassOnServer is { } onServer ? encounter.WithClassesOnServer(onServer) : encounter.WithClasses(_classOf);
        }

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

        ParticipantSnapshot[] ranked = encounter.Participants
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        // The top rows, then every one of the user's own characters that is doing damage, with its real rank.
        var shown = new List<(int Rank, ParticipantSnapshot Participant, bool Mine, bool Starts)>();
        for (int i = 0; i < ranked.Length; i++)
        {
            bool mine = ranked[i].TotalDamage > 0 && _isMine?.Invoke(ranked[i].Name) == true;
            if (i < MaxRows)
            {
                shown.Add((i + 1, ranked[i], mine, false));
            }
            else if (mine)
            {
                bool first = shown.Count <= MaxRows; // the first one below the top rows starts the "mine" section
                shown.Add((i + 1, ranked[i], true, first));
            }
        }

        long topDamage = ranked.Length > 0 ? ranked[0].TotalDamage : 0;

        for (int i = 0; i < shown.Count; i++)
        {
            if (i >= Rows.Count)
            {
                Rows.Add(new DpsRowViewModel());
            }

            Rows[i].Update(shown[i].Rank, shown[i].Participant, topDamage, shown[i].Mine, shown[i].Starts);
        }

        while (Rows.Count > shown.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}
