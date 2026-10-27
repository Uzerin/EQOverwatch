using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Settings;

namespace EQCompanion.Core.Services;

/// <summary>
/// Starts "active" and "cooldown" bars when an AA or discipline is used. Two log lines are recognized:
/// <c>You activate Defensive Discipline.</c> (the log's own character) and <c>Regnier activates Defensive Discipline.</c> (anyone else
/// the log can see). Each tracked ability is either <see cref="AbilityScope.Everyone"/> (every user gets bars) or
/// <see cref="AbilityScope.MyCharacters"/> (only the characters the app is monitoring).
/// <para>
/// Bars belong to the user: Regnier's Defensive and Fullfrontal's Defensive are separate bars, and one using it again never touches
/// the other's. Several of your logs seeing the same use (Regnier's own log and your warrior's log) count as one use
/// (<see cref="DuplicateWindow"/>).
/// </para>
/// </summary>
public sealed class AbilityTrackerService : ITriggerLineSink
{
    public const string TimerSourceName = "AA & Disc timers";

    public const string ActiveSuffix = "(active)";
    public const string CooldownSuffix = "(cooldown)";

    /// <summary>The same user using the same ability this soon is one use seen by several logs.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private const string SelfPrefix = "You activate ";
    private const string OtherInfix = " activates ";

    private readonly TriggerEngineService _timers;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Rule, string User), DateTime> _lastUse = new();
    private Dictionary<string, TrackedAbilitySetting> _byName = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _mine = new(StringComparer.OrdinalIgnoreCase);
    private long _started;
    private string _lastStarted = string.Empty;

    public AbilityTrackerService(TriggerEngineService timers)
    {
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
    }

    /// <summary>Bars started since the app opened (an active and a cooldown bar count as two).</summary>
    public long TimersStarted => Interlocked.Read(ref _started);

    /// <summary>"Regnier: Defensive Discipline (active)".</summary>
    public string LastStarted => Volatile.Read(ref _lastStarted);

    /// <summary>The enabled abilities that can start a bar (a name, and at least one of the two durations).</summary>
    public int AbilityCount => Volatile.Read(ref _byName).Count;

    /// <summary>Replaces the tracked list. Disabled abilities and ones with nothing to show are ignored; the first of a name wins.</summary>
    public void SetAbilities(IEnumerable<TrackedAbilitySetting> abilities)
    {
        ArgumentNullException.ThrowIfNull(abilities);

        var byName = new Dictionary<string, TrackedAbilitySetting>(StringComparer.OrdinalIgnoreCase);
        foreach (TrackedAbilitySetting ability in abilities)
        {
            string name = ability.Name?.Trim() ?? string.Empty;
            if (!ability.IsEnabled || name.Length == 0 || (ability.ActiveSeconds <= 0 && ability.CooldownSeconds <= 0))
            {
                continue;
            }

            if (!byName.ContainsKey(name))
            {
                TrackedAbilitySetting copy = ability.Clone();
                copy.Name = name;
                byName[name] = copy;
            }
        }

        lock (_gate)
        {
            Volatile.Write(ref _byName, byName);

            var ids = new HashSet<Guid>(byName.Values.Select(a => a.Id));
            foreach ((Guid Rule, string User) key in _lastUse.Keys.Where(k => !ids.Contains(k.Rule)).ToList())
            {
                _lastUse.Remove(key);
            }
        }
    }

    /// <summary>The characters this app monitors: the ones <see cref="AbilityScope.MyCharacters"/> abilities apply to.</summary>
    public void SetMonitoredCharacters(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Volatile.Write(
            ref _mine,
            new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase));
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        // Almost every line is something else: reject cheaply before looking at anything.
        if (message.Length < 12 || message[^1] != '.' || message.IndexOf("activate", StringComparison.Ordinal) < 0)
        {
            return;
        }

        Dictionary<string, TrackedAbilitySetting> abilities = Volatile.Read(ref _byName);
        if (abilities.Count == 0)
        {
            return;
        }

        string user;
        string abilityName;
        bool isSelf;

        if (message.StartsWith(SelfPrefix, StringComparison.Ordinal))
        {
            user = characterName;
            abilityName = message[SelfPrefix.Length..^1].Trim().ToString();
            isSelf = true;
        }
        else
        {
            int infix = message.IndexOf(OtherInfix, StringComparison.Ordinal);
            if (infix <= 0)
            {
                return;
            }

            ReadOnlySpan<char> who = message[..infix];
            if (who.IndexOf(' ') >= 0 || who.IndexOf('\'') >= 0 || who.IndexOf('`') >= 0)
            {
                return; // a player name is one word; "a goblin activates ..." and pets are not tracked
            }

            user = who.ToString();
            abilityName = message[(infix + OtherInfix.Length)..^1].Trim().ToString();
            isSelf = false;
        }

        if (user.Length == 0 || abilityName.Length == 0 || !abilities.TryGetValue(abilityName, out TrackedAbilitySetting? ability))
        {
            return;
        }

        if (ability.Scope == AbilityScope.MyCharacters && !isSelf && !Volatile.Read(ref _mine).Contains(user))
        {
            return;
        }

        lock (_gate)
        {
            if (_lastUse.TryGetValue((ability.Id, user), out DateTime last)
                && receivedUtc >= last && receivedUtc - last < DuplicateWindow)
            {
                return; // the same use, seen by another watched log
            }

            _lastUse[(ability.Id, user)] = receivedUtc;
        }

        if (ability.ActiveSeconds > 0)
        {
            Start(ability, user, ActiveSuffix, ability.ActiveSeconds, ability.ActiveOverlay, receivedUtc);
        }

        if (ability.CooldownSeconds > 0)
        {
            Start(ability, user, CooldownSuffix, ability.CooldownSeconds, ability.CooldownOverlay, receivedUtc);
        }
    }

    private void Start(TrackedAbilitySetting ability, string user, string suffix, int seconds, TimerOverlayCategory overlay, DateTime now)
    {
        string label = $"{user}: {ability.Name} {suffix}";
        _timers.StartTimer(ability.Id, TimerSourceName, user, label, seconds, now, overlay);
        Volatile.Write(ref _lastStarted, label);
        Interlocked.Increment(ref _started);
    }
}
