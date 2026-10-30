using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core;
using EQOverwatch.Core.Discovery;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

// The Characters tab and the Settings tab's folder, history, CH chain and cooldown sections.
public sealed partial class MainViewModel
{
    // ───────────────────────────── identity ─────────────────────────────

    /// <summary>"EQ Overwatch".</summary>
    public string AppName => AppInfo.Name;

    /// <summary>"v1.0.24", read from this assembly's version.</summary>
    public string AppVersion { get; } = AppInfo.FormatVersion(typeof(MainViewModel).Assembly);

    /// <summary>"EQ Overwatch v1.0.24": the window title, and the footer of the Discord picture.</summary>
    public string WindowTitle => $"{AppName} {AppVersion}";

    // ───────────────────────── EverQuest folder ─────────────────────────

    private const string NoFolderText = "No EverQuest folder chosen yet. Set it to find your characters and your spell file.";

    /// <summary>The game folder (the one with <c>Logs</c> and <c>spells_us.txt</c> in it).</summary>
    [ObservableProperty]
    private string _eqFolderPath = string.Empty;

    [ObservableProperty]
    private string _eqFolderStatus = NoFolderText;

    partial void OnEqFolderPathChanged(string value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    [RelayCommand]
    private async Task SetEqFolderAsync()
    {
        string? picked = _dialogs.PickEqFolder();
        if (picked is not null)
        {
            await ApplyEqFolderAsync(picked);
        }
    }

    [RelayCommand]
    private async Task RefreshEqFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(EqFolderPath))
        {
            EqFolderStatus = NoFolderText;
            return;
        }

