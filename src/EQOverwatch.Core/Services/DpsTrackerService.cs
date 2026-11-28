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

    /// <summary>Targets whose fights are always kept (the raid bosses); they do not count toward the completed-encounter limit.</summary>
    public Func<string, bool>? KeepAlways { get; set; }
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
    private readonly Dictionary<string, string> _ownerZones = new(StringComparer.OrdinalIgnoreCase);
    private readonly RepeatFilter _repeats = new();
    private string _server = string.Empty;
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
    private long _lastCombatTicks;

    /// <summary>Wall-clock source for <see cref="LastCombatUtc"/> (replaceable in tests).</summary>
    public Func<DateTime> WallClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>When the last live combat line of any kind (hit, miss, death) arrived, or null if none has.</summary>
    public DateTime? LastCombatUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastCombatTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }
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

    /// <summary>The server of the log whose line comes next: players, pets and classes are looked up on it.</summary>
    public void OnLogServer(string serverName)
    {
        lock (_gate)
        {
            _server = serverName ?? string.Empty;
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
            if (string.Equals(_logOwner, characterName, StringComparison.Ordinal))
            {
                return; // called for every line of every log: nothing to do while the same log goes on
            }

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

    /// <summary>
    /// A mob in a fight in progress swung at a player (hit or miss, not a rampage, not a spell): the player and the mob. Live lines only
    /// (not while a log is read in). Raised outside the tracker's lock.
    /// </summary>
    public event Action<string, string>? PlayerAttacked;

    /// <summary>
    /// A log's own character could not reach its target during a fight ("Your target is too far away, get closer!"): the character and
    /// whether it was out of sight ("You cannot see your target.") rather than out of range. Live lines only.
    /// </summary>
    public event Action<string, bool>? TargetOutOfReach;

    /// <summary>
    /// A notice line in a log (a follow that broke, a player gone linkdead, a /random roll, a pet buff fading, a level): the log's own
    /// character and the line. Live lines only.
    /// </summary>
    public event Action<string, EQOverwatch.Core.Parsing.NoticeLine>? Notice;

    /// <summary>A log's own character used /consider on a mob that showed its level: the character, the mob and its level. Live lines only.</summary>
    public event Action<string, string, int>? MobConsidered;

    public void OnCombatEvent(in CombatEvent combatEvent)
    {
        (string Player, string Mob)? attacked = null;
        EncounterSnapshot? killed = null;
        try
        {
            OnCombatEventLocked(in combatEvent, ref attacked, ref killed);
        }
        finally
        {
            if (attacked is { } a)
            {
                PlayerAttacked?.Invoke(a.Player, a.Mob);
            }

            if (killed is not null)
            {
                EncounterKilled?.Invoke(killed);
            }
        }
    }

    private void OnCombatEventLocked(in CombatEvent combatEvent, ref (string Player, string Mob)? attacked, ref EncounterSnapshot? killed)
    {
        lock (_gate)
        {
            if (_batchDepth == 0)
            {
                Interlocked.Exchange(ref _lastCombatTicks, WallClock().Ticks); // live lines only: a history import must not look like a fight in progress
            }

            bool changed = AdvanceClock(combatEvent.Timestamp);

            // Every character's log is read: a hit, miss or death another log already gave is counted once.
            if (_repeats.IsRepeat(CombatKey(combatEvent), combatEvent.Timestamp, _logOwner))
            {
                if (changed)
                {
                    Publish();
                }

                return;
            }

            switch (combatEvent.Kind)
            {
                case CombatEventKind.CriticalHit:
                    RememberCrit(combatEvent);
                    break;

                case CombatEventKind.Damage when combatEvent.Source == Parsing.CombatLineParser.UnknownDotSource:
                    changed |= OnUnknownDot(combatEvent);
                    break;

                case CombatEventKind.Damage:
                    // The older announcement line is consumed even if the hit is not counted (e.g. an NPC's crit); the current client
                    // instead tags the damage line itself: "... points of damage. (Critical)".
                    bool isCritical = ConsumeCrit(combatEvent) || combatEvent.IsCritical;
                    bool isAttack = IsAttack(combatEvent);
                    if (!isAttack && Classify(combatEvent.Target) != NameKind.Npc && Classify(FoeName(combatEvent.Source)) == NameKind.Npc)
                    {
                        RememberHitOnPlayer(combatEvent);
                    }

                    if (isAttack)
                    {
                        GetOrStart(combatEvent.Target, combatEvent.Timestamp)
                            .AddDamage(combatEvent.Timestamp, Attacker(combatEvent.Source), combatEvent.Amount, isCritical,
                                combatEvent.DamageKind != DamageKind.DamageShield && _roster is not null && _roster.IsPet(combatEvent.Source, _server)
                                    ? AttackType.Pet
                                    : AttackTypes.Classify(combatEvent.DamageKind, combatEvent.Detail),
                                MergedPet(combatEvent.Source));
                        changed = true;
                    }
                    else if (_active.TryGetValue(FoeName(combatEvent.Source), out CombatEncounter? foe)
                             && Classify(combatEvent.Target) != NameKind.Npc)
                    {
                        // The fight's target hurt a player or pet: that is damage taken in this fight.
                        foe.AddWitness(_logOwner);
                        if (combatEvent.DamageKind == DamageKind.Melee && !combatEvent.IsRampage && _batchDepth == 0)
                        {
                            attacked = (combatEvent.Target, foe.TargetName);
                        }

                        if (combatEvent.DamageKind == DamageKind.Melee && !combatEvent.IsRampage)
                        {
                            foe.AddTankSwing(combatEvent.Timestamp, combatEvent.Target);
                        }

                        foe.AddRecent(combatEvent.Target, new RecentEventSnapshot(combatEvent.Timestamp, foe.TargetName, RecentLabel(combatEvent),
                            combatEvent.Amount, false));
                        if (combatEvent.DamageKind == DamageKind.DamageShield)
                        {
                            foe.AddShieldTaken(combatEvent.Timestamp, combatEvent.Target, combatEvent.Amount);
                        }
                        else
                        {
                            foe.AddDamageTaken(combatEvent.Target, combatEvent.Amount, combatEvent.IsRampage, timestamp: combatEvent.Timestamp,
                                isMelee: combatEvent.DamageKind == DamageKind.Melee);
                        }

                        changed = true;
                    }

                    break;

                case CombatEventKind.Miss when IsAttack(combatEvent):
                    GetOrStart(combatEvent.Target, combatEvent.Timestamp)
                        .AddMiss(combatEvent.Timestamp, Attacker(combatEvent.Source), MergedPet(combatEvent.Source));
                    changed = true;
                    break;

                case CombatEventKind.Miss when _active.TryGetValue(FoeName(combatEvent.Source), out CombatEncounter? missingFoe)
                                               && Classify(combatEvent.Target) != NameKind.Npc:
                    // The fight's target swung at a player or pet and missed: the player avoided the attack.
                    missingFoe.AddWitness(_logOwner);
                    missingFoe.AddAvoided(combatEvent.Target, combatEvent.Avoid, combatEvent.IsRampage);
                    if (!combatEvent.IsRampage)
                    {
                        missingFoe.AddTankSwing(combatEvent.Timestamp, combatEvent.Target);
                    }

                    if (!combatEvent.IsRampage && _batchDepth == 0)
                    {
                        attacked = (combatEvent.Target, missingFoe.TargetName);
                    }

                    changed = true;
                    break;

                case CombatEventKind.Death:
                    // The slain line capitalises the first letter ("A Praetorian Pius"), the damage lines do not ("a Praetorian Pius").
                    if (ActiveKey(combatEvent.Target) is { } deadKey && _active.Remove(deadKey, out CombatEncounter? dead))
                    {
                        dead.Killed = true;
                        CloseEncounter(dead, combatEvent.Timestamp);
                        if (_batchDepth == 0)
                        {
                            killed = _completed[^1]; // live lines only: a history import never sends a kill out
                        }

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

    /// <summary>
    /// What makes two logs' lines the same line. The wording is left out ("You shoot" and "Myriad shoots" are one shot), and so is the way
    /// a miss was avoided, which reads differently from the player's own side.
    /// </summary>
    private static string CombatKey(in CombatEvent e) =>
        string.Create(CultureInfo.InvariantCulture, $"c{(int)e.Kind}|{e.Source}|{e.Target}|{e.Amount}|{(int)e.DamageKind}");

    /// <summary>
    /// Called with the mob's name, its level, and the zone the log last entered (null when not seen yet) for every /consider line that
    /// shows a level, while a log is tailed, imported or caught up.
    /// </summary>
    public Action<string, int, string?>? MobLevelSeen { get; set; }

    /// <summary>Called with the boss's name for every server kill announcement, while a log is tailed, imported or caught up.</summary>
    public Action<string>? RaidBossAnnounced { get; set; }

    /// <summary>Called (outside the lock) with each fight that ended in a kill, as soon as its death line is read.</summary>
    public Action<EncounterSnapshot>? EncounterKilled { get; set; }

    /// <summary>Lets the roster learn from /who and /pet leader lines, including while a log is imported or caught up.</summary>
    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        string server;
        lock (_gate)
        {
            server = _server;
        }

        _roster?.Observe(message, server);

        // Each log's zone, so a mob's level range is kept per zone.
        if (message.StartsWith("You have entered ", StringComparison.Ordinal) && EQOverwatch.Core.Maps.ZoneTracker.ParseZone(message) is { } entered)
        {
            lock (_gate)
            {
                if (_logOwner.Length > 0)
                {
                    _ownerZones[_logOwner] = entered;
                }
            }
        }

        // "Lord Inquisitor Seru has been defeated by a group of hardy adventurers! ..." names a raid boss (seen live and in old logs).
        if (RaidBossAnnounced is not null && EQOverwatch.Core.Discord.RaidBossAnnouncementParser.TryParse(message, out string announced))
        {
            RaidBossAnnounced(announced);
        }

        // /consider lines that show a level ("... (Lvl: 62)") feed the shared mob level ranges.
        Action<string, int, string?>? levelSeen = MobLevelSeen;
        if ((levelSeen is not null || MobConsidered is not null)
            && EQOverwatch.Core.Parsing.ConLineParser.TryParse(message, out string conMob, out int conLevel))
        {
            string? zone;
            lock (_gate)
            {
                zone = _ownerZones.TryGetValue(_logOwner, out string? z) ? z : null;
            }

            levelSeen?.Invoke(conMob, conLevel, zone);
            if (LiveOwner() is { } consider)
            {
                MobConsidered?.Invoke(consider, conMob, conLevel);
            }
        }

        // A box that cannot reach its target during a fight (a ranger past bow range, or out of sight).
        bool tooFar = message.SequenceEqual("Your target is too far away, get closer!") || message.SequenceEqual("Your target is out of range, get closer!")
            || message.SequenceEqual("You can't hit them from here.");
        bool unseen = !tooFar && message.SequenceEqual("You cannot see your target.");
        if ((tooFar || unseen) && TargetOutOfReach is not null && LiveOwner(inFight: true) is { } reacher)
        {
            TargetOutOfReach(reacher, unseen);
        }

        EQOverwatch.Core.Parsing.NoticeLine notice = message.Length > 0 && (Notice is not null || message[0] == 'Y')
            ? EQOverwatch.Core.Parsing.NoticeLineParser.Parse(message)
            : default;
        if (notice.Kind is EQOverwatch.Core.Parsing.NoticeKind.LostControl or EQOverwatch.Core.Parsing.NoticeKind.ControlBack)
        {
            notice = ObserveControl(notice);
        }

        if (Notice is not null && notice.Kind != EQOverwatch.Core.Parsing.NoticeKind.None && LiveOwner() is { } noticed)
        {
            Notice(noticed, notice);
        }

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

    /// <summary>The latest "X begins casting Y." line: the log it came from, its log time, the caster and the spell.</summary>
    private (string Owner, DateTime Time, string Caster, string Spell) _lastCastLine = (string.Empty, default, string.Empty, string.Empty);

    /// <summary>
    /// "You lose control of yourself!" / "You have control of yourself again.": files the stretch under every fight in progress, with the
    /// spell and caster of a cast in the same second. Returns the notice with the spell (Name) and caster (Other) filled in.
    /// </summary>
    private EQOverwatch.Core.Parsing.NoticeLine ObserveControl(EQOverwatch.Core.Parsing.NoticeLine notice)
    {
        lock (_gate)
        {
            if (_logOwner.Length == 0)
            {
                return notice;
            }

            if (notice.Kind == EQOverwatch.Core.Parsing.NoticeKind.LostControl)
            {
                (string owner, DateTime time, string caster, string spell) = _lastCastLine;
                bool same = owner.Equals(_logOwner, StringComparison.OrdinalIgnoreCase) && Math.Abs((_logClock - time).TotalSeconds) <= 1;
                string spellName = same ? spell : string.Empty;
                string casterName = same ? caster : string.Empty;
                foreach (CombatEncounter fight in _active.Values)
                {
                    fight.AddLostControl(_logOwner, _logClock, spellName, casterName);
                }

                if (_active.Count > 0)
                {
                    Publish();
                }

                return notice with { Name = spellName, Other = casterName };
            }

            bool changed = false;
            foreach (CombatEncounter fight in _active.Values)
            {
                changed |= fight.EndLostControl(_logOwner, _logClock);
            }

            if (changed)
            {
                Publish();
            }

            return notice;
        }
    }

    /// <summary>The log owner of a live line (none while a log is read in); with <paramref name="inFight"/>, only during a fight.</summary>
    private string? LiveOwner(bool inFight = false)
    {
        lock (_gate)
        {
            return _batchDepth == 0 && _logOwner.Length > 0 && (!inFight || _active.Count > 0) ? _logOwner : null;
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
            if (!caster.Equals("You", StringComparison.OrdinalIgnoreCase))
            {
                _lastCastLine = (_logOwner, _logClock, caster, spell);
            }

            string who = caster.Equals("You", StringComparison.OrdinalIgnoreCase) ? _logOwner : caster;
            if (who.Length > 0 && _repeats.IsRepeat("s|" + who + "|" + spell, _logClock, _logOwner))
            {
                return; // the same cast seen in another character's log
            }

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

        // The client tags a critical heal at the end: "... by Complete Heal. (Critical)".
        bool isCritical = match.Groups["tag"] is { Success: true } tag
                          && tag.ValueSpan.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0;
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

            string target = match.Groups["target"].Value;
            bool overTime = match.Groups["over"].Success;
            if (target.Equals("you", StringComparison.OrdinalIgnoreCase) || target.Equals("yourself", StringComparison.OrdinalIgnoreCase))
            {
                target = _logOwner;
            }

            if (_repeats.IsRepeat(string.Create(CultureInfo.InvariantCulture, $"h|{healer}|{target}|{actual}|{match.Groups["spell"].Value}"), _logClock, _logOwner))
            {
                return; // the same heal seen in another character's log
            }

            NoteRecent(target, new RecentEventSnapshot(_logClock, healer, match.Groups["spell"].Value, actual, true));
            foreach (CombatEncounter encounter in _active.Values)
            {
                encounter.AddHeal(healer, match.Groups["spell"].Value, actual, possible, isCritical, _logClock, target, overTime);
            }

            Publish();
        }
    }

    // "Leinhart healed Regnier for 328 (8415) hit points by Complete Heal."  /  "X healed you over time for 32 hit points by Spell."
    [GeneratedRegex(
        @"^(?<healer>.+?) healed (?<target>.+?) (?:(?<over>over time) )?for (?<actual>\d+)(?: \((?<possible>\d+)\))? hit points? by (?<spell>.+?)\.(?: \((?<tag>.+)\))?$",
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
        string blow = KillingBlowFor(death);
        if (!string.IsNullOrEmpty(death.Source) && _active.TryGetValue(FoeName(death.Source), out CombatEncounter? killerFight))
        {
            killerFight.AddDeath(death.Target, death.Timestamp, death.Source ?? string.Empty, blow);
            return true;
        }

        bool recorded = false;
        foreach (CombatEncounter encounter in _active.Values)
        {
            if (encounter.Involves(death.Target))
            {
                encounter.AddDeath(death.Target, death.Timestamp, death.Source ?? string.Empty, blow);
                recorded = true;
            }
        }

        return recorded;
    }

    private readonly record struct HitOnPlayer(DateTime Time, int Amount, string Label);

    /// <summary>The last hit each NPC landed on each player or pet, keyed victim and attacker, for the killing blow on a death.</summary>
    private readonly Dictionary<string, HitOnPlayer> _lastHitsOnPlayers = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxRememberedHits = 2000;

    /// <summary>A killing blow is only shown when the killer's last hit on the victim came at most this long before the death.</summary>
    public static readonly TimeSpan KillingBlowWindow = TimeSpan.FromSeconds(30);

    private static string HitKey(string victim, string attacker) => victim.Trim() + "|" + FoeName(attacker.Trim());

    /// <summary>A hit or heal on a player or pet goes into every running fight's "last seconds", so adds and heals show too.</summary>
    private void NoteRecent(string victim, RecentEventSnapshot e)
    {
        foreach (CombatEncounter encounter in _active.Values)
        {
            encounter.AddRecent(victim, e);
        }
    }

    private static string RecentLabel(in CombatEvent hit)
    {
        string label = KillingBlowLabel(hit.DamageKind, hit.Detail);
        if (hit.IsRampage)
        {
            label = "rampage";
        }

        return hit.IsCritical ? label + " (crit)" : label;
    }

    private void RememberHitOnPlayer(in CombatEvent hit)
    {
        if (_lastHitsOnPlayers.Count >= MaxRememberedHits)
        {
            _lastHitsOnPlayers.Clear();
        }

        _lastHitsOnPlayers[HitKey(hit.Target, hit.Source)] =
            new HitOnPlayer(hit.Timestamp, hit.Amount, KillingBlowLabel(hit.DamageKind, hit.Detail));
    }

    /// <summary>"175 punch": the killer's last hit on the victim, or empty when the log did not show one shortly before the death.</summary>
    private string KillingBlowFor(in CombatEvent death)
    {
        if (string.IsNullOrWhiteSpace(death.Source)
            || !_lastHitsOnPlayers.Remove(HitKey(death.Target, death.Source), out HitOnPlayer hit)
            || death.Timestamp - hit.Time > KillingBlowWindow)
        {
            return string.Empty;
        }

        return hit.Amount.ToString("N0", CultureInfo.InvariantCulture) + " " + hit.Label;
    }

    /// <summary>What kind of hit it was, as a short word: "punch" for "punches", the spell name, or "damage shield".</summary>
    public static string KillingBlowLabel(DamageKind kind, string? detail)
    {
        string text = (detail ?? string.Empty).Trim();
        switch (kind)
        {
            case DamageKind.DamageShield:
                return "damage shield";
            case DamageKind.NonMelee or DamageKind.DamageOverTime:
                // A spell name is capitalised ("Judgment"); "X hits Y for 50 points of non-melee damage" carries only the verb.
                return text.Length > 0 && char.IsUpper(text[0]) ? text : "non-melee";
        }

        if (text.Length == 0)
        {
            return "hit";
        }

        if (text.StartsWith("frenz", StringComparison.OrdinalIgnoreCase))
        {
            return "frenzy";
        }

        // Third-person verbs to the plain form: punches -> punch, slashes -> slash, bites -> bite, hits -> hit.
        foreach (string ending in new[] { "ches", "shes", "sses", "xes", "zes" })
        {
            if (text.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
            {
                return text[..^2];
            }
        }

        return text.EndsWith('s') && text.Length > 2 ? text[..^1] : text;
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
        _roster is { MergePetDamage: true } roster && roster.TryGetOwner(source, _server, out string owner) ? owner : source;

    /// <summary>The pet's own name when its damage is credited to its owner's row; otherwise null.</summary>
    private string? MergedPet(string source) =>
        _roster is { MergePetDamage: true } roster && roster.TryGetOwner(source, _server, out _) ? source : null;

    private NameKind Classify(string name)
    {
        if (_roster is not null && (_roster.IsVerifiedPlayer(name, _server) || _roster.IsPet(name, _server)))
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
    /// <summary>
    /// A DoT tick whose caster is not named: on a mob being fought it counts for an "Unknown (DoTs)" row; on a player it is damage taken in
    /// the fight going on (the newest one when there are several). It never starts a fight.
    /// </summary>
    private bool OnUnknownDot(in CombatEvent e)
    {
        if (_active.TryGetValue(e.Target, out CombatEncounter? fight))
        {
            fight.AddDamage(e.Timestamp, e.Source, e.Amount, e.IsCritical, AttackTypes.Classify(e.DamageKind, e.Detail), null);
            return true;
        }

        if (Classify(e.Target) != NameKind.Npc && _active.Count > 0)
        {
            CombatEncounter newest = _active.Values.MaxBy(f => f.EndTime)!;
            newest.AddRecent(e.Target, new RecentEventSnapshot(e.Timestamp, "Unknown", "DoT", e.Amount, false));
            newest.AddDamageTaken(e.Target, e.Amount, isRampage: false, isDot: true, timestamp: e.Timestamp);
            return true;
        }

        return false;
    }

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
            encounter = new CombatEncounter(_nextId++, LowerArticle(target), timestamp)
            {
                Server = _server,
                Zone = _ownerZones.TryGetValue(_logOwner, out string? zone) ? zone : string.Empty,
            };
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

        encounter.AddWitness(_logOwner);
        return encounter;
    }

    /// <summary>
    /// Moves the log clock forward and closes encounters idle for longer than the timeout. Mobs fought at the same time by the same
    /// people are one combined fight: none of them times out while any of them is still busy (a mezzed add waits for the fight), and
    /// they all end together once the whole fight has been quiet for the timeout. A mob that dies still ends at once.
    /// </summary>
    private bool AdvanceClock(DateTime timestamp)
    {
        if (timestamp > _logClock)
        {
            _logClock = timestamp;
        }

        if (_active.Count == 0 || !_active.Values.Any(e => _logClock - e.EndTime > _inactivityTimeout))
        {
            return false;
        }

        List<CombatEncounter>? expired = null;
        foreach (List<CombatEncounter> group in ActiveGroups())
        {
            DateTime lastActivity = group.Max(e => e.EndTime);
            if (_logClock - lastActivity > _inactivityTimeout)
            {
                (expired ??= new List<CombatEncounter>()).AddRange(group);
            }
        }

        if (expired is null)
        {
            return false;
        }

        foreach (CombatEncounter encounter in expired.OrderBy(e => e.EndTime).ThenBy(e => e.Id))
        {
            if (ActiveKey(encounter.TargetName) is { } key)
            {
                _active.Remove(key);
            }

            CloseEncounter(encounter, encounter.EndTime);
        }

        return true;
    }

    /// <summary>
    /// The fights in progress, grouped: mobs that share someone who hit them or was hit by them, or that the same character's log shows
    /// (a raid fighting a boss while its adds are tanked and mezzed nearby), are one combined fight. A camp no shared player or log sees
    /// is a fight of its own.
    /// </summary>
    private List<List<CombatEncounter>> ActiveGroups()
    {
        List<CombatEncounter> encounters = _active.Values.OrderBy(e => e.Id).ToList();
        if (encounters.Count < 2)
        {
            return encounters.Select(e => new List<CombatEncounter> { e }).ToList();
        }

        int[] parent = Enumerable.Range(0, encounters.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var seenIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < encounters.Count; i++)
        {
            foreach (string person in encounters[i].People)
            {
                if (seenIn.TryGetValue(person, out int other))
                {
                    int a = Find(i), b = Find(other);
                    if (a != b)
                    {
                        parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
                }
                else
                {
                    seenIn[person] = i;
                }
            }
        }

        return Enumerable.Range(0, encounters.Count)
            .GroupBy(Find)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(i => encounters[i]).ToList())
            .ToList();
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
        if (_completed.Count > _options.MaxCompletedEncounters)
        {
            // Fights KeepAlways names (the raid bosses) are kept and do not count toward the limit; the oldest other fights go.
            Func<string, bool>? keep = KeepAlways;
            int others = keep is null ? _completed.Count : _completed.Count(e => !keep(e.TargetName));
            for (int i = 0; i < _completed.Count && others > _options.MaxCompletedEncounters;)
            {
                if (keep is not null && keep(_completed[i].TargetName))
                {
                    i++;
                    continue;
                }

                _completed.RemoveAt(i);
                others--;
            }
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
        _repeats.Clear();
        _lastHitsOnPlayers.Clear();
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

        // A combined fight (several mobs at once) is published as one live fight; each mob gets its own row when it dies or the fight ends.
        var active = new List<EncounterSnapshot>(_active.Count);
        foreach (List<CombatEncounter> group in ActiveGroups())
        {
            active.Add(group.Count == 1
                ? EncounterSnapshot.From(group[0])
                : EncounterSnapshot.Combine(group.Select(EncounterSnapshot.From).ToList()));
        }

        active.Sort(static (a, b) => Math.Abs(a.Id).CompareTo(Math.Abs(b.Id)));

        Volatile.Write(ref _snapshot, new DpsSnapshot(++_version, _completedView, active.AsReadOnly()));
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
