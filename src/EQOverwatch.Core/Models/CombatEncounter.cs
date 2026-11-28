using CommunityToolkit.Mvvm.ComponentModel;

namespace EQOverwatch.Core.Models;

/// <summary>
/// One fight against one target. All times are log-clock times. While active, <see cref="EndTime"/> tracks the
/// most recent activity; <see cref="Close"/> freezes it. State transitions are driven by <c>DpsTrackerService</c> (Phase 2).
/// </summary>
public sealed class CombatEncounter : ObservableObject
{
    private readonly Dictionary<string, ParticipantStats> _byName = new(StringComparer.Ordinal);
    private readonly List<ParticipantStats> _participants = new();
    private readonly Dictionary<string, DamageTakenStats> _takenByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deaths = new(StringComparer.Ordinal);
    private readonly List<DeathSnapshot> _deathLog = new();
    private readonly List<BossCastSnapshot> _casts = new();
    private readonly Dictionary<string, HealStats> _healers = new(StringComparer.Ordinal);
    private readonly List<PlayerCastSnapshot> _playerCasts = new();
    private readonly TimelineRecorder _timeline = new();
    private readonly Dictionary<string, List<RecentEventSnapshot>> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Time, RecentEventSnapshot[] Events)> _lastDeathWindow = new(StringComparer.Ordinal);
    private const int MaxCasts = 2000;

    /// <summary>How far back the "last seconds" of a player go: before their death, or before the end of the fight.</summary>
    public const int RecentSeconds = 20;
    private const int MaxPlayerCasts = 20_000;

    public CombatEncounter(int id, string targetName, DateTime startTime)
    {
        Id = id;
        TargetName = targetName;
        StartTime = startTime;
        EndTime = startTime;
    }

    public int Id { get; }
    public string TargetName { get; }

    /// <summary>The server of the log that started the fight (empty when unknown); classes and pets are looked up there.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>The zone the log that started the fight was in ("" when its log had not entered a zone yet).</summary>
    public string Zone { get; set; } = string.Empty;
    public DateTime StartTime { get; }
    public DateTime EndTime { get; private set; }
    public bool IsActive { get; private set; } = true;
    public long TotalDamage { get; private set; }

    /// <summary>True when the log announced the target's death, so the fight ended in a kill rather than fading out.</summary>
    public bool Killed { get; internal set; }

    public double DurationSeconds => Math.Max(0.0, (EndTime - StartTime).TotalSeconds);

    /// <summary>Per-player rows, in first-seen order.</summary>
    public IReadOnlyList<ParticipantStats> ParticipantStats => _participants;

    /// <summary>Text for encounter pickers, e.g. "a frost giant — 11:45:01 (14s, 802 dmg)".</summary>
    public string DisplayName =>
        $"{TargetName} — {StartTime:HH:mm:ss} ({DurationSeconds:0}s, {TotalDamage:N0} dmg){(IsActive ? " • live" : string.Empty)}";

    internal void AddDamage(DateTime timestamp, string source, int amount, bool isCritical = false, AttackType attack = AttackType.Hit, string? pet = null)
    {
        ParticipantStats row = GetOrAdd(source);
        row.RecordHit(timestamp, amount, isCritical, attack);
        if (pet is not null)
        {
            row.RecordPet(pet, amount, hit: true);
        }

        _timeline.Record(source, (int)(timestamp - StartTime).TotalSeconds, amount);
        TotalDamage += amount;
        Touch(timestamp);
    }

    /// <summary>Damage the fight's target dealt to <paramref name="victim"/> (a player or pet) during the fight.</summary>
    internal void AddDamageTaken(string victim, int amount, bool isRampage = false, bool isDot = false, DateTime timestamp = default, bool isMelee = false)
    {
        if (timestamp != default)
        {
            _timeline.RecordIncoming((int)(timestamp - StartTime).TotalSeconds, amount);
            if (isMelee && !isRampage && !isDot)
            {
                _timeline.RecordTaken(victim, (int)(timestamp - StartTime).TotalSeconds, amount);
            }
        }

        if (!_takenByName.TryGetValue(victim, out DamageTakenStats? stats))
        {
            stats = new DamageTakenStats(victim);
            _takenByName.Add(victim, stats);
        }

        stats.Record(amount);
        if (isRampage)
        {
            stats.RecordRampage(amount);
        }

        if (isDot)
        {
            stats.RecordDot(amount);
        }

        TotalDamageTaken += amount;
    }

    private long _shieldTotal;
    private int _shieldHits;
    private double _shieldFirst = -1;
    private double _shieldLast;

    /// <summary>The fight's target hurt <paramref name="victim"/> with a damage shield (thorns, flames...). Counts as damage taken, not as an attack.</summary>
    internal void AddShieldTaken(DateTime timestamp, string victim, int amount)
    {
        if (!_takenByName.TryGetValue(victim, out DamageTakenStats? stats))
        {
            stats = new DamageTakenStats(victim);
            _takenByName.Add(victim, stats);
        }

        stats.RecordShield(amount);
        TotalDamageTaken += amount;
        _timeline.RecordIncoming((int)(timestamp - StartTime).TotalSeconds, amount);
        _timeline.RecordTaken(victim, (int)(timestamp - StartTime).TotalSeconds, amount);
        _shieldTotal += amount;
        _shieldHits++;
        double offset = Math.Max(0.0, (timestamp - StartTime).TotalSeconds);
        if (_shieldFirst < 0)
        {
            _shieldFirst = offset;
        }

        _shieldLast = Math.Max(_shieldLast, offset);
    }

    /// <summary>The damage shield the fight's target had on, or null when it never hurt anyone with one.</summary>
    internal DamageShieldSummary? ShieldSummary() =>
        _shieldHits == 0 ? null : new DamageShieldSummary(_shieldTotal, _shieldHits, _shieldFirst, _shieldLast);

    /// <summary>The fight's target attacked <paramref name="victim"/> and missed (the victim dodged, parried, blocked...).</summary>
    internal void AddAvoided(string victim, AvoidKind kind = AvoidKind.Miss, bool isRampage = false)
    {
        if (!_takenByName.TryGetValue(victim, out DamageTakenStats? stats))
        {
            stats = new DamageTakenStats(victim);
            _takenByName.Add(victim, stats);
        }

        stats.RecordAvoid(kind);
        if (isRampage)
        {
            stats.RecordRampage(0);
        }

    }

    /// <summary>Damage per second across the fight for the group and the <see cref="DpsTimeline.MaxPlayers"/> top dealers, or null when too short.</summary>
    internal DpsTimeline? BuildTimeline()
    {
        DpsTimeline? timeline = _timeline.Build(
            DurationSeconds,
            _participants.OrderByDescending(p => p.TotalDamage).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name).ToArray());
        if (timeline is null)
        {
            return null;
        }

        double Offset(DateTime time) => Math.Clamp((time - StartTime).TotalSeconds, 0, DurationSeconds);
        return timeline with
        {
            DurationSeconds = DurationSeconds,
            Deaths = new ValueList<TimelineMark>(_deathLog.Where(d => d.Time != default).Select(d => new TimelineMark(Offset(d.Time), d.Name)).ToArray()),
            Casts = new ValueList<TimelineMark>(_casts.Select(c => new TimelineMark(Offset(c.Time), c.Spell)).ToArray()),
        };
    }

    private readonly List<TankSpanSnapshot> _tankSpans = new();

    /// <summary>A stretch longer than this with no melee swing at the same player ends their turn tanking.</summary>
    public const double TankGapSeconds = 6;

    /// <summary>The fight's target swung at <paramref name="victim"/> in melee (not a rampage): who it was tanking, and when.</summary>
    internal void AddTankSwing(DateTime timestamp, string victim)
    {
        double at = Math.Max(0, (timestamp - StartTime).TotalSeconds);
        if (_tankSpans.Count > 0 && _tankSpans[^1] is { } last
            && string.Equals(last.Name, victim, StringComparison.Ordinal) && at - last.End <= TankGapSeconds)
        {
            _tankSpans[^1] = last with { End = Math.Max(last.End, at), Swings = last.Swings + 1 };
        }
        else if (_tankSpans.Count < 2000)
        {
            _tankSpans.Add(new TankSpanSnapshot(victim, at, at, 1));
        }
    }

    /// <summary>Who the target meleed, in turns, through the fight.</summary>
    internal IReadOnlyList<TankSpanSnapshot> TankSpans => _tankSpans;

    /// <summary>A player or pet died during the fight.</summary>
    internal void AddDeath(string name, DateTime time = default, string killedBy = "", string killingBlow = "")
    {
        _deaths[name] = _deaths.GetValueOrDefault(name) + 1;
        if (time != default)
        {
            RecentEventSnapshot[] window = _recent.TryGetValue(name, out List<RecentEventSnapshot>? events)
                ? events.Where(e => e.Time <= time && e.Time >= time.AddSeconds(-RecentSeconds)).ToArray()
                : Array.Empty<RecentEventSnapshot>();
            _lastDeathWindow[name] = (time, window);
        }

        if (_deathLog.Count < 200)
        {
            _deathLog.Add(new DeathSnapshot(time, name, killedBy ?? string.Empty, killingBlow ?? string.Empty));
        }
    }

    /// <summary>A hit (or heal) that landed on a player or pet while this fight was on, kept for <see cref="RecentSeconds"/>.</summary>
    internal void AddRecent(string victim, RecentEventSnapshot e)
    {
        if (!_recent.TryGetValue(victim, out List<RecentEventSnapshot>? events))
        {
            _recent[victim] = events = new List<RecentEventSnapshot>();
        }

        events.Add(e);
        DateTime oldest = e.Time.AddSeconds(-RecentSeconds - 1);
        if (events.Count > 16 && events[0].Time < oldest)
        {
            events.RemoveAll(x => x.Time < oldest);
        }
    }

    /// <summary>
    /// Each player's or pet's last <see cref="RecentSeconds"/> seconds of hits and heals: before their last death in the fight, or else
    /// before the fight's end.
    /// </summary>
    internal IReadOnlyList<LastSecondsSnapshot> LastSeconds()
    {
        var windows = new List<LastSecondsSnapshot>();
        foreach (string name in _takenByName.Keys.Concat(_lastDeathWindow.Keys).Distinct(StringComparer.Ordinal))
        {
            if (_lastDeathWindow.TryGetValue(name, out (DateTime Time, RecentEventSnapshot[] Events) death))
            {
                windows.Add(new LastSecondsSnapshot(name, death.Time, true, WithHealCasts(death.Events, death.Time)));
                continue;
            }

            RecentEventSnapshot[] events = _recent.TryGetValue(name, out List<RecentEventSnapshot>? list)
                ? list.Where(e => e.Time <= EndTime && e.Time >= EndTime.AddSeconds(-RecentSeconds)).ToArray()
                : Array.Empty<RecentEventSnapshot>();
            windows.Add(new LastSecondsSnapshot(name, EndTime, false, WithHealCasts(events, EndTime)));
        }

        return windows;
    }

    // Every heal anyone began casting in the window (a cast line does not say who it is for), with the hits and heals that landed.
    private ValueList<RecentEventSnapshot> WithHealCasts(IEnumerable<RecentEventSnapshot> events, DateTime end)
    {
        DateTime start = end.AddSeconds(-RecentSeconds);
        IEnumerable<RecentEventSnapshot> casts = _playerCasts
            .Where(c => c.Time >= start && c.Time <= end && _healSpells.Contains(c.Spell))
            .Select(c => new RecentEventSnapshot(c.Time, c.Caster, c.Spell, 0, true, IsCast: true));
        return new ValueList<RecentEventSnapshot>(events.Concat(casts).OrderBy(e => e.Time).ToArray());
    }

    /// <summary>Each death in order, with the log time and the killer when known.</summary>
    internal IReadOnlyList<DeathSnapshot> DeathLog => _deathLog;

    private readonly HashSet<string> _witnesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A character whose log showed this fight.</summary>
    internal void AddWitness(string logOwner)
    {
        if (!string.IsNullOrEmpty(logOwner))
        {
            _witnesses.Add(logOwner);
        }
    }

    /// <summary>Everyone who hit the target or was hit by it, and the characters whose logs showed the fight.</summary>
    internal IEnumerable<string> People => _byName.Keys.Concat(_takenByName.Keys).Concat(_witnesses);

    /// <summary>True when <paramref name="name"/> dealt or took damage in this fight.</summary>
    internal bool Involves(string name) => _byName.ContainsKey(name) || _takenByName.ContainsKey(name);

    /// <summary>Times <paramref name="name"/> died during the fight.</summary>
    public int DeathsOf(string name) => _deaths.GetValueOrDefault(name);

    /// <summary>A spell the fight's target started casting.</summary>
    internal void AddCast(DateTime timestamp, string spell)
    {
        if (_casts.Count < MaxCasts)
        {
            _casts.Add(new BossCastSnapshot(timestamp, spell));
        }
    }

    private readonly List<LostControlSnapshot> _lostControl = new();

    /// <summary>A log's own character lost control of itself (charm, fear or mind control) during the fight.</summary>
    internal void AddLostControl(string character, DateTime start, string spell, string caster)
    {
        if (_lostControl.Count < 500)
        {
            _lostControl.Add(new LostControlSnapshot(character, start, null, spell, caster));
        }
    }

    /// <summary>The character has control of itself again: ends its open stretch, if any.</summary>
    internal bool EndLostControl(string character, DateTime end)
    {
        for (int i = _lostControl.Count - 1; i >= 0; i--)
        {
            LostControlSnapshot open = _lostControl[i];
            if (open.Character.Equals(character, StringComparison.OrdinalIgnoreCase))
            {
                if (open.End is not null)
                {
                    return false;
                }

                _lostControl[i] = open with { End = end < open.Start ? open.Start : end };
                return true;
            }
        }

        return false;
    }

    /// <summary>Each time a log's own character lost control of itself during the fight.</summary>
    internal IReadOnlyList<LostControlSnapshot> LostControl => _lostControl;

    /// <summary>A player or pet began casting a spell during the fight.</summary>
    internal void AddPlayerCast(PlayerCastSnapshot cast)
    {
        if (_playerCasts.Count < MaxPlayerCasts)
        {
            _playerCasts.Add(cast);
        }
    }

    /// <summary>Marks <paramref name="caster"/>'s latest cast no earlier than <paramref name="notBefore"/> as having failed.</summary>
    internal void FailLastCast(string caster, DateTime notBefore)
    {
        for (int i = _playerCasts.Count - 1; i >= 0; i--)
        {
            PlayerCastSnapshot cast = _playerCasts[i];
            if (cast.Time < notBefore)
            {
                return;
            }

            if (cast.Caster.Equals(caster, StringComparison.OrdinalIgnoreCase))
            {
                _playerCasts[i] = cast with { Failed = true };
                return;
            }
        }
    }

    /// <summary>Spells players began casting, in log order.</summary>
    internal IReadOnlyList<PlayerCastSnapshot> PlayerCasts => _playerCasts;

    /// <summary>Spells the fight's target cast, in log order.</summary>
    internal IReadOnlyList<BossCastSnapshot> Casts => _casts;

    /// <summary>A heal landed during the fight. <paramref name="possible"/> is what the spell could have healed.</summary>
    private readonly HashSet<string> _healSpells = new(StringComparer.OrdinalIgnoreCase);

    internal void AddHeal(string healer, string spell, int actual, int possible, bool isCritical = false, DateTime timestamp = default,
        string target = "", bool overTime = false)
    {
        _healSpells.Add(spell);
        if (timestamp != default)
        {
            _timeline.RecordHealing((int)(timestamp - StartTime).TotalSeconds, actual);
            if (!overTime)
            {
                _timeline.RecordInstantHeal((int)(timestamp - StartTime).TotalSeconds, target);
            }
        }

        if (!_healers.TryGetValue(healer, out HealStats? stats))
        {
            stats = new HealStats(healer);
            _healers.Add(healer, stats);
        }

        stats.Record(spell, actual, possible, isCritical);
    }

    internal IReadOnlyCollection<HealStats> Healers => _healers.Values;

    /// <summary>Damage the target dealt to each player or pet, in first-seen order.</summary>
    internal IReadOnlyCollection<DamageTakenStats> TakenStats => _takenByName.Values;

    /// <summary>Total damage the target dealt to players and pets.</summary>
    public long TotalDamageTaken { get; private set; }

    internal void AddMiss(DateTime timestamp, string source, string? pet = null)
    {
        ParticipantStats row = GetOrAdd(source);
        row.RecordMiss(timestamp);
        if (pet is not null)
        {
            row.RecordPet(pet, 0, hit: false);
        }

        Touch(timestamp);
    }

    internal void Close(DateTime endTime)
    {
        Touch(endTime);
        IsActive = false;
        Recalculate();
    }

    /// <summary>Refreshes %-of-group and scaled DPS on every participant and notifies the UI once.</summary>
    public void Recalculate()
    {
        double seconds = Math.Max(1.0, DurationSeconds);
        foreach (ParticipantStats participant in _participants)
        {
            participant.Recalculate(TotalDamage, seconds);
        }

        OnPropertyChanged(string.Empty);
    }

    private ParticipantStats GetOrAdd(string name)
    {
        if (!_byName.TryGetValue(name, out ParticipantStats? stats))
        {
            stats = new ParticipantStats(name);
            _byName.Add(name, stats);
            _participants.Add(stats);
        }

        return stats;
    }

    private void Touch(DateTime timestamp)
    {
        if (timestamp > EndTime)
        {
            EndTime = timestamp;
        }
    }
}
