using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

// Auto-click on your cleric's turn in the CH chain: the switch, its delay, the click spot and its hotkey (Settings → Cleric CH chain →
// Advanced). While it is on, a red outline goes round the CH Chain overlay (or Raid Triggers when CH Chain is off).
public sealed partial class MainViewModel
{
    /// <summary>On: the left mouse button is clicked once on your turn in the chain, while EverQuest is the window in front. Kept as left.</summary>
    [ObservableProperty]
    private bool _autoClickEnabled;

    /// <summary>Milliseconds between the chain cue and the click.</summary>
    [ObservableProperty]
    private int _autoClickDelayMilliseconds;

    /// <summary>The global hotkey that switches the click on and off.</summary>
    [ObservableProperty]
    private string _autoClickHotkey = AutoClickService.DefaultHotkey;

    /// <summary>Where in the EverQuest window it clicks; null clicks wherever the pointer is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickSpotText), nameof(HasAutoClickSpot))]
    private ClickSpot? _autoClickSpot;

    /// <summary>True: press a fixed time after your own last Complete Heal (chat ignored for pressing). False: on the chat cue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickOnChat))]
    private bool _autoClickTimed;

    /// <summary>The other radio button of <see cref="AutoClickTimed"/>.</summary>
    public bool AutoClickOnChat
    {
        get => !AutoClickTimed;
        set => AutoClickTimed = !value;
    }

