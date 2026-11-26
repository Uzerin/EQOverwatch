using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Presentation;
using EQOverwatch.App.Services;
using EQOverwatch.Core;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Discovery;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Sharing;
using EQOverwatch.Core.Spells;
using EQOverwatch.Core.Updates;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.ViewModels;

/// <summary>
/// State and commands behind <c>MainWindow</c>: the always-visible toolbar and its six tabs (Characters, Custom Triggers, Raid Triggers,
/// Buff &amp; Song Tracker, DPS &amp; Encounter History, Settings). This file holds the core, the toolbar, the trigger editor,
/// the DPS browser, the spell tracker, raid timers and theme; <c>MainViewModel.Characters.cs</c> and <c>MainViewModel.Sharing.cs</c> add
/// the EverQuest folder / character / CH chain / history features and trigger sharing / exports. Everything the user changes is mirrored into <see cref="AppSettings"/> and queued for
/// a debounced, atomic save; <see cref="Shutdown"/> performs the final save and a clean teardown.
/// <para>
/// The view calls <see cref="Refresh"/> from a UI-thread timer; it only reads immutable snapshots, so no locking is needed.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly CompanionPipeline _pipeline;
    private readonly AppSettingsService _settingsService;
    private readonly IOverlayHost _overlays;
    private readonly IAudioControl _audio;
    private readonly IDialogService _dialogs;
    private readonly IThemeService _themeService;
    private readonly IClipboardService _clipboard;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<WatchedLogConfig> _subscribedLogs = new();

    private bool _restoring;
    private bool _isShutDown;
    private long _lastLiveVersion = -1;
    private long _lastHistoricalVersion = -1;
    private CancellationTokenSource? _importCts;

    private readonly IUpdateChecker? _updateChecker;
    private readonly Action<string>? _openUrl;

    public MainViewModel(
        CompanionPipeline pipeline,
        AppSettingsService settingsService,
        AppSettings settings,
        IOverlayHost overlays,
        IAudioControl audio,
        IDialogService dialogs,
        IThemeService themeService,
        IClipboardService clipboard,
        Func<DateTime>? clock = null,
        IUpdateChecker? updateChecker = null,
        Action<string>? openUrl = null,
        Services.ICharacterDataPresenter? characterDataPresenter = null,
        CharacterDataStore? characterData = null,
        RaidRosterStore? raidRoster = null,
        string? remoteDataDirectory = null,
        BossHistoryStore? bossHistory = null,
        string? dataDirectory = null,
        MobLevelStore? mobLevels = null)
    {
        _bossHistory = bossHistory ?? new BossHistoryStore();
        _mobLevels = mobLevels ?? new MobLevelStore();
        _dataDirectory = dataDirectory;
        _remoteDirectory = remoteDataDirectory;
        _raidRoster = raidRoster ?? new RaidRosterStore();
        _characterDataPresenter = characterDataPresenter;
        _characterData = characterData ?? new CharacterDataStore();
        _updateChecker = updateChecker;
        _openUrl = openUrl;
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _pipeline.Buffs.SetDurationProvider(CasterDurationFor);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _clock = clock ?? (() => DateTime.Now); // log timestamps are in the PC's local time
        _pipeline.LiveDps.MobLevelSeen = (mob, level, zone) => _mobLevels.Observe(mob, level, zone); // /consider lines feed the shared level ranges, per zone
        _pipeline.HistoricalDps.MobLevelSeen = (mob, level, zone) => _mobLevels.Observe(mob, level, zone);
        ArgumentNullException.ThrowIfNull(settings);

        PendingSharedTriggers.CollectionChanged += (_, _) => OnPendingSharedChanged();
        _pipeline.SharedTriggers.Offered += OnSharedOffered;
        _pipeline.Roster.Changed += OnRosterChanged;

        Restore(settings);
        HookDiscord();
        InitializeEdition();
        InitializeBoxAlerts();
        InitializeNotices();
        InitializeRelease82();
        InitializeReleaseFeatures();
        _characterData.SetPersonaResolver(PersonaWhenWritten); // worn gear is filed under the persona that was on
        _characterData.SetAccountResolver(AccountOf); // shared banks are kept per account
        _characterData.ScanAll(EqFolderPath); // output files written while the app was closed
        _characterData.PruneMissingFiles(EqFolderPath); // saved data whose output file has since been deleted
        RefreshCharacterInfo();
        InitializeRaidNight();
        _raidRoster.ScanAll(EqFolderPath); // raid roster dumps written while the app was closed
        LearnDumpedPlayers();
        RebuildAttendance();

        Logs.CollectionChanged += OnLogsChanged;
        _overlays.PlacementChanged += OnPlacementChanged;
        _overlays.CloseRequested += (_, kind) => CloseOverlay(kind);
        _overlays.CustomCloseRequested += (_, id) => CloseCustomOverlay(id);
        _pipeline.Error += ex => AddProblem(StatusMessage = $"Error: {ex.Message}");
        _pipeline.TailError += (log, ex) => AddProblem(StatusMessage = $"Cannot read {log.DisplayName}: {ex.Message}");
        _settingsService.SaveFailed += ex => AddProblem(StatusMessage = $"Settings could not be saved: {ex.Message}");

        if (_settingsService.LastLoadError is { } loadError)
        {
            AddProblem(StatusMessage = loadError);
        }

        CheckCrashLog(settings.LastCrashSeenUtc);

        if (!string.IsNullOrWhiteSpace(SpellsFilePath))
        {
            _ = ReloadSpellsAsync(); // progress and failures are reported through SpellsStatus
        }

        UpdateLiveStatus();
        Refresh();
    }

    // ───────────────────────────── toolbar ─────────────────────────────

    [ObservableProperty]
    private bool _isLiveRunning;

    [ObservableProperty]
    private string _liveStatusText = "Stopped";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockButtonText))]
    private bool _areOverlaysLocked;

    [ObservableProperty]
    private bool _isBuffsOverlayVisible = true;

    [ObservableProperty]
    private bool _isDebuffsOverlayVisible = true;

    [ObservableProperty]
    private bool _isSongsOverlayVisible = true;

    [ObservableProperty]
    private bool _isRaidTriggersOverlayVisible = true;

    /// <summary>
    /// The CH Chain overlay. Turned off by default: most characters are not in a Complete Heal chain. While it is off, chain bars and
    /// the late-cast alarm are shown in the Buffs overlay instead.
    /// </summary>
    [ObservableProperty]
    private bool _isChChainOverlayVisible;

    [ObservableProperty]
    private bool _isDpsOverlayVisible = true;

    [ObservableProperty]
    private bool _isTellsOverlayVisible;

    [ObservableProperty]
    private bool _isAudioMuted;

    /// <summary>Speaks "Name tells Character: message" for every tell sent to a watched character.</summary>
    [ObservableProperty]
    private bool _readTellsAloud = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string LockButtonText => AreOverlaysLocked ? "Unlock overlays" : "Lock overlays";

    partial void OnAreOverlaysLockedChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        _overlays.SetLocked(value);
        RequestSave();
    }

    partial void OnIsBuffsOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.Buffs, value);

    partial void OnIsDebuffsOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.Debuffs, value);

    partial void OnIsSongsOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.Songs, value);

    partial void OnIsRaidTriggersOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.RaidTriggers, value);

    partial void OnIsChChainOverlayVisibleChanged(bool value)
    {
        _pipeline.ChChain.UseChChainOverlay = value; // the chain's bars and alarm follow the overlay's on/off switch
        OnOverlayVisibilityChanged(OverlayKind.ChChain, value);
        OnEditionOverlaysChanged();
    }

    partial void OnIsDpsOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.Dps, value);

    partial void OnIsTellsOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.Tells, value);

    /// <summary>The Tells and mob card overlays are newer than most saved settings: they follow the other overlays' lock.</summary>
    private static bool FollowsLock(OverlayKind kind) => kind is OverlayKind.Tells or OverlayKind.MobCard || kind.IsFloatingText();

    private void OnOverlayVisibilityChanged(OverlayKind kind, bool visible)
    {
        if (_restoring)
        {
            return;
        }

        _overlays.SetVisible(kind, visible);
        RequestSave();
    }

    partial void OnReadTellsAloudChanged(bool value)
    {
        _pipeline.Tells.IsEnabled = value;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnIsAudioMutedChanged(bool value)
    {
        _audio.IsEnabled = !value;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>Fires the demo triggers and a synthetic encounter so both overlays can be checked without playing.</summary>
    [RelayCommand]
    private void TestOverlays()
    {
        string character = Logs.FirstOrDefault()?.CharacterName ?? "Stackprime";
        DateTime now = DateTime.UtcNow;

        _pipeline.Triggers.Test(DemoTriggers.All, character, DemoTriggers.SkinFadesLine, now);
        _pipeline.Triggers.Test(DemoTriggers.All, character, DemoTriggers.CompleteHealLine, now);

        // One bar for each of the other overlays, so all five timer overlays can be checked at once.
        Guid source = Guid.Parse("5b7f0f2a-3c1e-4a79-9d36-1f0c2d8b6e44");
        _pipeline.Triggers.StartTimer(source, "Overlay test", "Test", $"{character}: Disease Cloud (debuff)", 45, now, TimerOverlayCategory.Debuffs);
        _pipeline.Triggers.StartTimer(source, "Overlay test", "Test", $"{character}: Selo's Accelerando (song)", 24, now, TimerOverlayCategory.Songs);
        _pipeline.Triggers.StartTimer(source, "Overlay test", "Test", "Tormax: Dragon Roar (raid)", 30, now, TimerOverlayCategory.RaidTriggers);
        _pipeline.Triggers.StartTimer(source, "Overlay test", "Test", $"{character}: Complete Heal 2", ChChainService.BarSeconds, now, TimerOverlayCategory.ChChain);

        // Synthetic encounter using local time as the "log clock". Clicking again within the timeout extends it.
        DateTime start = DateTime.Now.AddSeconds(-3);
        (string Name, int Damage)[] hits =
        {
            ("Stackprime", 1450), ("Oodles", 980), ("Ream", 310),
            ("Stackprime", 1600), ("Oodles", 1210), ("Ream", 275),
            ("Stackprime", 1380), ("Oodles", 1100),
        };

        for (int i = 0; i < hits.Length; i++)
        {
            var combatEvent = new CombatEvent(
                start.AddMilliseconds(i * 350), CombatEventKind.Damage,
                hits[i].Name, "a test dummy", hits[i].Damage, DamageKind.Melee, "hit");
            _pipeline.LiveDps.OnCombatEvent(combatEvent);
        }

        StatusMessage = "Sent test alerts, a bar for every timer overlay and a synthetic encounter." +
                        (IsChChainOverlayVisible ? string.Empty : " The CH Chain overlay is turned off in Settings.");
    }

    // ─────────────────────── tab 1: watched logs ───────────────────────

    public WatchedLogCollection Logs => _pipeline.Logs;

    [RelayCommand]
    private void AddLog()
    {
        string[]? files = _dialogs.PickLogFiles();
        if (files is null)
        {
            return;
        }

        foreach (string path in files)
        {
            WatchedLogConfig log = Logs.AddFile(path);
            _pipeline.LiveDps.AddKnownPlayer(log.CharacterName);
        }

        ForgetLastPlayedLog();
        OfferAllLogsLoad(); // a new install's first logs: offer to read their history once

        StatusMessage = $"{Logs.Count} log file(s) watched.";
        ScanUnreadMobExperience();
    }

    [RelayCommand]
    private void RemoveLog(WatchedLogConfig? log)
    {
        if (log is not null && Logs.Remove(log))
        {
            StatusMessage = $"Stopped watching {log.DisplayName}.";
        }
    }

    // ───────────────────── tab 1: trigger editor ─────────────────────

    public ObservableCollection<TriggerRuleViewModel> Rules { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRule))]
    private TriggerRuleViewModel? _selectedRule;

    [ObservableProperty]
    private string _testLine = string.Empty;

    [ObservableProperty]
    private string _testResultText = string.Empty;

    public bool HasSelectedRule => SelectedRule is not null;

    [RelayCommand]
    private void AddRule()
    {
        TriggerRuleViewModel rule = CreateRule(new TriggerRule { Name = $"Trigger {Rules.Count + 1}" });
        Rules.Add(rule);
        SelectedRule = rule;
        OnRulesChanged();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        int index = Rules.IndexOf(SelectedRule);
        SelectedRule.PropertyChanged -= OnRulePropertyChanged;
        Rules.Remove(SelectedRule);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        TestResultText = string.Empty;
        OnRulesChanged();
    }

    [RelayCommand]
    private void PickWav()
    {
        if (SelectedRule is null)
        {
            return;
        }

        string? path = _dialogs.PickWavFile();
        if (path is not null)
        {
            SelectedRule.AudioFilePath = path;
        }
    }

    /// <summary>
    /// Runs the selected rule against <see cref="TestLine"/> (with or without a timestamp wrapper) and fires it for real:
    /// capture groups, flash alert, countdown and TTS/sound all happen immediately. Works even if the rule is disabled.
    /// </summary>
    [RelayCommand]
    private void FireSelectedRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        TriggerRule rule = SelectedRule.ToRule();
        rule.IsEnabled = true;

        string line = TestLine ?? string.Empty;
        string message = line.Length > LogLineFormat.MessageOffset && LogLineFormat.TryParseTimestamp(line, out _)
            ? line[LogLineFormat.MessageOffset..]
            : line;

        TriggerTestResult result = TriggerRuleTester.Evaluate(rule, message);
        if (!result.IsPatternValid)
        {
            TestResultText = $"Invalid pattern: {result.Error}";
            return;
        }

        if (!result.Matched)
        {
            TestResultText = string.IsNullOrWhiteSpace(message)
                ? "Enter a sample log line (the text after the timestamp) and try again."
                : "No match: the pattern does not match that line.";
            return;
        }

        string character = Logs.FirstOrDefault()?.CharacterName ?? "Tester";
        IReadOnlyList<TriggerFiredEvent> fired = _pipeline.Triggers.Test(rule.ToDefinitions(), character, message, DateTime.UtcNow);

        string captures = result.Groups.Count == 0
            ? "no captures"
            : string.Join(", ", result.Groups.Select(g => $"{g.Key}={g.Value}"));
        string shown = string.Join(" | ", fired.Select(f => f.DisplayText));
        string spoken = string.Join(" | ", fired.Where(f => f.AudioText is not null).Select(f => f.AudioText));

        TestResultText = $"Matched ({captures}). Shown: {shown}" + (spoken.Length > 0 ? $". Spoken: {spoken}" : string.Empty);
    }

    private TriggerRuleViewModel CreateRule(TriggerRule rule)
    {
        var vm = new TriggerRuleViewModel(rule);
        SetRuleGroup(vm);
        vm.PropertyChanged += OnRulePropertyChanged;
        return vm;
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TriggerRuleViewModel.Error) or nameof(TriggerRuleViewModel.HasError)
            or nameof(TriggerRuleViewModel.GroupTop) or nameof(TriggerRuleViewModel.GroupSub) or nameof(TriggerRuleViewModel.GroupOrder))
        {
            return; // set by ApplyTriggers or the grouping itself
        }

        if (e.PropertyName is nameof(TriggerRuleViewModel.Folder) or nameof(TriggerRuleViewModel.Zone) && sender is TriggerRuleViewModel changed)
        {
            SetRuleGroup(changed);
        }

        OnRulesChanged();
    }

    private void OnRulesChanged()
    {
        ApplyTriggers();
        RequestSave();
    }

    /// <summary>Pushes every rule to the engine and shows compile errors next to the offending rule.</summary>
    private void ApplyTriggers()
    {
        var definitions = new List<TriggerDefinition>();
        var owners = new Dictionary<Guid, TriggerRuleViewModel>();

        foreach (TriggerRuleViewModel rule in Rules)
        {
            rule.Error = null;
            foreach (TriggerDefinition definition in rule.ToRule().ToDefinitions())
            {
                definitions.Add(definition);
                owners[definition.Id] = rule;
            }
        }

        // Triggers routed to one of your own overlays show there instead of in their category's overlay.
        var routes = new Dictionary<Guid, string>();
        var known = CustomOverlays.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach ((Guid id, TriggerRuleViewModel owner) in owners)
        {
            if (owner.CustomOverlayId is { Length: > 0 } overlay && known.Contains(overlay))
            {
                routes[id] = overlay;
            }
        }

        TimerOverlayViewModel.SetRoutes(routes);

        foreach (TriggerCompileError error in _pipeline.Triggers.SetTriggers(definitions))
        {
            if (owners.TryGetValue(error.Trigger.Id, out TriggerRuleViewModel? owner))
            {
                owner.Error = error.Message;
            }
        }
    }

    // ─────────────────── tab 2: DPS & encounter history ───────────────────

    [ObservableProperty]
    private ObservableCollection<EncounterRowViewModel> _encounters = new();

    [ObservableProperty]
    private EncounterRowViewModel? _selectedEncounter;

    [ObservableProperty]
    private string _encounterSummary = NoEncounterText;

    [ObservableProperty]
    private double _encounterTimeoutSeconds = AppSettings.DefaultEncounterTimeoutSeconds;

    private const string NoEncounterText = "No encounter selected";

    public ObservableCollection<ParticipantRowViewModel> Participants { get; } = new();

    /// <summary>What the selected fight's target did to the group, most damage first.</summary>
    public ObservableCollection<DamageTakenRowViewModel> DamageTaken { get; } = new();

    /// <summary>The selected fight's DPS-over-time lines (null when it was too short to graph).</summary>
    [ObservableProperty]
    private DpsTimeline? _encounterTimeline;

    [ObservableProperty]
    private string _damageTakenSummary = string.Empty;

    partial void OnSelectedEncounterChanged(EncounterRowViewModel? value) => RefreshParticipants();

    partial void OnEncounterTimeoutSecondsChanged(double value)
    {
        double clamped = double.IsFinite(value)
            ? Math.Clamp(value, AppSettings.MinEncounterTimeoutSeconds, AppSettings.MaxEncounterTimeoutSeconds)
            : AppSettings.DefaultEncounterTimeoutSeconds;

        if (clamped != value)
        {
            EncounterTimeoutSeconds = clamped; // re-enters this handler with the clamped value
            return;
        }

        _pipeline.EncounterTimeout = TimeSpan.FromSeconds(clamped);
        if (!_restoring)
        {
            RequestSave();
        }
    }

    // ─────────────────────────── import bar ───────────────────────────

    [ObservableProperty]
    private string _importStartText = string.Empty;

    [ObservableProperty]
    private string _importEndText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportLogCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    private bool _isImporting;

    /// <summary>0..100.</summary>
    [ObservableProperty]
    private double _importProgress;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    private bool CanImport() => !IsImporting;

    private bool CanCancelImport() => IsImporting;

    /// <summary>Picks a log, then loads it into the historical tracker using the optional start/end filters.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportLogAsync()
    {
        if (!TryParseTime(ImportStartText, out DateTime? start))
        {
            ImportStatus = "The start time is not a valid date/time (try 2026-10-21 11:45:00).";
            return;
        }

        if (!TryParseTime(ImportEndText, out DateTime? end))
        {
            ImportStatus = "The end time is not a valid date/time (try 2026-10-21 12:30:00).";
            return;
        }

        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            ImportStatus = "The start time must not be later than the end time.";
            return;
        }

        string? path = _dialogs.PickImportLogFile();
        if (path is null)
        {
            return;
        }

        await RunImportAsync(path, start, end, $"Importing {Path.GetFileName(path)}…", successSuffix: string.Empty);
    }

    /// <summary>
    /// Loads <paramref name="path"/> into the historical tracker (the manual "Import log file…" button). Reports through
    /// <see cref="ImportStatus"/> and never throws.
    /// </summary>
    private Task RunImportAsync(string path, DateTime? start, DateTime? end, string startStatus, string successSuffix) =>
        RunImportAsync(new[] { path }, start, end, startStatus, successSuffix);

    /// <summary>
    /// Loads <paramref name="paths"/> into the historical tracker: one log, or every character's log read as one (a line several logs saw
    /// counts once). Reports through <see cref="ImportStatus"/> and never throws.
    /// </summary>
    private async Task RunImportAsync(
        IReadOnlyList<string> paths, DateTime? start, DateTime? end, string startStatus, string successSuffix)
    {
        using var cts = new CancellationTokenSource();
        _importCts = cts;
        NoteHistoricalSource(paths); // one log's fights are filed under its character; every log's under their own servers
        IsImporting = true;
        ImportProgress = 0;
        ImportStatus = startStatus;

        bool finished = false;
        var progress = new Progress<double>(fraction =>
        {
            if (!finished)
            {
                ImportProgress = fraction * 100.0;
            }
        });

        try
        {
            HistoricalImportResult result = await _pipeline.ImportHistoricalAsync(paths, start, end, progress, cts.Token);
            finished = true;
            ImportProgress = 100;
            ImportStatus =
                $"Imported {result.LinesRead:N0} lines for {result.CharacterName} in {result.Elapsed.TotalSeconds:0.0}s: " +
                $"{_pipeline.HistoricalDps.Snapshot.Completed.Count:N0} encounters.{successSuffix}";
            _lastHistoricalVersion = -1; // the imported snapshot changed even if the live one did not
            Refresh();
        }
        catch (OperationCanceledException)
        {
            finished = true;
            ImportStatus = "Import cancelled.";
        }
        catch (Exception ex)
        {
            finished = true;
            ImportStatus = $"Import failed: {ex.Message}";
        }
        finally
        {
            _importCts = null;
            IsImporting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport()
    {
        try
        {
            _importCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the import finished a moment ago
        }
    }

    private static bool TryParseTime(string? text, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // blank = no bound
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    // ───────────────────── tab 3: buff & song tracker ─────────────────────

    private const string NoSpellsText = "No spell file loaded. Browse to spells_us.txt in your EverQuest folder.";

    private readonly SemaphoreSlim _spellLoadGate = new(1, 1);
    private int _spellLoadSequence;

    [ObservableProperty]
    private string _spellsFilePath = string.Empty;

    /// <summary>1-125. Durations are recalculated from the spell file whenever this changes.</summary>
    [ObservableProperty]
    private int _defaultCasterLevel = SpellDatabaseService.DefaultCasterLevel;

    [ObservableProperty]
    private string _spellsStatus = NoSpellsText;

    /// <summary>Completes when the most recently started spell-file load has finished (also on failure).</summary>
    public Task SpellLoadTask { get; private set; } = Task.CompletedTask;

    partial void OnDefaultCasterLevelChanged(int value)
    {
        int clamped = Math.Clamp(value, SpellDatabaseService.MinCasterLevel, SpellDatabaseService.MaxCasterLevel);
        if (clamped != value)
        {
            DefaultCasterLevel = clamped; // re-enters this handler with the clamped value
            return;
        }

        if (_restoring)
        {
            return;
        }

        RequestSave();
        if (!string.IsNullOrWhiteSpace(SpellsFilePath))
        {
            _ = ReloadSpellsAsync();
        }
    }

    /// <summary>
    /// The level a spell's duration is worked out at when its caster's level is unknown (another player not seen in /who): your
    /// highest character's level, or 60 when none is known. Your own characters' durations use their own levels.
    /// </summary>
    private int FallbackCasterLevel()
    {
        int highest = _pipeline.Logs.Select(l => _pipeline.Roster.GetLevel(l.CharacterName, l.ServerName)).DefaultIfEmpty(0).Max();
        return highest > 0
            ? Math.Clamp(highest, SpellDatabaseService.MinCasterLevel, SpellDatabaseService.MaxCasterLevel)
            : SpellDatabaseService.DefaultCasterLevel;
    }

    [RelayCommand]
    private async Task BrowseSpellsAsync()
    {
        string? path = _dialogs.PickSpellsFile();
        if (path is null)
        {
            return;
        }

        SpellsFilePath = path;
        RequestSave();
        await ReloadSpellsAsync();
    }

    /// <summary>Parses the spell file on a background thread. Newer requests supersede older ones; only the latest is applied.</summary>
    private Task ReloadSpellsAsync()
    {
        int sequence = Interlocked.Increment(ref _spellLoadSequence);

        if (string.IsNullOrWhiteSpace(SpellsFilePath))
        {
            _pipeline.Spells.Clear();
            BuildSpellRows();
            SpellsStatus = NoSpellsText;
            return SpellLoadTask = Task.CompletedTask;
        }

        return SpellLoadTask = LoadSpellsAsync(SpellsFilePath, FallbackCasterLevel(), sequence);
    }

    private async Task LoadSpellsAsync(string path, int casterLevel, int sequence)
    {
        await _spellLoadGate.WaitAsync();
        try
        {
            if (sequence != Volatile.Read(ref _spellLoadSequence))
            {
                return; // superseded while waiting for the gate
            }

            SpellsStatus = $"Loading {Path.GetFileName(path)}…";
            SpellLoadReport report = await Task.Run(() => _pipeline.Spells.Load(path, casterLevel));

            if (sequence != Volatile.Read(ref _spellLoadSequence))
            {
                return;
            }

            BuildSpellRows();
            SpellsStatus = DescribeSpellLoad(report);
        }
        catch (Exception ex)
        {
            if (sequence == Volatile.Read(ref _spellLoadSequence))
            {
                SpellsStatus = $"Could not load spells: {ex.Message}";
            }
        }
        finally
        {
            _spellLoadGate.Release();
        }
    }

    private static string DescribeSpellLoad(SpellLoadReport report)
    {
        string layout = report.Format switch
        {
            SpellFileFormat.Split when report.StringsFilePath is not null => "split layout with spells_us_str.txt",
            SpellFileFormat.Split => "split layout, strings file missing",
            _ => "unified layout",
        };

        string text =
            $"Loaded {report.RowsKept:N0} duration spells ({layout}) at caster level {report.CasterLevel}; " +
            $"{report.RowsSkippedNoDuration:N0} instant or permanent spells skipped";

        if (report.RowsMalformed > 0)
        {
            text += $", {report.RowsMalformed:N0} unreadable rows ignored";
        }

        text += ".";
        return report.Warning is null ? text : $"{text} Warning: {report.Warning}";
    }

    // ─────────────────────────── refresh ───────────────────────────

    /// <summary>Call from a UI timer (about 4 times a second). Cheap when nothing changed.</summary>
    public void Refresh()
    {
        UpdateLiveStatus();
        DrainSharedOffers();
        UpdateTrackerActivity();
        UpdateAbilityActivity();
        UpdateKnownMobs();
        DrainRosterChanges();
        RefreshEdition();
        RefreshRelease82(DateTime.UtcNow);
        _pipeline.TimerPause.Tick(DateTime.UtcNow);
        RefreshRaidNight();
        RefreshReleaseFeatures();
        TickItemFoci(DateTime.UtcNow);
        TickDiscord(DateTime.UtcNow);
        ApplyOverlayFade();
        _ = TickUpdateCheck(DateTime.UtcNow);

        DpsSnapshot live = _pipeline.LiveDps.Snapshot;
        DpsSnapshot imported = _pipeline.HistoricalDps.Snapshot;
        if (live.Version == _lastLiveVersion && imported.Version == _lastHistoricalVersion)
        {
            return;
        }

        bool importChanged = imported.Version != _lastHistoricalVersion;
        _lastLiveVersion = live.Version;
        _lastHistoricalVersion = imported.Version;
        SyncEncounters(live, imported, importChanged);
    }

    /// <summary>One fight of the merged list and where it came from.</summary>
    private readonly record struct EncounterEntry(EncounterSnapshot Snapshot, bool Imported)
    {
        public long Key => ((Imported ? 1L : 0L) << 32) | (uint)Snapshot.Id;
    }

    /// <summary>True when a fight is on the server chosen in the Server box (a fight whose server is not known shows under every server).</summary>
    private static bool OnShownServer(EncounterSnapshot encounter, string? server) =>
        server is null || string.IsNullOrEmpty(encounter.Server) || encounter.Server.Equals(server, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shows the DPS tab's fights again after the Server box changed.</summary>
    private void RefilterEncounters() => SyncEncounters(_pipeline.LiveDps.Snapshot, _pipeline.HistoricalDps.Snapshot, rebuild: true);

    private const int EncounterRebuildThreshold = 50;
    private const double SameFightSeconds = 3.0;

    /// <summary>
    /// One list for every fight, newest first: the live ones (including the one in progress) and the imported ones. An imported fight
    /// that is also in the live list (same target, starting within a few seconds) is shown once, as the live one.
    /// </summary>
    private void SyncEncounters(DpsSnapshot live, DpsSnapshot imported, bool rebuild)
    {
        var entries = new List<EncounterEntry>(live.Active.Count + live.Completed.Count + imported.Active.Count + imported.Completed.Count);
        string? server = ToolsServerFilter;
        int all = 0;
        foreach (EncounterSnapshot encounter in live.All)
        {
            if (OnShownServer(encounter, server))
            {
                all++;
                if (InEncounterDates(encounter))
                {
                    entries.Add(new EncounterEntry(encounter, Imported: false));
                }
            }
        }

        LearnBossZones(live.All.Concat(imported.All));
        Dictionary<string, DateTime[]> liveStarts = live.All
            .GroupBy(e => e.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.StartTime).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (EncounterSnapshot encounter in imported.All)
        {
            if (liveStarts.TryGetValue(encounter.TargetName, out DateTime[]? starts)
                && starts.Any(s => Math.Abs((s - encounter.StartTime).TotalSeconds) <= SameFightSeconds))
            {
                continue;
            }

            if (OnShownServer(encounter, server))
            {
                all++;
                if (InEncounterDates(encounter))
                {
                    entries.Add(new EncounterEntry(encounter, Imported: true));
                }
            }
        }

        EncounterEntry[] ordered = entries
            .OrderByDescending(e => e.Snapshot.StartTime)
            .ThenBy(e => e.Imported)
            .ThenByDescending(e => e.Snapshot.Id)
            .ToArray();
        ShowEncounterCount(ordered.Length, all);

        if (rebuild || Math.Abs(ordered.Length - Encounters.Count) > EncounterRebuildThreshold)
        {
            // An import can produce thousands of encounters: swap in a fully built collection (one UI update).
            long? selectedKey = SelectedEncounter?.Key;
            var rebuilt = new ObservableCollection<EncounterRowViewModel>();
            foreach (EncounterEntry entry in ordered)
            {
                var row = new EncounterRowViewModel(entry.Snapshot.Id, entry.Imported);
                row.Update(entry.Snapshot);
                row.IsRaidBoss = IsRaidBoss(row.TargetName);
                rebuilt.Add(row);
            }

            Encounters = rebuilt;
            SelectedEncounter = selectedKey is long key ? rebuilt.FirstOrDefault(r => r.Key == key) : null;
        }
        else
        {
            ObservableSync.Sync(
                Encounters, ordered,
                snapshotKey: e => e.Key,
                viewModelKey: r => r.Key,
                create: e => new EncounterRowViewModel(e.Snapshot.Id, e.Imported),
                update: (row, e) =>
                {
                    row.Update(e.Snapshot);
                    row.IsRaidBoss = IsRaidBoss(row.TargetName);
                });

            if (SelectedEncounter is not null && !Encounters.Contains(SelectedEncounter))
            {
                SelectedEncounter = null; // dropped from the capped live history
            }
        }

        MarkPersonalBestRows();
        if (SelectedEncounter is null && Encounters.Count > 0)
        {
            SelectedEncounter = Encounters[0]; // newest first
        }
        else
        {
            RefreshParticipants();
        }
    }

    /// <summary>"\nDamage shield from X: 1,240 in 12 hits, from 0:03 to 0:41 into the fight (38s)." — how long the shield was up before it was dispelled; empty when there was none.</summary>
    public static string ShieldSummaryText(EncounterSnapshot encounter)
    {
        if (encounter.DamageShield is not { } shield)
        {
            return string.Empty;
        }

        static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
        return $"\nDamage shield from {encounter.TargetName}: {shield.Total:N0} in {shield.Hits:N0} hit{(shield.Hits == 1 ? string.Empty : "s")}, " +
               $"from {Clock(shield.FirstSeconds)} to {Clock(shield.LastSeconds)} into the fight ({shield.ActiveSeconds:0}s).";
    }

    /// <summary>"\nDoTs: Gnorm 9 (3 ticks)" — damage taken from DoTs whose caster the log did not name; empty when none.</summary>
    public static string DotSummaryText(EncounterSnapshot encounter)
    {
        List<DamageTakenSnapshot> hit = encounter.TakenRows.Where(t => t.DotTicks > 0)
            .OrderByDescending(t => t.DotDamage).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        return hit.Count == 0
            ? string.Empty
            : "\nDoTs: " + string.Join(", ", hit.Select(t => $"{t.Name} {t.DotDamage:N0} ({t.DotTicks:N0} ticks)"));
    }

    /// <summary>"\nRampage: Teek 12 (3,400), Abundancei 5 (900)" — who the target's rampages went to, most first; empty when none.</summary>
    public static string RampageSummaryText(EncounterSnapshot encounter)
    {
        List<DamageTakenSnapshot> hit = encounter.TakenRows.Where(t => t.Rampages > 0)
            .OrderByDescending(t => t.Rampages).ThenByDescending(t => t.RampageDamage).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        return hit.Count == 0
            ? string.Empty
            : "\nRampage: " + string.Join(", ", hit.Select(t => $"{t.Name} {t.Rampages:N0} ({t.RampageDamage:N0})"));
    }

    private void RefreshParticipants()
    {
        EncounterSnapshot? encounter = CurrentEncounter();
        bool combined = encounter?.CombinedCount > 1;
        if (encounter is null)
        {
            Participants.Clear();
            DamageTaken.Clear();
            DamageTakenSummary = string.Empty;
            ShowLastSeconds(null);
            ShowTimeline(null);
            UpdateEncounterDetails(null);
            EncounterSummary = NoEncounterText;
            return;
        }

        IReadOnlyList<ParticipantSnapshot> ranked = encounter.WithClassesOnServer(ClassLabelOnServer).Participants;
        long topDealt = ranked.Count > 0 ? ranked.Max(p => p.TotalDamage) : 0;
        for (int i = 0; i < ranked.Count; i++)
        {
            if (i >= Participants.Count)
            {
                Participants.Add(new ParticipantRowViewModel());
            }

            Participants[i].Update(i + 1, ranked[i], topDealt);
            Participants[i].Benchmark = combined ? string.Empty : BenchmarkBadge(encounter, ranked[i]);
        }

        IReadOnlyList<DamageTakenSnapshot> taken = encounter.TakenRows;
        long topTaken = taken.Count > 0 ? taken.Max(t => t.TotalDamage) : 0;
        for (int i = 0; i < taken.Count; i++)
        {
            if (i >= DamageTaken.Count)
            {
                DamageTaken.Add(new DamageTakenRowViewModel());
            }

            DamageTaken[i].Update(i + 1, taken[i], topTaken);
        }

        while (DamageTaken.Count > taken.Count)
        {
            DamageTaken.RemoveAt(DamageTaken.Count - 1);
        }

        ShowLastSeconds(encounter);
        DamageTakenSummary = (combined ? $"Damage taken: {encounter.TotalDamageTaken:N0}" : $"Damage taken from {encounter.TargetName}: {encounter.TotalDamageTaken:N0}") + ShieldSummaryText(encounter) + RampageSummaryText(encounter) + DotSummaryText(encounter);
        ShowTimeline(encounter);

        UpdateEncounterDetails(encounter);

        while (Participants.Count > ranked.Count)
        {
            Participants.RemoveAt(Participants.Count - 1);
        }

        if (combined)
        {
            EncounterSummary = CombinedFights.Header(encounter);
            return;
        }

        double groupDps = encounter.TotalDamage / Math.Max(1.0, encounter.DurationSeconds);
        EncounterSummary =
            $"{encounter.TargetName} — {encounter.StartTime:yyyy-MM-dd HH:mm:ss}, {OverlayFormat.Duration(encounter.DurationSeconds)}, " +
            $"{encounter.TotalDamage:N0} damage, group DPS {groupDps:N0}" + (encounter.IsActive ? " (in progress)" : string.Empty) +
            (RegenFor(encounter.TargetName) is { } regen ? " · " + regen.Describe() : string.Empty);
    }

    private void UpdateLiveStatus()
    {
        bool running = _pipeline.IsLiveRunning;
        IsLiveRunning = running;

        LiveStatusText = running
            ? $"Live: watching {LogsText(Logs.Count)}, DPS from all of them"
            : "Stopped";
    }

    // ─────────────────────── tab 4: raid boss timers ───────────────────────

    private const int MaxRaidSpellResults = 200;
    private const string RaidSpellsNotLoaded =
        "Load spells_us.txt on the Settings tab to search boss spells, or type the spell name and the log text by hand.";

    public ObservableCollection<RaidTimerRuleViewModel> RaidRules { get; } = new();

    public IReadOnlyList<OverlayCategoryOption> CategoryOptions => OverlayCategoryOption.All;

    /// <summary>The boss the new rule applies to. Empty means any boss.</summary>
    [ObservableProperty]
    private string _raidMobName = string.Empty;

    [ObservableProperty]
    private string _raidSpellSearchText = string.Empty;

    /// <summary>Matches from <c>AllSpellDefinitions</c> (instant boss AEs included), at most 200.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpellDefinition> _raidSpellResults = Array.Empty<SpellDefinition>();

    [ObservableProperty]
    private SpellDefinition? _selectedRaidSpell;

    [ObservableProperty]
    private string _raidSpellSearchStatus = RaidSpellsNotLoaded;

    [ObservableProperty]
    private string _raidSpellName = string.Empty;

    /// <summary>Filled in from the picked spell's landing message; can be edited by hand.</summary>
    [ObservableProperty]
    private string _raidMatchText = string.Empty;

    [ObservableProperty]
    private int _raidRecastSeconds = 30;

    [ObservableProperty]
    private TimerOverlayCategory _raidCategory = TimerOverlayCategory.RaidTriggers;

    /// <summary>Spoken the moment the trigger starts.</summary>
    [ObservableProperty]
    private string _raidStartTtsText = string.Empty;

    /// <summary>Spoken when 10 seconds remain before the next cast.</summary>
    [ObservableProperty]
    private string _raidWarningTtsText = string.Empty;

    [ObservableProperty]
    private string _raidBuilderStatus = string.Empty;

    /// <summary>The saved trigger being edited in the builder, or null when the builder is making a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingRaidRule))]
    [NotifyPropertyChangedFor(nameof(RaidSaveButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CancelRaidEditCommand))]
    private RaidTimerRuleViewModel? _editingRaidRule;

    private int _editingRaidSpellId;

    public bool IsEditingRaidRule => EditingRaidRule is not null;

    public string RaidSaveButtonText => IsEditingRaidRule ? "Save Changes" : "Add Raid Trigger";

    partial void OnRaidSpellSearchTextChanged(string value) => UpdateRaidSpellResults();

    partial void OnSelectedRaidSpellChanged(SpellDefinition? value)
    {
        if (value is null)
        {
            return;
        }

        RaidSpellName = value.Name;
        RaidMatchText = RaidBossTimerRule.SuggestMatchText(value);
    }

    /// <summary>Builds a rule from the builder's fields, or explains in <see cref="RaidBuilderStatus"/> what is missing.</summary>
    private bool TryBuildRaidRule(out RaidBossTimerRule rule)
    {
        rule = new RaidBossTimerRule();

        string spellName = RaidSpellName?.Trim() ?? string.Empty;
        if (spellName.Length == 0)
        {
            RaidBuilderStatus = "Pick a spell from the list, or type its name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(RaidMatchText))
        {
            RaidBuilderStatus = "Enter the log text to look for (it is filled in with the casting line when you pick a spell).";
            return false;
        }

        if (RaidRecastSeconds < 1)
        {
            RaidBuilderStatus = "The recast time must be at least 1 second.";
            return false;
        }

        rule = new RaidBossTimerRule
        {
            MobName = RaidMobName?.Trim() ?? string.Empty,
            SpellId = SelectedRaidSpell?.SpellId ?? _editingRaidSpellId,
            SpellName = spellName,
            MatchText = RaidMatchText,
            RecastSeconds = RaidRecastSeconds,
            OverlayCategory = RaidCategory,
            StartTtsText = RaidStartTtsText?.Trim() ?? string.Empty,
            WarningTtsText = RaidWarningTtsText?.Trim() ?? string.Empty,
        };

        return true;
    }

    [RelayCommand]
    private void AddRaidTrigger()
    {
        if (!TryBuildRaidRule(out RaidBossTimerRule rule))
        {
            return;
        }

        if (EditingRaidRule is { } editing)
        {
            // Saving an edit replaces the trigger in place: same id, same on/off state, same position in the list.
            rule.Id = editing.Id;
            rule.IsEnabled = editing.IsEnabled;
            int index = RaidRules.IndexOf(editing);
            if (index >= 0)
            {
                RaidRules[index] = CreateRaidRule(rule);
            }
            else
            {
                RaidRules.Add(CreateRaidRule(rule));
            }

            OnRaidRulesChanged();
            ResetRaidBuilderForNext();
            EditingRaidRule = null;
            RaidBuilderStatus = $"Saved changes to {rule.Label}.";
            return;
        }

        RaidRules.Add(CreateRaidRule(rule));
        OnRaidRulesChanged();
        RaidBuilderStatus = $"Added {rule.Label}: every {rule.RecastSeconds}s in the {rule.OverlayCategory.DisplayName()} overlay.";

        // Ready for the next ability of the same boss: keep the boss, recast time and overlay, clear the spell part.
        ResetRaidBuilderForNext();
    }

    private void ResetRaidBuilderForNext()
    {
        SelectedRaidSpell = null;
        RaidSpellSearchText = string.Empty;
        RaidSpellName = string.Empty;
        RaidMatchText = string.Empty;
        RaidStartTtsText = string.Empty;
        RaidWarningTtsText = string.Empty;
        _editingRaidSpellId = 0;
    }

    /// <summary>Loads a saved trigger into the builder; "Save Changes" then replaces it.</summary>
    [RelayCommand]
    private void EditRaidTrigger(RaidTimerRuleViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        RaidBossTimerRule rule = row.ToRule();
        SelectedRaidSpell = null;
        RaidSpellSearchText = string.Empty;
        _editingRaidSpellId = rule.SpellId;
        RaidMobName = rule.MobName;
        RaidSpellName = rule.SpellName;
        RaidMatchText = rule.MatchText;
        RaidRecastSeconds = rule.RecastSeconds;
        RaidCategory = rule.OverlayCategory;
        RaidStartTtsText = rule.StartTtsText;
        RaidWarningTtsText = rule.WarningTtsText;
        EditingRaidRule = row;
        RaidBuilderStatus = $"Editing {rule.Label}. Change what you need, then click Save Changes.";
    }

    [RelayCommand(CanExecute = nameof(IsEditingRaidRule))]
    private void CancelRaidEdit()
    {
        ResetRaidBuilderForNext();
        EditingRaidRule = null;
        RaidBuilderStatus = "Edit cancelled.";
    }

    [RelayCommand]
    private void RemoveRaidTrigger(RaidTimerRuleViewModel? row)
    {
        if (row is not null && RaidRules.Remove(row))
        {
            if (ReferenceEquals(row, EditingRaidRule))
            {
                ResetRaidBuilderForNext();
                EditingRaidRule = null;
            }

            OnRaidRulesChanged();
            RaidBuilderStatus = $"Removed {row.MobText}: {row.SpellName}.";
        }
    }

    private RaidTimerRuleViewModel CreateRaidRule(RaidBossTimerRule rule) => new(rule, _ => OnRaidRulesChanged());

    private void OnRaidRulesChanged()
    {
        _pipeline.RaidBosses.SetRules(RaidRules.Select(r => r.ToRule()));
        RequestSave();
    }

    private void UpdateRaidSpellResults()
    {
        IReadOnlyList<SpellDefinition> all = _pipeline.Spells.Database.AllSpellDefinitions;
        if (all.Count == 0)
        {
            RaidSpellResults = Array.Empty<SpellDefinition>();
            RaidSpellSearchStatus = RaidSpellsNotLoaded;
            return;
        }

        string term = RaidSpellSearchText?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            RaidSpellResults = Array.Empty<SpellDefinition>();
            RaidSpellSearchStatus = $"{all.Count:N0} spells. Type a name or spell id to search.";
            return;
        }

        List<SpellDefinition> matches = all
            .Where(d => d.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || d.SpellId.ToString(CultureInfo.InvariantCulture) == term)
            .Take(MaxRaidSpellResults)
            .ToList();

        RaidSpellResults = matches;
        RaidSpellSearchStatus = matches.Count == MaxRaidSpellResults
            ? $"Showing the first {MaxRaidSpellResults} matches: type more of the name to narrow it down."
            : $"{matches.Count} match(es).";
    }

    // ─────────────────────────────── theme ───────────────────────────────

    private ThemeConfig _theme = new();

    /// <summary>The collapsible Theme bar under the toolbar.</summary>
    [ObservableProperty]
    private bool _isThemeBarOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsObsidianSlateSelected))]
    [NotifyPropertyChangedFor(nameof(IsVeliousFrostSelected))]
    [NotifyPropertyChangedFor(nameof(IsKunarkEmberSelected))]
    [NotifyPropertyChangedFor(nameof(IsCustomThemeSelected))]
    private string _themePresetName = ThemePresets.ObsidianSlateName;

    public bool IsObsidianSlateSelected => ThemePresetName == ThemePresets.ObsidianSlateName;
    public bool IsVeliousFrostSelected => ThemePresetName == ThemePresets.VeliousFrostName;
    public bool IsKunarkEmberSelected => ThemePresetName == ThemePresets.KunarkEmberName;
    public bool IsCustomThemeSelected => ThemePresetName == ThemePresets.CustomName;

    /// <summary>One swatch + hex box per configurable color.</summary>
    public ObservableCollection<ThemeColorEditorViewModel> ThemeColors { get; } = new();

    /// <summary>A copy of the theme choice that is being saved.</summary>
    public ThemeConfig Theme => _theme.Clone();

    /// <summary>The colors in effect right now.</summary>
    public ThemePalette ActivePalette => ThemePresets.Resolve(_theme);

    [RelayCommand]
    private void ToggleThemeBar() => IsThemeBarOpen = !IsThemeBarOpen;

    /// <summary>Switches to a preset ("ObsidianSlate", "VeliousFrost", "KunarkEmber") or back to "Custom". Applies immediately.</summary>
    [RelayCommand]
    private void SelectThemePreset(string? presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        _theme.PresetName = ThemePresets.Canonical(presetName);
        ApplyTheme(refreshEditors: true);
        RequestSave();
    }

    /// <summary>
    /// A hex box was edited to a valid color. Editing while a preset is active starts a custom theme from that preset's colors;
    /// the custom colors are remembered while presets are used.
    /// </summary>
    private void OnThemeColorEdited(ThemeColorKey key, string hex)
    {
        if (_theme.PresetName != ThemePresets.CustomName)
        {
            _theme.SetCustomPalette(ThemePresets.Resolve(_theme));
            _theme.PresetName = ThemePresets.CustomName;
        }

        _theme.SetCustomPalette(_theme.GetCustomPalette().With(key, hex));
        ApplyTheme(refreshEditors: false);
        RequestSave();
    }

    private void ApplyTheme(bool refreshEditors)
    {
        ThemePalette palette = ThemePresets.Resolve(_theme);
        _themeService.Apply(palette);
        ThemePresetName = _theme.PresetName;

        if (refreshEditors)
        {
            foreach (ThemeColorEditorViewModel editor in ThemeColors)
            {
                editor.SetFromTheme(palette.Get(editor.Key));
            }
        }
    }

    // ───────────────────── settings: restore / save ─────────────────────

    private void Restore(AppSettings settings)
    {
        _restoring = true;
        try
        {
            foreach ((string name, WindowPlacementSetting placement) in settings.WindowPlacements ?? new Dictionary<string, WindowPlacementSetting>())
            {
                if (placement is { IsUsable: true })
                {
                    _windowPlacements[name] = placement;
                }
            }

            foreach (WatchedLogSettings saved in settings.WatchedLogs)
            {
                if (string.IsNullOrWhiteSpace(saved.FilePath))
                {
                    continue;
                }

                WatchedLogConfig log = Logs.AddFile(saved.FilePath);
                log.IsTriggerEnabled = saved.IsTriggerEnabled;
                log.TankChoice = saved.IsTank;
                log.ChChainPosition = saved.ChChainPosition;
                log.IsRetired = saved.IsRetired;
            }

            EqFolderPath = settings.EqFolderPath ?? string.Empty;
            int newCharacters = DiscoverCharacters(); // characters created since the last run show up without a manual refresh
            DescribeEqFolderAtStartup(newCharacters);

            HistoricalDaysLimit = settings.HistoricalDaysLimit;
            RestoreTabs(settings);
            SyncModeIndex = SyncModeFromText(settings.SyncMode);
            SyncCode = settings.SyncCode ?? string.Empty;
            SyncPcName = string.IsNullOrWhiteSpace(settings.SyncPcName) ? Environment.MachineName : settings.SyncPcName;
            SyncHostAddress = settings.SyncHostAddress ?? string.Empty;
            RestoreOverlayProfiles(settings);
            IsCooldownTrackerEnabled = settings.IsCooldownTrackerEnabled;
            _pipeline.Cooldowns.IsEnabled = IsCooldownTrackerEnabled;
            CheckForUpdates = settings.CheckForUpdates;
            UpdateCheckHours = settings.UpdateCheckHours;
            RestoreSharedDrops(settings);
            RestoreLeveling(settings);
            RestoreRelease80(settings);
            RestoreTells(settings);
            _skippedUpdateVersion = settings.SkippedUpdateVersion ?? string.Empty;

            ChChainSettings chain = (settings.ChChain ?? new ChChainSettings()).Sanitized();
            ChainPositionTagFormat = chain.PositionTagFormat;
            ChainCueKeyword = chain.CueKeyword;
            ChainLateThresholdSeconds = chain.LateThresholdSeconds;
            PushChainSettings();

            SubscribeToLogs();
            RebuildSortedCharacters();
            SyncChainPositions();

            _groupTriggersByFolder = string.Equals(settings.TriggerGrouping, "Folders", StringComparison.OrdinalIgnoreCase);
            foreach (TriggerRule rule in settings.Triggers)
            {
                Rules.Add(CreateRule(rule));
            }

            ApplyTriggerGrouping();
            RestoreLook(settings);
            _backfillFrom = settings.BackfillFromLocal;
            _backfillKey = settings.BackfillLogsKey ?? string.Empty;

            SelectedRule = Rules.FirstOrDefault();
            ApplyTriggers();

            IsAudioMuted = settings.IsAudioMuted;
            _audio.IsEnabled = !settings.IsAudioMuted;
            ReadTellsAloud = settings.ReadTellsAloud;
            _pipeline.Tells.IsEnabled = settings.ReadTellsAloud;
            ShowAllBardSongs = settings.ShowAllBardSongs;
            _pipeline.Songs.ShowAllBards = settings.ShowAllBardSongs;
            CharmWatchEnabled = settings.CharmWatchEnabled;
            _pipeline.Charm.IsEnabled = settings.CharmWatchEnabled;
            RestoreEdition(settings);
            SafeZonesText = string.Join(Environment.NewLine, settings.NonCombatZones ?? TimerPauseService.DefaultSafeZones.ToList());
            RestoreDurationProfiles(settings.DurationProfiles);
            OverlayIdlePercent = settings.OverlayIdlePercent;
            DpsHideSeconds = settings.DpsHideSeconds;
            RestoreAttackColors(settings.AttackColors);

            EncounterTimeoutSeconds = settings.EncounterTimeoutSeconds;
            _pipeline.EncounterTimeout = TimeSpan.FromSeconds(EncounterTimeoutSeconds);

            RestoreTrackedSpells(settings);
            RestoreTrackedAbilities(settings);
            RestoreRoster(settings);
            RestoreAdditionalCharacters(settings); // after the roster: older entries are filed under a server first
            SpellsFilePath = settings.SpellsFilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(SpellsFilePath) && EqDirectoryScanner.HasSpellsFile(EqFolderPath))
            {
                SpellsFilePath = EqDirectoryScanner.GetSpellsFilePath(EqFolderPath); // the folder was set before spells were loaded
            }

            DefaultCasterLevel = settings.DefaultCasterLevel;

            foreach (RaidBossTimerRule saved in settings.RaidBossTimers)
            {
                RaidRules.Add(CreateRaidRule(saved.Clone()));
            }

            _pipeline.RaidBosses.SetRules(RaidRules.Select(r => r.ToRule()));

            _theme = ThemePresets.Sanitize((settings.Theme ?? new ThemeConfig()).Clone());
            ThemePalette palette = ThemePresets.Resolve(_theme);
            foreach (ThemeColorKey key in ThemeColorKeyExtensions.All)
            {
                ThemeColors.Add(new ThemeColorEditorViewModel(key, palette.Get(key), OnThemeColorEdited));
            }

            ApplyTheme(refreshEditors: false);

            foreach (OverlayKind kind in OverlayKindExtensions.All)
            {
                _overlays.Apply(kind, GetOverlaySettings(settings, kind));
            }

            RestoreCustomOverlays(settings.CustomOverlays);

            IsBuffsOverlayVisible = settings.BuffsOverlay.IsVisible;
            IsDebuffsOverlayVisible = settings.DebuffsOverlay.IsVisible;
            IsSongsOverlayVisible = settings.SongsOverlay.IsVisible;
            IsRaidTriggersOverlayVisible = settings.RaidTriggersOverlay.IsVisible;
            IsChChainOverlayVisible = settings.ChChainOverlay.IsVisible;
            _pipeline.ChChain.UseChChainOverlay = IsChChainOverlayVisible; // not raised when the value equals the default
            IsDpsOverlayVisible = settings.DpsOverlay.IsVisible;
            IsTellsOverlayVisible = settings.TellsOverlay.IsVisible;
            IsMobCardOverlayVisible = settings.MobCardOverlay.IsVisible;
            IsFctDamageOverlayVisible = settings.FctDamageOverlay.IsVisible;
            IsFctHealsOverlayVisible = settings.FctHealsOverlay.IsVisible;
            FctDamageTextSize = settings.FctDamageTextSize;
            FctHealsTextSize = settings.FctHealsTextSize;
            RestoreFloatingColors(settings.FloatingColors);
            FctMergeHits = settings.FctMergeHits;
            FctShowMisses = settings.FctShowMisses;
            FctFont = string.IsNullOrWhiteSpace(settings.FctFont) ? "Segoe UI" : settings.FctFont;
            FctWeight = string.IsNullOrWhiteSpace(settings.FctWeight) ? "SemiBold" : settings.FctWeight;
            _characterAccounts = new Dictionary<string, string>(settings.CharacterAccounts ?? new(), StringComparer.OrdinalIgnoreCase);
            _savedAccountColors = new Dictionary<string, string>(settings.AccountColors ?? new(), StringComparer.OrdinalIgnoreCase);
            TellToast = settings.TellToast;
            LevelUpToast = settings.LevelUpToast;
            FollowAlert = settings.FollowAlert;
            PetBuffAlert = settings.PetBuffAlert;
            TellsOverlayNotices = settings.TellsOverlayNotices;
            RestoreRelease82(settings);
            RebuildAccountColors();
            AggroAlert = settings.AggroAlert;
            RangeAlert = settings.RangeAlert;
            ShowFightProgress = settings.ShowFightProgress;
            RestoreDiscord(settings);
            TakeHoldAlert = settings.TakeHoldAlert;
            ResistStreakAlert = settings.ResistStreakAlert;
            FeignDeathAlert = settings.FeignDeathAlert;
            RootMezAlert = settings.RootMezAlert;
            QuietLogAlert = settings.QuietLogAlert;
            LearnedBossTimers = settings.LearnedBossTimers;
            StartWithWindows = settings.StartWithWindows;
            // The Tells and mob card overlays are newer than most saved settings, so they follow the others' lock rather than deciding it.
            AreOverlaysLocked = OverlayKindExtensions.All.Where(kind => !FollowsLock(kind)).All(kind => GetOverlaySettings(settings, kind).IsLocked);
            foreach (OverlayKind follower in OverlayKindExtensions.All.Where(FollowsLock))
            {
                OverlaySettings saved = GetOverlaySettings(settings, follower);
                if (AreOverlaysLocked && !saved.IsLocked)
                {
                    _overlays.Apply(follower, saved with { IsLocked = true });
                }
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private static OverlaySettings GetOverlaySettings(AppSettings settings, OverlayKind kind) => kind switch
    {
        OverlayKind.Buffs => settings.BuffsOverlay,
        OverlayKind.Debuffs => settings.DebuffsOverlay,
        OverlayKind.Songs => settings.SongsOverlay,
        OverlayKind.RaidTriggers => settings.RaidTriggersOverlay,
        OverlayKind.ChChain => settings.ChChainOverlay,
        OverlayKind.Tells => settings.TellsOverlay,
        OverlayKind.MobCard => settings.MobCardOverlay,
        OverlayKind.FctDamage => settings.FctDamageOverlay,
        OverlayKind.FctHeals => settings.FctHealsOverlay,
        _ => settings.DpsOverlay,
    };

    /// <summary>Snapshot of everything that is persisted, including the overlays' current bounds and lock state.</summary>
    public AppSettings BuildSettings()
    {
        AppSettings settings = BuildCoreSettings();
        SaveRelease82(settings);
        SaveDiscord(settings);
        SaveEdition(settings);
        return settings;
    }

    // Hooks an edition fills in with its own settings and features (they do nothing in this one).
    partial void InitializeEdition();

    partial void RestoreEdition(AppSettings settings);

    partial void SaveEdition(AppSettings settings);

    partial void RefreshEdition();

    partial void OnEditionOverlaysChanged();

    private AppSettings BuildCoreSettings() => new()
    {
        WatchedLogs = Logs.Select(l => new WatchedLogSettings
        {
            FilePath = l.FilePath,
            CharacterName = l.CharacterName,
            IsTriggerEnabled = l.IsTriggerEnabled,
            IsTank = l.TankChoice,
            ChChainPosition = l.ChChainPosition,
            IsRetired = l.IsRetired,
        }).ToList(),
        Triggers = Rules.Select(r => r.ToRule()).ToList(),
        TriggerGrouping = GroupTriggersByFolder ? "Folders" : "Auto",
        UiScalePercent = Math.Round(UiScalePercent),
        CompactMode = CompactMode,
        MinimizeToTray = MinimizeToTray,
        LastCrashSeenUtc = _lastCrashSeenUtc,
        BackfillFromLocal = _backfillFrom,
        BackfillLogsKey = _backfillKey,
        EncounterTimeoutSeconds = EncounterTimeoutSeconds,
        IsAudioMuted = IsAudioMuted,
        ReadTellsAloud = ReadTellsAloud,
        ShowAllBardSongs = ShowAllBardSongs,
        CharmWatchEnabled = CharmWatchEnabled,
        NonCombatZones = SafeZoneList(),
        DurationProfiles = DurationProfileSettings(),
        OverlayIdlePercent = OverlayIdlePercent,
        DpsHideSeconds = DpsHideSeconds,
        AttackColors = AttackColorSettings(),
        BuffsOverlay = _overlays.Capture(OverlayKind.Buffs),
        DebuffsOverlay = _overlays.Capture(OverlayKind.Debuffs),
        SongsOverlay = _overlays.Capture(OverlayKind.Songs),
        RaidTriggersOverlay = _overlays.Capture(OverlayKind.RaidTriggers),
        ChChainOverlay = _overlays.Capture(OverlayKind.ChChain),
        DpsOverlay = _overlays.Capture(OverlayKind.Dps),
        TellsOverlay = _overlays.Capture(OverlayKind.Tells),
        MobCardOverlay = _overlays.Capture(OverlayKind.MobCard),
        FctDamageOverlay = _overlays.Capture(OverlayKind.FctDamage),
        FctHealsOverlay = _overlays.Capture(OverlayKind.FctHeals),
        CustomOverlays = CustomOverlaySettings(),
        FctDamageTextSize = FctDamageTextSize,
        FctHealsTextSize = FctHealsTextSize,
        FloatingColors = FloatingColors.ToDictionary(c => c.Key, c => c.Hex),
        FctMergeHits = FctMergeHits,
        FctShowMisses = FctShowMisses,
        FctFont = FctFont,
        FctWeight = FctWeight,
        CharacterAccounts = new Dictionary<string, string>(_characterAccounts, StringComparer.OrdinalIgnoreCase),
        AccountColors = AccountColorsToSave(),
        TellToast = TellToast,
        LevelUpToast = LevelUpToast,
        FollowAlert = FollowAlert,
        PetBuffAlert = PetBuffAlert,
        TellsOverlayNotices = TellsOverlayNotices,
        AggroAlert = AggroAlert,
        RangeAlert = RangeAlert,
        ShowFightProgress = ShowFightProgress,
        TakeHoldAlert = TakeHoldAlert,
        ResistStreakAlert = ResistStreakAlert,
        FeignDeathAlert = FeignDeathAlert,
        RootMezAlert = RootMezAlert,
        QuietLogAlert = QuietLogAlert,
        LearnedBossTimers = LearnedBossTimers,
        StartWithWindows = StartWithWindows,
        TellKeepDays = TellKeepDays,
        RaidBossTimers = RaidRules.Select(r => r.ToRule()).ToList(),
        Theme = _theme.Clone(),
        SpellsFilePath = SpellsFilePath,
        DefaultCasterLevel = DefaultCasterLevel,
        TrackedSpellIds = new HashSet<int>(TrackedSpells.Where(r => r.IsEnabled).Select(r => r.SpellId)),
        TrackedSpells = TrackedSpells.Select(r => r.ToSetting()).ToList(),
        TrackedAbilities = TrackedAbilities.Select(r => r.ToSetting()).ToList(),
        Roster = _pipeline.Roster.ToSettings(),
        EqFolderPath = EqFolderPath,
        AdditionalMonitoredCharacters = AdditionalCharacters.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        AdditionalCharacterServers = AdditionalCharacterRows.Select(r => new AdditionalCharacterSetting { Name = r.Name, Server = r.Server }).ToList(),
        WindowPlacements = new Dictionary<string, WindowPlacementSetting>(_windowPlacements, StringComparer.OrdinalIgnoreCase),
        HistoricalDaysLimit = HistoricalDaysLimit,
        LastTabIndex = SelectedMainTab,
        LastTriggersTab = SelectedTriggersTab,
        LastLootTab = SelectedLootTab,
        LastRaidTab = SelectedRaidTab,
        LastProgressTab = SelectedProgressTab,
        LastLogsTab = SelectedLogsTab,
        TabLayout = AppSettings.CurrentTabLayout,
        OverlayProfiles = _overlayProfiles.ToList(),
        AutoSwitchOverlayProfiles = AutoSwitchOverlayProfiles,
        SyncMode = SyncModeToText(SyncModeIndex),
        SyncCode = SyncCode,
        SyncPcName = SyncPcName,
        SyncHostAddress = SyncHostAddress,
        ChChain = BuildChainSettings(),
        IsCooldownTrackerEnabled = IsCooldownTrackerEnabled,
        CheckForUpdates = CheckForUpdates,
        UpdateCheckHours = UpdateCheckHours,
        SkippedUpdateVersion = _skippedUpdateVersion,
        UseSharedDrops = UseSharedDrops,
        PoolNormalLootDrops = PoolNormalLootDrops,
        ShareMyDrops = ShareMyDrops,
        UseSharedLeveling = UseSharedLeveling,
        ShareServerFirsts = ShareServerFirsts,
        UseSharedMobStats = UseSharedMobStats,
        ShareMyMobStats = ShareMyMobStats,
        MobStatsSharedThrough = new Dictionary<string, long>(_mobStatsSharedThrough, StringComparer.OrdinalIgnoreCase),
        ShareMyLeveling = ShareMyLeveling,
        LevelingId = _levelingId,
        LevelingKey = _levelingKey,
        LevelingExpansions = new Dictionary<string, List<int>>(_levelingExpansions, StringComparer.OrdinalIgnoreCase),
        LevelingGroup = LevelingGroup,
        SharedDropsId = _sharedDropsId,
        SharedDropsKey = _sharedDropsKey,
        SharingDefaultsVersion = Core.Settings.AppSettings.CurrentSharingDefaults, // so switching sharing off stays off
        CountMeIn = CountMeIn,
        AllLogsLoadOffered = _allLogsLoadOffered,
        PlayerCountId = _playerCountId,
        PlayerCountDay = _playerCountDay,
    };

    private readonly Dictionary<string, WindowPlacementSetting> _windowPlacements = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the named window was last (see <see cref="WindowPlacementSetting"/> for the names), or null to open it where it opens by default.</summary>
    public WindowPlacementSetting? GetWindowPlacement(string window) => _windowPlacements.GetValueOrDefault(window);

    /// <summary>Remembers where the named window is now and saves it with the settings. Unusable sizes are ignored.</summary>
    public void SetWindowPlacement(string window, WindowPlacementSetting placement)
    {
        if (string.IsNullOrWhiteSpace(window) || placement is not { IsUsable: true }
            || (_windowPlacements.TryGetValue(window, out WindowPlacementSetting? known) && known == placement))
        {
            return;
        }

        _windowPlacements[window] = placement;
        RequestSave();
    }

    private void RequestSave()
    {
        if (_restoring || _isShutDown)
        {
            return;
        }

        _settingsService.QueueSave(BuildSettings());
    }

    private void OnPlacementChanged(object? sender, EventArgs e) => RequestSave();

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeToLogs();
        RefreshOwnCharacters();
        RebuildSortedCharacters();
        SyncChainPositions();
        UpdateLiveStatus();
        RequestSave();
    }

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateLiveStatus();
        if (e.PropertyName is nameof(WatchedLogConfig.IsTank) or nameof(WatchedLogConfig.ClassName))
        {
            RefreshOwnCharacters();
        }

        if (e.PropertyName == nameof(WatchedLogConfig.ChChainPosition))
        {
            SyncChainPositions();
        }

        RequestSave();
    }

    /// <summary>Keeps one PropertyChanged subscription per watched log (the collection only reports adds and removes).</summary>
    private void SubscribeToLogs()
    {
        HashSet<WatchedLogConfig> current = Logs.ToHashSet();

        foreach (WatchedLogConfig gone in _subscribedLogs.Where(l => !current.Contains(l)).ToList())
        {
            gone.PropertyChanged -= OnLogPropertyChanged;
            _subscribedLogs.Remove(gone);
        }

        foreach (WatchedLogConfig log in current)
        {
            if (_subscribedLogs.Add(log))
            {
                log.PropertyChanged += OnLogPropertyChanged;
            }
        }
    }

    // ───────────────────────────── shutdown ─────────────────────────────

    /// <summary>
    /// Final save (while the overlay windows still exist), then stop live monitoring, close both overlays, dispose the
    /// audio service and the settings service. Safe to call more than once; call on the UI thread.
    /// </summary>
    public void Shutdown()
    {
        if (_isShutDown)
        {
            return;
        }

        try
        {
            _settingsService.QueueSave(BuildSettings());
            _settingsService.Flush();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Settings could not be saved: {ex.Message}";
        }

        _isShutDown = true;
        _pipeline.TimerPause.Save();
        _pipeline.Drops.Flush();
        _pipeline.Progress.Flush();
        _pipeline.TellInbox.Flush();
        _pipeline.MobHits.Flush();
        StopSync();

        Logs.CollectionChanged -= OnLogsChanged;
        _overlays.PlacementChanged -= OnPlacementChanged;
        _pipeline.SharedTriggers.Offered -= OnSharedOffered;
        _pipeline.Roster.Changed -= OnRosterChanged;
        foreach (WatchedLogConfig log in _subscribedLogs)
        {
            log.PropertyChanged -= OnLogPropertyChanged;
        }

        _subscribedLogs.Clear();

        try
        {
            _importCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the import just finished
        }

        _overlays.CloseAll();

        try
        {
            // On the pool so the UI synchronization context can never deadlock the wait.
            Task.Run(() => _pipeline.StopLiveAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stopping live monitoring failed: {ex.Message}";
        }

        _audio.Dispose();
        _settingsService.Dispose();
    }
}
