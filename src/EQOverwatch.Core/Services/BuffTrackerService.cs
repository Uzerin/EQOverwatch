using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Turns log lines into buff, song and DoT countdown bars using the spell database and the user's whitelist of tracked spells.
/// It only sees live lines from trigger-enabled logs (it is attached to the trigger side of the pipeline, which historical
/// imports never use). Timers appear in the same snapshot the timer overlay already renders.
/// <para>
/// <b>1. Pending casts.</b> "You begin casting/singing X.", "Name begins to cast a spell. &lt;X&gt;" and "Name begins to sing a
/// song. &lt;X&gt;" for a tracked X queue a pending cast that stays valid for the spell's cast time plus 2.5 s.
/// </para>
/// <para>
/// <b>2. Landing.</b> (Songs are the exception: a bard song, or any spell announced with "sing", starts only from the singer's
/// own "You begin singing X." line and never from landing text.) A line equal to a tracked spell's cast-on-you text, or ending with its cast-on-other text, starts a bar
/// (or restarts the existing one, so a bard song that is re-sung is refreshed). When several tracked spells share that text, a
/// pending cast of the same character's log decides; with none, the longest-lasting tracked spell wins. The bar is labelled
/// "Target: Spell" and lasts the spell's base duration.
/// </para>
/// <para>
/// <b>3. Early fade.</b> A line equal to a tracked spell's fade text (or "Your " followed by it) removes that character's
/// "Character: Spell" bar. If several tracked spells share the fade text, each matching bar is removed.
/// </para>
/// Bars are keyed by (target, spell), not by which log saw the line, so two watched characters seeing the same landing share one bar.
/// <para>
/// <b>Allowed targets.</b> In a 72-person raid almost every "cast on other" line is about a stranger. Once
/// <see cref="SetAllowedTargets"/> has been called, a landing on someone else only starts a bar when that someone is in the set
/// (your watched characters plus any extra monitored characters, case-insensitive). Landings on the character whose log saw the line
/// ("cast on you") always count. Until it has been called, every target is allowed.
/// </para>
/// </summary>
public sealed class BuffTrackerService : ITriggerLineSink
{
    /// <summary>Source id the bars are registered under in <see cref="TriggerEngineService"/>.</summary>
    public static readonly Guid TimerSourceId = new("8f3b1a52-6c2e-4d7a-9a41-5b0c7e2d9f10");

    public const string TimerSourceName = "Buffs & songs";

    /// <summary>Extra time after the cast time during which a landing still counts as the pending cast's.</summary>
    public static readonly TimeSpan PendingCastGrace = TimeSpan.FromMilliseconds(2500);

    private const string YouBeginCasting = "You begin casting ";
    private const string YouBeginSinging = "You begin singing ";
    private const string OtherCastMarker = " begins to cast a spell. <";
    private const string OtherSingMarker = " begins to sing a song. <";
    private const string OtherCastingMarker = " begins casting ";
    private const string OtherSingingMarker = " begins singing ";
    private const string YourPrefix = "Your ";
    private const int MaxPendingCasts = 64;
    private const int MinSuffixLength = 3;

    private sealed record PendingCast(SpellInfo Spell, string ObservedBy, DateTime ExpiresUtc, bool IsSinging);

    private sealed record SuffixGroup(string Suffix, SpellInfo[] Spells);

    private sealed class TrackedState
    {
        public static readonly TrackedState Empty = new();

        public Dictionary<string, SpellInfo> ByName { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SpellInfo[]> ByCastOnYou { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpellInfo[]> ByFade { get; init; } = new(StringComparer.Ordinal);
        public SuffixGroup[] OtherSuffixes { get; init; } = Array.Empty<SuffixGroup>();
        public HashSet<int> YouLengths { get; init; } = new();
        public HashSet<int> FadeLengths { get; init; } = new();

        public bool IsEmpty => ByName.Count == 0;
    }

    private readonly SpellDatabaseService _spells;
    private readonly TriggerEngineService _timers;
    private readonly object _gate = new();
    private readonly List<PendingCast> _pending = new();
    private HashSet<int> _trackedIds = new();
    private TrackedState _state = TrackedState.Empty;
    private HashSet<string>? _allowedTargets;
    private IReadOnlyDictionary<int, TimerOverlayCategory> _overlayOverrides = new Dictionary<int, TimerOverlayCategory>();
    private long _timersStarted;
    private string _lastStarted = string.Empty;

    public BuffTrackerService(SpellDatabaseService spells, TriggerEngineService timers)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));

