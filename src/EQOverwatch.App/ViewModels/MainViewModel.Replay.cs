using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>A row of the Graph tab's replay table: an attacker's standing at the replayed moment.</summary>
public sealed record ReplayRowView(int Rank, string Name, string Damage, string Dps, string Share);

// The Graph tab: the selected fight's timeline (with the boss's estimated health) and the fight replay slider.
public sealed partial class MainViewModel
{
    private DpsTimeline? _timelineSource;
    private string _timelineFight = string.Empty;
    private bool _settingReplay;

    /// <summary>Seconds into the fight the replay shows; at <see cref="ReplayMax"/> it is the whole fight.</summary>
    [ObservableProperty]
    private double _replaySeconds;

    /// <summary>The fight's length in seconds (the slider's end).</summary>
    [ObservableProperty]
    private double _replayMax;

    /// <summary>"Replay at 3:20 of 11:32", or "Whole fight (11:32)".</summary>
    [ObservableProperty]
    private string _replayTitle = string.Empty;

    /// <summary>Where the graph draws the replay line (null at the end of the fight).</summary>
    [ObservableProperty]
    private double? _replayCursor;

    /// <summary>The attackers' damage and DPS up to the replayed moment, highest first.</summary>
    public ObservableCollection<ReplayRowView> ReplayRows { get; } = new();

    partial void OnReplaySecondsChanged(double value)
    {
        if (!_settingReplay)
        {
            ShowReplay();
        }
    }

    /// <summary>Puts the selected fight's timeline on the Graph tab, with the boss's health line when its hit points are known.</summary>
    private void ShowTimeline(EncounterSnapshot? encounter)
    {
        DpsTimeline? source = encounter?.Timeline;
        string fight = encounter is null ? string.Empty : $"{encounter.TargetName}|{encounter.StartTime:O}";
        if (Equals(source, _timelineSource) && fight == _timelineFight)
        {
            return;
        }

        bool sameFight = fight == _timelineFight;
        bool atEnd = ReplaySeconds >= ReplayMax - 0.001;
        _timelineSource = source;
        _timelineFight = fight;
        EncounterTimeline = source is not null && encounter is not null && RegenFor(encounter.TargetName) is { } regen
            ? source.WithBossHp(regen.HitPoints, regen.RegenPerSecond)
            : source;

        _settingReplay = true;
        ReplayMax = source?.Span ?? 0;
        if (!sameFight || atEnd)
        {
            ReplaySeconds = ReplayMax;
        }

        _settingReplay = false;
        ShowReplay();
    }

    /// <summary>Fills the replay table for the slider's position.</summary>
    private void ShowReplay()
    {
        DpsTimeline? timeline = EncounterTimeline;
        if (timeline is null)
        {
            ReplayRows.Clear();
            ReplayTitle = string.Empty;
            ReplayCursor = null;
            return;
        }

        double at = Math.Clamp(ReplaySeconds, 0, timeline.Span);
        bool end = at >= timeline.Span - 0.001;
        ReplayCursor = end ? null : at;
        ReplayTitle = end
            ? $"Whole fight ({DpsGraph.FormatTime(timeline.Span)})"
            : $"At {DpsGraph.FormatTime(at)} of {DpsGraph.FormatTime(timeline.Span)}";

        IReadOnlyList<(string Name, long Damage, double Dps)> standings = timeline.Standings(at);
        long sum = standings.Sum(s => s.Damage);
        ReplayRows.Clear();
        for (int i = 0; i < standings.Count; i++)
        {
            (string name, long damage, double dps) = standings[i];
            ReplayRows.Add(new ReplayRowView(
                i + 1,
                name,
                damage.ToString("N0", CultureInfo.CurrentCulture),
                dps.ToString("N0", CultureInfo.CurrentCulture),
                sum > 0 ? (100.0 * damage / sum).ToString("0.0", CultureInfo.CurrentCulture) + "%" : "–"));
        }
    }
}
