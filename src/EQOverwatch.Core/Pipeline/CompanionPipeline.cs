using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Sharing;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Pipeline;

/// <summary>
/// Composition root for everything in Core. Live and historical work are fully separate:
/// <list type="bullet">
/// <item><description>Live: <see cref="Logs"/> → <see cref="LiveLogMonitor"/> → <see cref="Triggers"/> + <see cref="LiveDps"/>.</description></item>
/// <item><description>Historical: <see cref="ImportHistoricalAsync"/> → <see cref="HistoricalDps"/> only.</description></item>
/// </list>
/// The two DPS trackers share no state, so tailing and triggers keep running while a historical file is loaded and inspected.
/// </summary>
public sealed class CompanionPipeline : IAsyncDisposable
{
    private readonly LiveLogMonitor _monitor;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private volatile bool _isImporting;

    public CompanionPipeline(TimeSpan? inactivityTimeout = null)
    {
        Logs = new WatchedLogCollection();
        Triggers = new TriggerEngineService();
        Spells = new SpellDatabaseService();
        Buffs = new BuffTrackerService(Spells, Triggers);
        Uptime = new BuffUptimeTracker(Buffs);
        TimerPause = new TimerPauseService(Triggers);
        Roster = new PlayerRoster();
        LiveDps = new LiveDpsTracker(inactivityTimeout, roster: Roster);
        LiveDpsTracker liveDps = LiveDps;
        // A tracked debuff landing on the mob being fought counts for uptime (it gets no bar of its own).
        Buffs.SetBystanderProbe(name => liveDps.Snapshot.Active.Any(e => string.Equals(e.TargetName, name, StringComparison.OrdinalIgnoreCase)));
        RaidBosses = new RaidBossTimerService(Triggers, () => liveDps.Snapshot);
        ChChain = new ChChainService(Triggers);
        Cooldowns = new CooldownTrackerService(Triggers);
        Abilities = new AbilityTrackerService(Triggers);
        SharedTriggers = new SharedTriggerScanner();
        Loot = new LootTracker();
        Tells = new TellReaderService(Triggers);
        AutoClick = new AutoClickService(ChChain, Triggers);
        HistoricalDps = new HistoricalDpsTracker(inactivityTimeout, Roster);
        Songs = new SongTrackerService(Spells, Triggers);
        Charm = new CharmWatchService(Spells, Triggers);
        Experience = new ExperienceTracker(DateTime.UtcNow);
        MobExperience = new MobExperienceStore();
        ResistStore = new MobResistStore();
        Resists = new ResistTrackerService(Spells, ResistStore);
        BuffUptimeTracker uptime = Uptime;
        Songs.SongStarted += (singer, song, seconds, when) => uptime.OnLanded(singer, song, seconds, when);

        Triggers.Error += RaiseError;
        Logs.CollectionChanged += (_, _) => RefreshMonitoredCharacters();

        // Every sink sees every line of every trigger-enabled log. Historical imports use none of them.
        var triggerSinks = new CompositeTriggerSink(
            new ITriggerLineSink[] { Triggers, Buffs, Songs, Charm, TimerPause, RaidBosses, ChChain, Cooldowns, Abilities, SharedTriggers, Roster, Loot, Tells, Experience, MobExperience, Resists }, RaiseError);
        Activity = new CharacterActivityService();
        Songs.SetOwnCharacterProbe(IsMonitoredCharacter);
        Songs.SetActivityProbe(Activity.IsActive);
        Songs.SetLevelProvider((name, server) => Roster.GetLevel(name, server));
        MobExperience.SetLevelProvider((name, server) => Roster.GetLevel(name, server));
        Resists.SetOwnCharacterProbe(IsMonitoredCharacter);
        Resists.SetPlayerProbe(name => IsMonitoredCharacter(name) || Roster.IsVerifiedPlayer(name));
        _monitor = new LiveLogMonitor(Logs, triggerSinks, LiveDps, RaiseError, Activity);
        _monitor.TailError += (config, ex) => TailError?.Invoke(config, ex);
    }

    /// <summary>Which characters are logged in: ones whose log has had a line in the last minute.</summary>
    public CharacterActivityService Activity { get; }

    public WatchedLogCollection Logs { get; }
    public TriggerEngineService Triggers { get; }

    /// <summary>The loaded spell database (empty until a spell file is loaded).</summary>
    public SpellDatabaseService Spells { get; }

