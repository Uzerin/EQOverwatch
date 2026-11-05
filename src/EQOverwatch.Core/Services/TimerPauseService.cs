using System.Text.Json;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>What one character's buff and debuff bars are doing, and why.</summary>
public enum TimerClockState
{
    /// <summary>Counting down.</summary>
    Running,

    /// <summary>In a safe zone (such as the Plane of Knowledge): the game does not tick buffs there, so the bars are stopped but still shown.</summary>
    SafeZone,

    /// <summary>Logged out: the bars are stopped and hidden until the character logs in again.</summary>
    LoggedOut,
}

/// <summary>
/// Keeps each character's buff, debuff and DoT bars honest about the game's clock. From the log it learns when a character
/// <list type="bullet">
/// <item>enters a safe zone ("You have entered The Bazaar.") or leaves it: the bars stop, then start again with the time they stopped for given back;</item>
/// <item>logs out (it prepared to camp and did not abandon it) or in again (any new line from its log): the bars are hidden and stopped, then shown and started again;</item>
/// <item>dies ("You have been slain by ..."): the bars are removed, as the game removes the buffs.</item>
/// </list>
/// The bars and the state are saved to a file, so closing and reopening the app does not lose them. Bars that were running keep their
/// end time, because the game's clock went on; stopped bars keep the time they had left.
/// <para>Songs are stopped only when the character logs out, not in a safe zone. Thread-safe.</para>
/// </summary>
public sealed class TimerPauseService : ITriggerLineSink
{
    /// <summary>The zones that stop buff timers unless the user changes the list.</summary>
    public static readonly IReadOnlyList<string> DefaultSafeZones = new[] { "Plane of Knowledge", "Guild Lobby", "Guild Hall", "Bazaar", "Nexus" };

    /// <summary>How long after "prepare your camp" without "abandon" the character counts as logged out (the camp takes 30 seconds).</summary>
    public static readonly TimeSpan CampTime = TimeSpan.FromSeconds(33);

    private const string CampLine = "It will take you about 30 seconds to prepare your camp.";
    private const string AbandonCampLine = "You abandon your preparations to camp.";
    private const string EnteredPrefix = "You have entered ";
    private const string SlainPrefix = "You have been slain by ";
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private sealed class CharacterState
    {
        public bool LoggedOut;
        public bool InSafeZone;
        public DateTime? CampStartedUtc;
        public TimerClockState Applied = TimerClockState.Running;
    }

    internal sealed record SavedCharacter(string Name, bool LoggedOut, bool InSafeZone);

    internal sealed record SavedBar(
        string Subject, string Label, string SourceName, double DurationSeconds, DateTime StartedUtc, DateTime ExpiresUtc,
        DateTime? PausedUtc, bool Hidden, TimerOverlayCategory Category);

    internal sealed record SavedState(DateTime SavedUtc, List<SavedCharacter> Characters, List<SavedBar> Bars);

    private readonly TriggerEngineService _engine;
    private readonly Guid _sourceId;
    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterState> _characters = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _safeZones = Normalize(DefaultSafeZones);
    private string? _filePath;
    private long _savedVersion = -1;
    private DateTime _lastSaveUtc;
    private volatile bool _dirty;

    public TimerPauseService(TriggerEngineService engine, Guid? sourceId = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _sourceId = sourceId ?? BuffTrackerService.TimerSourceId;
    }

