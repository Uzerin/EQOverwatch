using System.Globalization;
using System.Text.RegularExpressions;
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
public partial class DpsTrackerService : IDpsEventSink
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
    private readonly Dictionary<string, CombatEncounter> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EncounterSnapshot> _completed = new();
    private readonly HashSet<string> _seedPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmedPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _confirmedNpcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingCrits = new(StringComparer.Ordinal);

    /// <summary>A critical announcement applies to the attacker's next damage line only if it follows within this long.</summary>
    public static readonly TimeSpan CritWindow = TimeSpan.FromSeconds(1);
    private const int MaxPendingCrits = 64;

    /// <summary>Casts by a mob that no one has hit yet are kept this long, so a boss that casts before the pull still gets them.</summary>
    private static readonly TimeSpan PendingCastWindow = TimeSpan.FromSeconds(20);
    private const int MaxPendingCastNames = 32;
    private readonly Dictionary<string, List<BossCastSnapshot>> _recentCasts = new(StringComparer.OrdinalIgnoreCase);
    private string _logOwner = string.Empty;
    private readonly List<PlayerCastSnapshot> _recentPlayerCasts = new();
    private static readonly TimeSpan PlayerCastLead = TimeSpan.FromSeconds(10);
    private const int MaxRecentPlayerCasts = 300;
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
            _logOwner = characterName;
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
                    else if (_active.TryGetValue(FoeName(combatEvent.Source), out CombatEncounter? foe)
                             && Classify(combatEvent.Target) != NameKind.Npc)
                    {
                        // The fight's target hurt a player or pet: that is damage taken in this fight.
                        foe.AddDamageTaken(combatEvent.Target, combatEvent.Amount);
                        changed = true;
                    }

                    break;

                case CombatEventKind.Miss when IsAttack(combatEvent):
                    GetOrStart(combatEvent.Target, combatEvent.Timestamp)
                        .AddMiss(combatEvent.Timestamp, Attacker(combatEvent.Source));
                    changed = true;
                    break;

                case CombatEventKind.Death:
                    // The slain line capitalises the first letter ("A Praetorian Pius"), the damage lines do not ("a Praetorian Pius").
                    if (ActiveKey(combatEvent.Target) is { } deadKey && _active.Remove(deadKey, out CombatEncounter? dead))
                    {
                        dead.Killed = true;
                        CloseEncounter(dead, combatEvent.Timestamp);
                        changed = true;
                    }
                    else if (Classify(combatEvent.Target) != NameKind.Npc)
                    {
                        changed |= RecordPlayerDeath(combatEvent);
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
    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        _roster?.Observe(message);

        // Boss casts and heals are not combat events, so they arrive here with the log clock set by the line's own timestamp.
        if (message.StartsWith("You begin casting ", StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
        {
            ObserveCast(message.ToString());
        }
        else if (message.Equals("Your spell fizzles!", StringComparison.Ordinal) || message.Equals("Your spell is interrupted.", StringComparison.Ordinal))
        {
            ObserveOwnCastFailed();
        }
        else if (message.IndexOf(" begins casting ", StringComparison.Ordinal) > 0
            || message.IndexOf(" begins to cast a spell. <", StringComparison.Ordinal) > 0)
        {
            ObserveCast(message.ToString());
        }
        else if (message.IndexOf(" healed ", StringComparison.Ordinal) > 0 && message.IndexOf(" hit point", StringComparison.Ordinal) > 0)
        {
            ObserveHeal(message.ToString());
        }
    }

    /// <summary>The key of the active fight against <paramref name="name"/>, ignoring capitalisation; null when there is none.</summary>
    private string? ActiveKey(string name)
    {
        if (_active.ContainsKey(name))
        {
            return name;
        }

        foreach (string key in _active.Keys)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>"Lord Inquisitor Seru's corpse" is still the boss: damage-over-time keeps ticking from it after it dies.</summary>
    private static string FoeName(string name) =>
        name.EndsWith("'s corpse", StringComparison.Ordinal) ? name[..^"'s corpse".Length] : name;

    private void ObserveCast(string message)
    {
        string caster;
        string spell;
        int newForm = message.IndexOf(" begins casting ", StringComparison.Ordinal);
        if (message.StartsWith("You begin casting ", StringComparison.Ordinal) && message.EndsWith('.'))
        {
            caster = "You";
            spell = message["You begin casting ".Length..^1];
        }
        else if (newForm > 0 && message.EndsWith('.'))
        {
            caster = message[..newForm];
            spell = message[(newForm + " begins casting ".Length)..^1];
        }
        else
        {
            const string oldMarker = " begins to cast a spell. <";
            int oldForm = message.IndexOf(oldMarker, StringComparison.Ordinal);
            if (oldForm <= 0 || !message.EndsWith('>'))
            {
                return;
            }

            caster = message[..oldForm];
            spell = message[(oldForm + oldMarker.Length)..^1];
        }

        if (caster.Length == 0 || spell.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (caster.Equals("You", StringComparison.OrdinalIgnoreCase))
            {
                if (_logOwner.Length == 0)
                {
                    return;
                }

                RecordPlayerCast(_logOwner, spell);
                return;
            }

            if (_active.TryGetValue(caster, out CombatEncounter? fight))
            {
                fight.AddCast(_logClock, spell);
                Publish();
                return;
            }

            if (Classify(caster) != NameKind.Npc)
            {
                // A player or pet (or a name not yet known either way): a spell cast in the raid.
                RecordPlayerCast(caster, spell);
                return;
            }

            if (!_recentCasts.TryGetValue(caster, out List<BossCastSnapshot>? list))
            {
                if (_recentCasts.Count >= MaxPendingCastNames)
                {
                    _recentCasts.Clear();
                }

                _recentCasts[caster] = list = new List<BossCastSnapshot>();
            }

            list.RemoveAll(c => _logClock - c.Time > PendingCastWindow);
            list.Add(new BossCastSnapshot(_logClock, spell));
        }
    }

    /// <summary>Files a player's cast under every fight in progress, and keeps it briefly for a fight that starts just after.</summary>
    private void RecordPlayerCast(string caster, string spell)
    {
        var cast = new PlayerCastSnapshot(_logClock, caster, spell);
        _recentPlayerCasts.RemoveAll(c => _logClock - c.Time > PlayerCastLead);
        if (_recentPlayerCasts.Count >= MaxRecentPlayerCasts)
        {
            _recentPlayerCasts.RemoveAt(0);
        }

        _recentPlayerCasts.Add(cast);

        if (_active.Count == 0)
        {
            return;
        }

        foreach (CombatEncounter encounter in _active.Values)
        {
            encounter.AddPlayerCast(cast);
        }

        Publish();
    }

    /// <summary>"Your spell fizzles!" / "Your spell is interrupted.": the log owner's latest cast did not go off.</summary>
    private void ObserveOwnCastFailed()
    {
        lock (_gate)
        {
            if (_logOwner.Length == 0)
            {
                return;
            }

            DateTime notBefore = _logClock - TimeSpan.FromSeconds(15);
            int i = _recentPlayerCasts.FindLastIndex(c => c.Caster.Equals(_logOwner, StringComparison.OrdinalIgnoreCase) && !c.Failed);
            if (i >= 0)
            {
                _recentPlayerCasts[i] = _recentPlayerCasts[i] with { Failed = true };
            }

            foreach (CombatEncounter encounter in _active.Values)
            {
                encounter.FailLastCast(_logOwner, notBefore);
            }

            if (_active.Count > 0)
            {
                Publish();
            }
        }
    }

    private void ObserveHeal(string message)
    {
        Match match = HealRegex().Match(message);
        if (!match.Success
            || !int.TryParse(match.Groups["actual"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int actual))
        {
            return;
        }

        int possible = actual;
        if (match.Groups["possible"].Success)
        {
            int.TryParse(match.Groups["possible"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out possible);
        }

        string healer = match.Groups["healer"].Value;
        lock (_gate)
        {
            if (healer.Equals("You", StringComparison.OrdinalIgnoreCase))
            {
                if (_logOwner.Length == 0)
                {
                    return;
                }

                healer = _logOwner;
            }

            if (_active.Count == 0 || Classify(healer) == NameKind.Npc)
            {
                return;
            }

            foreach (CombatEncounter encounter in _active.Values)
            {
                encounter.AddHeal(healer, match.Groups["spell"].Value, actual, possible);
            }

            Publish();
        }
    }

    // "Leinhart healed Regnier for 328 (8415) hit points by Complete Heal."  /  "X healed you over time for 32 hit points by Spell."
    [GeneratedRegex(
        @"^(?<healer>.+?) healed (?<target>.+?) (?:over time )?for (?<actual>\d+)(?: \((?<possible>\d+)\))? hit points? by (?<spell>.+)\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HealRegex();

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

    /// <summary>
    /// A player or pet died. It counts in the fight of the NPC that killed it; when the killer is not in a fight, in every running
    /// fight the victim took part in.
    /// </summary>
    private bool RecordPlayerDeath(in CombatEvent death)
    {
        if (!string.IsNullOrEmpty(death.Source) && _active.TryGetValue(FoeName(death.Source), out CombatEncounter? killerFight))
        {
            killerFight.AddDeath(death.Target, death.Timestamp, death.Source ?? string.Empty);
            return true;
        }

        bool recorded = false;
        foreach (CombatEncounter encounter in _active.Values)
        {
            if (encounter.Involves(death.Target))
            {
                encounter.AddDeath(death.Target, death.Timestamp, death.Source ?? string.Empty);
                recorded = true;
            }
        }

        return recorded;
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

    /// <summary>
    /// Lines that start a sentence capitalise the mob's article ("A Praetorian Pius has taken 100 damage"), others do not
    /// ("You slash a Praetorian Pius"). Fights are kept under the lower-case form so both spellings are one fight.
    /// </summary>
    private static string LowerArticle(string name)
    {
        foreach (string article in new[] { "A ", "An ", "The " })
        {
            if (name.StartsWith(article, StringComparison.Ordinal))
            {
                return char.ToLowerInvariant(name[0]) + name[1..];
            }
        }

        return name;
    }

    private CombatEncounter GetOrStart(string target, DateTime timestamp)
    {
        if (!_active.TryGetValue(target, out CombatEncounter? encounter))
        {
            encounter = new CombatEncounter(_nextId++, LowerArticle(target), timestamp);
            _active.Add(target, encounter);

            foreach (PlayerCastSnapshot early in _recentPlayerCasts.Where(c => timestamp - c.Time <= PlayerCastLead))
            {
                encounter.AddPlayerCast(early);
            }

            if (_recentCasts.Remove(target, out List<BossCastSnapshot>? earlier))
            {
                foreach (BossCastSnapshot cast in earlier.Where(c => timestamp - c.Time <= PendingCastWindow))
                {
                    encounter.AddCast(cast.Time, cast.Spell);
                }
            }

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
        _recentCasts.Clear();
        _recentPlayerCasts.Clear();
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
