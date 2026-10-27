using EQCompanion.Core.Models;
using EQCompanion.Core.Services;
using EQCompanion.Core.Sharing;
using EQCompanion.Core.Spells;

namespace EQCompanion.Core.Pipeline;

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
        LiveDps = new LiveDpsTracker(inactivityTimeout);
        LiveDpsTracker liveDps = LiveDps;
        RaidBosses = new RaidBossTimerService(Triggers, () => liveDps.Snapshot);
        ChChain = new ChChainService(Triggers);
        Cooldowns = new CooldownTrackerService(Triggers);
        Abilities = new AbilityTrackerService(Triggers);
        SharedTriggers = new SharedTriggerScanner();
        HistoricalDps = new HistoricalDpsTracker(inactivityTimeout);

        Triggers.Error += RaiseError;
        Logs.CollectionChanged += (_, _) => RefreshMonitoredCharacters();

        // Every sink sees every line of every trigger-enabled log. Historical imports use none of them.
        var triggerSinks = new CompositeTriggerSink(
            new ITriggerLineSink[] { Triggers, Buffs, RaidBosses, ChChain, Cooldowns, Abilities, SharedTriggers }, RaiseError);
        _monitor = new LiveLogMonitor(Logs, triggerSinks, LiveDps, RaiseError);
        _monitor.TailError += (config, ex) => TailError?.Invoke(config, ex);
    }

    public WatchedLogCollection Logs { get; }
    public TriggerEngineService Triggers { get; }

    /// <summary>The loaded spell database (empty until a spell file is loaded).</summary>
    public SpellDatabaseService Spells { get; }

    /// <summary>Starts and cancels countdown bars for the user's tracked buffs, songs and DoTs.</summary>
    public BuffTrackerService Buffs { get; }

    /// <summary>Recast countdowns for raid boss abilities.</summary>
    public RaidBossTimerService RaidBosses { get; }

    /// <summary>Complete Heal chain bars and the late-cast alarm.</summary>
    public ChChainService ChChain { get; }

    /// <summary>Cooldown bars for class abilities such as Divine Aura.</summary>
    public CooldownTrackerService Cooldowns { get; }

    /// <summary>Active and cooldown bars for tracked AAs and disciplines.</summary>
    public AbilityTrackerService Abilities { get; }

    /// <summary>Spots <c>{EQO:...}</c> shared triggers in chat.</summary>
    public SharedTriggerScanner SharedTriggers { get; }
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
            var importer = new HistoricalLogImporter(HistoricalDps, RaiseError);
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
        RaidBosses.Dispose();
        ChChain.Dispose();
    }

    private void RaiseError(Exception ex) => Error?.Invoke(ex);

    private string[] _additionalCharacters = Array.Empty<string>();

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

    /// <summary>Replaces the additional monitored characters and re-registers everyone.</summary>
    public void SetAdditionalMonitoredCharacters(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _additionalCharacters = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
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

        Buffs.SetAllowedTargets(everyone);
        Abilities.SetMonitoredCharacters(everyone);
    }
}
