using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Wall-clock trigger and timer engine for every trigger-enabled log. Lines arrive as spans; each trigger's regex is first
/// run against the span (no allocation) and only a line that matches is turned into a string to read capture groups.
/// Timer start/expiry use the <c>receivedUtc</c> stamped when the line was read from disk, never the log's own timestamp.
/// <para>
/// Matching is protected by a per-match timeout, so a pathological user pattern is reported through <see cref="Error"/>
/// instead of stalling the pipeline. Timers are keyed by (trigger, character, kind, text): re-triggering the same alert
/// restarts it rather than stacking duplicates, while the same trigger on two characters yields two timers.
/// </para>
/// </summary>
public sealed class TriggerEngineService : ITriggerLineSink
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private sealed class CompiledTrigger
    {
        public CompiledTrigger(TriggerDefinition definition, Regex regex)
        {
            Definition = definition;
            Regex = regex;
            if (definition.Characters is { Count: > 0 } characters)
            {
                Characters = new HashSet<string>(characters, StringComparer.OrdinalIgnoreCase);
            }
        }

        public TriggerDefinition Definition { get; }
        public Regex Regex { get; }
        public HashSet<string>? Characters { get; }
    }

    private readonly record struct TimerKey(Guid TriggerId, string Character, TimerKind Kind, string Text);

    private readonly record struct PauseState(DateTime At, bool Hidden, bool IncludeSongs);

    private readonly object _gate = new();
    private readonly Dictionary<TimerKey, ActiveTimerSnapshot> _timers = new();
    private readonly Dictionary<(Guid Source, string Subject), PauseState> _paused = new();
    private CompiledTrigger[] _compiled = Array.Empty<CompiledTrigger>();
    private TriggerSnapshot _snapshot = TriggerSnapshot.Empty;
    private long _version;
    private long _nextTimerId;

    /// <summary>Raised on the calling thread (the pipeline consumer for live lines) whenever a trigger matches, after timers are updated.</summary>
    public event Action<TriggerFiredEvent>? TriggerFired;

    /// <summary>Raised for regex timeouts and exceptions from <see cref="TriggerFired"/> handlers.</summary>
    public event Action<Exception>? Error;

    /// <summary>Latest running alerts/timers. Safe to read from any thread without locking.</summary>
    public TriggerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Replaces the active trigger set. Disabled triggers are skipped; triggers with an empty or invalid pattern are
    /// returned as errors and skipped while the rest stay active. Running timers are left alone.
    /// </summary>
    public IReadOnlyList<TriggerCompileError> SetTriggers(IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        CompiledTrigger[] compiled = Compile(triggers, out List<TriggerCompileError> errors);
        Volatile.Write(ref _compiled, compiled);
        return errors;
    }

    /// <summary>
    /// Evaluates <paramref name="definitions"/> against one line as if it had just been read from <paramref name="characterName"/>'s
    /// log, without touching the active trigger set. Matches update timers, raise <see cref="TriggerFired"/> and are returned.
    /// Used by the editor's "Test / Fire" button and the overlay self-test. Disabled definitions are ignored.
    /// </summary>
    public IReadOnlyList<TriggerFiredEvent> Test(
        IEnumerable<TriggerDefinition> definitions,
        string characterName,
        string message,
        DateTime receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        CompiledTrigger[] compiled = Compile(definitions, out _);
        return Process(compiled, characterName, message, receivedUtc);
    }

    /// <summary>
    /// Starts (or restarts) a countdown bar that was not produced by a trigger, e.g. by the buff tracker. A bar is identified by
    /// (<paramref name="sourceId"/>, <paramref name="subject"/>, <paramref name="label"/>), so calling this again resets it.
    /// </summary>
    /// <param name="sourceId">Identifies the feature that owns the bar.</param>
    /// <param name="sourceName">Shown as the bar's trigger name.</param>
    /// <param name="subject">Whom the bar is about (shown as the bar's character).</param>
    public void StartTimer(
        Guid sourceId,
        string sourceName,
        string subject,
        string label,
        double durationSeconds,
        DateTime nowUtc,
        TimerOverlayCategory category = TimerOverlayCategory.RaidTriggers)
    {
        if (durationSeconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            PruneLocked(nowUtc);
            var key = new TimerKey(sourceId, subject, TimerKind.Countdown, label);
            ActiveTimerSnapshot timer = new(
                ++_nextTimerId, sourceId, sourceName, subject, TimerKind.Countdown, label,
                nowUtc, nowUtc.AddSeconds(durationSeconds), durationSeconds, category);
            if (_paused.TryGetValue((sourceId, subject.ToLowerInvariant()), out PauseState pause) && (pause.IncludeSongs || category != TimerOverlayCategory.Songs))
            {
                timer = timer with { PausedUtc = nowUtc, Hidden = pause.Hidden }; // started while the character's clock is stopped
            }

            _timers[key] = timer;
            PublishLocked();
        }
    }

    /// <summary>
    /// Whether <paramref name="timer"/> belongs to the subject (a character) of a pause request: same source, same name (capitals ignored),
    /// and a countdown bar.
    /// </summary>
    private static bool IsSubjectBar(ActiveTimerSnapshot timer, Guid sourceId, string subject) =>
        timer.TriggerId == sourceId && timer.Kind == TimerKind.Countdown
        && string.Equals(timer.CharacterName, subject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the clock of every bar of <paramref name="subject"/> from <paramref name="sourceId"/> (bars started later while it is stopped
    /// start stopped too). <paramref name="hidden"/> also keeps the bars off the overlays. Bars in the Songs overlay are left running
    /// unless <paramref name="includeSongs"/> is true. Calling it again changes the hidden and songs settings and keeps the original stop time.
    /// </summary>
    public void PauseSubject(Guid sourceId, string subject, DateTime nowUtc, bool hidden, bool includeSongs)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        lock (_gate)
        {
            var key = (sourceId, subject.Trim().ToLowerInvariant());
            DateTime at = _paused.TryGetValue(key, out PauseState existing) ? existing.At : nowUtc;
            _paused[key] = new PauseState(at, hidden, includeSongs);
            foreach (TimerKey timerKey in _timers.Keys.ToList())
            {
                ActiveTimerSnapshot timer = _timers[timerKey];
                if (!IsSubjectBar(timer, sourceId, subject.Trim()))
                {
                    continue;
                }

                bool applies = includeSongs || timer.Category != TimerOverlayCategory.Songs;
                _timers[timerKey] = applies
                    ? timer with { PausedUtc = timer.PausedUtc ?? nowUtc, Hidden = hidden }
                    : timer;
            }

            PublishLocked();
        }
    }

    /// <summary>Starts the clock again for the bars <see cref="PauseSubject"/> stopped: each one gets back the time it was stopped for.</summary>
    public void ResumeSubject(Guid sourceId, string subject, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        lock (_gate)
        {
            _paused.Remove((sourceId, subject.Trim().ToLowerInvariant()));
            foreach (TimerKey timerKey in _timers.Keys.ToList())
            {
                ActiveTimerSnapshot timer = _timers[timerKey];
                if (!IsSubjectBar(timer, sourceId, subject.Trim()) || timer.PausedUtc is not { } pausedAt)
                {
                    continue;
                }

                TimeSpan stopped = nowUtc > pausedAt ? nowUtc - pausedAt : TimeSpan.Zero;
                _timers[timerKey] = timer with
                {
                    StartedUtc = timer.StartedUtc + stopped,
                    ExpiresUtc = timer.ExpiresUtc + stopped,
                    PausedUtc = null,
                    Hidden = false,
                };
            }

            PublishLocked();
        }
    }

    /// <summary>Removes every bar of <paramref name="subject"/> from <paramref name="sourceId"/> (a character who died loses their buffs). Returns how many.</summary>
    public int CancelSubject(Guid sourceId, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return 0;
        }

        lock (_gate)
        {
            List<TimerKey> doomed = _timers
                .Where(p => IsSubjectBar(p.Value, sourceId, subject.Trim()))
                .Select(p => p.Key)
                .ToList();
            foreach (TimerKey key in doomed)
            {
                _timers.Remove(key);
            }

            if (doomed.Count > 0)
            {
                PublishLocked();
            }

            return doomed.Count;
        }
    }

    /// <summary>True when <paramref name="subject"/>'s bars from <paramref name="sourceId"/> are stopped right now.</summary>
    public bool IsSubjectPaused(Guid sourceId, string subject)
    {
        lock (_gate)
        {
            return _paused.ContainsKey((sourceId, subject.Trim().ToLowerInvariant()));
        }
    }

    /// <summary>The bars of <paramref name="sourceId"/> as they are now, for saving to disk.</summary>
    public IReadOnlyList<ActiveTimerSnapshot> BarsOf(Guid sourceId)
    {
        lock (_gate)
        {
            return _timers.Values.Where(t => t.TriggerId == sourceId && t.Kind == TimerKind.Countdown).OrderBy(t => t.Id).ToList();
        }
    }

    /// <summary>
    /// Puts back a bar saved by an earlier run. A bar that was running keeps its end time (the game's clock kept going); a bar that was
    /// stopped keeps the time it had left and stays stopped until <see cref="ResumeSubject"/>. A bar that has run out is dropped.
    /// </summary>
    public bool RestoreBar(ActiveTimerSnapshot saved, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ActiveTimerSnapshot bar = saved;
        if (saved.PausedUtc is { } pausedAt)
        {
            double left = Math.Max(0.0, (saved.ExpiresUtc - pausedAt).TotalSeconds);
            if (left <= 0)
            {
                return false;
            }

            DateTime expires = nowUtc.AddSeconds(left);
            bar = saved with { ExpiresUtc = expires, StartedUtc = expires.AddSeconds(-saved.DurationSeconds), PausedUtc = nowUtc };
        }
        else if (saved.ExpiresUtc <= nowUtc)
        {
            return false;
        }

        lock (_gate)
        {
            _timers[new TimerKey(bar.TriggerId, bar.CharacterName, TimerKind.Countdown, bar.Text)] = bar with { Id = ++_nextTimerId };
            PublishLocked();
        }

        return true;
    }

    /// <summary>Marks a subject as stopped without a bar yet (used when saved state is put back).</summary>
    public void RestorePausedSubject(Guid sourceId, string subject, DateTime nowUtc, bool hidden, bool includeSongs)
    {
        lock (_gate)
        {
            _paused[(sourceId, subject.Trim().ToLowerInvariant())] = new PauseState(nowUtc, hidden, includeSongs);
        }
    }

    /// <summary>
    /// Speaks <paramref name="audioText"/> through the same <see cref="TriggerFired"/> event trigger audio uses, without a
    /// trigger or a timer behind it (e.g. a raid boss warning).
    /// </summary>
    public void PublishAudio(Guid sourceId, string sourceName, string characterName, string audioText, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(audioText))
        {
            return;
        }

        var fired = new TriggerFiredEvent(
            sourceId, sourceName, characterName, TriggerActions.Audio, audioText, audioText, null, 0, nowUtc);
        try
        {
            TriggerFired?.Invoke(fired);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    /// <summary>Removes a bar started with <see cref="StartTimer"/>. Returns false if it was not running.</summary>
    public bool CancelTimer(Guid sourceId, string subject, string label)
    {
        lock (_gate)
        {
            if (!_timers.Remove(new TimerKey(sourceId, subject, TimerKind.Countdown, label)))
            {
                return false;
            }

            PublishLocked();
            return true;
        }
    }

    /// <summary>
    /// Shows (or refreshes) a flash banner that was not produced by a trigger, e.g. the Complete Heal late-cast alarm. Like
    /// <see cref="StartTimer"/>, an alert is identified by (<paramref name="sourceId"/>, <paramref name="subject"/>, <paramref name="text"/>).
    /// </summary>
    public void StartAlert(
        Guid sourceId,
        string sourceName,
        string subject,
        string text,
        double durationSeconds,
        DateTime nowUtc,
        TimerOverlayCategory category = TimerOverlayCategory.RaidTriggers)
    {
        if (durationSeconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            PruneLocked(nowUtc);
            var key = new TimerKey(sourceId, subject, TimerKind.TextAlert, text);
            _timers[key] = new ActiveTimerSnapshot(
                ++_nextTimerId, sourceId, sourceName, subject, TimerKind.TextAlert, text,
                nowUtc, nowUtc.AddSeconds(durationSeconds), durationSeconds, category);
            PublishLocked();
        }
    }

    /// <summary>Removes an alert started with <see cref="StartAlert"/>. Returns false if it was not showing.</summary>
    public bool CancelAlert(Guid sourceId, string subject, string text)
    {
        lock (_gate)
        {
            if (!_timers.Remove(new TimerKey(sourceId, subject, TimerKind.TextAlert, text)))
            {
                return false;
            }

            PublishLocked();
            return true;
        }
    }

    /// <summary>Removes every running alert and timer.</summary>
    public void ClearTimers()
    {
        lock (_gate)
        {
            _timers.Clear();
            PublishLocked();
        }
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        CompiledTrigger[] triggers = Volatile.Read(ref _compiled);
        if (triggers.Length == 0)
        {
            return;
        }

        Process(triggers, characterName, message, receivedUtc);
    }

    private static CompiledTrigger[] Compile(IEnumerable<TriggerDefinition> triggers, out List<TriggerCompileError> errors)
    {
        var compiled = new List<CompiledTrigger>();
        errors = new List<TriggerCompileError>();

        foreach (TriggerDefinition trigger in triggers)
        {
            if (!trigger.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(trigger.Pattern))
            {
                errors.Add(new TriggerCompileError(trigger, "Pattern is empty."));
                continue;
            }

            try
            {
                var regex = new Regex(trigger.Pattern, RegexOptions.CultureInvariant, MatchTimeout);
                compiled.Add(new CompiledTrigger(trigger, regex));
            }
            catch (ArgumentException ex) // RegexParseException derives from ArgumentException
            {
                errors.Add(new TriggerCompileError(trigger, ex.Message));
            }
        }

        return compiled.ToArray();
    }

    private IReadOnlyList<TriggerFiredEvent> Process(
        CompiledTrigger[] triggers,
        string characterName,
        ReadOnlySpan<char> message,
        DateTime receivedUtc)
    {
        string? line = null; // materialized once, and only if some trigger matches
        List<TriggerFiredEvent>? fired = null;

        foreach (CompiledTrigger trigger in triggers)
        {
            if (trigger.Characters is not null && !trigger.Characters.Contains(characterName))
            {
                continue;
            }

            try
            {
                if (!trigger.Regex.IsMatch(message))
                {
                    continue;
                }

                line ??= message.ToString();
                Match match = trigger.Regex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                (fired ??= new List<TriggerFiredEvent>()).Add(Build(trigger, match, characterName, receivedUtc));
            }
            catch (RegexMatchTimeoutException ex)
            {
                Error?.Invoke(ex);
            }
        }

        if (fired is null)
        {
            return Array.Empty<TriggerFiredEvent>();
        }

        lock (_gate)
        {
            PruneLocked(receivedUtc);
            foreach (TriggerFiredEvent e in fired)
            {
                ApplyLocked(e);
            }

            PublishLocked();
        }

        foreach (TriggerFiredEvent e in fired)
        {
            try
            {
                TriggerFired?.Invoke(e);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }

        return fired;
    }

    private static TriggerFiredEvent Build(CompiledTrigger trigger, Match match, string characterName, DateTime receivedUtc)
    {
        TriggerDefinition def = trigger.Definition;
        string display = TriggerTemplate.Render(def.DisplayText, trigger.Regex, match, characterName, def.Name);

        string? audioText = null;
        if (def.Actions.HasFlag(TriggerActions.Audio))
        {
            if (def.AudioText is not null)
            {
                audioText = TriggerTemplate.Render(def.AudioText, trigger.Regex, match, characterName, def.Name);
            }
            else if (string.IsNullOrWhiteSpace(def.AudioFilePath))
            {
                audioText = display; // no explicit TTS text and no sound file: speak the display text
            }
        }

        return new TriggerFiredEvent(
            def.Id, def.Name, characterName, def.Actions, display, audioText,
            def.AudioFilePath, def.EffectiveDurationSeconds, receivedUtc, def.OverlayCategory);
    }

    private void ApplyLocked(TriggerFiredEvent e)
    {
        if (e.Actions.HasFlag(TriggerActions.TextOverlay))
        {
            Upsert(e, TimerKind.TextAlert);
        }

        if (e.Actions.HasFlag(TriggerActions.CountdownTimer))
        {
            Upsert(e, TimerKind.Countdown);
        }
    }

    private void Upsert(TriggerFiredEvent e, TimerKind kind)
    {
        var key = new TimerKey(e.TriggerId, e.CharacterName, kind, e.DisplayText);
        _timers[key] = new ActiveTimerSnapshot(
            ++_nextTimerId, e.TriggerId, e.TriggerName, e.CharacterName, kind, e.DisplayText,
            e.ReceivedUtc, e.ReceivedUtc.AddSeconds(e.DurationSeconds), e.DurationSeconds, e.Category);
    }

    private void PruneLocked(DateTime nowUtc)
    {
        List<TimerKey>? expired = null;
        foreach (KeyValuePair<TimerKey, ActiveTimerSnapshot> pair in _timers)
        {
            if (pair.Value.IsExpired(nowUtc))
            {
                (expired ??= new List<TimerKey>()).Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (TimerKey key in expired)
            {
                _timers.Remove(key);
            }
        }
    }

    private void PublishLocked()
    {
        ActiveTimerSnapshot[] timers = _timers.Values.OrderBy(t => t.ExpiresUtc).ThenBy(t => t.Id).ToArray();
        Volatile.Write(ref _snapshot, new TriggerSnapshot(++_version, Array.AsReadOnly(timers)));
    }
}
