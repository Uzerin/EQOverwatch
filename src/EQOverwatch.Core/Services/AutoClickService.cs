using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>What starts the auto-press: the chain's chat cue for your position, or a fixed time after your own last Complete Heal.</summary>
public enum AutoClickTiming
{
    ChatCue,
    Timed,
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

/// <summary>What the auto-press needs from the operating system. The app supplies the Windows version; tests supply a fake.</summary>
public interface IClickSender
{
    /// <summary>True when the EverQuest client is the window in front (and not minimized).</summary>
    bool IsGameFocused();

    /// <summary>Presses and lets go of a key (with its Shift, Ctrl and Alt) in the window in front.</summary>
    void PressKey(KeyChord key);
}

/// <summary>
/// Presses your Complete Heal key when it is your cleric's turn in the CH chain, and optionally an assist key 1.0 s and again 0.5 s
/// before it. It only presses keys, never the mouse, and only while EverQuest is the window in front. It stays on or off as it was left.
/// <para>
/// <b>Chat cue.</b> A turn is the chain cue for a position one of your characters holds (see <see cref="ChChainService"/>). From the second
/// call of a fight the chain's pace (the median time between its calls) predicts your call, so the assist presses come before it; when
/// the call arrives the CH key is pressed (after any assist press still due, 0.5 s apart). On the first turn of a fight, or when the call
/// comes before the assist presses, the assist key is pressed at the call and the CH key 1.0 s later. <see cref="NewFightGap"/> with no
/// call starts the pace over.
/// </para>
/// <para>
/// <b>Timed.</b> Chat is ignored. After it is switched on it waits for your first Complete Heal by hand, then presses the CH key
/// <see cref="IntervalSeconds"/> after its own last press (the assist key 1.0 s and 0.5 s before). The cast that press starts is not
/// counted again (<see cref="OwnCastEcho"/>); any other cast of yours restarts the count.
/// </para>
/// The same cue read from several logs makes one turn. At most <see cref="MaxClicksPerMinute"/> CH presses are made in any minute.
/// </summary>
public sealed class AutoClickService : IDisposable
{
    public const string DefaultHotkey = "Ctrl+Alt+F9";
    public const int MaxClicksPerMinute = 10;

    /// <summary>The assist key goes this long, and again <see cref="SecondAssistLead"/>, before the CH key.</summary>
    public static readonly TimeSpan FirstAssistLead = TimeSpan.FromSeconds(1.0);

    public static readonly TimeSpan SecondAssistLead = TimeSpan.FromSeconds(0.5);

    /// <summary>How long past the expected call a predicted turn is given up if the call has not come.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(1.5);

    /// <summary>No chain call for this long: a new fight, whose pace is learned again.</summary>
    public static readonly TimeSpan NewFightGap = TimeSpan.FromSeconds(30);

    /// <summary>The time between presses in timed mode, in seconds (tenths).</summary>
    public const double MinIntervalSeconds = 10.0;

    /// <summary>In timed mode a cast logged this soon after the app's own CH press is that press's cast, and does not restart the count.</summary>
    public static readonly TimeSpan OwnCastEcho = TimeSpan.FromSeconds(3);

    public const double MaxIntervalSeconds = 60.0;

    public const double DefaultIntervalSeconds = 10.0;

    /// <summary>The CH key until one is set: 2.</summary>
    public static readonly KeyChord DefaultChKey = new(0x32);

    /// <summary>The assist key until one is set: 1.</summary>
    public static readonly KeyChord DefaultAssistKey = new(0x31);

    private static readonly Guid SourceId = new("a3d7f1c2-6b84-4e09-8c5d-1f2e9b7a4d60");
    private static readonly TimeSpan SameTurnWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private const int PaceCalls = 9;

    private readonly object _gate = new();
    private readonly object _pressGate = new();
    private readonly ChChainService _chain;
    private readonly TriggerEngineService? _engine;
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _pressTimes = new();
    private readonly List<(int Slot, DateTime At)> _calls = new();
    private readonly Timer? _timer;
    private IClickSender? _sender;
    private bool _armed;
    private AutoClickTiming _timing;
    private double _intervalSeconds = DefaultIntervalSeconds;
    private KeyChord _key = DefaultChKey;
    private KeyChord _assistKey = DefaultAssistKey;
    private bool _useAssist;
    private DateTime? _lastCueUtc;
    private DateTime? _expectedUtc;
    private DateTime? _nextTimedUtc;
    private DateTime? _lastTimedPressUtc;

