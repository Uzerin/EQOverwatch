using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Backup;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>One boss on the Boss History tab.</summary>
public sealed record BossHistoryRow(string Boss, int Kills, int Attempts, string Fastest, string Average, string BestDps, int Players, string LastFought);

// Boss kill history, backup and restore, and saved overlay layouts.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan BossHistoryEvery = TimeSpan.FromSeconds(5);

    private readonly BossHistoryStore _bossHistory;
    private string? _dataDirectory;
    private long _bossLiveVersion = -1;
    private long _bossHistoricalVersion = -1;
    private DateTime _lastBossHistoryCheck;
    private volatile bool _bossHistoryDirty = true;
    private readonly List<OverlayProfile> _overlayProfiles = new();

    private void InitializeArchive()
    {
        _bossHistory.Changed += () => _bossHistoryDirty = true;
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
            if (live.Version != _bossLiveVersion || imported.Version != _bossHistoricalVersion)
            {
                _bossLiveVersion = live.Version;
                _bossHistoricalVersion = imported.Version;
                _bossHistory.Record(live.Completed.Concat(imported.Completed));
            }
        }

        if (_bossHistoryDirty)
        {
            _bossHistoryDirty = false;
            RebuildBossHistory();
        }
    }

    // ───────────────────────── boss history ─────────────────────────

    public IReadOnlyList<string> BossHistorySizes { get; } = new[] { "Any size", "6+ players", "12+ players", "24+ players" };

    [ObservableProperty]
    private int _bossHistorySizeIndex = 1;

    [ObservableProperty]
    private string _bossHistoryFilter = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<BossHistoryRow> _bossHistoryRows = Array.Empty<BossHistoryRow>();

    [ObservableProperty]
    private string _bossHistoryStatus = "No finished fights recorded yet.";

    partial void OnBossHistorySizeIndexChanged(int value) => RebuildBossHistory();

    partial void OnBossHistoryFilterChanged(string value) => RebuildBossHistory();

    private int BossHistoryMinPlayers => BossHistorySizeIndex switch { 1 => 6, 2 => 12, 3 => 24, _ => 1 };

    private void RebuildBossHistory()
    {
        IReadOnlyList<BossFight> fights = _bossHistory.Fights();
        string filter = BossHistoryFilter?.Trim() ?? string.Empty;
        BossHistoryRow[] rows = BossHistoryStore.Summarize(fights, BossHistoryMinPlayers)
            .Where(s => filter.Length == 0 || s.Boss.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new BossHistoryRow(
                s.Boss, s.Kills, s.Attempts,
                BossHistoryStore.FormatDuration(s.FastestSeconds),
                BossHistoryStore.FormatDuration(s.AverageSeconds),
                s.BestDps > 0 ? s.BestDps.ToString("N0", CultureInfo.CurrentCulture) : "—",
                s.MostPlayers,
                s.LastFought.ToString("MMM d", CultureInfo.CurrentCulture)))
            .ToArray();
        BossHistoryRows = rows;

        BossHistoryStatus = fights.Count == 0
            ? "No finished fights recorded yet. Fights are added as they finish, and from the history you load in Settings."
            : $"{rows.Length:N0} boss{(rows.Length == 1 ? string.Empty : "es")} from {fights.Count:N0} recorded fight{(fights.Count == 1 ? string.Empty : "s")}. " +
              "An attempt is a fight that ended without the boss dying.";
    }

    [RelayCommand]
    private void ClearBossHistory()
    {
        _bossHistory.Clear();
        RebuildBossHistory();
    }

    // ───────────────────────── backup and restore ─────────────────────────

    [ObservableProperty]
    private string _backupStatus = "Saves your settings, triggers, loot, attendance, characters and boss history in one file.";

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
                "This replaces your settings, triggers, loot, attendance, characters and boss history with the ones in the backup. " +
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
