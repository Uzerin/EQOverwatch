using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.App.ViewModels;

/// <summary>A character in the Experience by mob drop-down.</summary>
public sealed record MobExperienceCharacterChoice(string Character, string Server)
{
    public string Label => string.IsNullOrEmpty(Server) ? Character : $"{Character} ({Server})";
}

/// <summary>One row of the Experience by mob table.</summary>
public sealed record MobExperienceRowView(string Level, string Mob, int Kills, string AveragePercent, string TotalPercent, string LastKill, bool StartsLevel);

// Experience by mob, per character level, on the Experience tab.
public sealed partial class MainViewModel
{
    private const string AllLevels = "All";
    private static readonly TimeSpan MobExperienceFlushEvery = TimeSpan.FromSeconds(10);

    private long _mobExperienceVersionShown = -1;
    private DateTime _lastMobExperienceFlush;
    private bool _mobExperienceScanning;

    public ObservableCollection<MobExperienceCharacterChoice> MobExperienceCharacters { get; } = new();

    public ObservableCollection<string> MobExperienceLevels { get; } = new() { AllLevels };

    [ObservableProperty]
    private MobExperienceCharacterChoice? _selectedMobExperienceCharacter;

    [ObservableProperty]
    private string _selectedMobExperienceLevel = AllLevels;

    [ObservableProperty]
    private IReadOnlyList<MobExperienceRowView> _mobExperienceRows = Array.Empty<MobExperienceRowView>();

    [ObservableProperty]
    private string _mobExperienceStatus =
        "Fills in as your characters gain experience. Each experience line is matched to the kill line from the same second; "
        + "gains with no kill line next to them show as (unknown mob).";

    partial void OnSelectedMobExperienceCharacterChanged(MobExperienceCharacterChoice? value) => RebuildMobExperience(levelsToo: true);

    partial void OnSelectedMobExperienceLevelChanged(string value) => RebuildMobExperience(levelsToo: false);

    /// <summary>Loads the saved table and reads the history of any watched log not read before.</summary>
    private void InitializeMobExperience()
    {
        if (_dataDirectory is not null)
        {
            _pipeline.MobExperience.SetStoragePath(Path.Combine(_dataDirectory, "mobexperience.json"));
        }

        ScanUnreadMobExperience();
        RebuildMobExperience(levelsToo: true);
    }

    /// <summary>Reads the history of watched logs not read before (only with a data folder, so the result is kept).</summary>
    private void ScanUnreadMobExperience()
    {
        if (_dataDirectory is null)
        {
            return;
        }

        List<(string Path, string Character, string Server)> unread = WatchedLogsForMobExperience()
            .Where(l => !_pipeline.MobExperience.IsScanned(l.Character, l.Server))
            .ToList();
        if (unread.Count > 0)
        {
            _ = ScanMobExperienceAsync(unread);
        }
    }

    private List<(string Path, string Character, string Server)> WatchedLogsForMobExperience()
    {
        var logs = new List<(string, string, string)>();
        foreach (string path in Logs.Select(l => l.FilePath))
        {
            if (LogFileName.TryParse(path, out string character, out string server))
            {
                logs.Add((path, character, server));
            }
        }

        return logs;
    }

    /// <summary>Reads every watched log from the start again and rebuilds its character's table.</summary>
    [RelayCommand]
    private Task ScanMobExperience() => ScanMobExperienceAsync(WatchedLogsForMobExperience());

    private async Task ScanMobExperienceAsync(List<(string Path, string Character, string Server)> logs)
    {
        if (_mobExperienceScanning || logs.Count == 0)
        {
            return;
        }

        _mobExperienceScanning = true;
        string finished;
        try
        {
            int done = 0;
            foreach ((string path, string character, string server) in logs)
            {
                MobExperienceStatus = $"Reading the log history of {character} ({done + 1} of {logs.Count})…";
                int level = _pipeline.Roster.GetLevel(character, server);
                await Task.Run(() => _pipeline.MobExperience.ScanLog(path, character, server, level));
                done++;
            }

            finished = $"Read the log history of {done} character{(done == 1 ? string.Empty : "s")}. "
                + "Each experience line is matched to the kill line from the same second; gains with no kill line next to them show as (unknown mob).";
        }
        finally
        {
            _mobExperienceScanning = false;
        }

        RebuildMobExperience(levelsToo: true);
        MobExperienceStatus = finished;
    }

