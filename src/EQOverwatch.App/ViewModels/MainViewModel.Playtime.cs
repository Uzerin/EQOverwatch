using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Session;

namespace EQOverwatch.App.ViewModels;

/// <summary>One day's bar on the playtime chart.</summary>
public sealed record PlaytimeBar(string Label, double Height, string Tip);

/// <summary>A level a character reached, and how long after its previous level.</summary>
public sealed record DingRow(string Character, int Level, string Reached, DateTime ReachedSort, string Gap, double GapSort);

// The Experience tab's playtime chart: hours played per day over the last four weeks, for all characters or one, read from the logs'
// timestamps (gaps over five minutes are time away). Read in the background while the Experience tab is open, at most every 10 minutes.
public sealed partial class MainViewModel
{
    public const string AllPlaytimeCharacters = "All characters";
    private const int PlaytimeDays = 28;
    private const double PlaytimeChartHeight = 100;
    private static readonly TimeSpan PlaytimeEvery = TimeSpan.FromMinutes(10);
    private DateTime _nextPlaytimeUtc;
    private int _playtimeRunning;
    private Dictionary<string, Dictionary<DateTime, double>> _playtime = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private IReadOnlyList<PlaytimeBar> _playtimeBars = Array.Empty<PlaytimeBar>();

    [ObservableProperty]
    private IReadOnlyList<string> _playtimeCharacters = new[] { AllPlaytimeCharacters };

    [ObservableProperty]
    private string _playtimeCharacter = AllPlaytimeCharacters;

    [ObservableProperty]
    private string _playtimeSummary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DingRow> _dingRows = Array.Empty<DingRow>();

    partial void OnPlaytimeCharacterChanged(string value)
    {
        ShowPlaytime();
        ShowDings();
    }

    /// <summary>The levels reached by the picked character (or everyone), newest first, with the days since the level before.</summary>
    private void ShowDings()
    {
        bool all = PlaytimeCharacter is null or AllPlaytimeCharacters;
        CultureInfo culture = CultureInfo.CurrentCulture;
        var rows = new List<DingRow>();
        foreach (WatchedLogConfig log in Logs.ToList())
        {
            if (!all && !log.CharacterName.Equals(PlaytimeCharacter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.AddRange(DingRowsFor(log.CharacterName, _leveling.Levels(log.FilePath), culture));
        }

        DingRows = rows.OrderByDescending(r => r.ReachedSort).ToList();
    }

    /// <summary>Turns a character's level events (oldest first) into rows; the first level has no gap.</summary>
    public static IReadOnlyList<DingRow> DingRowsFor(string character, IReadOnlyList<EQOverwatch.Core.Leveling.LevelEvent> levels, CultureInfo culture)
    {
        var rows = new List<DingRow>();
        DateTime? previous = null;
        foreach (EQOverwatch.Core.Leveling.LevelEvent level in levels.OrderBy(l => l.Time))
        {
            DateTime at = DateTimeOffset.FromUnixTimeSeconds(level.Time).LocalDateTime;
            double gap = previous is { } before ? (at - before).TotalDays : -1;
            string gapText = gap < 0 ? string.Empty
                : gap < 1 ? (gap * 24).ToString("0.0", culture) + " h"
                : gap.ToString("0.0", culture) + " days";
            rows.Add(new DingRow(character, level.Level, at.ToString("yyyy-MM-dd HH:mm", culture), at, gapText, gap));
            previous = at;
        }

        return rows;
    }

    /// <summary>Called by the refresh tick: reads the playtime while the Experience tab is on screen.</summary>
    private void RefreshPlaytime(DateTime nowUtc)
    {
        if (SelectedMainTab != ProgressTab || SelectedProgressTab != 0 || nowUtc < _nextPlaytimeUtc || Logs.Count == 0
            || Interlocked.CompareExchange(ref _playtimeRunning, 1, 0) != 0)
        {
            return;
        }

        _nextPlaytimeUtc = nowUtc + PlaytimeEvery;
        DateTime from = _clock().Date.AddDays(-(PlaytimeDays - 1));
        var logs = Logs.Select(l => (l.FilePath, l.CharacterName)).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                var result = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
                foreach ((string path, string character) in logs)
                {
                    Dictionary<DateTime, double> days = Playtime.PerDay(path, from);
                    if (days.Count == 0)
                    {
                        continue;
                    }

                    Dictionary<DateTime, double> total = result.TryGetValue(character, out var existing) ? existing : result[character] = new();
                    foreach ((DateTime day, double seconds) in days)
                    {
                        total[day] = total.GetValueOrDefault(day) + seconds;
                    }
                }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _playtime = result;
                    var names = result.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    names.Insert(0, AllPlaytimeCharacters);
                    if (!names.SequenceEqual(PlaytimeCharacters))
                    {
                        string keep = PlaytimeCharacter;
                        PlaytimeCharacters = names;
                        PlaytimeCharacter = names.Contains(keep) ? keep : AllPlaytimeCharacters;
                    }

                    ShowPlaytime();
                    ShowDings();
                });
            }
            finally
            {
                Interlocked.Exchange(ref _playtimeRunning, 0);
            }
        });
    }

    private void ShowPlaytime()
    {
        DateTime today = _clock().Date;
        bool all = PlaytimeCharacter is null or AllPlaytimeCharacters;
        CultureInfo culture = CultureInfo.CurrentCulture;
        var hours = new List<(DateTime Day, double Hours, string Who)>();
        for (int i = PlaytimeDays - 1; i >= 0; i--)
        {
            DateTime day = today.AddDays(-i);
            var who = _playtime
                .Where(p => all || p.Key.Equals(PlaytimeCharacter, StringComparison.OrdinalIgnoreCase))
                .Select(p => (Name: p.Key, Hours: p.Value.GetValueOrDefault(day) / 3600.0))
                .Where(p => p.Hours >= 0.05)
                .OrderByDescending(p => p.Hours)
                .ToList();
            hours.Add((day, who.Sum(w => w.Hours), string.Join("\n", who.Select(w => $"{w.Name}: {w.Hours.ToString("0.0", culture)} h"))));
        }

        double top = Math.Max(1, hours.Max(h => h.Hours));
        PlaytimeBars = hours.Select(h => new PlaytimeBar(
                h.Day.ToString("%d", culture),
                Math.Round(PlaytimeChartHeight * h.Hours / top, 1),
                $"{h.Day.ToString("ddd MMM d", culture)}: {h.Hours.ToString("0.0", culture)} h" + (h.Who.Length > 0 && all ? "\n" + h.Who : string.Empty)))
            .ToList();
        double total = hours.Sum(h => h.Hours);
        PlaytimeSummary = $"{total.ToString("0", culture)} h in 4 weeks, {(total / PlaytimeDays).ToString("0.0", culture)} h a day on average.";
    }
}