    // The turn in progress: when the CH key is due, whether that is certain (the call came, or timed) or only predicted, and how many
    // assist presses were made for it.
    private DateTime? _chDueUtc;
    private bool _chConfirmed;
    private int _assistsDone;

    private int _clicks;
    private int _skipped;
    private string _spokenPositions = string.Empty;

    /// <param name="engine">Used to say "Auto press on" and "Auto press off" aloud; null for silence.</param>
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

    /// <summary>Raised (on whatever thread changed it) when it is switched on or off.</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>What presses the keys and says whether EverQuest is in front. Without one, nothing is ever pressed.</summary>
    public IClickSender? Sender
    {
        get { lock (_gate) { return _sender; } }
        set { lock (_gate) { _sender = value; } }
    }

    public bool IsArmed
    {
        get { lock (_gate) { return _armed; } }
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
                _lastTimedPressUtc = null;
                _expectedUtc = null;
                ClearTurnLocked();
            }
        }
    }

    /// <summary>Seconds between presses in timed mode, kept between <see cref="MinIntervalSeconds"/> and <see cref="MaxIntervalSeconds"/>, in tenths.</summary>
    public double IntervalSeconds
    {
        get { lock (_gate) { return _intervalSeconds; } }
        set { lock (_gate) { _intervalSeconds = Math.Round(Math.Clamp(value, MinIntervalSeconds, MaxIntervalSeconds), 1); } }
    }

    /// <summary>The Complete Heal key.</summary>
    public KeyChord Key
    {
        get { lock (_gate) { return _key; } }
        set { lock (_gate) { _key = value; } }
    }

    /// <summary>The assist key, pressed 1.0 s and 0.5 s before the CH key while <see cref="UseAssist"/> is on.</summary>
    public KeyChord AssistKey
    {
        get { lock (_gate) { return _assistKey; } }
        set { lock (_gate) { _assistKey = value; } }
    }

    public bool UseAssist
    {
        get { lock (_gate) { return _useAssist; } }
        set { lock (_gate) { _useAssist = value; } }
    }

    /// <summary>In timed mode, when the next CH press is due (null until your first Complete Heal after switching on).</summary>
    public DateTime? NextTimedUtc
    {
        get { lock (_gate) { return _nextTimedUtc; } }
    }

    /// <summary>When your next call is expected from the chain's pace, or null (no pace yet, or not your turn next).</summary>
    public DateTime? ExpectedCallUtc
    {
        get { lock (_gate) { return _expectedUtc; } }
    }

    /// <summary>CH presses made since the app started.</summary>
    public int Clicks
    {
        get { lock (_gate) { return _clicks; } }
    }

    /// <summary>Turns that came up while EverQuest was not the window in front, so nothing was pressed.</summary>
    public int Skipped
    {
        get { lock (_gate) { return _skipped; } }
    }

    /// <summary>Switches it on or off. Does nothing when it is already in that state.</summary>
    public void SetArmed(bool armed)
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
            _expectedUtc = null;
            _lastTimedPressUtc = null;
            _nextTimedUtc = null; // timed mode waits for your first Complete Heal
            ClearTurnLocked();
        }

        string positions = PositionsText();
        lock (_gate)
        {
            _spokenPositions = positions;
        }

        string text = armed
            ? positions.Length == 0 ? "Auto press on, no chain position" : $"Auto press on, position {positions}"
            : "Auto press off";
        _engine?.PublishAudio(SourceId, "Auto-press", string.Empty, text, now);
        ArmedChanged?.Invoke(armed);
    }

    private void ClearTurnLocked()
    {
        _chDueUtc = null;
        _chConfirmed = false;
        _assistsDone = 0;
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
            if (!_chConfirmed)
            {
                ClearTurnLocked();
            }
        }

        if (say)
        {
            _engine?.PublishAudio(SourceId, "Auto-press", string.Empty,
                positions.Length == 0 ? "Auto press has no chain position" : $"Auto press now on position {positions}", _clock());
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

            if (!_armed || _timing != AutoClickTiming.ChatCue || _chConfirmed || Pace() is not { } pace)
            {
                return;
            }

            foreach (int mine in positions.Values)
            {
                int before = mine == 1 ? total : mine - 1;
                if (before == slot && mine != slot)
                {
                    _expectedUtc = atUtc + pace;
                    _chDueUtc = _expectedUtc;
                    _chConfirmed = false;
                    _assistsDone = 0;
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

        var gaps = new List<double>();
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

    /// <summary>Your call: the CH key now, or after the assist presses still due (0.5 s apart).</summary>
    private void OnCue(string characterName, DateTime cueUtc)
    {
        DateTime now = _clock();
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
            int done = _chDueUtc is not null && !_chConfirmed ? _assistsDone : 0;
            _assistsDone = done;
            _chConfirmed = true;
            _chDueUtc = !_useAssist || done >= 2 ? now
                : done == 1 ? now + (FirstAssistLead - SecondAssistLead)
                : now + FirstAssistLead;
        }

        Tick(now);
    }

    /// <summary>Timed mode: your Complete Heal restarts the count.</summary>
    private void OnCastStarted(string characterName, DateTime atUtc)
    {
        lock (_gate)
        {
            if (_armed && _timing == AutoClickTiming.Timed)
            {
                // Log times are whole seconds, so the press's own cast can read up to a second before the press.
                if (_lastTimedPressUtc is { } pressed && atUtc >= pressed - TimeSpan.FromSeconds(1) && atUtc <= pressed + OwnCastEcho)
                {
                    return;
                }

                _nextTimedUtc = atUtc + TimeSpan.FromSeconds(_intervalSeconds);
                ClearTurnLocked(); // the turn is planned again from this cast
            }
        }
    }

    /// <summary>Makes the presses that have come due. Called by the internal timer; public so tests can drive it.</summary>
    public void Tick(DateTime nowUtc)
    {
        lock (_pressGate)
        {
            while (NextPress(nowUtc) is { } press)
            {
                Press(press.Key, press.IsCh, nowUtc);
            }
        }
    }

    /// <summary>The press due now, if any, with the turn's state moved on past it.</summary>
    private (KeyChord Key, bool IsCh)? NextPress(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return null;
            }

            if (_timing == AutoClickTiming.Timed && _chDueUtc is null && _nextTimedUtc is { } next)
            {
                _chDueUtc = next;
                _chConfirmed = true;
                _assistsDone = 0;
            }

            if (_chDueUtc is not { } due)
            {
                return null;
            }

            if (_useAssist && _assistsDone == 0 && nowUtc >= due - FirstAssistLead)
            {
                _assistsDone = 1;
                return (_assistKey, false);
            }

            if (_useAssist && _assistsDone == 1 && nowUtc >= due - SecondAssistLead)
            {
                _assistsDone = 2;
                return (_assistKey, false);
            }

            if (_chConfirmed && nowUtc >= due)
            {
                ClearTurnLocked();
                if (_timing == AutoClickTiming.Timed)
                {
                    _nextTimedUtc = nowUtc + TimeSpan.FromSeconds(_intervalSeconds); // until a cast by hand moves it
                    _lastTimedPressUtc = nowUtc;
                }

                return (_key, true);
            }

            if (!_chConfirmed && nowUtc >= due + GiveUpAfter)
            {
                ClearTurnLocked(); // the predicted call did not come
                _expectedUtc = null;
            }

            return null;
        }
    }

    private void Press(KeyChord key, bool isCh, DateTime nowUtc)
    {
        IClickSender? sender;
        lock (_gate)
        {
            if (isCh)
            {
                while (_pressTimes.Count > 0 && nowUtc - _pressTimes.Peek() >= TimeSpan.FromMinutes(1))
                {
                    _pressTimes.Dequeue();
                }

                if (_pressTimes.Count >= MaxClicksPerMinute)
                {
                    _skipped++;
                    return;
                }
            }

            sender = _sender;
        }

        if (sender is null || !sender.IsGameFocused())
        {
            if (isCh)
            {
                lock (_gate)
                {
                    _skipped++;
                }
            }

            return;
        }

        sender.PressKey(key);
        if (isCh)
        {
            lock (_gate)
            {
                _clicks++;
                _pressTimes.Enqueue(nowUtc);
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
