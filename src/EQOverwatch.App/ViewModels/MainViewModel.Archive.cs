using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Backup;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>One kill in the list beside the mob table. <see cref="IsFastest"/> and <see cref="IsBestDps"/> mark the standouts.</summary>
/// <summary>One bar of the kill-time trend: how tall to draw it and what it says when hovered.</summary>
public sealed record BossTrendBar(double Height, string Tip);

public sealed record BossKillRow(
    int Number, string Started, string ClearTime, string Dps, int Players, int Deaths, string Damage, bool IsFastest, bool IsBestDps);

/// <summary>
/// One mob on the Mob History tab. The mob and its level range are the same whichever log is loaded; the rest of the row is the loaded
/// (Primary DPS) character's own record against it.
/// </summary>
public sealed record BossHistoryRow(
    string Boss, string HighestLevel, string LowestLevel, int Kills, int Attempts, string Fastest, string Average, string BestDps, int Players, string LastFought);

// Boss kill history, backup and restore, and saved overlay layouts.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan BossHistoryEvery = TimeSpan.FromSeconds(5);

    private readonly BossHistoryStore _bossHistory;
    private readonly MobLevelStore _mobLevels;
    private string? _historicalCharacter;
    private string? _historicalServer;
    private string? _dataDirectory;
    private long _bossLiveVersion = -1;
    private long _bossHistoricalVersion = -1;
    private DateTime _lastBossHistoryCheck;
    private volatile bool _bossHistoryDirty = true;
    private readonly List<OverlayProfile> _overlayProfiles = new();

    private void InitializeArchive()
    {
        _bossHistory.Changed += () => _bossHistoryDirty = true;
        _mobLevels.Changed += () => _bossHistoryDirty = true;
        RebuildProfileNames();
    }

    /// <summary>Called from <see cref="RefreshRaidNight"/>: files finished fights into the history and refreshes the tab.</summary>
    private void RefreshArchive()
    {
        DateTime now = _clock();
        if (now - _lastBossHistoryCheck >= BossHistoryEvery)
        {
            _lastBossHistoryCheck = now;
            DpsSnapshot live = _pipeline.LiveDps.Snapshot;
            DpsSnapshot imported = _pipeline.HistoricalDps.Snapshot;
            if (live.Version != _bossLiveVersion)
            {
                // Live and caught-up fights come from the Primary DPS character's log; each fight is filed under that character and server
                // (kept in the background, never shown).
                _bossLiveVersion = live.Version;
                WatchedLogConfig? primary = Logs.PrimaryDpsLog;
                _bossHistory.Record(live.Completed, primary?.CharacterName, primary?.ServerName);
                PostFinishedFightsToDiscord(live);
            }

            if (imported.Version != _bossHistoricalVersion)
            {
                _bossHistoricalVersion = imported.Version;
                _bossHistory.Record(imported.Completed, _historicalCharacter, _historicalServer);
            }
        }

        if (_bossHistoryDirty)
        {
            _bossHistoryDirty = false;
            RebuildBossHistory();
        }
    }

    // ───────────────────────── mob history ─────────────────────────

    /// <summary>Remembers whose log a manual import reads, so the fights it produces are filed under that character and server.</summary>
    private void NoteHistoricalSource(string path)
    {
        if (LogFileName.TryParse(path, out string character, out string server))
        {
            _historicalCharacter = character;
            _historicalServer = server;
        }
        else
        {
            _historicalCharacter = Path.GetFileNameWithoutExtension(path);
            _historicalServer = null;
        }
    }

    /// <summary>The fights of the loaded log: the Primary DPS character's own, or every fight when no character is chosen.</summary>
    private IReadOnlyList<BossFight> LoadedLogFights()
    {
        WatchedLogConfig? primary = Logs.PrimaryDpsLog;
        return BossHistoryStore.ForLog(_bossHistory.Fights(), primary?.CharacterName, primary?.ServerName);
    }

    public IReadOnlyList<string> BossHistorySizes { get; } = new[] { "Any size", "6+ players", "12+ players", "24+ players" };

    [ObservableProperty]
    private int _bossHistorySizeIndex = 1;

    [ObservableProperty]
    private string _bossHistoryFilter = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BossHistoryRow> _bossHistoryRows = Array.Empty<BossHistoryRow>();

    [ObservableProperty]
    private string _bossHistoryStatus = "No finished fights recorded yet.";

    /// <summary>The mob whose kills are listed beside the table; kept when the table is rebuilt.</summary>
    [ObservableProperty]
    private BossHistoryRow? _selectedBossHistoryRow;

    [ObservableProperty]
    private IReadOnlyList<BossKillRow> _bossKillRows = Array.Empty<BossKillRow>();

    [ObservableProperty]
    private string _bossKillsTitle = "Select a mob to see its kills";

    [ObservableProperty]
    private string _bossKillsHint = string.Empty;

    [ObservableProperty]
    private string _bossAttemptsLine = string.Empty;

    [ObservableProperty]
    private string _bossTrendLine = string.Empty;

    /// <summary>One bar per kill, oldest first; a shorter bar is a faster kill.</summary>
    [ObservableProperty]
    private IReadOnlyList<BossTrendBar> _bossTrendBars = Array.Empty<BossTrendBar>();

    private string? _selectedBossName;
    private BossKillReport? _bossKillReport;

    partial void OnSelectedBossHistoryRowChanged(BossHistoryRow? value)
    {
        if (value is not null)
        {
            _selectedBossName = value.Boss;
        }

        RebuildBossKills();
    }

    partial void OnBossHistorySizeIndexChanged(int value) => RebuildBossHistory();

    partial void OnBossHistoryFilterChanged(string value) => RebuildBossHistory();

    private int BossHistoryMinPlayers => BossHistorySizeIndex switch { 1 => 6, 2 => 12, 3 => 24, _ => 1 };

    private void RebuildBossHistory()
    {
        IReadOnlyList<BossFight> fights = _bossHistory.Fights();
        IReadOnlyList<BossFight> loaded = LoadedLogFights();
        string filter = BossHistoryFilter?.Trim() ?? string.Empty;
        BossHistoryRow[] rows = BossHistoryStore.SummarizeMobs(fights, loaded, BossHistoryMinPlayers)
            .Where(s => filter.Length == 0 || s.Boss.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                MobLevels? levels = _mobLevels.Find(s.Boss);
                return new BossHistoryRow(
                    s.Boss,
                    levels is null ? "—" : levels.Highest.ToString(CultureInfo.CurrentCulture),
                    levels is null ? "—" : levels.Lowest.ToString(CultureInfo.CurrentCulture),
                    s.Kills, s.Attempts,
                    BossHistoryStore.FormatDuration(s.FastestSeconds),
                    BossHistoryStore.FormatDuration(s.AverageSeconds),
                    s.BestDps > 0 ? s.BestDps.ToString("N0", CultureInfo.CurrentCulture) : "—",
                    s.MostPlayers,
                    s.LastFought == default ? "—" : s.LastFought.ToString("MMM d", CultureInfo.CurrentCulture));
            })
            .ToArray();
        BossHistoryRows = rows;
        SelectedBossHistoryRow = _selectedBossName is null
            ? null
            : rows.FirstOrDefault(r => r.Boss.Equals(_selectedBossName, StringComparison.OrdinalIgnoreCase));
        RebuildBossKills();

        WatchedLogConfig? primaryLog = Logs.PrimaryDpsLog;
        BossHistoryStatus = fights.Count == 0
            ? "No finished fights recorded yet. Fights are added as they finish, and from the history you load in Settings."
            : $"{rows.Length:N0} mob{(rows.Length == 1 ? string.Empty : "s")} from {fights.Count:N0} recorded fight{(fights.Count == 1 ? string.Empty : "s")}. " +
              "The mobs and their levels cover every log; the kills, times and DPS are " +
              (primaryLog is null ? "from every log." : $"{primaryLog.CharacterName}'s (the Primary DPS character). ") +
              "An attempt is a fight that ended without the mob dying.";
    }

    /// <summary>Lists every kill of the selected mob from the loaded log, fastest first, and sums up the attempts that did not end in one.</summary>
    private void RebuildBossKills()
    {
        BossHistoryRow? selected = SelectedBossHistoryRow;
        if (selected is null)
        {
            _bossKillReport = null;
            BossKillRows = Array.Empty<BossKillRow>();
            BossKillsTitle = "Select a mob to see its kills";
            BossKillsHint = string.Empty;
            BossAttemptsLine = string.Empty;
            BossTrendLine = string.Empty;
            BossTrendBars = Array.Empty<BossTrendBar>();
            return;
        }

        BossKillReport report = BossHistoryStore.KillReport(LoadedLogFights(), selected.Boss, BossHistoryMinPlayers);
        _bossKillReport = report;

        double fastest = report.FastestSeconds;
        double bestDps = report.BestDps;
        bool several = report.Kills.Count > 1;
        BossKillRows = report.Kills
            .Select((k, i) => new BossKillRow(
                i + 1,
                k.Start.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture),
                BossHistoryStore.FormatDuration(k.Seconds),
                Math.Round(k.Dps).ToString("N0", CultureInfo.CurrentCulture),
                k.Players,
                k.Deaths,
                k.Damage.ToString("N0", CultureInfo.CurrentCulture),
                several && Math.Abs(k.Seconds - fastest) < 0.001,
                several && Math.Abs(k.Dps - bestDps) < 0.001))
            .ToArray();

        BossKillsTitle = $"Kills of {report.Boss}";
        BossKillsHint = report.Kills.Count == 0
            ? "No kills recorded yet."
            : "Only kills Overwatch has saved are listed; read older logs with Read a log file… to add earlier kills.";
        BossAttemptsLine = AttemptsLine(report.Attempts);
        ShowBossTrend(BossHistoryStore.Trend(report.Kills));
    }

    private void ShowBossTrend(BossTrend? trend)
    {
        if (trend is null || trend.Kills < 2)
        {
            BossTrendLine = string.Empty;
            BossTrendBars = Array.Empty<BossTrendBar>();
            return;
        }

        const int MaxBars = 30;
        IReadOnlyList<BossFight> shown = trend.Chronological.Count > MaxBars ? trend.Chronological.Skip(trend.Chronological.Count - MaxBars).ToList() : trend.Chronological;
        double slowest = shown.Max(k => k.Seconds);
        BossTrendBars = shown
            .Select(k => new BossTrendBar(
                Math.Max(4.0, 40.0 * (slowest > 0 ? k.Seconds / slowest : 0)),
                $"{k.Start.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)}: {BossHistoryStore.FormatDuration(k.Seconds)}, {Math.Round(k.Dps):N0} DPS"))
            .ToArray();
        BossTrendLine = trend.Describe();
    }

    /// <summary>"2 attempts without a kill: Oct 9, 9:02 PM (1:12) and Oct 2, 8:05 PM (0:48)"; empty when there were none.</summary>
    public static string AttemptsLine(IReadOnlyList<BossFight> attempts)
    {
        const int Shown = 5;
        if (attempts.Count == 0)
        {
            return string.Empty;
        }

        string[] items = attempts
            .Take(Shown)
            .Select(a => $"{a.Start.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)} ({BossHistoryStore.FormatDuration(a.Seconds)})")
            .ToArray();
        string list = attempts.Count > Shown
            ? string.Join(", ", items) + $", and {attempts.Count - Shown} more"
            : items.Length == 1 ? items[0] : string.Join(", ", items[..^1]) + " and " + items[^1];
        return $"{attempts.Count} attempt{(attempts.Count == 1 ? string.Empty : "s")} without a kill: {list}";
    }

    [RelayCommand]
    private void CopyBossKills()
    {
        BossKillReport? report = _bossKillReport;
        if (report is null || report.Kills.Count == 0)
        {
            StatusMessage = "Select a mob with kills first.";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{report.Boss}: {report.Kills.Count} kill{(report.Kills.Count == 1 ? string.Empty : "s")}, fastest {BossHistoryStore.FormatDuration(report.FastestSeconds)}, average {BossHistoryStore.FormatDuration(report.AverageSeconds)}, best DPS {Math.Round(report.BestDps):N0}"));
        foreach (BossFight kill in report.Kills)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{kill.Start:MMM d, h:mm tt}  {BossHistoryStore.FormatDuration(kill.Seconds)}  {Math.Round(kill.Dps):N0} DPS  {kill.Players} players  {kill.Deaths} deaths"));
        }

        try
        {
            _clipboard.SetText(sb.ToString().TrimEnd());
            StatusMessage = $"Copied the {report.Kills.Count} kill{(report.Kills.Count == 1 ? string.Empty : "s")} of {report.Boss}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the kills: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyBossKillsImage()
    {
        BossKillReport? report = _bossKillReport;
        if (report is null || report.Kills.Count == 0)
        {
            StatusMessage = "Select a mob with kills first.";
            return;
        }

        try
        {
            _clipboard.SetBossKillsImage(report, ActivePalette, WindowTitle);
            StatusMessage = $"Copied a picture of all {report.Kills.Count} kill{(report.Kills.Count == 1 ? string.Empty : "s")} of {report.Boss}. Paste it into Discord.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The picture could not be copied: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearBossHistory()
    {
        _bossHistory.Clear();
        RebuildBossHistory();
    }

    // ───────────────────────── backup and restore ─────────────────────────

    [ObservableProperty]
    private string _backupStatus = "Saves your settings, triggers, loot, attendance, characters and mob history in one file.";

    [RelayCommand]
    private void ExportBackup()
    {
        if (_dataDirectory is null)
        {
            BackupStatus = "Backups are not available here.";
            return;
        }

        string? path = _dialogs.PickBackupSavePath($"EQOverwatch-backup-{_clock():yyyy-MM-dd}.zip");
        if (path is null)
        {
            return;
        }

        try
        {
            _settingsService.QueueSave(BuildSettings()); // so the file holds what is on screen
            _settingsService.Flush();
            int files = BackupService.Export(_dataDirectory, path, AppVersion);
            BackupStatus = $"Saved {files} file{(files == 1 ? string.Empty : "s")} to {Path.GetFileName(path)}. The pairing code is not included.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = $"The backup could not be saved: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        if (_dataDirectory is null)
        {
            BackupStatus = "Restoring is not available here.";
            return;
        }

        string? path = _dialogs.PickBackupFile();
        if (path is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Restore backup",
                "This replaces your settings, triggers, loot, attendance, characters and mob history with the ones in the backup. " +
                "It takes effect the next time EQ Overwatch starts. Continue?"))
        {
            return;
        }

        try
        {
            int files = BackupService.StageRestore(path, _dataDirectory);
            BackupStatus = $"{files} file{(files == 1 ? string.Empty : "s")} ready. Close EQ Overwatch and start it again to finish restoring.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = $"That file could not be restored: {ex.Message}";
        }
    }

    // ───────────────────────── overlay profiles ─────────────────────────

    public ObservableCollection<string> OverlayProfileNames { get; } = new();

    [ObservableProperty]
    private string? _selectedOverlayProfile;

    [ObservableProperty]
    private string _newOverlayProfileName = string.Empty;

    [ObservableProperty]
    private bool _autoSwitchOverlayProfiles;

    [ObservableProperty]
    private string _overlayProfileStatus = "Save where your overlays are now, and bring that layout back later.";

    partial void OnAutoSwitchOverlayProfilesChanged(bool value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void RebuildProfileNames()
    {
        string? keep = SelectedOverlayProfile;
        OverlayProfileNames.Clear();
        foreach (string name in _overlayProfiles.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            OverlayProfileNames.Add(name);
        }

        SelectedOverlayProfile = keep is not null && OverlayProfileNames.Contains(keep) ? keep : OverlayProfileNames.FirstOrDefault();
    }

    private static OverlaySettings ProfileSettings(OverlayProfile profile, OverlayKind kind) => kind switch
    {
        OverlayKind.Buffs => profile.Buffs,
        OverlayKind.Debuffs => profile.Debuffs,
        OverlayKind.Songs => profile.Songs,
        OverlayKind.RaidTriggers => profile.RaidTriggers,
        OverlayKind.ChChain => profile.ChChain,
        _ => profile.Dps,
    };

    /// <summary>Saves the overlays as they are now under the typed name (or the selected one), for this monitor setup.</summary>
    [RelayCommand]
    private void SaveOverlayProfile()
    {
        string name = NewOverlayProfileName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            name = SelectedOverlayProfile ?? string.Empty;
        }

        if (name.Length == 0)
        {
            OverlayProfileStatus = "Type a name for this layout first, for example \"Two monitors\".";
            return;
        }

        var profile = new OverlayProfile
        {
            Name = name,
            DisplaySignature = _overlays.DisplaySignature,
            Buffs = _overlays.Capture(OverlayKind.Buffs),
            Debuffs = _overlays.Capture(OverlayKind.Debuffs),
            Songs = _overlays.Capture(OverlayKind.Songs),
            RaidTriggers = _overlays.Capture(OverlayKind.RaidTriggers),
            ChChain = _overlays.Capture(OverlayKind.ChChain),
            Dps = _overlays.Capture(OverlayKind.Dps),
        };

        _overlayProfiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _overlayProfiles.Add(profile);
        NewOverlayProfileName = string.Empty;
        RebuildProfileNames();
        SelectedOverlayProfile = name;
        RequestSave();
        OverlayProfileStatus = $"Saved the layout \"{name}\".";
    }

    [RelayCommand]
    private void ApplyOverlayProfile()
    {
        OverlayProfile? profile = _overlayProfiles.FirstOrDefault(p => p.Name.Equals(SelectedOverlayProfile, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            OverlayProfileStatus = "Pick a saved layout first.";
            return;
        }

        ApplyProfile(profile);
        OverlayProfileStatus = $"Applied the layout \"{profile.Name}\".";
    }

    [RelayCommand]
    private void DeleteOverlayProfile()
    {
        if (SelectedOverlayProfile is null)
        {
            return;
        }

        string name = SelectedOverlayProfile;
        _overlayProfiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        RebuildProfileNames();
        RequestSave();
        OverlayProfileStatus = $"Deleted the layout \"{name}\".";
    }

    private void ApplyProfile(OverlayProfile profile)
    {
        foreach (OverlayKind kind in OverlayKindExtensions.All)
        {
            _overlays.Apply(kind, ProfileSettings(profile, kind));
        }

        IsBuffsOverlayVisible = profile.Buffs.IsVisible;
        IsDebuffsOverlayVisible = profile.Debuffs.IsVisible;
        IsSongsOverlayVisible = profile.Songs.IsVisible;
        IsRaidTriggersOverlayVisible = profile.RaidTriggers.IsVisible;
        IsChChainOverlayVisible = profile.ChChain.IsVisible;
        IsDpsOverlayVisible = profile.Dps.IsVisible;
        AreOverlaysLocked = OverlayKindExtensions.All.All(kind => ProfileSettings(profile, kind).IsLocked);
        RequestSave();
    }

    /// <summary>
    /// Called when Windows reports a different monitor setup (and at start-up). With automatic switching on, applies the newest
    /// saved layout that was made for this setup.
    /// </summary>
    public void OnDisplayConfigurationChanged()
    {
        if (!AutoSwitchOverlayProfiles)
        {
            return;
        }

        string signature = _overlays.DisplaySignature;
        OverlayProfile? match = signature.Length == 0
            ? null
            : _overlayProfiles.LastOrDefault(p => p.DisplaySignature == signature);
        if (match is null)
        {
            return;
        }

        ApplyProfile(match);
        SelectedOverlayProfile = match.Name;
        OverlayProfileStatus = $"The monitors changed, so the layout \"{match.Name}\" was applied.";
    }

    private void RestoreOverlayProfiles(AppSettings settings)
    {
        _overlayProfiles.Clear();
        _overlayProfiles.AddRange(settings.OverlayProfiles ?? new List<OverlayProfile>());
        AutoSwitchOverlayProfiles = settings.AutoSwitchOverlayProfiles;
    }
}
