using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

/// <summary>Which key Set Key… is recording.</summary>
public enum AutoClickKeySlot
{
    None,
    Ch,
    Assist,
}

// Auto-press on your cleric's turn in the CH chain: the switch, the timing, the CH key and the assist key, and the hotkey (Settings →
// Cleric CH chain → Advanced). It only presses keys. While it is on, a red outline goes round the CH Chain overlay (or Raid Triggers
// when CH Chain is off).
public sealed partial class MainViewModel
{
    /// <summary>On: your CH key is pressed on your turn in the chain, while EverQuest is the window in front. Kept as left.</summary>
    [ObservableProperty]
    private bool _autoClickEnabled;

    /// <summary>The global hotkey that switches it on and off.</summary>
    [ObservableProperty]
    private string _autoClickHotkey = AutoClickService.DefaultHotkey;

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

    /// <summary>The Complete Heal key.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickKeyText))]
    private KeyChord _autoClickKey = AutoClickService.DefaultChKey;

    /// <summary>The assist key, pressed 1.0 s and 0.5 s before the CH key while <see cref="AutoClickUseAssist"/> is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickAssistKeyText))]
    private KeyChord _autoClickAssistKey = AutoClickService.DefaultAssistKey;

    [ObservableProperty]
    private bool _autoClickUseAssist;

    /// <summary>The key Set Key… is waiting for, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoClickKeyText), nameof(AutoClickAssistKeyText), nameof(IsRecordingAutoClickKey))]
    private AutoClickKeySlot _recordingAutoClickKey;

    /// <summary>True between Set Key… and the next key pressed in the Advanced window.</summary>
    public bool IsRecordingAutoClickKey => RecordingAutoClickKey != AutoClickKeySlot.None;

    public string AutoClickKeyText => RecordingAutoClickKey == AutoClickKeySlot.Ch ? "Press a key…" : AutoClickKey.ToString();

    public string AutoClickAssistKeyText => RecordingAutoClickKey == AutoClickKeySlot.Assist ? "Press a key…" : AutoClickAssistKey.ToString();

    [ObservableProperty]
    private string _autoClickStatus = "Off.";

    [ObservableProperty]
    private string _autoClickHotkeyNote = string.Empty;

    /// <summary>Raised when the hotkey text changes, so the window can register the new one.</summary>
    public event Action? AutoClickHotkeyChanged;

    /// <summary>Raised by the Advanced… button: the main window opens the auto-press window.</summary>
    public event Action? ShowAutoClickRequested;

    partial void OnAutoClickEnabledChanged(bool value)
    {
        _pipeline.AutoClick.SetArmed(value);
        UpdateAutoClickStatus();
        UpdateArmedOutline();
        SaveUnlessRestoring();
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

    partial void OnAutoClickKeyChanged(KeyChord value)
    {
        _pipeline.AutoClick.Key = value;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    partial void OnAutoClickAssistKeyChanged(KeyChord value)
    {
        _pipeline.AutoClick.AssistKey = value;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    partial void OnAutoClickUseAssistChanged(bool value)
    {
        _pipeline.AutoClick.UseAssist = value;
        UpdateAutoClickStatus();
        SaveUnlessRestoring();
    }

    partial void OnAutoClickHotkeyChanged(string value)
    {
        AutoClickHotkeyChanged?.Invoke();
        SaveUnlessRestoring();
    }

    [RelayCommand]
    private void OpenAutoClickAdvanced() => ShowAutoClickRequested?.Invoke();

    [RelayCommand]
    private void SetAutoClickKey() => RecordingAutoClickKey = AutoClickKeySlot.Ch;

    [RelayCommand]
    private void SetAutoClickAssistKey() => RecordingAutoClickKey = AutoClickKeySlot.Assist;

    /// <summary>The Advanced window passes the next key pressed after Set Key… (null: Esc, cancelled).</summary>
    public void RecordAutoClickKey(KeyChord? key)
    {
        if (key is { } chord)
        {
            if (RecordingAutoClickKey == AutoClickKeySlot.Ch)
            {
                AutoClickKey = chord;
            }
            else if (RecordingAutoClickKey == AutoClickKeySlot.Assist)
            {
                AutoClickAssistKey = chord;
            }
        }

        RecordingAutoClickKey = AutoClickKeySlot.None;
    }

    /// <summary>Switches it on or off; the global hotkey calls this.</summary>
    public void ToggleAutoClick() => AutoClickEnabled = !AutoClickEnabled;

    /// <summary>The window reports whether Windows accepted the hotkey.</summary>
    public void ReportAutoClickHotkey(bool registered) =>
        AutoClickHotkeyNote = registered
            ? $"{AutoClickHotkey} switches it on and off from any program."
            : $"{AutoClickHotkey} could not be used as a hotkey (it may be invalid, or another program has it). The checkbox still works. Write it like Ctrl+Alt+F9.";

    /// <summary>The overlay that gets the red "auto-press is on" outline: CH Chain, or Raid Triggers when CH Chain is off; none while off.</summary>
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
        AutoClickService press = _pipeline.AutoClick;
        if (!press.IsArmed)
        {
            AutoClickStatus = "Off.";
            return;
        }

        string presses = AutoClickUseAssist ? $"presses {AutoClickAssistKey} at 1.0 s and 0.5 s before, then {AutoClickKey}" : $"presses {AutoClickKey}";
        string done = $"{press.Clicks:N0} press(es) so far, {press.Skipped:N0} turn(s) skipped.";
        if (AutoClickTimed)
        {
            string seconds = press.IntervalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
            AutoClickStatus = press.NextTimedUtc is null
                ? $"On, timed: waiting for your first Complete Heal, then {presses}, {seconds} s after your last one. Chat cues only show on the overlay. {done}"
                : $"On, timed: {presses}, {seconds} s after your last Complete Heal. Chat cues only show on the overlay. {done}";
            return;
        }

        IReadOnlyDictionary<string, int> positions = _pipeline.ChChain.Positions();
        if (positions.Count == 0)
        {
            AutoClickStatus = "On, but none of your characters holds a CH Chain position (Characters tab), so it will not press.";
            return;
        }

        string cue = _pipeline.ChChain.Settings.CueKeyword;
        string who = string.Join("; ", positions.OrderBy(p => p.Value)
            .Select(p => $"{p.Key} at position {p.Value}, on \"{_pipeline.ChChain.TagFor(p.Value)} {cue}\""));
        AutoClickStatus = $"On. {who}: {presses}. {done} It follows position and tag changes straight away.";
    }
}
