using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>A spot inside the EverQuest window, in pixels from the top-left corner of its client area.</summary>
public readonly record struct ClickSpot(int X, int Y);

/// <summary>What starts the auto-click: the chain's chat cue for your position, or a fixed time after your own last Complete Heal.</summary>
public enum AutoClickTiming
{
    ChatCue,
    Timed,
}

/// <summary>What the auto-click does: a left mouse click, or a key press.</summary>
public enum AutoClickAction
{
    MouseClick,
    KeyPress,
}

/// <summary>A key with the Shift, Ctrl and Alt keys held with it, as Windows virtual-key codes. Written like "Shift+F3" or "1".</summary>
public readonly record struct KeyChord(int VirtualKey, bool Ctrl = false, bool Alt = false, bool Shift = false)
{
    public override string ToString()
    {
        string name = VirtualKey switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),
            >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x6F).ToString(System.Globalization.CultureInfo.InvariantCulture),
            >= 0x60 and <= 0x69 => "Num" + (VirtualKey - 0x60).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => NameOf(VirtualKey),
        };
        return (Ctrl ? "Ctrl+" : string.Empty) + (Alt ? "Alt+" : string.Empty) + (Shift ? "Shift+" : string.Empty) + name;
    }

    private static string NameOf(int virtualKey) =>
        Names.FirstOrDefault(n => n.Value == virtualKey).Key ?? "Key" + virtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27,
        ["Down"] = 0x28, ["Minus"] = 0xBD, ["Equals"] = 0xBB, ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["Semicolon"] = 0xBA, ["Quote"] = 0xDE, ["LeftBracket"] = 0xDB, ["RightBracket"] = 0xDD, ["Backslash"] = 0xDC, ["Tilde"] = 0xC0,
    };

    /// <summary>Reads "Shift+F3", "Ctrl+Alt+1" or "Num5". False for anything else.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool ctrl = false, alt = false, shift = false;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (string modifier in parts[..^1])
        {
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL": ctrl = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                default: return false;
            }
        }

        string key = parts[^1];
        int vk;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            vk = char.ToUpperInvariant(key[0]);
        }
        else if (key.Length >= 2 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            vk = 0x6F + f;
        }
        else if (key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && key.Length == 4 && char.IsAsciiDigit(key[3]))
        {
            vk = 0x60 + (key[3] - '0');
        }
        else if (!Names.TryGetValue(key, out vk))
        {
            return false;
        }

        chord = new KeyChord(vk, ctrl, alt, shift);
        return true;
    }
}

/// <summary>What the auto-click needs from the operating system. The app supplies the Windows version; tests supply a fake.</summary>
public interface IClickSender
{
    /// <summary>True when the EverQuest client is the window in front (and not minimized).</summary>
    bool IsGameFocused();

    /// <summary>A left click where the mouse pointer is now. The pointer is not moved.</summary>
    void ClickAtCursor();

    /// <summary>Presses and lets go of a key (with its Shift, Ctrl and Alt) in the window in front.</summary>
    void PressKey(KeyChord key)
    {
    }

    /// <summary>
    /// Remembers where the pointer is, puts it on <paramref name="spot"/> in the EverQuest window in front and holds it there (the pointer
    /// cannot be moved off it). False, with nothing changed, when EverQuest is not in front.
    /// </summary>
    bool HoldAt(ClickSpot spot) => false;

    /// <summary>Puts the pointer back on the spot while it is held (in case the game moved it). Does nothing when it is not held.</summary>
    void KeepHeld()
    {
    }

    /// <summary>Lets go of the pointer and puts it back where it was before <see cref="HoldAt"/>. Does nothing when it is not held.</summary>
    void Release()
    {
    }
}

