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

    partial void OnEncounterFromChanged(DateTime? value) => RefilterEncounters();

    partial void OnEncounterToChanged(DateTime? value) => RefilterEncounters();

    [RelayCommand]
    private void ResetEncounterDates()
    {
        _encounterFrom = null;
        _encounterTo = null;
        OnPropertyChanged(nameof(EncounterFrom));
        OnPropertyChanged(nameof(EncounterTo));
        RefilterEncounters();
    }

    /// <summary>True when the fight started inside the chosen days (log times are the PC's local time).</summary>
    private bool InEncounterDates(EncounterSnapshot encounter) =>
        (EncounterFrom is not { } from || encounter.StartTime.Date >= from.Date)
        && (EncounterTo is not { } to || encounter.StartTime.Date <= to.Date);

    private void ShowEncounterCount(int shown, int all) =>
        EncounterCountText = EncounterFrom is null && EncounterTo is null ? string.Empty : $"{shown:N0} of {all:N0} fight" + (all == 1 ? string.Empty : "s");
}