    /// <summary>Starts and cancels countdown bars for the user's tracked buffs, songs and DoTs.</summary>
    public BuffTrackerService Buffs { get; }

    /// <summary>Stops buff and debuff bars in safe zones and while a character is logged out, and keeps them across restarts.</summary>
    public TimerPauseService TimerPause { get; }

    /// <summary>How long tracked buffs and debuffs were up during each fight.</summary>
    public BuffUptimeTracker Uptime { get; }

    /// <summary>Recast countdowns for raid boss abilities.</summary>
    public RaidBossTimerService RaidBosses { get; }

    /// <summary>Complete Heal chain bars and the late-cast alarm.</summary>
    public ChChainService ChChain { get; }

    /// <summary>Cooldown bars for class abilities such as Divine Aura.</summary>
    public CooldownTrackerService Cooldowns { get; }

    /// <summary>Active and cooldown bars for tracked AAs and disciplines.</summary>
    public AbilityTrackerService Abilities { get; }

    /// <summary>Song bars for every song your characters sing (and, when switched on, every other bard's).</summary>
    public SongTrackerService Songs { get; }

    /// <summary>Countdown and break alarm for your characters' own charms.</summary>
    public CharmWatchService Charm { get; }

    /// <summary>Experience, levels and AA points gained this session.</summary>
    public ExperienceTracker Experience { get; }

    /// <summary>Experience by mob, per character, server and character level (kept between runs).</summary>
    public MobExperienceStore MobExperience { get; }

    /// <summary>Resist counts per mob and resist type, yours and the raid's shares.</summary>
    public MobResistStore ResistStore { get; }

    /// <summary>Reads your characters' spells on mobs and the shared resist numbers in raid and guild chat.</summary>
    public ResistTrackerService Resists { get; }

    /// <summary>Loot and the rolls won for it, collected from every trigger-enabled log.</summary>
    public LootTracker Loot { get; }

    /// <summary>Clicks the mouse on your cleric's turn in the chain, while EverQuest is the window in front. Off until switched on.</summary>
    public AutoClickService AutoClick { get; }

    /// <summary>Speaks incoming tells aloud.</summary>
    public TellReaderService Tells { get; }

    /// <summary>Spots <c>{EQO:...}</c> shared triggers in chat.</summary>
    public SharedTriggerScanner SharedTriggers { get; }

    /// <summary>Verified players (with classes) and pets, learned from /who and /pet leader lines.</summary>
    public PlayerRoster Roster { get; }
    public LiveDpsTracker LiveDps { get; }
    public HistoricalDpsTracker HistoricalDps { get; }

    /// <summary>Encounter inactivity timeout for both trackers (applies to events processed from now on).</summary>
    public TimeSpan EncounterTimeout
    {
        get => LiveDps.InactivityTimeout;
        set
        {
            LiveDps.InactivityTimeout = value;
            HistoricalDps.InactivityTimeout = value;
        }
    }

    public bool IsLiveRunning => _monitor.IsRunning;
    public bool IsImporting => _isImporting;

    /// <summary>Non-fatal errors from sinks and trigger matching (raised on pipeline threads).</summary>
    public event Action<Exception>? Error;

    /// <summary>A watched log could not be read; it is retried automatically.</summary>
    public event Action<WatchedLogConfig, Exception>? TailError;

    /// <summary>Starts tailing <see cref="Logs"/>. Call on the UI thread.</summary>
    public void StartLive() => _monitor.Start();

    public Task StopLiveAsync() => _monitor.StopAsync();

    /// <summary>
    /// Loads one log into <see cref="HistoricalDps"/> (replacing any previous import). Live monitoring is unaffected.
    /// Only one import may run at a time.
    /// </summary>
    public async Task<HistoricalImportResult> ImportHistoricalAsync(
        string filePath,
        DateTime? startTime = null,
        DateTime? endTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_importGate.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("A historical import is already running.");
        }

        _isImporting = true;
        try
        {
            // The log's own spells on mobs are read for resist tracking too.
            var importer = new HistoricalLogImporter(Resists.WrapImport(HistoricalDps), RaiseError);
            return await importer.ImportAsync(filePath, startTime, endTime, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isImporting = false;
            _importGate.Release();
        }
    }