/// <summary>
/// Clicks the mouse once when it is your cleric's turn in the Complete Heal chain. It touches nothing inside the game client: it only
/// moves and presses the mouse, and only while EverQuest is the window in front. It stays on or off as it was left.
/// <para>
/// <b>When.</b> A turn is the chain cue for a position one of your characters holds (see <see cref="ChChainService"/>). The click is
/// made <see cref="DelayMilliseconds"/> after the cue. The same cue read from several characters' logs, or repeated within a few
/// seconds, makes one click. At most <see cref="MaxClicksPerMinute"/> clicks are made in any minute.
/// </para>
/// <para>
/// <b>Where.</b> With no <see cref="Spot"/> it clicks wherever the pointer is. With a spot, the pointer is put on it and held there for
/// the click, then let go and put back where it was once your Complete Heal starts (or <see cref="CastWait"/> after the click). From
/// the second call of a fight on, the chain's pace (the median time between its calls) predicts your call: the pointer is put on the
/// spot and held <see cref="HoldLead"/> before it, and let go again <see cref="GiveUpAfter"/> past the expected time if the call has
/// not come. <see cref="NewFightGap"/> with no call starts the pace over.
/// </para>
/// </summary>
public sealed class AutoClickService : IDisposable
{
    public const string DefaultHotkey = "Ctrl+Alt+F9";
    public const int MaxDelayMilliseconds = 5000;
    public const int MaxClicksPerMinute = 10;

    /// <summary>How long before the expected call the pointer is put on the spot and held.</summary>
    public static readonly TimeSpan HoldLead = TimeSpan.FromSeconds(0.5);

    /// <summary>How long past the expected call the pointer is let go if the call has not come.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(1.5);

    /// <summary>How long after the click the pointer stays held waiting for the cast to start.</summary>
    public static readonly TimeSpan CastWait = TimeSpan.FromSeconds(2);

    /// <summary>No chain call for this long: a new fight, whose pace is learned again.</summary>
    public static readonly TimeSpan NewFightGap = TimeSpan.FromSeconds(30);

    /// <summary>The time between presses in timed mode, in seconds (tenths).</summary>
    public const double MinIntervalSeconds = 10.0;

    public const double MaxIntervalSeconds = 60.0;

    public const double DefaultIntervalSeconds = 10.0;

    private static readonly Guid SourceId = new("a3d7f1c2-6b84-4e09-8c5d-1f2e9b7a4d60");
    private static readonly TimeSpan SameTurnWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private const int PaceCalls = 9;

    private readonly object _gate = new();
    private readonly ChChainService _chain;
    private readonly TriggerEngineService? _engine;
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _clickTimes = new();
    private readonly List<(int Slot, DateTime At)> _calls = new();
    private readonly Timer? _timer;
    private IClickSender? _sender;
    private bool _armed;
    private int _delayMilliseconds;
    private ClickSpot? _spot;
    private DateTime? _lastCueUtc;
    private DateTime? _pendingDueUtc;
    private DateTime? _expectedUtc;
    private bool _held;
    private DateTime? _releaseAtUtc;
    private bool _clickedWhileHeld;
    private int _clicks;
    private int _skipped;
    private string _spokenPositions = string.Empty;
    private AutoClickTiming _timing;
    private AutoClickAction _action;
    private KeyChord _key = new(0x31);
    private double _intervalSeconds = DefaultIntervalSeconds;
    private DateTime? _nextTimedUtc;

    /// <param name="engine">Used to say "Auto click on" and "Auto click off" aloud; null for silence.</param>
    /// <param name="autoTick">False in tests, which call <see cref="Tick"/> themselves.</param>
    public AutoClickService(ChChainService chain, TriggerEngineService? engine = null, IClickSender? sender = null, bool autoTick = true, Func<DateTime>? clock = null)
    {
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
        _engine = engine;
        _sender = sender;
        _clock = clock ?? (() => DateTime.UtcNow);
        chain.CueReceived += OnCue;
        chain.CallHeard += OnCallHeard;
        chain.CastStarted += OnCastStarted;
        chain.PositionsChanged += OnPositionsChanged;

        if (autoTick)
        {
            _timer = new Timer(_ => Tick(_clock()), null, TickInterval, TickInterval);
        }
    }

    /// <summary>Raised (on whatever thread changed it) when the click is switched on or off.</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>What moves and presses the mouse and says whether EverQuest is in front. Without one, nothing is ever clicked.</summary>
    public IClickSender? Sender
    {
        get { lock (_gate) { return _sender; } }
        set { lock (_gate) { _sender = value; } }
    }