        _spells.DatabaseChanged += Rebuild;
        Rebuild();
    }

    /// <summary>The whitelist. Ids that are not in the loaded database are kept and take effect once it contains them.</summary>
    public IReadOnlyCollection<int> TrackedSpellIds
    {
        get
        {
            lock (_gate)
            {
                return _trackedIds.ToArray();
            }
        }
    }

    /// <summary>
    /// The characters a "cast on other" landing may start a bar for, or null when every target is allowed (the default).
    /// </summary>
    public IReadOnlyCollection<string>? AllowedTargets => Volatile.Read(ref _allowedTargets);

    /// <summary>
    /// Restricts "cast on other" bars to these characters (case-insensitive). Pass null to allow every target again.
    /// An empty set allows nobody but the characters whose own logs report the landing.
    /// </summary>
    public void SetAllowedTargets(IEnumerable<string>? names)
    {
        HashSet<string>? set = names is null
            ? null
            : new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _allowedTargets, set);
    }

    /// <summary>Casts waiting to land (including ones that have expired but have not been purged yet).</summary>
    public int PendingCastCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Replaces the whitelist of tracked spell ids. Each spell's bar goes to the overlay the spell file suggests (any overlay choices
    /// made with <see cref="SetTrackedSpellOverlays"/> are forgotten).
    /// </summary>
    public void SetTrackedSpells(IEnumerable<int> spellIds)
    {
        ArgumentNullException.ThrowIfNull(spellIds);

        lock (_gate)
        {
            _trackedIds = new HashSet<int>(spellIds);
            Volatile.Write(ref _overlayOverrides, new Dictionary<int, TimerOverlayCategory>());
        }

        Rebuild();
    }

    /// <summary>
    /// Replaces the whitelist with these spells, each with the overlay its bar must appear in. The user's choice always wins over the
    /// spell file's class-based category and over the "sung song" rule.
    /// </summary>
    public void SetTrackedSpellOverlays(IEnumerable<KeyValuePair<int, TimerOverlayCategory>> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);

        var overrides = new Dictionary<int, TimerOverlayCategory>();
        foreach (KeyValuePair<int, TimerOverlayCategory> pair in spells)
        {
            overrides[pair.Key] = pair.Value;
        }

        lock (_gate)
        {
            _trackedIds = new HashSet<int>(overrides.Keys);
            Volatile.Write(ref _overlayOverrides, overrides);
        }

        Rebuild();
    }

    /// <summary>How many bars this service has started since it was created (for the tracker tab's "is it working?" line).</summary>
    public long TimersStarted => Interlocked.Read(ref _timersStarted);

    /// <summary>"Oodles: Spirit of Wolf" for the most recent bar started, or empty.</summary>
    public string LastStarted => Volatile.Read(ref _lastStarted);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        TrackedState state = Volatile.Read(ref _state);
        if (state.IsEmpty || message.IsEmpty)
        {
            return;
        }

        if (TryHandleCast(state, characterName, message, receivedUtc))
        {
            return;
        }

        if (TryHandleFade(state, characterName, message))
        {
            return;
        }

        TryHandleLanding(state, characterName, message, receivedUtc);
    }

    // ───────────────────────── step 1: pending casts ─────────────────────────

    private bool TryHandleCast(TrackedState state, string observedBy, ReadOnlySpan<char> message, DateTime now)
    {
        ReadOnlySpan<char> spellName;
        bool isSinging = false;

        if (message.StartsWith(YouBeginCasting, StringComparison.Ordinal))
        {
            spellName = WithoutTrailingPeriod(message[YouBeginCasting.Length..]);
        }
        else if (message.StartsWith(YouBeginSinging, StringComparison.Ordinal))
        {
            spellName = WithoutTrailingPeriod(message[YouBeginSinging.Length..]);
            isSinging = true;
        }
        else if ((message[^1] == '>' || message[^1] == '.') && TrySplitOtherCast(message, out spellName, out isSinging))
        {
            // spellName and isSinging were set by TrySplitOtherCast
        }
        else
        {
            return false;
        }

        if (spellName.IsEmpty)
        {
            return false;
        }

        if (state.ByName.TryGetValue(spellName.ToString(), out SpellInfo? spell))
        {
            lock (_gate)
            {
                PurgeExpiredLocked(now);
                _pending.Add(new PendingCast(spell, observedBy, now.AddMilliseconds(spell.CastTimeMs) + PendingCastGrace, isSinging));
                if (_pending.Count > MaxPendingCasts)
                {
                    _pending.RemoveAt(0);
                }
            }

            if (isSinging && message.StartsWith(YouBeginSinging, StringComparison.Ordinal))
            {
                StartSong(spell, observedBy, now);
            }
        }

        return true; // a cast announcement is never also a landing or a fade
    }

    /// <summary>
    /// Songs have no landing text worth waiting for: your own "You begin singing X." starts the bar at once and restarts it every
    /// time the song is sung again. (Another bard's "begins to sing a song" only queues a pending cast, because the song lands on
    /// the group and not on the singer.)
    /// </summary>
    private void StartSong(SpellInfo spell, string character, DateTime now)
    {
        TimerOverlayCategory category = Volatile.Read(ref _overlayOverrides).TryGetValue(spell.SpellId, out TimerOverlayCategory chosen)
            ? chosen
            : spell.BaseDurationSeconds <= SpellDatabaseService.MaxBardSongSeconds ? TimerOverlayCategory.Songs : spell.Category;

        string label = Label(character, spell);
        _timers.StartTimer(TimerSourceId, TimerSourceName, character, label, spell.BaseDurationSeconds, now, category);
        Interlocked.Increment(ref _timersStarted);
        Volatile.Write(ref _lastStarted, label);
    }

    /// <summary>Handles "(?&lt;caster&gt;\w+) begins to cast a spell. &lt;(?&lt;spell&gt;.+)&gt;" and the "sing a song" variant.</summary>
    private static bool TrySplitOtherCast(ReadOnlySpan<char> message, out ReadOnlySpan<char> spellName, out bool isSinging)
    {
        spellName = default;
        isSinging = false;

        int marker = OtherCastMarker.Length;
        int index = message.IndexOf(OtherCastMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            marker = OtherSingMarker.Length;
            index = message.IndexOf(OtherSingMarker, StringComparison.Ordinal);
            isSinging = true;
        }

        if (index < 0)
        {
            // Newer wording: "Trick begins casting Complete Heal." / "Oodles begins singing Warsong of the Vah Shir."
            marker = OtherCastingMarker.Length;
            index = message.IndexOf(OtherCastingMarker, StringComparison.Ordinal);
            isSinging = false;
            if (index < 0)
            {
                marker = OtherSingingMarker.Length;
                index = message.IndexOf(OtherSingingMarker, StringComparison.Ordinal);
                isSinging = true;
            }

            if (index <= 0 || message[^1] != '.' || !IsWord(message[..index]))
            {
                return false;
            }

            spellName = message[(index + marker)..^1];
            return !spellName.IsEmpty;
        }

        if (index <= 0 || !IsWord(message[..index]))
        {
            return false;
        }

        spellName = message[(index + marker)..^1];
        return !spellName.IsEmpty;
    }

    // ───────────────────────── step 3: early fade ─────────────────────────

    private bool TryHandleFade(TrackedState state, string characterName, ReadOnlySpan<char> message)
    {
        if (!state.FadeLengths.Contains(message.Length))
        {
            return false;
        }

        string text = message.ToString();
        bool handled = false;

        if (state.ByFade.TryGetValue(text, out SpellInfo[]? exact))
        {
            CancelAll(exact, characterName);
            handled = true;
        }

        if (text.StartsWith(YourPrefix, StringComparison.Ordinal) && state.ByFade.TryGetValue(text[YourPrefix.Length..], out SpellInfo[]? afterYour))
        {
            CancelAll(afterYour, characterName);
            handled = true;
        }

        return handled;
    }

    private void CancelAll(SpellInfo[] spells, string characterName)
    {
        foreach (SpellInfo spell in spells)
        {
            _timers.CancelTimer(TimerSourceId, characterName, Label(characterName, spell));
        }
    }

    // ───────────────────────── step 2: landing ─────────────────────────

    private void TryHandleLanding(TrackedState state, string characterName, ReadOnlySpan<char> message, DateTime now)
    {
        SpellInfo[]? candidates = null;
        string target = characterName;

        if (state.YouLengths.Contains(message.Length))
        {
            state.ByCastOnYou.TryGetValue(message.ToString(), out candidates);
        }

        if (candidates is null)
        {
            // The longest matching suffix wins, so "looks a lot faster." is not mistaken for a spell ending in "faster.".
            foreach (SuffixGroup group in state.OtherSuffixes)
            {
                if (message.Length <= group.Suffix.Length || !message.EndsWith(group.Suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                ReadOnlySpan<char> who = message[..^group.Suffix.Length].TrimEnd();
                if (who.IsEmpty)
                {
                    continue;
                }

                candidates = group.Spells;
                target = who.ToString();

                if (Volatile.Read(ref _allowedTargets) is { } allowed && !allowed.Contains(target))
                {
                    return; // a bystander, not one of the characters being monitored
                }

                break;
            }
        }

        if (candidates is null || candidates.Length == 0)
        {
            return;
        }

        (SpellInfo spell, bool wasSung) = Resolve(candidates, characterName, now);

        // The user's overlay choice wins. Without one, a spell announced with "sing" and short enough to be a song belongs in the
        // Songs overlay even if the file's class columns say otherwise.
        TimerOverlayCategory category;
        if (Volatile.Read(ref _overlayOverrides).TryGetValue(spell.SpellId, out TimerOverlayCategory chosen))
        {
            category = chosen;
        }
        else
        {
            category = wasSung && spell.BaseDurationSeconds <= SpellDatabaseService.MaxBardSongSeconds
                ? TimerOverlayCategory.Songs
                : spell.Category;
        }

        // Songs start only from the singer's own "You begin singing X." line (see StartSong). Their landing text appears in the log
        // of everyone in range, which would otherwise put a bar in the Songs overlay for each of those characters. A spell counts as
        // a song when the file says so, when it was announced with "sing", or when the user put it in the Songs overlay.
        if (wasSung || spell.Category == TimerOverlayCategory.Songs || category == TimerOverlayCategory.Songs)
        {
            return;
        }

        string label = Label(target, spell);
        _timers.StartTimer(TimerSourceId, TimerSourceName, target, label, spell.BaseDurationSeconds, now, category);
        Interlocked.Increment(ref _timersStarted);
        Volatile.Write(ref _lastStarted, label);
    }

    /// <summary>A pending cast from the same log decides; otherwise the longest-lasting tracked candidate (they are pre-sorted).</summary>
    private (SpellInfo Spell, bool WasSung) Resolve(SpellInfo[] candidates, string observedBy, DateTime now)
    {
        lock (_gate)
        {
            PurgeExpiredLocked(now);

            for (int i = 0; i < _pending.Count; i++)
            {
                PendingCast pending = _pending[i];
                if (!string.Equals(pending.ObservedBy, observedBy, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (SpellInfo candidate in candidates)
                {
                    if (candidate.SpellId == pending.Spell.SpellId)
                    {
                        _pending.RemoveAt(i);
                        return (candidate, pending.IsSinging);
                    }
                }
            }
        }

        return (candidates[0], false);
    }

    // ───────────────────────── state ─────────────────────────

    private void PurgeExpiredLocked(DateTime now) => _pending.RemoveAll(p => p.ExpiresUtc < now);

    private static string Label(string target, SpellInfo spell) => $"{target}: {spell.Name}";

    private static ReadOnlySpan<char> WithoutTrailingPeriod(ReadOnlySpan<char> text) =>
        text.Length > 0 && text[^1] == '.' ? text[..^1] : default; // a cast line must end with '.'

    private static bool IsWord(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return !text.IsEmpty;
    }

    /// <summary>Re-indexes the tracked spells after the whitelist or the database changed.</summary>
    private void Rebuild()
    {
        SpellDatabase database = _spells.Database;

        TrackedState state;
        lock (_gate)
        {
            var tracked = new List<SpellInfo>();
            foreach (int id in _trackedIds)
            {
                if (database.FindById(id) is { } spell)
                {
                    tracked.Add(spell);
                }
            }

            _pending.Clear();
            state = tracked.Count == 0 ? TrackedState.Empty : BuildState(tracked);
        }

        Volatile.Write(ref _state, state);
    }

    private static TrackedState BuildState(List<SpellInfo> tracked)
    {
        var byName = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (SpellInfo spell in tracked)
        {
            if (!byName.TryGetValue(spell.Name, out SpellInfo? existing) || spell.BaseDurationSeconds > existing.BaseDurationSeconds)
            {
                byName[spell.Name] = spell;
            }
        }

        Dictionary<string, SpellInfo[]> Group(Func<SpellInfo, string> key) => tracked
            .Where(s => !string.IsNullOrEmpty(key(s)))
            .GroupBy(key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Sorted(g), StringComparer.Ordinal);

        Dictionary<string, SpellInfo[]> byYou = Group(s => s.CastOnYou);
        Dictionary<string, SpellInfo[]> byFade = Group(s => s.SpellFades.Trim());

        SuffixGroup[] suffixes = Group(s => s.CastOnOtherSuffix)
            .Where(p => p.Key.Length >= MinSuffixLength)
            .Select(p => new SuffixGroup(p.Key, p.Value))
            .OrderByDescending(g => g.Suffix.Length)
            .ToArray();

        var youLengths = new HashSet<int>(byYou.Keys.Select(k => k.Length));

        var fadeLengths = new HashSet<int>();
        foreach (string fade in byFade.Keys)
        {
            fadeLengths.Add(fade.Length);
            fadeLengths.Add(fade.Length + YourPrefix.Length); // the "Your " + text form
        }

        return new TrackedState
        {
            ByName = byName,
            ByCastOnYou = byYou,
            ByFade = byFade,
            OtherSuffixes = suffixes,
            YouLengths = youLengths,
            FadeLengths = fadeLengths,
        };
    }

    private static SpellInfo[] Sorted(IEnumerable<SpellInfo> spells) =>
        spells.OrderByDescending(s => s.BaseDurationSeconds).ThenBy(s => s.SpellId).ToArray();
}
