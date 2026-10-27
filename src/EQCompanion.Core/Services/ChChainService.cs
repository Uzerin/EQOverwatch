using System.Text.RegularExpressions;
using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Settings;

namespace EQCompanion.Core.Services;

/// <summary>
/// Follows a raid's Complete Heal chain from raid-say chat.
/// <para>
/// <b>Chain bars.</b> A cleric announces a cast by putting the position tag anywhere in a raid-say line, e.g.
/// <c>Ream tells the raid, '4-4-4 - Tank'</c> (or <c>You tell your raid, ...</c>). Nothing else in the line is read: it starts a
/// 10 second bar labelled <c>Ream: Complete Heal 4</c>. The bar goes to the
/// <see cref="TimerOverlayCategory.ChChain"/> overlay when <see cref="UseChChainOverlay"/> is true and to the Buffs overlay otherwise.
/// The tag is built from <see cref="ChChainSettings.PositionTagFormat"/>, so a raid that says "3/3/3" or "[3]" works too.
/// </para>
/// <para>
/// <b>Cue and late alarm.</b> When someone says the tag for the position of one of your characters together with the cue keyword
/// (<c>Ream tells the raid, '3-3-3 CAST NOW!'</c>; any letter case, anywhere in the line), that character is expected to start casting. If its own log does not show
/// <c>You begin casting Complete Heal.</c> within <see cref="ChChainSettings.LateThresholdSeconds"/> of the cue, a flashing
/// "CAST CH NOW!" banner and a spoken alarm repeat every second until the cast begins or 10 seconds have passed. A fizzle or an
/// interrupt right after the cast began re-arms the alarm at once.
/// </para>
/// The alarm is evaluated by <see cref="Tick"/>, which an internal timer calls a few times a second (tests call it themselves).
/// All times are the wall-clock <c>receivedUtc</c> the log line was read at.
/// </summary>
public sealed class ChChainService : ITriggerLineSink, IDisposable
{
    public static readonly Guid TimerSourceId = new("3c5d8e21-7a4b-4f60-b1c9-2d8e0f6a9b37");

    public const string TimerSourceName = "CH chain";
    public const string AlarmText = "CAST CH NOW!";
    public const string AlarmSpeech = "Cast C H now";

    /// <summary>How long a chain bar runs: a Complete Heal takes about ten seconds to land.</summary>
    public const double BarSeconds = 10.0;

    /// <summary>The alarm gives up this long after the cue (or after the fizzle that re-armed it).</summary>
    public const double AlarmWindowSeconds = 10.0;

    /// <summary>The alarm repeats this often while it is active.</summary>
    public const double AlarmRepeatSeconds = 1.0;

    /// <summary>A fizzle or interrupt only counts for a cast that began within this many seconds.</summary>
    public const double CastRecoveryWindowSeconds = 15.0;

    private const string BeginCastingLine = "You begin casting Complete Heal.";
    private const string FizzleLine = "Your spell fizzles!";
    private const string InterruptLine = "Your spell is interrupted.";
    private const string RaidSayMarker = "tells the raid,";
    private const string SelfRaidSayMarker = "You tell your raid,";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private sealed class WatchState
    {
        public bool AwaitingCast;
        public DateTime AlarmDueUtc;
        public DateTime DeadlineUtc;
        public DateTime NextAlarmUtc;
        public bool AlarmShown;
        public DateTime? CastStartedUtc;
    }

    private readonly TriggerEngineService _engine;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WatchState> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _tickTimer;
    private volatile bool _useChChainOverlay;
    private ChainPatterns _patterns;
    private ChChainSettings _settings;

    /// <param name="autoTick">False in tests, which call <see cref="Tick"/> themselves.</param>
    public ChChainService(TriggerEngineService engine, ChChainSettings? settings = null, bool autoTick = true)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = (settings ?? new ChChainSettings()).Sanitized();
        _patterns = ChainPatterns.Build(_settings);