    public bool IsArmed
    {
        get { lock (_gate) { return _armed; } }
    }

    /// <summary>How long after the cue the click is made, 0 to <see cref="MaxDelayMilliseconds"/>.</summary>
    public int DelayMilliseconds
    {
        get { lock (_gate) { return _delayMilliseconds; } }
        set { lock (_gate) { _delayMilliseconds = Math.Clamp(value, 0, MaxDelayMilliseconds); } }
    }

    /// <summary>Where in the EverQuest window to click; null clicks wherever the pointer is.</summary>
    public ClickSpot? Spot
    {
        get { lock (_gate) { return _spot; } }
        set
        {
            lock (_gate)
            {
                _spot = value;
            }

            if (value is null)
            {
                ReleaseHold();
            }
        }
    }

    /// <summary>The chain's chat cue (default), or a fixed time after your own last Complete Heal (chat is then ignored for pressing).</summary>
    public AutoClickTiming Timing
    {
        get { lock (_gate) { return _timing; } }
        set
        {
            lock (_gate)
            {
                _timing = value;
                _nextTimedUtc = null;
                _expectedUtc = null;
                _pendingDueUtc = null;
            }

            ReleaseHold();
        }
    }

    /// <summary>A mouse click (default) or a key press.</summary>
    public AutoClickAction Action
    {
        get { lock (_gate) { return _action; } }
        set
        {
            lock (_gate)
            {
                _action = value;
            }

            if (value == AutoClickAction.KeyPress)
            {
                ReleaseHold();
            }
        }
    }

    /// <summary>The key pressed in key mode.</summary>
    public KeyChord Key
    {
        get { lock (_gate) { return _key; } }
        set { lock (_gate) { _key = value; } }
    }

    /// <summary>Seconds between presses in timed mode, kept between <see cref="MinIntervalSeconds"/> and <see cref="MaxIntervalSeconds"/>, in tenths.</summary>
    public double IntervalSeconds
    {
        get { lock (_gate) { return _intervalSeconds; } }
        set { lock (_gate) { _intervalSeconds = Math.Round(Math.Clamp(value, MinIntervalSeconds, MaxIntervalSeconds), 1); } }
    }

    /// <summary>In timed mode, when the next press is due (null until your first Complete Heal after switching on).</summary>
    public DateTime? NextTimedUtc
    {
        get { lock (_gate) { return _nextTimedUtc; } }
    }

    /// <summary>True while the pointer is held on the spot.</summary>
    public bool IsHolding
    {
        get { lock (_gate) { return _held; } }
    }

    /// <summary>When your next call is expected from the chain's pace, or null (no pace yet, or not your turn next).</summary>
    public DateTime? ExpectedCallUtc
    {
        get { lock (_gate) { return _expectedUtc; } }
    }

    /// <summary>Clicks made since the app started.</summary>
    public int Clicks
    {
        get { lock (_gate) { return _clicks; } }
    }

    /// <summary>Turns that came up while EverQuest was not the window in front, so no click was made.</summary>
    public int Skipped
    {
        get { lock (_gate) { return _skipped; } }
    }

    /// <summary>Switches the click on or off. Does nothing when it is already in that state.</summary>
    public void SetArmed(bool armed) => SetArmed(armed, announce: true);

    private void SetArmed(bool armed, bool announce)
    {
        DateTime now = _clock();
        lock (_gate)
        {
            if (_armed == armed)
            {
                return;
            }

            _armed = armed;
            _lastCueUtc = null;
            _pendingDueUtc = null;
            _expectedUtc = null;
            _nextTimedUtc = null; // timed mode waits for your first Complete Heal
        }

        if (!armed)
        {
            ReleaseHold();
        }

        string positions = PositionsText();
        lock (_gate)
        {
            _spokenPositions = positions;
        }

        if (announce)
        {
            string text = armed
                ? positions.Length == 0 ? "Auto click on, no chain position" : $"Auto click on, position {positions}"
                : "Auto click off";
            _engine?.PublishAudio(SourceId, "Auto-click", string.Empty, text, now);
        }

        ArmedChanged?.Invoke(armed);
    }

