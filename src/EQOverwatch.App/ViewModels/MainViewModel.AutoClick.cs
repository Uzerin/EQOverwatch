using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

// Auto-click on your cleric's turn in the CH chain: the switch, its delay and its hotkey.
public sealed partial class MainViewModel
{
    /// <summary>On: the left mouse button is clicked once on your turn in the chain, while EverQuest is the window in front. Always starts off.</summary>
    [ObservableProperty]
    private bool _autoClickEnabled;

    /// <summary>Milliseconds between the chain cue and the click.</summary>
    [ObservableProperty]
    private int _autoClickDelayMilliseconds;

    /// <summary>The global hotkey that switches the click on and off.</summary>
    [ObservableProperty]
    private string _autoClickHotkey = AutoClickService.DefaultHotkey;

    [ObservableProperty]
    private string _autoClickStatus = "Off.";

    [ObservableProperty]
    private string _autoClickHotkeyNote = string.Empty;

    /// <summary>Raised when the hotkey text changes, so the window can register the new one.</summary>
    public event Action? AutoClickHotkeyChanged;

    partial void OnAutoClickEnabledChanged(bool value)
    {
        _pipeline.AutoClick.SetArmed(value);
        UpdateAutoClickStatus();
    }

    partial void OnAutoClickDelayMillisecondsChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, AutoClickService.MaxDelayMilliseconds);
        if (clamped != value)
        {
            AutoClickDelayMilliseconds = clamped;
            return;
        }

        _pipeline.AutoClick.DelayMilliseconds = clamped;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnAutoClickHotkeyChanged(string value)
    {
        AutoClickHotkeyChanged?.Invoke();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>Switches the click on or off; the global hotkey calls this.</summary>
    public void ToggleAutoClick() => AutoClickEnabled = !AutoClickEnabled;

    /// <summary>The window reports whether Windows accepted the hotkey.</summary>
    public void ReportAutoClickHotkey(bool registered) =>
        AutoClickHotkeyNote = registered
            ? $"{AutoClickHotkey} switches it on and off from any program."
            : $"{AutoClickHotkey} could not be used as a hotkey (it may be invalid, or another program has it). The checkbox still works. Write it like Ctrl+Alt+F9.";

    /// <summary>Called from <see cref="Refresh"/>: the click can switch itself off, so the checkbox follows it.</summary>
    private void SyncAutoClick()
    {
        bool armed = _pipeline.AutoClick.IsArmed;
        if (armed != AutoClickEnabled)
        {
            AutoClickEnabled = armed;
        }

        UpdateAutoClickStatus();
    }

    private void UpdateAutoClickStatus()
    {
        AutoClickService click = _pipeline.AutoClick;
        AutoClickStatus = click.IsArmed
            ? $"On. Clicks once per turn at the mouse pointer while EverQuest is the window in front. {click.Clicks:N0} click(s) so far, {click.Skipped:N0} turn(s) skipped. " +
              "It switches itself off after five minutes with no chain cue."
            : "Off.";
    }
}
