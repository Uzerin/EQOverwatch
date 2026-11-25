using CommunityToolkit.Mvvm.ComponentModel;

namespace EQOverwatch.App.ViewModels;

// How the app looks: interface size (50% to 150%), compact mode, and whether minimizing goes to the system tray.
public sealed partial class MainViewModel
{
    /// <summary>Interface size in percent, 50 to 150 (overlays are not affected).</summary>
    [ObservableProperty]
    private double _uiScalePercent = 100;

    [ObservableProperty]
    private bool _compactMode;

    /// <summary>Minimizing hides the window to the system tray (the ✕ still exits).</summary>
    [ObservableProperty]
    private bool _minimizeToTray = true;

    /// <summary>"100%".</summary>
    public string UiScaleText => $"{Math.Round(UiScalePercent):0}%";

    partial void OnUiScalePercentChanged(double value)
    {
        OnPropertyChanged(nameof(UiScaleText));
        ApplyLook();
        SaveUnlessRestoring();
    }

    partial void OnCompactModeChanged(bool value)
    {
        ApplyLook();
        SaveUnlessRestoring();
    }

    partial void OnMinimizeToTrayChanged(bool value) => SaveUnlessRestoring();

    private void ApplyLook()
    {
        if (System.Windows.Application.Current is not null)
        {
            Interop.DarkWindowChrome.SetLook(Math.Clamp(UiScalePercent, 50, 150) / 100.0, CompactMode);
        }
    }

    private void RestoreLook(Core.Settings.AppSettings settings)
    {
        UiScalePercent = Math.Clamp(settings.UiScalePercent is > 0 ? settings.UiScalePercent : 100, 50, 150);
        CompactMode = settings.CompactMode;
        MinimizeToTray = settings.MinimizeToTray;
        ApplyLook();
    }
}