        await ApplyEqFolderAsync(EqFolderPath);
    }

    /// <summary>
    /// Makes <paramref name="path"/> the EverQuest folder (a path ending in <c>Logs</c> is replaced by its parent), lists the character
    /// logs in it on the Characters tab and loads <c>spells_us.txt</c> from it. Completes when the spell file has been read.
    /// </summary>
    public async Task ApplyEqFolderAsync(string? path)
    {
        string folder = EqDirectoryScanner.Normalize(path);
        if (folder.Length == 0)
        {
            EqFolderStatus = "Choose the folder that contains EverQuest's Logs folder.";
            return;
        }

        if (!Directory.Exists(folder))
        {
            EqFolderStatus = $"The folder {folder} does not exist.";
            return;
        }

        EqFolderPath = folder;
        int found = EqDirectoryScanner.Discover(folder).Count;
        int added = DiscoverCharacters();
        string characters = DescribeCharacterLogs(folder, found, added);

        string spells;
        if (EqDirectoryScanner.HasSpellsFile(folder))
        {
            SpellsFilePath = EqDirectoryScanner.GetSpellsFilePath(folder);
            RequestSave();
            await ReloadSpellsAsync();
            spells = "spells_us.txt loaded.";
        }
        else
        {
            spells = "spells_us.txt was not found in that folder; use \"Choose spell file…\" if it lives elsewhere.";
        }

        EqFolderStatus = $"{characters} {spells}";
        StatusMessage = $"EverQuest folder: {folder}";
    }

    private static string DescribeCharacterLogs(string folder, int found, int added) => found == 0
        ? $"No eqlog_<Character>_<Server>.txt files in {EqDirectoryScanner.GetLogsFolder(folder)} (turn logging on in EverQuest with /log)."
        : $"{found} character log(s) found ({added} new).";

    /// <summary>
    /// Fills in the folder status line when the app opens with a saved folder, so it does not say "No EverQuest folder chosen yet"
    /// until Refresh is pressed. The characters were already scanned (<paramref name="added"/> are new since the last run) and the
    /// spell file is loaded by the normal start-up path.
    /// </summary>
    private void DescribeEqFolderAtStartup(int added)
    {
        string folder = EqDirectoryScanner.Normalize(EqFolderPath);
        if (folder.Length == 0)
        {
            return;
        }

        if (!Directory.Exists(folder))
        {
            EqFolderStatus = $"The folder {folder} does not exist.";
            return;
        }

        string characters = DescribeCharacterLogs(folder, EqDirectoryScanner.Discover(folder).Count, added);
        string spells = EqDirectoryScanner.HasSpellsFile(folder)
            ? "spells_us.txt found."
            : "spells_us.txt was not found in that folder; use \"Choose spell file…\" if it lives elsewhere.";
        EqFolderStatus = $"{characters} {spells}";
    }

    /// <summary>
    /// Adds a character for every <c>eqlog_*.txt</c> in the folder's Logs folder that is not watched yet. They start with triggers off,
    /// so a box full of alts does not all get tailed: tick "Enable Triggers" for the ones being played. Returns how many were added.
    /// </summary>
    private int DiscoverCharacters()
    {
        int added = 0;
        foreach (DiscoveredLog discovered in EqDirectoryScanner.Discover(EqFolderPath))
        {
            if (Logs.Any(l => string.Equals(
                    l.FilePath, discovered.FilePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                continue;
            }

            Logs.Add(new WatchedLogConfig(discovered.FilePath) { IsTriggerEnabled = false });
            added++;
        }

        return added;
    }

    // ──────────────────── characters grid (server, then character) ────────────────────

    /// <summary>The watched characters ordered by server (A-Z) and then character (A-Z): what the Characters grid shows.</summary>
    [ObservableProperty]
    private ObservableCollection<WatchedLogConfig> _sortedCharacters = new();

    private void RebuildSortedCharacters()
    {
        List<WatchedLogConfig> ordered = Logs.ToList();
        ordered.Sort((a, b) => EqDirectoryScanner.Compare(a.ServerName, a.CharacterName, b.ServerName, b.CharacterName));
        SortedCharacters = new ObservableCollection<WatchedLogConfig>(ordered);
        RefreshCharacterInfo();
    }

    /// <summary>Level and class for every watched and additional character, read from the roster (what /who and level-ups taught it).</summary>
    private void RefreshCharacterInfo()
    {
        foreach (WatchedLogConfig log in Logs)
        {
            log.Level = _pipeline.Roster.GetLevel(log.CharacterName);
            log.ClassName = _pipeline.Roster.GetClass(log.CharacterName);
            log.DataText = DescribeCharacterData(log);
        }

        AdditionalCharacterRows.Clear();
        foreach (string name in AdditionalCharacters)
        {
            AdditionalCharacterRows.Add(new CharacterInfoRow(name, _pipeline.Roster.GetLevel(name), _pipeline.Roster.GetClass(name)));
        }
    }

    // ──────────────── additional characters (other PCs, groupmates) ────────────────

    /// <summary>Characters whose buffs are tracked although they are not played here.</summary>
    public ObservableCollection<string> AdditionalCharacters { get; } = new();

    /// <summary>The same characters with their level and class, as the table under the Characters grid shows them.</summary>
    public ObservableCollection<CharacterInfoRow> AdditionalCharacterRows { get; } = new();

    [ObservableProperty]
    private string _newAdditionalCharacter = string.Empty;

    [ObservableProperty]
    private string _additionalCharacterStatus = string.Empty;

    [RelayCommand]
    private void AddAdditionalCharacter()
    {
        string typed = NewAdditionalCharacter?.Trim() ?? string.Empty;
        if (typed.Length == 0)
        {
            AdditionalCharacterStatus = "Type the character's name first.";
            return;
        }

        if (typed.Any(c => !char.IsLetter(c)))
        {
            AdditionalCharacterStatus = "EverQuest character names contain letters only.";
            return;
        }

        string name = char.ToUpperInvariant(typed[0]) + typed[1..].ToLowerInvariant();
        if (AdditionalCharacters.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            AdditionalCharacterStatus = $"{name} is already in the list.";
            return;
        }

        AdditionalCharacters.Add(name);
        NewAdditionalCharacter = string.Empty;
        AdditionalCharacterStatus = $"Now tracking buffs on {name}.";
        ApplyMonitoredCharacters();
    }

    [RelayCommand]
    private void RemoveAdditionalCharacter(string? name)
    {
        if (name is null)
        {
            return;
        }

        string? existing = AdditionalCharacters.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && AdditionalCharacters.Remove(existing))
        {
            AdditionalCharacterStatus = $"No longer tracking {existing}.";
            ApplyMonitoredCharacters();
        }
    }

    /// <summary>Registers the extra characters with the DPS trackers and the buff tracker, then saves.</summary>
    private void ApplyMonitoredCharacters()
    {
        _pipeline.SetAdditionalMonitoredCharacters(AdditionalCharacters);
        RefreshCharacterInfo();
        RequestSave();
    }

    // ─────────────────────── CH chain (Characters + Settings) ───────────────────────

    public IReadOnlyList<ChainPositionOption> ChainPositionOptions => ChainPositionOption.All;

    /// <summary>Raid-say prefix; each {N} is the cleric's position, e.g. "{N}-{N}-{N}" gives "3-3-3".</summary>
    [ObservableProperty]
    private string _chainPositionTagFormat = ChChainSettings.DefaultPositionTagFormat;

    /// <summary>What a cleric says after the tag to tell the next one to cast, e.g. "CAST NOW".</summary>
    [ObservableProperty]
    private string _chainCueKeyword = ChChainSettings.DefaultCueKeyword;

    /// <summary>Seconds after a cue the assigned cleric may take to start casting before the alarm sounds.</summary>
    [ObservableProperty]
    private double _chainLateThresholdSeconds = ChChainSettings.DefaultLateThresholdSeconds;

    [ObservableProperty]
    private string _chainTagError = string.Empty;

    [ObservableProperty]
    private string _chainSummary = NoChainText;

    private const string NoChainText = "No character holds a chain position. Pick one in the CH Chain Position column.";

    partial void OnChainPositionTagFormatChanged(string value)
    {
        ChainTagError = ChChainSettings.IsValidTagFormat(value)
            ? string.Empty
            : "The tag must contain {N} (the position number), for example {N}-{N}-{N}.";
        OnChainSettingChanged();
    }

    partial void OnChainCueKeywordChanged(string value) => OnChainSettingChanged();

    partial void OnChainLateThresholdSecondsChanged(double value)
    {
        double clamped = double.IsFinite(value)
            ? Math.Clamp(value, ChChainSettings.MinLateThresholdSeconds, ChChainSettings.MaxLateThresholdSeconds)
            : ChChainSettings.DefaultLateThresholdSeconds;
        if (clamped != value)
        {
            ChainLateThresholdSeconds = clamped;
            return;
        }

        OnChainSettingChanged();
    }

    private void OnChainSettingChanged()
    {
        if (_restoring)
        {
            return;
        }

        PushChainSettings();
        RequestSave();
    }

    private ChChainSettings BuildChainSettings() => new()
    {
        PositionTagFormat = ChainPositionTagFormat,
        CueKeyword = ChainCueKeyword,
        LateThresholdSeconds = ChainLateThresholdSeconds,
    };

    private void PushChainSettings() => _pipeline.ChChain.Configure(BuildChainSettings());

    /// <summary>Tells the chain service which character holds which position, and refreshes the summary line.</summary>
    private void SyncChainPositions()
    {
        _pipeline.ChChain.SetPositions(
            Logs.Where(l => l.ChChainPosition > 0).Select(l => new KeyValuePair<string, int>(l.CharacterName, l.ChChainPosition)));
        UpdateChainSummary();
    }

    private void UpdateChainSummary()
    {
        List<WatchedLogConfig> assigned = Logs.Where(l => l.ChChainPosition > 0).OrderBy(l => l.ChChainPosition).ToList();
        if (assigned.Count == 0)
        {
            ChainSummary = NoChainText;
            return;
        }

        string who = string.Join(", ", assigned.Select(l => $"#{l.ChChainPosition} {l.CharacterName}"));
        ChainSummary = $"Chain: {who}.";
    }

    // ───────────────────────── class cooldowns ─────────────────────────

    /// <summary>The built-in cooldowns (Divine Aura spell and its two AAs), listed on the Raid Triggers tab.</summary>
    public IReadOnlyList<CooldownRuleInfo> CooldownRules => CooldownTrackerService.BuiltInRules;

    [ObservableProperty]
    private bool _isCooldownTrackerEnabled = true;

    /// <summary>Completes when the start-up catch-up and live start (<see cref="StartSessionAsync"/>) have finished.</summary>
    public Task SessionStartTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opening the app mid-fight: reads the Primary DPS log (the last <see cref="HistoricalDaysLimit"/> days) into the live DPS
    /// meter as if the app had been open all along, then starts live monitoring (which then runs for as long as the app is open).
    /// Called once by the application after the window exists; failures are reported in <see cref="ImportStatus"/>.
    /// </summary>
    public Task StartSessionAsync() => SessionStartTask = RunStartSessionAsync();

    private async Task RunStartSessionAsync()
    {
        try
        {
            WatchedLogConfig? primary = Logs.PrimaryDpsLog;
            if (primary is not null && File.Exists(primary.FilePath))
            {
                await CatchUpLiveDpsAsync(primary);
            }

            if (!_pipeline.IsLiveRunning)
            {
                _pipeline.StartLive();
                StatusMessage = Logs.Count == 0
                    ? "Live monitoring started, but no log files are being watched yet."
                    : $"Live monitoring {Logs.Count} log file(s).";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Start-up: {ex.Message}";
        }

        UpdateLiveStatus();
        _lastLiveVersion = -1;
        Refresh();
    }

    private async Task CatchUpLiveDpsAsync(WatchedLogConfig primary)
    {
        using var cts = new CancellationTokenSource();
        _importCts = cts;
        IsImporting = true;
        ImportProgress = 0;

        DateTime now = _clock();
        string window = AppSettings.DescribeHistoricalDays(HistoricalDaysLimit);
        ImportStatus = $"Loading {window} of {primary.CharacterName}'s log into the DPS meter…";

        var progress = new Progress<double>(fraction => ImportProgress = fraction * 100.0);
        try
        {
            HistoricalImportResult result = await _pipeline.BackfillLiveAsync(
                primary.FilePath, AppSettings.GetHistoricalStart(HistoricalDaysLimit, now), now, progress, cts.Token);
            ImportProgress = 100;
            ImportStatus = $"Loaded {result.LinesRead:N0} lines for {result.CharacterName} in {result.Elapsed.TotalSeconds:0.0}s: " +
                           $"{_pipeline.LiveDps.Snapshot.All.Count():N0} encounters.";
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "Loading the log was cancelled.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Could not load {primary.CharacterName}'s log: {ex.Message}";
        }
        finally
        {
            _importCts = null;
            IsImporting = false;
        }
    }

    partial void OnIsCooldownTrackerEnabledChanged(bool value)
    {
        _pipeline.Cooldowns.IsEnabled = value;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    // ───────────────────────── history auto-import ─────────────────────────

    public IReadOnlyList<HistoricalDaysOption> HistoricalDaysOptions => HistoricalDaysOption.All;

    /// <summary>Days of history loaded for the Primary DPS character (0 = all history). Seven by default.</summary>
    [ObservableProperty]
    private int _historicalDaysLimit = AppSettings.DefaultHistoricalDaysLimit;

    /// <summary>Completes when the most recently started automatic import has finished (also on failure or cancellation).</summary>
    public Task AutoImportTask { get; private set; } = Task.CompletedTask;

    private int _autoImportSequence;

    partial void OnHistoricalDaysLimitChanged(int value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        QueueAutoImport();
    }

    [RelayCommand]
    private void ReloadHistory()
    {
        if (Logs.PrimaryDpsLog is null)
        {
            ImportStatus = "Choose a Primary DPS character on the Characters tab first.";
            return;
        }

        QueueAutoImport();
    }

    /// <summary>Starts loading the Primary DPS log's recent history in the background (nothing happens without a Primary DPS log).</summary>
    private void QueueAutoImport()
    {
        WatchedLogConfig? primary = Logs.PrimaryDpsLog;
        if (primary is null)
        {
            return;
        }

        AutoImportTask = AutoImportAsync(primary.FilePath, primary.CharacterName, HistoricalDaysLimit);
    }

    private async Task AutoImportAsync(string path, string character, int days)
    {
        int sequence = Interlocked.Increment(ref _autoImportSequence);

        if (!File.Exists(path))
        {
            ImportStatus = $"The log for {character} was not found: {path}";
            return;
        }

        // A newer request replaces a running import: cancel it and wait (briefly) until the pipeline is free again.
        if (IsImporting)
        {
            CancelImport();
            for (int i = 0; i < 500 && IsImporting; i++)
            {
                await Task.Delay(10);
            }
        }

        if (sequence != Volatile.Read(ref _autoImportSequence))
        {
            return; // an even newer request is on its way
        }

        DateTime? start = AppSettings.GetHistoricalStart(days, _clock());
        string window = AppSettings.DescribeHistoricalDays(days);
        await RunImportAsync(
            path, start, null,
            startStatus: $"Loading {window} of history for {character}…",
            successSuffix: string.Empty);
    }
}

/// <summary>One line of the "additional characters" table.</summary>
public sealed record CharacterInfoRow(string Name, int Level, string ClassName)
{
    public string LevelText => Level > 0 ? Level.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";

    public string ClassText => ClassName.Length > 0 ? ClassName : "—";
}
