using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

// Retired characters: kept with all their data, but hidden from the Characters list and with their triggers off.
public sealed partial class MainViewModel
{
    /// <summary>Lists retired characters too (not saved: the list opens without them).</summary>
    [ObservableProperty]
    private bool _showRetired;

    partial void OnShowRetiredChanged(bool value) => RebuildSortedCharacters();

    [RelayCommand]
    private void RetireLog(WatchedLogConfig? log)
    {
        if (log is null)
        {
            return;
        }

        log.IsRetired = !log.IsRetired;
        log.IsTriggerEnabled = !log.IsRetired; // retired: no triggers, timers or overlays; unretired: back on
        RebuildSortedCharacters();
        RequestSave();
        StatusMessage = log.IsRetired
            ? $"{log.CharacterName} is retired. Tick Show retired on the Characters tab to bring it back."
            : $"{log.CharacterName} is back.";
    }
}