    /// <summary>The safe zones, as the user wrote them.</summary>
    public IReadOnlyList<string> SafeZones
    {
        get
        {
            lock (_gate)
            {
                return _safeZones.OrderBy(z => z, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>Replaces the safe zone list (an empty list means no zone stops the timers). Characters already in a zone are re-checked at their next zone line.</summary>
    public void SetSafeZones(IEnumerable<string> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        lock (_gate)
        {
            _safeZones = Normalize(zones);
        }
    }

    /// <summary>What <paramref name="character"/>'s bars are doing now.</summary>
    public TimerClockState StateOf(string character)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(character, out CharacterState? state) ? Describe(state) : TimerClockState.Running;
        }
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.IsEmpty || string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        bool camp = message.SequenceEqual(CampLine);
        bool abandon = message.SequenceEqual(AbandonCampLine);
        bool slain = message.StartsWith(SlainPrefix, StringComparison.Ordinal);
        string? zone = message.StartsWith(EnteredPrefix, StringComparison.Ordinal) ? ParseZone(message) : null;

        lock (_gate)
        {
            CharacterState state = StateFor(characterName);

            // Any line at all means the character is in the game: a log only grows while someone is logged in.
            if (!camp)
            {
                state.LoggedOut = false;
            }

            if (camp)
            {
                state.CampStartedUtc = receivedUtc;
            }
            else if (abandon)
            {
                state.CampStartedUtc = null;
            }

            if (zone is not null)
            {
                state.InSafeZone = IsSafeZone(zone);
            }

            if (slain)
            {
                _engine.CancelSubject(_sourceId, characterName);
            }

            ApplyLocked(characterName, state, receivedUtc);
        }
    }

    /// <summary>Call about every quarter second: finishes a camp that was not abandoned, and saves changes every few seconds.</summary>
    public void Tick(DateTime nowUtc)
    {
        lock (_gate)
        {
            foreach ((string name, CharacterState state) in _characters)
            {
                if (state.CampStartedUtc is { } started && nowUtc - started >= CampTime)
                {
                    state.CampStartedUtc = null;
                    state.LoggedOut = true;
                    ApplyLocked(name, state, nowUtc);
                }
            }
        }

        if (_filePath is not null && nowUtc - _lastSaveUtc >= SaveEvery)
        {
            _lastSaveUtc = nowUtc;
            if (_dirty || _engine.Snapshot.Version != _savedVersion)
            {
                Save(nowUtc);
            }
        }
    }

    // ───────────────────────── state ─────────────────────────

    private CharacterState StateFor(string name)
    {
        if (!_characters.TryGetValue(name, out CharacterState? state))
        {
            state = new CharacterState();
            _characters[name] = state;
        }

        return state;
    }

    private static TimerClockState Describe(CharacterState state) =>
        state.LoggedOut ? TimerClockState.LoggedOut : state.InSafeZone ? TimerClockState.SafeZone : TimerClockState.Running;

    private void ApplyLocked(string name, CharacterState state, DateTime nowUtc)
    {
        TimerClockState wanted = Describe(state);
        if (wanted == state.Applied)
        {
            return;
        }

        // Stopped time is given back exactly once: leaving a stopped state resumes the bars, and a safe zone entered straight from
        // a logout resumes first (songs run again) and then stops the bars that belong to the safe zone.
        switch (wanted)
        {
            case TimerClockState.Running:
                _engine.ResumeSubject(_sourceId, name, nowUtc);
                break;
            case TimerClockState.SafeZone:
                if (state.Applied == TimerClockState.LoggedOut)
                {
                    _engine.ResumeSubject(_sourceId, name, nowUtc);
                }

                _engine.PauseSubject(_sourceId, name, nowUtc, hidden: false, includeSongs: false);
                break;
            case TimerClockState.LoggedOut:
                _engine.PauseSubject(_sourceId, name, nowUtc, hidden: true, includeSongs: true);
                break;
        }

        _dirty = true;
        state.Applied = wanted;
    }

    private bool IsSafeZone(string zone) => _safeZones.Contains(NormalizeZone(zone));

    /// <summary>The zone in "You have entered The Bazaar.", or null for lines that only look like one ("You have entered an area where levitation is prohibited.").</summary>
    private static string? ParseZone(ReadOnlySpan<char> message)
    {
        ReadOnlySpan<char> name = message[EnteredPrefix.Length..].Trim();
        if (name.IsEmpty || name[^1] != '.')
        {
            return null;
        }

        name = name[..^1].Trim();
        if (name.IsEmpty || name.StartsWith("an area", StringComparison.OrdinalIgnoreCase) || name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name.ToString();
    }

    private static string NormalizeZone(string zone)
    {
        string text = zone.Trim();
        if (text.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].TrimStart();
        }

        return text.ToLowerInvariant();
    }

    private static HashSet<string> Normalize(IEnumerable<string> zones) =>
        new(zones.Where(z => !string.IsNullOrWhiteSpace(z)).Select(NormalizeZone), StringComparer.Ordinal);

    // ───────────────────────── saving and loading ─────────────────────────

    /// <summary>Where the bars are saved. Reads the file now and puts back what it holds.</summary>
    public void SetStoragePath(string path, DateTime? nowUtc = null)
    {
        _filePath = path;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            SavedState? saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path));
            if (saved is null)
            {
                return;
            }

            DateTime now = nowUtc ?? DateTime.UtcNow;
            lock (_gate)
            {
                foreach (SavedCharacter c in saved.Characters ?? new List<SavedCharacter>())
                {
                    if (string.IsNullOrWhiteSpace(c.Name))
                    {
                        continue;
                    }

                    CharacterState state = StateFor(c.Name);
                    state.LoggedOut = c.LoggedOut;
                    state.InSafeZone = c.InSafeZone;
                    state.Applied = Describe(state);
                    if (state.Applied == TimerClockState.LoggedOut)
                    {
                        _engine.RestorePausedSubject(_sourceId, c.Name, now, hidden: true, includeSongs: true);
                    }
                    else if (state.Applied == TimerClockState.SafeZone)
                    {
                        _engine.RestorePausedSubject(_sourceId, c.Name, now, hidden: false, includeSongs: false);
                    }
                }

                foreach (SavedBar bar in saved.Bars ?? new List<SavedBar>())
                {
                    if (string.IsNullOrWhiteSpace(bar.Subject) || string.IsNullOrWhiteSpace(bar.Label) || bar.DurationSeconds <= 0)
                    {
                        continue;
                    }

                    _engine.RestoreBar(
                        new ActiveTimerSnapshot(
                            0, _sourceId, bar.SourceName ?? BuffTrackerService.TimerSourceName, bar.Subject, TimerKind.Countdown, bar.Label,
                            bar.StartedUtc, bar.ExpiresUtc, bar.DurationSeconds, bar.Category, bar.PausedUtc, bar.Hidden),
                        now);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a damaged file starts empty
        }
    }

    /// <summary>Writes the bars and each character's state to the file.</summary>
    public void Save(DateTime? nowUtc = null)
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            TriggerSnapshot snapshot = _engine.Snapshot;
            List<SavedCharacter> characters;
            lock (_gate)
            {
                characters = _characters
                    .Select(p => new SavedCharacter(p.Key, p.Value.LoggedOut, p.Value.InSafeZone))
                    .ToList();
            }

            var bars = _engine.BarsOf(_sourceId)
                .Select(b => new SavedBar(b.CharacterName, b.Text, b.TriggerName, b.DurationSeconds, b.StartedUtc, b.ExpiresUtc, b.PausedUtc, b.Hidden, b.Category))
                .ToList();
            string json = JsonSerializer.Serialize(new SavedState(nowUtc ?? DateTime.UtcNow, characters, bars));
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
            _savedVersion = snapshot.Version;
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // saved again at the next change
        }
    }
}
