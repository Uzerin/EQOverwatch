using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

// The Encounter DPS tab's date filter: fights that started from Start to End (both days included); an empty box leaves that side open.
public sealed partial class MainViewModel
{
    [ObservableProperty]
    private DateTime? _encounterFrom;

    [ObservableProperty]
    private DateTime? _encounterTo;

    /// <summary>"24 of 500 fights" while a date is set; empty otherwise.</summary>
    [ObservableProperty]
    private string _encounterCountText = string.Empty;

    /// <summary>Part of a mob's name: only fights against matching mobs are listed.</summary>
    [ObservableProperty]
    private string _encounterNameFilter = string.Empty;

    partial void OnEncounterNameFilterChanged(string value) => RefilterEncounters();

    partial void OnEncounterFromChanged(DateTime? value) => RefilterEncounters();

    partial void OnEncounterToChanged(DateTime? value) => RefilterEncounters();

    [RelayCommand]
    private void ResetEncounterDates()
    {
        _encounterFrom = null;
        _encounterTo = null;
        _encounterNameFilter = string.Empty;
        OnPropertyChanged(nameof(EncounterFrom));
        OnPropertyChanged(nameof(EncounterTo));
        OnPropertyChanged(nameof(EncounterNameFilter));
        RefilterEncounters();
    }

    /// <summary>True when the fight started inside the chosen days (log times are the PC's local time).</summary>
    private bool InEncounterDates(EncounterSnapshot encounter) =>
        (EncounterNameFilter.Trim().Length == 0 || encounter.TargetName.Contains(EncounterNameFilter.Trim(), StringComparison.OrdinalIgnoreCase))
        && (EncounterFrom is not { } from || encounter.StartTime.Date >= from.Date)
        && (EncounterTo is not { } to || encounter.StartTime.Date <= to.Date);

    private void ShowEncounterCount(int shown, int all) =>
        EncounterCountText = EncounterFrom is null && EncounterTo is null && EncounterNameFilter.Trim().Length == 0 ? string.Empty : $"{shown:N0} of {all:N0} fight" + (all == 1 ? string.Empty : "s");
}