    private void RefreshMobExperience(DateTime nowUtc)
    {
        _pipeline.MobExperience.ResolveExpired(nowUtc.AddSeconds(-3));
        if (nowUtc - _lastMobExperienceFlush >= MobExperienceFlushEvery)
        {
            _lastMobExperienceFlush = nowUtc;
            _pipeline.MobExperience.Flush();
        }

        if (_pipeline.MobExperience.Version != _mobExperienceVersionShown)
        {
            RebuildMobExperience(levelsToo: true);
        }
    }

    private void RebuildMobExperience(bool levelsToo)
    {
        MobExperienceStore store = _pipeline.MobExperience;
        _mobExperienceVersionShown = store.Version;

        // The character list, kept in place so the selection holds.
        List<MobExperienceCharacterChoice> characters = store.Characters()
            .Select(c => new MobExperienceCharacterChoice(c.Character, c.Server))
            .ToList();
        if (!characters.SequenceEqual(MobExperienceCharacters))
        {
            MobExperienceCharacterChoice? keep = SelectedMobExperienceCharacter;
            MobExperienceCharacters.Clear();
            foreach (MobExperienceCharacterChoice choice in characters)
            {
                MobExperienceCharacters.Add(choice);
            }

            SelectedMobExperienceCharacter = keep is not null && characters.Contains(keep) ? keep : characters.FirstOrDefault();
            return; // the selection change rebuilds the rest
        }

        if (SelectedMobExperienceCharacter is null && characters.Count > 0)
        {
            SelectedMobExperienceCharacter = characters[0];
            return;
        }

        IReadOnlyList<MobExperienceRow> rows = SelectedMobExperienceCharacter is { } selected
            ? store.Rows(selected.Character, selected.Server)
            : Array.Empty<MobExperienceRow>();

        if (levelsToo)
        {
            List<string> levels = new[] { AllLevels }
                .Concat(rows.Select(r => r.Level).Distinct().OrderByDescending(l => l).Select(LevelText))
                .ToList();
            if (!levels.SequenceEqual(MobExperienceLevels))
            {
                string keep = SelectedMobExperienceLevel;
                MobExperienceLevels.Clear();
                foreach (string level in levels)
                {
                    MobExperienceLevels.Add(level);
                }

                if (!levels.Contains(keep))
                {
                    SelectedMobExperienceLevel = AllLevels;
                    return; // the level change rebuilds the rows
                }

                SelectedMobExperienceLevel = keep;
            }
        }

        DateTime nowUtc = DateTime.UtcNow;
        string filter = SelectedMobExperienceLevel;
        int previous = int.MinValue;
        var views = new List<MobExperienceRowView>();
        foreach (MobExperienceRow row in rows)
        {
            if (filter != AllLevels && LevelText(row.Level) != filter)
            {
                continue;
            }

            bool first = row.Level != previous;
            previous = row.Level;
            views.Add(new MobExperienceRowView(
                first ? LevelText(row.Level) : string.Empty,
                row.Mob.Length == 0 ? "(unknown mob)" : row.Mob,
                row.Kills,
                Percent(row.AveragePercent),
                Percent(row.TotalPercent),
                row.LastKillUtc == default ? "—" : LastKill(row.LastKillUtc, nowUtc),
                first && views.Count > 0));
        }

        MobExperienceRows = views;

        static string LevelText(int level) => level > 0 ? level.ToString(CultureInfo.CurrentCulture) : "?";

        static string Percent(double value) => value.ToString("0.000", CultureInfo.CurrentCulture) + "%";

        static string LastKill(DateTime utc, DateTime nowUtc) =>
            nowUtc - utc < TimeSpan.FromHours(20)
                ? utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)
                : utc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
