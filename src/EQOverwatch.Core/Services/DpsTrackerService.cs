using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Roster;

namespace EQOverwatch.Core.Services;

/// <summary>Name-shape hints used when nothing better is known about an entity.</summary>
public static class PlayerNameHeuristic
{
    /// <summary>
    /// True when the name <i>could</i> be a player: a single capitalized word made only of letters ("Stackprime").
    /// Multi-word names, names starting with a lowercase letter ("a frost giant") and names with apostrophes or
    /// backticks (pets) can never be players. A single capitalized word is ambiguous ("Oodles" vs the boss "Tormax"),
    /// which is why <see cref="DpsTrackerService"/> resolves those from observed combat.
    /// </summary>
    public static bool CouldBePlayer(string name)
    {
        if (name.Length < 3 || !char.IsUpper(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsLetter(c))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record DpsTrackerOptions
{
    /// <summary>An encounter ends when the log clock moves more than this past its last activity.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Oldest completed encounters are dropped beyond this count.</summary>
    public int MaxCompletedEncounters { get; init; } = int.MaxValue;

    /// <summary>Names that are always players (watched characters, the log owner). Survive <see cref="DpsTrackerService.Reset"/>.</summary>
    public IReadOnlyCollection<string>? KnownPlayers { get; init; }

    /// <summary>Clear all encounters when a batch (historical import) begins.</summary>
    public bool ResetOnBeginBatch { get; init; }

    /// <summary>
    /// Verified players and pets. Roster players count as players, pets count as friendly, and (when
    /// <see cref="PlayerRoster.MergePetDamage"/> is on) a pet's damage is credited to its owner. The tracker also feeds it the
    /// /who and /pet leader lines it sees.
    /// </summary>
    public PlayerRoster? Roster { get; init; }
}

/// <summary>
/// Log-clock encounter state machine. Every start, end, duration and timeout comes from <see cref="CombatEvent.Timestamp"/>
/// (or the timestamp passed to <see cref="OnLogClock"/>); wall time is never consulted, so a live tail and a bulk import
/// of the same lines produce identical encounters.
/// <para>
/// <b>Encounters.</b> One active encounter per target name. A player damaging or missing an NPC opens or extends that
/// target's encounter. A target death closes its encounter at the death time. Any event or clock tick more than
/// <see cref="DpsTrackerOptions.InactivityTimeout"/> past an encounter's last activity closes it at that last activity.
/// </para>
/// <para>
/// <b>Players vs NPCs.</b> Names are classified as Player, Npc or Unknown. Seeded players (the log owner, watched characters)
/// are Players. Multi-word names, lowercase-initial names and names with non-letters are always Npcs. A single capitalized
/// word is Unknown until observed: a Player attacking an Unknown (or an Unknown attacking a Player) makes the Unknown an Npc;
/// an Unknown attacking a known Npc becomes a Player. Only Player-to-Npc events count as damage dealt, so a boss like
/// "Tormax" is tracked as a target and its hits on players are ignored. Two Unknowns fighting each other are ignored until one
/// is resolved. Same-named mobs share an encounter. A pet is only known once <c>/pet leader</c> has been used on it (see
/// <see cref="PlayerRoster"/>); from then on its damage is credited to the owner.
/// </para>
/// All mutation happens under a lock; readers use <see cref="Snapshot"/>, which is lock-free.
/// </summary>
public class DpsTrackerService : IDpsEventSink
{
    private enum NameKind
    {
        Unknown,
        Player,
        Npc,
    }

    private readonly object _gate = new();
    private readonly DpsTrackerOptions _options;
    private readonly PlayerRoster? _roster;
    private readonly Dictionary<string, CombatEncounter> _active = new(StringComparer.Ordinal);
    private readonly List<EncounterSnapshot> _completed = new();
    private readonly HashSet<string> _seedPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedNpcs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _pendingCrits = new(StringComparer.Ordinal);

    /// <summary>A critical announcement applies to the attacker's next damage line only if it follows within this long.</summary>
    public static readonly TimeSpan CritWindow = TimeSpan.FromSeconds(1);
    private const int MaxPendingCrits = 64;
    private bool _keepActiveOnEndBatch;

    private IReadOnlyList<EncounterSnapshot> _completedView = Array.AsReadOnly(Array.Empty<EncounterSnapshot>());
    private bool _completedDirty;
    private DpsSnapshot _snapshot = DpsSnapshot.Empty;
    private long _version;
    private int _nextId = 1;
    private int _batchDepth;
    private DateTime _logClock;
    private TimeSpan _inactivityTimeout;

    public DpsTrackerService(DpsTrackerOptions? options = null)
    {
        _options = options ?? new DpsTrackerOptions();
        if (_options.InactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "InactivityTimeout must be positive.");
        }

        _inactivityTimeout = _options.InactivityTimeout;
        _roster = _options.Roster;

        if (_options.KnownPlayers is not null)
        {
            foreach (string name in _options.KnownPlayers)
            {
                _seedPlayers.Add(name);
                _confirmedPlayers.Add(name);
            }
        }
    }

    /// <summary>Latest published state. Safe to read from any thread without locking.</summary>
    public DpsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// How long an encounter may be idle (log clock) before it ends. Can be changed at any time; it applies to events
    /// processed afterwards and does not re-split encounters that are already complete.
    /// </summary>
    public TimeSpan InactivityTimeout
    {
        get
        {
            lock (_gate)
            {
                return _inactivityTimeout;
            }
        }
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "InactivityTimeout must be positive.");
            }

            lock (_gate)
            {
                _inactivityTimeout = value;
            }
        }
    }

    /// <summary>Marks a name as a player permanently (it survives <see cref="Reset"/> and new batches).</summary>
    public void AddKnownPlayer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            _seedPlayers.Add(name);
            ConfirmPlayerLocked(name);
        }
    }

    /// <summary>Marks the owner of the log being delivered as a player for the current batch / until the next reset.</summary>
    public void OnLogOwner(string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return;
        }

        lock (_gate)
        {
            ConfirmPlayerLocked(characterName);
        }
    }

    /// <summary>
    /// When true, <see cref="EndBatch"/> leaves encounters that are still in progress open instead of closing them. Used while a
    /// live tracker is back-filled from a log at start-up, so a fight that is happening right now keeps running and the
    /// tailed lines that follow continue it.
    /// </summary>
    public bool KeepActiveEncountersOnEndBatch
    {
        get
        {
            lock (_gate)
            {
                return _keepActiveOnEndBatch;
            }
        }
        set
        {
            lock (_gate)
            {
                _keepActiveOnEndBatch = value;
            }
        }
    }

    public void BeginBatch()
    {
        lock (_gate)
        {
            if (_batchDepth == 0 && _options.ResetOnBeginBatch)
            {
                ResetLocked();
            }

            _batchDepth++;
        }
    }

    public void EndBatch()
    {
        lock (_gate)
        {
            if (_batchDepth == 0)
            {
                return;
            }

            if (--_batchDepth == 0)
            {
                if (!_keepActiveOnEndBatch)
                {
                    CloseAllActiveLocked();
                }

                Publish();
            }
        }
    }

    public void OnCombatEvent(in CombatEvent combatEvent)
    {
        lock (_gate)
        {
            bool changed = AdvanceClock(combatEvent.Timestamp);

            switch (combatEvent.Kind)
            {
                case CombatEventKind.CriticalHit:
                    RememberCrit(combatEvent);
                    break;

                case CombatEventKind.Damage:
                    // The older announcement line is consumed even if the hit is not counted (e.g. an NPC's crit); the current client
                    // instead tags the damage line itself: "... points of damage. (Critical)".
                    bool isCritical = ConsumeCrit(combatEvent) || combatEvent.IsCritical;
                    if (IsAttack(combatEvent))
                    {
                        GetOrStart(combatEvent.Target, combatEvent.Timestamp)
                            .AddDamage(combatEvent.Timestamp, Attacker(combatEvent.Source), combatEvent.Amount, isCritical);
                        changed = true;
                    }

                    break;

                case CombatEventKind.Miss when IsAttack(combatEvent):
                    GetOrStart(combatEvent.Target, combatEvent.Timestamp)
                        .AddMiss(combatEvent.Timestamp, Attacker(combatEvent.Source));
                    changed = true;
                    break;

                case CombatEventKind.Death:
                    if (_active.Remove(combatEvent.Target, out CombatEncounter? dead))
                    {
                        CloseEncounter(dead, combatEvent.Timestamp);
                        changed = true;
                    }

                    break;
            }

            if (changed)
            {
                Publish();
            }
        }
    }

    /// <summary>Lets the roster learn from /who and /pet leader lines, including while a log is imported or caught up.</summary>
    public void OnNonCombatMessage(ReadOnlySpan<char> message) => _roster?.Observe(message);

    public void OnLogClock(DateTime logTimestamp)
    {
        lock (_gate)
        {
            if (AdvanceClock(logTimestamp))
            {
                Publish();
            }
        }
    }

    /// <summary>Discards all encounters and learned names (e.g. a "Clear" button); seeded players are kept.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            ResetLocked();
        }
    }

    /// <summary>Remembers "X scores a critical hit/blast" for the attacker's next damage line.</summary>
    private void RememberCrit(in CombatEvent announcement)
    {
        DateTime announcedAt = announcement.Timestamp;
        string attacker = announcement.Source;
        if (_pendingCrits.Count >= MaxPendingCrits)
        {
            foreach (string stale in _pendingCrits.Where(p => announcedAt - p.Value > CritWindow).Select(p => p.Key).ToList())
            {
                _pendingCrits.Remove(stale);
            }

            if (_pendingCrits.Count >= MaxPendingCrits)
            {
                _pendingCrits.Clear();
            }
        }

        _pendingCrits[attacker] = announcedAt;
    }

    /// <summary>
    /// True when this melee or non-melee hit follows a critical announcement by the same attacker on the same log second
    /// (or within <see cref="CritWindow"/>). The announcement is consumed either way, so it never leaks onto a later hit.
    /// Damage-over-time ticks neither use nor consume announcements.
    /// </summary>
    private bool ConsumeCrit(in CombatEvent damage)
    {
        if (damage.DamageKind is not (DamageKind.Melee or DamageKind.NonMelee)
            || !_pendingCrits.Remove(damage.Source, out DateTime announcedAt))
        {
            return false;
        }

        TimeSpan gap = damage.Timestamp - announcedAt;
        return gap >= -CritWindow && gap <= CritWindow;
    }

    private void ConfirmPlayerLocked(string name)
    {
        _confirmedPlayers.Add(name);
        _confirmedNpcs.Remove(name);
    }

    /// <summary>The row a hit is credited to: the owner when the attacker is a known pet and merging is on.</summary>
    private string Attacker(string source) =>
        _roster is { MergePetDamage: true } roster && roster.TryGetOwner(source, out string? owner) ? owner : source;

    private NameKind Classify(string name)
    {
        if (_roster is not null && (_roster.IsVerifiedPlayer(name) || _roster.IsPet(name)))
        {
            return NameKind.Player;
        }

        if (_confirmedPlayers.Contains(name))
        {
            return NameKind.Player;
        }

        if (_confirmedNpcs.Contains(name))
        {
            return NameKind.Npc;
        }

        return PlayerNameHeuristic.CouldBePlayer(name) ? NameKind.Unknown : NameKind.Npc;
    }

    /// <summary>Learns from the pair, then reports whether this is a player dealing (or attempting) damage to an NPC.</summary>
    private bool IsAttack(in CombatEvent e)
    {
        NameKind source = Classify(e.Source);
        NameKind target = Classify(e.Target);

        if (source == NameKind.Player && target == NameKind.Unknown)
        {
            _confirmedNpcs.Add(e.Target);
            target = NameKind.Npc;
        }
        else if (source == NameKind.Unknown && target == NameKind.Player)
        {
            _confirmedNpcs.Add(e.Source);
            source = NameKind.Npc;
        }
        else if (source == NameKind.Unknown && target == NameKind.Npc)
        {
            _confirmedPlayers.Add(e.Source);
            source = NameKind.Player;
        }

        return source == NameKind.Player && target == NameKind.Npc;
    }

    private CombatEncounter GetOrStart(string target, DateTime timestamp)
    {
        if (!_active.TryGetValue(target, out CombatEncounter? encounter))
        {
            encounter = new CombatEncounter(_nextId++, target, timestamp);
            _active.Add(target, encounter);
        }

        return encounter;
    }

    /// <summary>Moves the log clock forward and closes encounters idle for longer than the timeout.</summary>
    private bool AdvanceClock(DateTime timestamp)
    {
        if (timestamp > _logClock)
        {
            _logClock = timestamp;
        }

        if (_active.Count == 0)
        {
            return false;
        }

        List<CombatEncounter>? expired = null;
        foreach (CombatEncounter encounter in _active.Values)
        {
            if (_logClock - encounter.EndTime > _inactivityTimeout)
            {
                (expired ??= new List<CombatEncounter>()).Add(encounter);
            }
        }

        if (expired is null)
        {
            return false;
        }

        foreach (CombatEncounter encounter in expired.OrderBy(e => e.EndTime).ThenBy(e => e.Id))
        {
            _active.Remove(encounter.TargetName);
            CloseEncounter(encounter, encounter.EndTime);
        }

        return true;
    }

    private void CloseAllActiveLocked()
    {
        foreach (CombatEncounter encounter in _active.Values.OrderBy(e => e.EndTime).ThenBy(e => e.Id).ToList())
        {
            CloseEncounter(encounter, encounter.EndTime);
        }

        _active.Clear();
    }

    private void CloseEncounter(CombatEncounter encounter, DateTime endTime)
    {
        encounter.Close(endTime);
        _completed.Add(EncounterSnapshot.From(encounter));
        while (_completed.Count > _options.MaxCompletedEncounters)
        {
            _completed.RemoveAt(0);
        }

        _completedDirty = true;
    }

    private void ResetLocked()
    {
        _active.Clear();
        _completed.Clear();
        _completedDirty = true;
        _logClock = default;
        _nextId = 1;

        _pendingCrits.Clear();
        _confirmedNpcs.Clear();
        _confirmedPlayers.Clear();
        foreach (string name in _seedPlayers)
        {
            _confirmedPlayers.Add(name);
        }

        Publish();
    }

    /// <summary>Builds and swaps in a new immutable snapshot. Suppressed while a batch is running.</summary>
    private void Publish()
    {
        if (_batchDepth > 0)
        {
            return;
        }

        if (_completedDirty)
        {
            _completedView = Array.AsReadOnly(_completed.ToArray());
            _completedDirty = false;
        }

        var active = new EncounterSnapshot[_active.Count];
        int i = 0;
        foreach (CombatEncounter encounter in _active.Values)
        {
            active[i++] = EncounterSnapshot.From(encounter);
        }

        Array.Sort(active, static (a, b) => a.Id.CompareTo(b.Id));

        Volatile.Write(ref _snapshot, new DpsSnapshot(++_version, _completedView, Array.AsReadOnly(active)));
    }
}

/// <summary>Tracker fed by live tailing. Bounded history; never reset by imports.</summary>
public sealed class LiveDpsTracker : DpsTrackerService
{
    public const int DefaultMaxCompletedEncounters = 500;

    public LiveDpsTracker(
        TimeSpan? inactivityTimeout = null,
        int maxCompletedEncounters = DefaultMaxCompletedEncounters,
        PlayerRoster? roster = null)
        : base(new DpsTrackerOptions
        {
            InactivityTimeout = inactivityTimeout ?? TimeSpan.FromSeconds(8),
            MaxCompletedEncounters = maxCompletedEncounters,
            Roster = roster,
        })
    {
    }
}

/// <summary>Tracker fed by the historical importer. Unbounded history; each import replaces the previous result.</summary>
public sealed class HistoricalDpsTracker : DpsTrackerService
{
    public HistoricalDpsTracker(TimeSpan? inactivityTimeout = null, PlayerRoster? roster = null)
        : base(new DpsTrackerOptions
        {
            InactivityTimeout = inactivityTimeout ?? TimeSpan.FromSeconds(8),
            ResetOnBeginBatch = true,
            Roster = roster,
        })
    {
    }
}