    /// <summary>The timed interval as typed, 10.0 to 60.0 seconds.</summary>
    [ObservableProperty]
    private string _autoClickIntervalText = AutoClickService.DefaultIntervalSeconds.ToString("0.0", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private string _autoClickIntervalError = string.Empty;

    /// <summary>True: press <see cref="AutoClickKey"/>. False: click the mouse.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickMouse))]
    private bool _autoClickPressKey;

    /// <summary>The other radio button of <see cref="AutoClickPressKey"/>.</summary>
    public bool AutoClickMouse
    {
        get => !AutoClickPressKey;
        set => AutoClickPressKey = !value;
    }

    /// <summary>The key pressed in key mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickKeyText))]
    private KeyChord _autoClickKey = new(0x31);

    /// <summary>True between Set Key… and the next key pressed in the Advanced window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickKeyText))]
    private bool _isRecordingAutoClickKey;

    public string AutoClickKeyText => IsRecordingAutoClickKey ? "Press a key…" : AutoClickKey.ToString();

    [ObservableProperty]
    private string _autoClickStatus = "Off.";

    [ObservableProperty]
    private string _autoClickHotkeyNote = string.Empty;

    [ObservableProperty]
    private string _autoClickSpotNote = string.Empty;

    /// <summary>"412, 655 in the EverQuest window" or "Where the mouse pointer is".</summary>
    public string AutoClickSpotText => AutoClickSpot is { } spot
        ? string.Create(CultureInfo.CurrentCulture, $"{spot.X}, {spot.Y} in the EverQuest window")
        : "Where the mouse pointer is";

    public bool HasAutoClickSpot => AutoClickSpot is not null;

    /// <summary>Raised when the hotkey text changes, so the window can register the new one.</summary>
    public event Action? AutoClickHotkeyChanged;

    /// <summary>Raised by the Advanced… button: the main window opens the auto-click window.</summary>
    public event Action? ShowAutoClickRequested;

    /// <summary>Raised by Set Position…: the auto-click window runs the picker and calls <see cref="ApplyAutoClickSpot"/>.</summary>
    public event Action? PickAutoClickSpotRequested;

    partial void OnAutoClickEnabledChanged(bool value)
    {
        _pipeline.AutoClick.SetArmed(value);
        UpdateAutoClickStatus();
        UpdateArmedOutline();
        if (!_restoring)
        {
            RequestSave();
        }
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

    partial void OnAutoClickSpotChanged(ClickSpot? value)
    {
        _pipeline.AutoClick.Spot = value;
        UpdateAutoClickStatus();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnAutoClickTimedChanged(bool value)
    {
        _pipeline.AutoClick.Timing = value ? AutoClickTiming.Timed : AutoClickTiming.ChatCue;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    partial void OnAutoClickIntervalTextChanged(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds)
            && seconds >= AutoClickService.MinIntervalSeconds && seconds <= AutoClickService.MaxIntervalSeconds)
        {
            _pipeline.AutoClick.IntervalSeconds = seconds;
            AutoClickIntervalError = Math.Round(seconds, 1) != seconds ? "Rounded to a tenth of a second." : string.Empty;
            UpdateAutoClickStatus();
            SaveUnlessRestoring();
        }
        else
        {
            AutoClickIntervalError = "Between 10.0 and 60.0 seconds, in tenths. The last good value is kept.";
        }
    }

    partial void OnAutoClickPressKeyChanged(bool value)
    {
        _pipeline.AutoClick.Action = value ? AutoClickAction.KeyPress : AutoClickAction.MouseClick;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    partial void OnAutoClickKeyChanged(KeyChord value)
    {
        _pipeline.AutoClick.Key = value;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    [RelayCommand]
    private void SetAutoClickKey() => IsRecordingAutoClickKey = true;

    /// <summary>The Advanced window passes the next key pressed after Set Key… (Esc cancels).</summary>
    public void RecordAutoClickKey(KeyChord? key)
    {
        if (key is { } chord)
        {
            AutoClickKey = chord;
        }

        IsRecordingAutoClickKey = false;
    }

    partial void OnAutoClickHotkeyChanged(string value)
    {
        AutoClickHotkeyChanged?.Invoke();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    [RelayCommand]
    private void OpenAutoClickAdvanced() => ShowAutoClickRequested?.Invoke();

    [RelayCommand]
    private void SetAutoClickSpot()
    {
        AutoClickSpotNote = string.Empty;
        PickAutoClickSpotRequested?.Invoke();
    }

    [RelayCommand]
    private void UseMousePointer()
    {
        AutoClickSpot = null;
        AutoClickSpotNote = "It clicks wherever the mouse pointer is.";
    }

    /// <summary>The picker's result: the spot clicked, or null when it was cancelled (with <paramref name="problem"/> when something was wrong).</summary>
    public void ApplyAutoClickSpot(ClickSpot? spot, string? problem = null)
    {
        if (spot is { } picked)
        {
            AutoClickSpot = picked;
            AutoClickSpotNote = "Saved. That click was kept from the game.";
        }
        else
        {
            AutoClickSpotNote = problem ?? "Cancelled: the spot was not changed.";
        }
    }

    /// <summary>Switches the click on or off; the global hotkey calls this.</summary>
    public void ToggleAutoClick() => AutoClickEnabled = !AutoClickEnabled;

    /// <summary>The window reports whether Windows accepted the hotkey.</summary>
    public void ReportAutoClickHotkey(bool registered) =>
        AutoClickHotkeyNote = registered
            ? $"{AutoClickHotkey} switches it on and off from any program."
            : $"{AutoClickHotkey} could not be used as a hotkey (it may be invalid, or another program has it). The checkbox still works. Write it like Ctrl+Alt+F9.";

    /// <summary>The overlay that gets the red "auto-click is on" outline: CH Chain, or Raid Triggers when CH Chain is off; none while off.</summary>
    public OverlayKind? ArmedOutlineOverlay => AutoClickEnabled ? IsChChainOverlayVisible ? OverlayKind.ChChain : OverlayKind.RaidTriggers : null;

    private void UpdateArmedOutline()
    {
        if (!_restoring)
        {
            _overlays.SetArmedOutline(ArmedOutlineOverlay);
        }
    }

    /// <summary>Called from <see cref="Refresh"/>: keeps the checkbox and the status line current.</summary>
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
        if (!click.IsArmed)
        {
            AutoClickStatus = "Off.";
            return;
        }

        IReadOnlyDictionary<string, int> positions = _pipeline.ChChain.Positions();
        if (positions.Count == 0 && !AutoClickTimed)
        {
            AutoClickStatus = "On, but none of your characters holds a CH Chain position (Characters tab), so it will not click.";
            return;
        }

        string cue = _pipeline.ChChain.Settings.CueKeyword;
        string how = AutoClickPressKey ? $"Presses {AutoClickKey}" : AutoClickSpot is null ? "Clicks where the pointer is" : "Clicks at the saved spot";
        string done = $"{click.Clicks:N0} press(es) so far, {click.Skipped:N0} turn(s) skipped.";
        if (AutoClickTimed)
        {
            string seconds = click.IntervalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
            AutoClickStatus = click.NextTimedUtc is null
                ? $"On, timed: waiting for your first Complete Heal, then {how.ToLowerInvariant()} every {seconds} s after your last one. Chat cues only show on the overlay. {done}"
                : $"On, timed: {how.ToLowerInvariant()} every {seconds} s after your last Complete Heal. Chat cues only show on the overlay. {done}";
            return;
        }

        string who = string.Join("; ", positions.OrderBy(p => p.Value)
            .Select(p => $"{p.Key} at position {p.Value}, on \"{_pipeline.ChChain.TagFor(p.Value)} {cue}\""));
        AutoClickStatus = $"On. {how} for {who}. {done} It follows position and tag changes straight away.";
    }
}
