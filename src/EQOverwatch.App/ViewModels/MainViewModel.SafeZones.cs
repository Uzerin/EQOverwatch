using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

// The zones where buff and debuff bars stop counting (the game does not tick buffs there).
public sealed partial class MainViewModel
{
    /// <summary>One zone per line. A character in one of these zones has its buff and debuff bars stopped until it leaves.</summary>
    [ObservableProperty]
    private string _safeZonesText = string.Empty;

    partial void OnSafeZonesTextChanged(string value)
    {
        _pipeline.TimerPause.SetSafeZones(SafeZoneList());
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>The zones typed in the box, one per line, without blanks or repeats.</summary>
    private List<string> SafeZoneList() =>
        (SafeZonesText ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [RelayCommand]
    private void ResetSafeZones() => SafeZonesText = string.Join(Environment.NewLine, TimerPauseService.DefaultSafeZones);
}
