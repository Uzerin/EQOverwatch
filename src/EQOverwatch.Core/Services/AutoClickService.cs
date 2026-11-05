using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>What the auto-click needs from the operating system. The app supplies the Windows version; tests supply a fake.</summary>
public interface IClickSender
{
    /// <summary>True when the EverQuest client is the window in front (and not minimized).</summary>
    bool IsGameFocused();

    /// <summary>A left click where the mouse pointer is now. The pointer is not moved.</summary>
    void ClickAtCursor();
}

/// <summary>
/// Clicks the mouse once when it is your cleric's turn in the Complete Heal chain. It is for a hotbar button you have parked the
/// pointer on. It touches nothing inside the game client: it only presses the left mouse button, and only while EverQuest is the
/// window in front. It is <b>off</b> every time the app starts, and switches itself off after <see cref="IdleDisarmAfter"/> with no cue.
/// <para>
/// <b>When.</b> A turn is the chain cue for a position one of your characters holds (see <see cref="ChChainService"/>). The click is
/// made <see cref="DelayMilliseconds"/> after the cue. The same cue read from several characters' logs, or repeated within a few
/// seconds, makes one click. At most <see cref="MaxClicksPerMinute"/> clicks are made in any minute.
/// </para>
/// </summary>
public sealed class AutoClickService : IDisposable
{
    public const string DefaultHotkey = "Ctrl+Alt+F9";
    public const int MaxDelayMilliseconds = 5000;
    public const int MaxClicksPerMinute = 10;

    /// <summary>Armed, but no chain cue for this long: the chain is over, so the click switches itself off.</summary>
    public static readonly TimeSpan IdleDisarmAfter = TimeSpan.FromMinutes(5);

    private static readonly Guid SourceId = new("a3d7f1c2-6b84-4e09-8c5d-1f2e9b7a4d60");
    private static readonly TimeSpan SameTurnWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly TriggerEngineService? _engine;
    private readonly Func<DateTime> _clock;
    private readonly Queue<DateTime> _clickTimes = new();
    private readonly Timer? _timer;
    private IClickSender? _sender;
    private bool _armed;
    private int _delayMilliseconds;
    private DateTime _lastActivityUtc;
    private DateTime? _lastCueUtc;
    private DateTime? _pendingDueUtc;
    private int _clicks;
    private int _skipped;

    /// <param name="engine">Used to say "Auto click on" and "Auto click off" aloud; null for silence.</param>
    /// <param name="autoTick">False in tests, which call <see cref="Tick"/> themselves.</param>
    public AutoClickService(ChChainService chain, TriggerEngineService? engine = null, IClickSender? sender = null, bool autoTick = true, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        _engine = engine;
        _sender = sender;
        _clock = clock ?? (() => DateTime.UtcNow);
        chain.CueReceived += OnCue;

        if (autoTick)
        {
            _timer = new Timer(_ => Tick(_clock()), null, TickInterval, TickInterval);
        }
    }

    /// <summary>Raised (on whatever thread changed it) when the click is switched on or off, including when it switches itself off.</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>What presses the mouse and says whether EverQuest is in front. Without one, nothing is ever clicked.</summary>
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
            _lastActivityUtc = now;
            _lastCueUtc = null;
            _pendingDueUtc = null;
        }

        if (announce)
        {
            _engine?.PublishAudio(SourceId, "Auto-click", string.Empty, armed ? "Auto click on" : "Auto click off", now);
        }

        ArmedChanged?.Invoke(armed);
    }

    private void OnCue(string characterName, DateTime cueUtc)
    {
        bool clickNow = false;
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            _lastActivityUtc = _clock();
            if (_lastCueUtc is { } last && (cueUtc - last).Duration() < SameTurnWindow)
            {
                return; // the same turn, read from another log or said twice
            }

            _lastCueUtc = cueUtc;
            _pendingDueUtc = _clock().AddMilliseconds(_delayMilliseconds);
            clickNow = _delayMilliseconds == 0;
        }

        if (clickNow)
        {
            Fire(_clock());
        }
    }

    /// <summary>Makes a click that has come due and switches off after a long quiet spell. Called by the internal timer; public so tests can drive it.</summary>
    public void Tick(DateTime nowUtc)
    {
        bool disarm = false;
        bool due = false;
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            if (nowUtc - _lastActivityUtc >= IdleDisarmAfter)
            {
                disarm = true;
            }
            else if (_pendingDueUtc is { } at && nowUtc >= at)
            {
                due = true;
            }
        }

        if (disarm)
        {
            SetArmed(false, announce: true);
        }
        else if (due)
        {
            Fire(nowUtc);
        }
    }

    private void Fire(DateTime nowUtc)
    {
        IClickSender? sender;
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
        }

        if (sender is null || !sender.IsGameFocused())
        {
            lock (_gate)
            {
                _skipped++;
            }

            return;
        }

        sender.ClickAtCursor();
        lock (_gate)
        {
            _clicks++;
            _clickTimes.Enqueue(nowUtc);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