    /// <summary>Your characters' positions, "6" or "3 and 6".</summary>
    private string PositionsText() =>
        string.Join(" and ", _chain.Positions().Values.Distinct().OrderBy(p => p).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private void OnPositionsChanged()
    {
        string positions = PositionsText();
        bool say;
        lock (_gate)
        {
            say = _armed && !string.Equals(positions, _spokenPositions, StringComparison.Ordinal);
            _spokenPositions = positions;
            _expectedUtc = null; // the predicted turn was for the old position
        }

        if (say)
        {
            _engine?.PublishAudio(SourceId, "Auto-click", string.Empty,
                positions.Length == 0 ? "Auto click has no chain position" : $"Auto click now on position {positions}", _clock());
        }
    }

    /// <summary>Every call in the chain: learns the pace, and predicts your call when the position before yours is called.</summary>
    private void OnCallHeard(int slot, DateTime atUtc)
    {
        IReadOnlyDictionary<string, int> positions = _chain.Positions();
        int total = Math.Max(1, _chain.Settings.TotalClerics);
        lock (_gate)
        {
            if (_calls.Count > 0 && atUtc - _calls[^1].At > NewFightGap)
            {
                _calls.Clear(); // a new fight: its pace is learned again
            }

            if (_calls.Any(c => c.Slot == slot && (atUtc - c.At).Duration() < SameTurnWindow))
            {
                return; // the same call read from another log
            }

            _calls.Add((slot, atUtc));
            if (_calls.Count > PaceCalls)
            {
                _calls.RemoveAt(0);
            }

            if (!_armed || _timing != AutoClickTiming.ChatCue || _action != AutoClickAction.MouseClick || _spot is null || Pace() is not { } pace)
            {
                return;
            }

            foreach (int mine in positions.Values)
            {
                int before = mine == 1 ? total : mine - 1;
                if (before == slot && mine != slot)
                {
                    _expectedUtc = atUtc + pace;
                    return;
                }
            }
        }
    }

    /// <summary>The median time between the fight's calls so far, or null before there are two.</summary>
    private TimeSpan? Pace()
    {
        if (_calls.Count < 2)
        {
            return null;
        }

        List<double> gaps = new();
        for (int i = 1; i < _calls.Count; i++)
        {
            double seconds = (_calls[i].At - _calls[i - 1].At).TotalSeconds;
            if (seconds > 0)
            {
                gaps.Add(seconds);
            }
        }

        if (gaps.Count == 0)
        {
            return null;
        }

        gaps.Sort();
        double median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
        return TimeSpan.FromSeconds(median);
    }

    private void OnCue(string characterName, DateTime cueUtc)
    {
        bool clickNow = false;
        lock (_gate)
        {
            if (!_armed || _timing == AutoClickTiming.Timed)
            {
                return; // timed mode ignores chat for pressing
            }

            if (_lastCueUtc is { } last && (cueUtc - last).Duration() < SameTurnWindow)
            {
                return; // the same turn, read from another log or said twice
            }

            _lastCueUtc = cueUtc;
            _expectedUtc = null;
            _pendingDueUtc = _clock().AddMilliseconds(_delayMilliseconds);
            clickNow = _delayMilliseconds == 0;
        }

        if (clickNow)
        {
            Fire(_clock());
        }
    }

    /// <summary>Your cleric started casting: the held pointer is let go and put back.</summary>
    private void OnCastStarted(string characterName, DateTime atUtc)
    {
        bool release;
        lock (_gate)
        {
            release = _held && _clickedWhileHeld;
            if (_armed && _timing == AutoClickTiming.Timed)
            {
                _nextTimedUtc = atUtc + TimeSpan.FromSeconds(_intervalSeconds); // every cast of yours restarts the count
            }
        }

        if (release)
        {
            ReleaseHold();
        }
    }

    /// <summary>Holds the pointer before an expected call, lets it go when it waited too long, and makes a click that has come due.</summary>
    public void Tick(DateTime nowUtc)
    {
        bool hold = false;
        bool release = false;
        bool keep = false;
        bool due = false;
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            if (_timing == AutoClickTiming.Timed && _nextTimedUtc is { } next && nowUtc >= next && _pendingDueUtc is null)
            {
                _pendingDueUtc = nowUtc;
                _nextTimedUtc = nowUtc + TimeSpan.FromSeconds(_intervalSeconds); // until your cast moves it
            }

            if (_pendingDueUtc is { } at && nowUtc >= at)
            {
                due = true;
            }
            else if (_held && _releaseAtUtc is { } letGo && nowUtc >= letGo)
            {
                release = true;
            }
            else if (!_held && _spot is not null && _action == AutoClickAction.MouseClick && HoldTarget() is { } expected
                     && nowUtc >= expected - HoldLead && nowUtc < expected + GiveUpAfter)
            {
                hold = true;
            }
            else if (_held)
            {
                keep = true;
            }
        }

        if (due)
        {
            Fire(nowUtc);
        }
        else if (release)
        {
            ReleaseHold();
            lock (_gate)
            {
                _expectedUtc = null;
            }
        }
        else if (hold)
        {
            TryHold(nowUtc, giveUpAt: null);
        }
        else if (keep)
        {
            Sender?.KeepHeld();
        }
    }

