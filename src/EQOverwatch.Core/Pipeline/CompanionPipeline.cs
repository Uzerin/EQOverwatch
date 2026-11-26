using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Progress;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Maps;
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
public sealed partial class CompanionPipeline : IAsyncDisposable
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
        Guilds = new GuildBook();
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
        HistoricalDps = new HistoricalDpsTracker(inactivityTimeout, Roster);
        Songs = new SongTrackerService(Spells, Triggers);
        Charm = new CharmWatchService(Spells, Triggers);
        SpellAlerts = new SpellAlertService(Spells, Triggers);
        OutputFiles = new OutputFileWatch();
        Chat = new Chat.ChatLog();
        BossAbilities = new History.BossAbilityLearner();
        Experience = new ExperienceTracker(DateTime.UtcNow);
        MobExperience = new MobExperienceStore();
        Zones = new ZoneTracker();
        ResourceHunter = new ResourceHunter.ResourceHunterStore();
        Drops = new DropStore();
        Progress = new ProgressStore();
        TellInbox = new TellInbox();
        DynamicZones = new EQOverwatch.Core.Progress.DynamicZoneTracker();
        RaidDiscord = new EQOverwatch.Core.Discord.RaidDiscordSink();
        ResistStore = new MobResistStore();
        Resists = new ResistTrackerService(Spells, ResistStore);
        BuffUptimeTracker uptime = Uptime;
        Songs.SongStarted += (singer, song, seconds, when) => uptime.OnLanded(singer, song, seconds, when);

        Triggers.Error += RaiseError;
        Logs.CollectionChanged += (_, _) => RefreshMonitoredCharacters();

        // Every sink sees every line of every trigger-enabled log. Historical imports use none of them.
        var triggerSinks = new CompositeTriggerSink(
            new ITriggerLineSink[] { Triggers, Buffs, Songs, Charm, SpellAlerts, TimerPause, RaidBosses, ChChain, Cooldowns, Abilities, SharedTriggers, Roster, Loot, Tells, Experience, MobExperience, Resists, Zones, Drops, Progress, TellInbox, DynamicZones, RaidDiscord, OutputFiles, Chat, Guilds, ResourceHunter }, RaiseError);
        Activity = new CharacterActivityService();
        Songs.SetOwnCharacterProbe(IsMonitoredCharacter);
        Songs.SetActivityProbe(Activity.IsActive);
        Songs.SetLevelProvider((name, server) => Roster.GetLevel(name, server));
        MobExperience.SetLevelProvider((name, server) => Roster.GetLevel(name, server));
        Resists.SetOwnCharacterProbe(IsMonitoredCharacter);
        Resists.SetPlayerProbe(name => IsMonitoredCharacter(name) || Roster.IsVerifiedPlayer(name));
        ZoneTracker zones = Zones;
        Drops.SetZoneProvider((name, server) => zones.Get(name, server)?.Zone);
        ResourceHunter.SetZoneProvider((name, server) => zones.Get(name, server)?.Zone);
        Drops.SetPlayerProbe(name => IsMonitoredCharacter(name) || Roster.IsVerifiedPlayer(name) || Roster.IsPet(name));
        TellInbox.SetOwnCharacterProbe(IsMonitoredCharacter);
        Progress.SetClassProvider((name, server) => Roster.GetClass(name, server));
        Progress.PersonaChanged += (name, server, persona, level) => Roster.LearnPlayer(name, persona, level, server); // the class follows the persona
        Progress.SetPlayerProbe(name => IsMonitoredCharacter(name) || Roster.IsVerifiedPlayer(name) || Roster.IsPet(name));
        PlayerRoster roster = Roster;
        FloatingCombat = new FloatingCombatFeed(IsMonitoredCharacter,
            pet => roster.MergePetDamage && roster.TryGetOwner(pet, out string owner) ? owner : null);
        MobHits = new EQOverwatch.Core.History.MobHitStore(IsMonitoredCharacter);
        _monitor = new LiveLogMonitor(Logs, triggerSinks, new FanOutDpsSink(LiveDps, FloatingCombat, MobHits), RaiseError, Activity);
        _monitor.TailError += (config, ex) => TailError?.Invoke(config, ex);
        CreateEditionServices();
    }

    // Hooks an edition fills in with services of its own (they do nothing in this one).
    partial void CreateEditionServices();

    partial void DisposeEditionServices();

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

    /// <summary>Spoken alerts: a buff that did not take hold, a resist streak, a failed feign death, root or mez breaking.</summary>
    public SpellAlertService SpellAlerts { get; }

    /// <summary>Files EverQuest just wrote (/outputfile, /output achievements, raid and guild dumps), read on the next tick.</summary>
    public OutputFileWatch OutputFiles { get; }

    /// <summary>Player chat from every log, for the Chat tab.</summary>
    public Chat.ChatLog Chat { get; }

    /// <summary>Numbers for the floating combat text overlays.</summary>
    public FloatingCombatFeed FloatingCombat { get; }

    /// <summary>The biggest hit each mob has landed on your characters (for the mob card).</summary>
    public EQOverwatch.Core.History.MobHitStore MobHits { get; }

    /// <summary>Boss ability timing learned from killed fights, for the learned boss timers.</summary>
    public History.BossAbilityLearner BossAbilities { get; }

    /// <summary>Experience, levels and AA points gained this session.</summary>
    public ExperienceTracker Experience { get; }

    /// <summary>Experience by mob, per character, server and character level (kept between runs).</summary>
    public MobExperienceStore MobExperience { get; }

    /// <summary>Each character's zone and last /loc, for the Maps tab.</summary>
    public ZoneTracker Zones { get; }

    /// <summary>The Resource Hunter zone bonuses your logs show, and today's bonuses other players and the tracking websites report.</summary>
    public ResourceHunter.ResourceHunterStore ResourceHunter { get; }

    /// <summary>Players' guilds from /who lines, per server.</summary>
    public GuildBook Guilds { get; }

    /// <summary>The drop table: which mobs drop which items and how often, per server.</summary>
    public DropStore Drops { get; }

    /// <summary>Skill-ups, tradeskill combines and faction changes per character.</summary>
    public ProgressStore Progress { get; }

    /// <summary>Player tells to your characters, answered or not.</summary>
    public TellInbox TellInbox { get; }

    /// <summary>Short notices (rolls, linkdead players) shown on the Tells overlay for 30 seconds.</summary>
    public EQOverwatch.Core.Progress.OverlayNoticeFeed Notices { get; } = new();

    /// <summary>Your characters' dynamic zones and when each closes (shown on the Tells overlay).</summary>
    public EQOverwatch.Core.Progress.DynamicZoneTracker DynamicZones { get; }

    /// <summary>Raid keys heard in raid chat and the loot lines after raid boss kills, for the Discord posts.</summary>
    public EQOverwatch.Core.Discord.RaidDiscordSink RaidDiscord { get; }

    /// <summary>Resist counts per mob and resist type, yours and the raid's shares.</summary>
    public MobResistStore ResistStore { get; }

    /// <summary>Reads your characters' spells on mobs and the shared resist numbers in raid and guild chat.</summary>
    public ResistTrackerService Resists { get; }

    /// <summary>Loot and the rolls won for it, collected from every trigger-enabled log.</summary>
    public LootTracker Loot { get; }

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
    public Task<HistoricalImportResult> ImportHistoricalAsync(
        string filePath,
        DateTime? startTime = null,
        DateTime? endTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ImportHistoricalAsync(new[] { filePath }, startTime, endTime, progress, cancellationToken);

    /// <summary>Loads several characters' logs into <see cref="HistoricalDps"/> as one, in time order; a line several logs saw counts once.</summary>
    public async Task<HistoricalImportResult> ImportHistoricalAsync(
        IReadOnlyList<string> filePaths,
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
            return await importer.ImportAsync(filePaths, startTime, endTime, progress, cancellationToken).ConfigureAwait(false);
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
    public Task<HistoricalImportResult> BackfillLiveAsync(
        string filePath,
        DateTime? startTime,
        DateTime nowLocal,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        BackfillLiveAsync(new[] { filePath }, startTime, nowLocal, progress, cancellationToken);

    /// <summary>The catch-up from every character's log at once, in time order (see the one-log overload).</summary>
    public async Task<HistoricalImportResult> BackfillLiveAsync(
        IReadOnlyList<string> filePaths,
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
                    await importer.ImportAsync(filePaths, startTime, null, progress, cancellationToken).ConfigureAwait(false);
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
        DisposeEditionServices();
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