    /// <summary>
    /// Catches the live DPS tracker up from a log file, as if the app had been running all along: the log (from
    /// <paramref name="startTime"/> on) is read into <see cref="LiveDps"/>, which is cleared first. A fight that is still going on stays
    /// in progress; fights that ended before <paramref name="nowLocal"/> minus the encounter timeout are closed. Call it before
    /// <see cref="StartLive"/> (lines tailed during the catch-up would be out of order); live monitoring then continues from
    /// the end of the file. Only one import or catch-up may run at a time.
    /// </summary>
    public async Task<HistoricalImportResult> BackfillLiveAsync(
        string filePath,
        DateTime? startTime,
        DateTime nowLocal,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_importGate.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("A historical import is already running.");
        }

        _isImporting = true;
        try
        {
            LiveDps.Reset();
            LiveDps.KeepActiveEncountersOnEndBatch = true;
            try
            {
                var importer = new HistoricalLogImporter(LiveDps, RaiseError);
                HistoricalImportResult result =
                    await importer.ImportAsync(filePath, startTime, null, progress, cancellationToken).ConfigureAwait(false);
                LiveDps.OnLogClock(nowLocal); // closes fights that ended long ago; a fight in progress stays open
                return result;
            }
            finally
            {
                LiveDps.KeepActiveEncountersOnEndBatch = false;
            }
        }
        finally
        {
            _isImporting = false;
            _importGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync().ConfigureAwait(false);
        AutoClick.Dispose();
        RaidBosses.Dispose();
        ChChain.Dispose();
    }

    private void RaiseError(Exception ex) => Error?.Invoke(ex);

    private string[] _additionalCharacters = Array.Empty<string>();
    private (string Name, string Server)[] _additionalOnServers = Array.Empty<(string, string)>();

    /// <summary>Characters monitored in addition to the watched logs (a second PC, groupmates).</summary>
    public IReadOnlyList<string> AdditionalMonitoredCharacters => _additionalCharacters;

    /// <summary>Every watched log's character plus the additional ones, without duplicates (case-insensitive).</summary>
    public IReadOnlyList<string> MonitoredCharacters
    {
        get
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Logs.Select(l => l.CharacterName).Concat(_additionalCharacters))
            {
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    private HashSet<string> _monitoredSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="name"/> is one of the characters this app monitors ("my characters").</summary>
    public bool IsMonitoredCharacter(string name) => Volatile.Read(ref _monitoredSet).Contains(name);

    /// <summary>
    /// Whether an overlay shows a bar about <paramref name="name"/>. A character with a watched log shows while it is logged in (its log had
    /// a line in the last minute). Anyone else, such as another raid member using a disc or an additional character with a buff, shows
    /// while any of your characters is logged in, so nothing shows when nobody is.
    /// </summary>
    public bool IsBarShown(string name, DateTime nowUtc) =>
        Logs.Any(l => string.Equals(l.CharacterName, name, StringComparison.OrdinalIgnoreCase))
            ? Activity.IsActive(name, nowUtc)
            : Activity.AnyActive(nowUtc);

    /// <summary>Replaces the additional monitored characters and re-registers everyone.</summary>
    public void SetAdditionalMonitoredCharacters(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        SetAdditionalMonitoredCharacters(names.Select(n => (n, string.Empty)));
    }

    /// <summary>
    /// Replaces the additional monitored characters, each on its own server (empty for any server), and re-registers everyone.
    /// Buff landings on them count only in logs on their server.
    /// </summary>
    public void SetAdditionalMonitoredCharacters(IEnumerable<(string Name, string Server)> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        _additionalOnServers = characters
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => (c.Name.Trim(), c.Server?.Trim() ?? string.Empty))
            .Distinct()
            .ToArray();
        _additionalCharacters = _additionalOnServers
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RefreshMonitoredCharacters();
    }

    /// <summary>
    /// Every monitored character is by definition a player: the DPS trackers learn the names (which anchors their NPC-vs-player
    /// inference) and the buff tracker accepts them as targets of "cast on other" landings.
    /// </summary>
    private void RefreshMonitoredCharacters()
    {
        IReadOnlyList<string> everyone = MonitoredCharacters;
        foreach (string name in everyone)
        {
            LiveDps.AddKnownPlayer(name);
            HistoricalDps.AddKnownPlayer(name);
        }

        Volatile.Write(ref _monitoredSet, new HashSet<string>(everyone, StringComparer.OrdinalIgnoreCase));
        Buffs.SetAllowedTargetsOnServers(Logs.Select(l => (l.CharacterName, l.ServerName)).Concat(_additionalOnServers).ToList());
        Abilities.SetMonitoredCharacters(everyone);
    }
}