        if (autoTick)
        {
            _tickTimer = new Timer(_ => Tick(DateTime.UtcNow), null, TickInterval, TickInterval);
        }
    }

    /// <summary>True: bars, cues and the alarm go to the CH Chain overlay. False: they go to the Buffs overlay.</summary>
    public bool UseChChainOverlay
    {
        get => _useChChainOverlay;
        set => _useChChainOverlay = value;
    }

    /// <summary>The overlay that currently receives the chain's bars and alarm.</summary>
    public TimerOverlayCategory Category => _useChChainOverlay ? TimerOverlayCategory.ChChain : TimerOverlayCategory.Buffs;

    public ChChainSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings.Clone();
            }
        }
    }

    /// <summary>Applies new tag/cue/threshold settings (invalid values fall back to the defaults).</summary>
    public void Configure(ChChainSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ChChainSettings clean = settings.Sanitized();
        lock (_gate)
        {
            _settings = clean;
            _patterns = ChainPatterns.Build(clean);
        }
    }

    /// <summary>
    /// Gives <paramref name="characterName"/> chain slot <paramref name="position"/> (1-12), or removes the character from the
    /// chain with 0. A slot belongs to one character, so assigning it to a second character frees it on the first.
    /// </summary>
    public void SetPosition(string characterName, int position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);

        lock (_gate)
        {
            if (position <= 0)
            {
                _positions.Remove(characterName);
                CancelAlarmLocked(characterName);
                _watch.Remove(characterName);
                return;
            }

            int clamped = Math.Min(position, WatchedLogConfig.MaxChChainPosition);
            foreach (string other in _positions.Where(p => p.Value == clamped && !p.Key.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                         .Select(p => p.Key).ToList())
            {
                _positions.Remove(other);
                CancelAlarmLocked(other);
                _watch.Remove(other);
            }

            _positions[characterName] = clamped;
        }
    }

    /// <summary>Replaces every assignment at once (character name to position; zero or less is ignored).</summary>
    public void SetPositions(IEnumerable<KeyValuePair<string, int>> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        lock (_gate)
        {
            foreach (string name in _positions.Keys.ToList())
            {
                CancelAlarmLocked(name);
            }

            _positions.Clear();
            _watch.Clear();
        }

        foreach (KeyValuePair<string, int> pair in assignments)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
            {
                SetPosition(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>The chain slot held by <paramref name="characterName"/>, or 0.</summary>
    public int GetPosition(string characterName)
    {
        lock (_gate)
        {
            return _positions.TryGetValue(characterName, out int position) ? position : 0;
        }
    }

    /// <summary>True while the late-cast alarm is waiting to sound or sounding for this character.</summary>
    public bool IsAwaitingCast(string characterName)
    {
        lock (_gate)
        {
            return _watch.TryGetValue(characterName, out WatchState? state) && state.AwaitingCast;
        }
    }

    /// <summary>The instant the cue arrived for a character that is still expected to cast, or null.</summary>
    public DateTime? GetAlarmDueUtc(string characterName)
    {
        lock (_gate)
        {
            return _watch.TryGetValue(characterName, out WatchState? state) && state.AwaitingCast ? state.AlarmDueUtc : null;
        }
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.IsEmpty)
        {
            return;
        }

        // Cheap pre-filters on the span: almost every log line is none of the four things this service cares about.
        bool isRaidSay = message.IndexOf(RaidSayMarker, StringComparison.Ordinal) > 0
                         || message.StartsWith(SelfRaidSayMarker, StringComparison.Ordinal);

        if (isRaidSay)
        {
            HandleRaidSay(characterName, message.ToString(), receivedUtc);
            return;
        }

        if (message.SequenceEqual(BeginCastingLine))
        {
            HandleBeginCasting(characterName, receivedUtc);
        }
        else if (message.SequenceEqual(FizzleLine) || message.SequenceEqual(InterruptLine))
        {
            HandleCastFailed(characterName, receivedUtc);
        }
    }

    /// <summary>Sounds, repeats and ends alarms. Called by the internal timer; public so tests can drive it.</summary>
    public void Tick(DateTime nowUtc)
    {
        List<string>? sounding = null;
        TimerOverlayCategory category = Category;
        double alarmSeconds = AlarmRepeatSeconds + 0.5;

        lock (_gate)
        {
            if (_watch.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, WatchState> pair in _watch)
            {
                WatchState state = pair.Value;
                if (!state.AwaitingCast)
                {
                    continue;
                }

                if (nowUtc >= state.DeadlineUtc)
                {
                    state.AwaitingCast = false;
                    state.AlarmShown = false;
                    _engine.CancelAlert(TimerSourceId, pair.Key, AlarmText);
                    continue;
                }

                if (nowUtc >= state.AlarmDueUtc && nowUtc >= state.NextAlarmUtc)
                {
                    state.AlarmShown = true;
                    state.NextAlarmUtc = nowUtc.AddSeconds(AlarmRepeatSeconds);
                    (sounding ??= new List<string>()).Add(pair.Key);
                }
            }
        }

        if (sounding is null)
        {
            return;
        }

        foreach (string character in sounding)
        {
            // A fresh banner every second keeps it flashing; the text is identical, so it replaces the previous one.
            _engine.StartAlert(TimerSourceId, TimerSourceName, character, AlarmText, alarmSeconds, nowUtc, category);
            _engine.PublishAudio(TimerSourceId, TimerSourceName, character, AlarmSpeech, nowUtc);
        }
    }

    public void Dispose() => _tickTimer?.Dispose();

    // ───────────────────────── raid say: announcements and cues ─────────────────────────

    private void HandleRaidSay(string observedBy, string message, DateTime now)
    {
        ChainPatterns patterns;
        lock (_gate)
        {
            patterns = _patterns;
        }

        try
        {
            Match say = patterns.Say.Match(message);
            if (!say.Success)
            {
                return;
            }

            string body = say.Groups["body"].Value;
            Match tag = patterns.Tag.Match(body);
            if (!tag.Success || !int.TryParse(tag.Groups["pos"].Value, out int slot))
            {
                return;
            }

            bool hasCaster = say.Groups["caster"].Success;
            if (body.Contains(patterns.CueKeyword, StringComparison.OrdinalIgnoreCase))
            {
                // "3-3-3 CAST NOW!" tells that position to cast; "You tell your raid" is never a cue for you
                if (hasCaster)
                {
                    HandleCue(slot, now);
                }

                return;
            }

            // Any other line with a position tag is that cleric starting their Complete Heal.
            string caster = hasCaster ? say.Groups["caster"].Value : observedBy;
            _engine.StartTimer(
                TimerSourceId, TimerSourceName, caster, $"{caster}: Complete Heal {slot}", BarSeconds, now, Category);
        }
        catch (RegexMatchTimeoutException)
        {
            // a pathological chat line is simply not a chain line
        }
    }

    /// <summary>Every character holding the cued slot is now expected to start casting.</summary>
    private void HandleCue(int slot, DateTime now)
    {
        lock (_gate)
        {
            double threshold = _settings.LateThresholdSeconds;
            foreach (KeyValuePair<string, int> assignment in _positions)
            {
                if (assignment.Value != slot)
                {
                    continue;
                }

                WatchState state = GetStateLocked(assignment.Key);
                state.AwaitingCast = true;
                state.AlarmShown = false;
                state.CastStartedUtc = null;
                state.AlarmDueUtc = now.AddSeconds(threshold);
                state.NextAlarmUtc = state.AlarmDueUtc;
                state.DeadlineUtc = now.AddSeconds(AlarmWindowSeconds);
            }
        }
    }

    // ───────────────────────── the assigned cleric's own log ─────────────────────────

    private void HandleBeginCasting(string observedBy, DateTime now)
    {
        lock (_gate)
        {
            if (!_watch.TryGetValue(observedBy, out WatchState? state))
            {
                if (!_positions.ContainsKey(observedBy))
                {
                    return;
                }

                state = GetStateLocked(observedBy);
            }

            state.CastStartedUtc = now;
            if (state.AwaitingCast)
            {
                state.AwaitingCast = false;
                state.AlarmShown = false;
                _engine.CancelAlert(TimerSourceId, observedBy, AlarmText);
            }
        }
    }

    private void HandleCastFailed(string observedBy, DateTime now)
    {
        lock (_gate)
        {
            if (!_watch.TryGetValue(observedBy, out WatchState? state)
                || state.CastStartedUtc is not { } started
                || (now - started).TotalSeconds > CastRecoveryWindowSeconds)
            {
                return; // not a failed Complete Heal of the chain
            }

            state.CastStartedUtc = null;
            state.AwaitingCast = true;
            state.AlarmShown = false;
            state.AlarmDueUtc = now;
            state.NextAlarmUtc = now;
            state.DeadlineUtc = now.AddSeconds(AlarmWindowSeconds);
        }
    }

    private WatchState GetStateLocked(string characterName)
    {
        if (!_watch.TryGetValue(characterName, out WatchState? state))
        {
            state = new WatchState();
            _watch[characterName] = state;
        }

        return state;
    }

    private void CancelAlarmLocked(string characterName)
    {
        if (_watch.TryGetValue(characterName, out WatchState? state) && state.AlarmShown)
        {
            _engine.CancelAlert(TimerSourceId, characterName, AlarmText);
            state.AlarmShown = false;
        }
    }

    // ───────────────────────── patterns ─────────────────────────

    /// <summary>The raid-say pattern, the position-tag pattern and the cue keyword built from the settings.</summary>
    private sealed record ChainPatterns(Regex Say, Regex Tag, string CueKeyword)
    {
        public static ChainPatterns Build(ChChainSettings settings)
        {
            // "{N}-{N}-{N}" becomes (?<pos>\d+)\-\k<pos>\-\k<pos>: every {N} must be the same number.
            string tag = BuildTag(settings.PositionTagFormat, firstGroup: @"(?<pos>\d+)", repeat: @"\k<pos>");

            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
            TimeSpan timeout = TriggerEngineService.MatchTimeout;

            // Ream tells the raid, '...'   or   You tell your raid, '...'   (the quoted text is read only for the tag and the cue)
            var say = new Regex(
                @"^(?:(?<caster>[^\s']+) tells the raid,|You tell your raid,)\s*'(?<body>.*)'\s*$", options, timeout);

            // the tag anywhere in the text, but not as part of a longer number ("13-13-13" is not "3-3-3")
            var tagRegex = new Regex($@"(?<!\d){tag}(?!\d)", options, timeout);

            return new ChainPatterns(say, tagRegex, settings.CueKeyword);
        }

        private static string BuildTag(string format, string firstGroup, string repeat)
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            int index = 0;
            while (index < format.Length)
            {
                int token = format.IndexOf(ChChainSettings.PositionToken, index, StringComparison.Ordinal);
                if (token < 0)
                {
                    sb.Append(Regex.Escape(format[index..]));
                    break;
                }

                sb.Append(Regex.Escape(format[index..token]));
                sb.Append(first ? firstGroup : repeat);
                first = false;
                index = token + ChChainSettings.PositionToken.Length;
            }

            return sb.ToString();
        }
    }
}