    /// <summary>When the next press is expected: the predicted chat call, or the timer in timed mode.</summary>
    private DateTime? HoldTarget() => _timing == AutoClickTiming.Timed ? _nextTimedUtc : _expectedUtc;

    /// <summary>Puts the pointer on the spot and holds it; it is let go at <paramref name="giveUpAt"/>, or past the expected call.</summary>
    private bool TryHold(DateTime nowUtc, DateTime? giveUpAt)
    {
        IClickSender? sender;
        ClickSpot spot;
        lock (_gate)
        {
            if (_held)
            {
                return true;
            }

            if (_spot is not { } s || _sender is null)
            {
                return false;
            }

            spot = s;
            sender = _sender;
        }

        if (!sender.IsGameFocused() || !sender.HoldAt(spot))
        {
            return false;
        }

        lock (_gate)
        {
            _held = true;
            _clickedWhileHeld = false;
            _releaseAtUtc = giveUpAt ?? (HoldTarget() is { } expected ? expected + GiveUpAfter : nowUtc + GiveUpAfter);
        }

        return true;
    }

    private void ReleaseHold()
    {
        IClickSender? sender;
        lock (_gate)
        {
            if (!_held)
            {
                return;
            }

            _held = false;
            _clickedWhileHeld = false;
            _releaseAtUtc = null;
            sender = _sender;
        }

        sender?.Release();
    }

    private void Fire(DateTime nowUtc)
    {
        IClickSender? sender;
        bool useSpot;
        KeyChord? pressKey;
        lock (_gate)
        {
            if (!_armed || _pendingDueUtc is null)
            {
                return;
            }

            _pendingDueUtc = null;
            while (_clickTimes.Count > 0 && nowUtc - _clickTimes.Peek() >= TimeSpan.FromMinutes(1))
            {
                _clickTimes.Dequeue();
            }

            if (_clickTimes.Count >= MaxClicksPerMinute)
            {
                _skipped++;
                return;
            }

            sender = _sender;
            useSpot = _spot is not null && _action == AutoClickAction.MouseClick;
            pressKey = _action == AutoClickAction.KeyPress ? _key : null;
        }

        if (sender is null || !sender.IsGameFocused())
        {
            lock (_gate)
            {
                _skipped++;
            }

            ReleaseHold();
            return;
        }

        // With a spot: on it and held for the click (already, when the call was expected), until the cast starts.
        if (useSpot && !TryHold(nowUtc, nowUtc + CastWait))
        {
            lock (_gate)
            {
                _skipped++;
            }

            return;
        }

        if (pressKey is { } key)
        {
            sender.PressKey(key);
        }
        else
        {
            sender.ClickAtCursor();
        }

        lock (_gate)
        {
            _clicks++;
            _clickTimes.Enqueue(nowUtc);
            if (_held)
            {
                _clickedWhileHeld = true;
                _releaseAtUtc = nowUtc + CastWait;
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        ReleaseHold();
    }
}
