using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Search;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One row of the Experience tab.</summary>
public sealed record ExperienceRowView(
    string Character, string Level, string XpSession, string XpPerHour, string NextLevel, int AaGained, string AaPerHour, int Kills, string LastGain);

/// <summary>One resist type of the selected mob: landed, resisted and the resist %.</summary>
public sealed record ResistTypeRow(string Type, int Landed, int Resisted, string Percent);

/// <summary>One contributor to the selected mob's raid figures.</summary>
public sealed record ResistContributorRow(
    string Player, string Shared, string Magic, string Fire, string Cold, string Poison, string Disease, string Status, bool IsOutlier);

/// <summary>One line found by the Log search tab.</summary>
public sealed record LogSearchRow(string Time, string Character, string Channel, string Line);

/// <summary>A channel button on the Log search tab.</summary>
public sealed partial class ChannelToggle : ObservableObject
{
    public ChannelToggle(string label, ChatChannel channel, bool isOn)
    {
        Label = label;
        Channel = channel;
        _isOn = isOn;
    }

    public string Label { get; }

    public ChatChannel Channel { get; }

    [ObservableProperty]
    private bool _isOn;
}

// Automatic songs, charm watch, the Experience tab, resists by type on Mob History (with raid sharing) and the Log search tab.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan ResistFlushEvery = TimeSpan.FromSeconds(10);

    private DateTime _lastResistFlush;
    private long _resistVersionShown = -1;
    private long _experienceVersionShown = -1;
    private DateTime _lastExperienceRefresh;
    private int _shareAllPage;
    private CancellationTokenSource? _searchCancel;

    // ─────────────────────────── settings ───────────────────────────

    /// <summary>Show other players' songs on the Songs overlay too.</summary>
    [ObservableProperty]
    private bool _showAllBardSongs;

    /// <summary>Charm bars and the break alarm.</summary>
    [ObservableProperty]
    private bool _charmWatchEnabled = true;

    partial void OnShowAllBardSongsChanged(bool value)
    {
        _pipeline.Songs.ShowAllBards = value;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnCharmWatchEnabledChanged(bool value)
    {
        _pipeline.Charm.IsEnabled = value;
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>Only Buffs and Debuffs: songs need no list.</summary>
    public IReadOnlyList<OverlayCategoryOption> SpellOverlayOptions { get; } =
        OverlayCategoryOption.All.Where(o => o.Value is TimerOverlayCategory.Buffs or TimerOverlayCategory.Debuffs).ToList();

    private void InitializeReleaseFeatures()
    {
        if (_dataDirectory is not null)
        {
            _pipeline.ResistStore.SetStoragePath(Path.Combine(_dataDirectory, "mobresists.json"), DateTime.UtcNow);
        }

        // Where each character is in its level, from the last level-up near the end of its log.
        string[] logs = Logs.Select(l => l.FilePath).ToArray();
        ExperienceTracker experience = _pipeline.Experience;
        _ = Task.Run(() =>
        {
            foreach (string path in logs)
            {
                if (LogFileName.TryParse(path, out string character, out _) && ExperienceTracker.ScanStartingPoint(path) is { } start)
                {
                    experience.SetStartingPoint(character, start.Level, start.Progress);
                }
            }
        });
    }

    private void RefreshReleaseFeatures()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastResistFlush >= ResistFlushEvery)
        {
            _lastResistFlush = nowUtc;
            _pipeline.ResistStore.Flush();
        }

        long resistVersion = _pipeline.ResistStore.Version;
        if (resistVersion != _resistVersionShown)
        {
            _resistVersionShown = resistVersion;
            _bossHistoryDirty = true;
        }

        if (_pipeline.Experience.Version != _experienceVersionShown || nowUtc - _lastExperienceRefresh >= TimeSpan.FromSeconds(5))
        {
            _experienceVersionShown = _pipeline.Experience.Version;
            _lastExperienceRefresh = nowUtc;
            RebuildExperience(nowUtc);
        }
    }

    /// <summary>Drops song rows from a saved Spells list: songs now reach the Songs overlay on their own.</summary>
    private void RemoveSongRows(SpellDatabase? database)
    {
        List<TrackedSpellRowViewModel> songs = TrackedSpells
            .Where(r => r.Overlay == TimerOverlayCategory.Songs || database?.FindById(r.SpellId)?.Category == TimerOverlayCategory.Songs)
            .ToList();
        foreach (TrackedSpellRowViewModel row in songs)
        {
            TrackedSpells.Remove(row);
        }

        if (songs.Count > 0)
        {
            ApplyTracked();
            TrackerStatus = songs.Count == 1
                ? $"{songs[0].Name} was removed from the list: songs go to the Songs overlay on their own."
                : $"{songs.Count} songs were removed from the list: songs go to the Songs overlay on their own.";
        }
    }

    // ─────────────────────────── experience tab ───────────────────────────

    [ObservableProperty]
    private IReadOnlyList<ExperienceRowView> _experienceRows = Array.Empty<ExperienceRowView>();

    [ObservableProperty]
    private string _experienceSession = string.Empty;

    [ObservableProperty]
    private string _experienceSessionLength = "0m";

    [ObservableProperty]
    private string _experienceKillSplit = "0 / 0 / 0";

    [ObservableProperty]
    private string _experienceLevelsGained = "0";

    [ObservableProperty]
    private string _experienceAaGained = "0";

    [ObservableProperty]
    private string _experienceHint = "Experience your characters gain shows here as it happens.";

    [RelayCommand]
    private void ResetExperience()
    {
        _pipeline.Experience.Reset(DateTime.UtcNow);
        RebuildExperience(DateTime.UtcNow);
        StatusMessage = "Experience session started again.";
    }

    private void RebuildExperience(DateTime nowUtc)
    {
        IReadOnlyList<ExperienceRow> rows = _pipeline.Experience.Rows(nowUtc, _pipeline.Roster.GetLevel);
        DateTime start = _pipeline.Experience.SessionStartUtc;
        ExperienceRows = rows.Select(r => new ExperienceRowView(
                r.Character,
                r.Level > 0 ? r.Level.ToString(CultureInfo.CurrentCulture) : "—",
                Percent(r.XpPercent),
                Percent(r.XpPerHour),
                r.NextLevelIn is { } next ? FormatSpan(next) : "—",
                r.AaGained,
                r.AaPerHour.ToString("0.0", CultureInfo.CurrentCulture),
                r.Kills,
                r.LastGainUtc == default ? "—" : Ago(nowUtc - r.LastGainUtc)))
            .ToList();

        ExperienceSession = $"Session started {start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
        ExperienceSessionLength = FormatSpan(nowUtc - start);
        ExperienceKillSplit = $"{rows.Sum(r => r.PartyKills):N0} / {rows.Sum(r => r.RaidKills):N0} / {rows.Sum(r => r.SoloKills):N0}";
        ExperienceLevelsGained = rows.Sum(r => r.LevelsGained).ToString("N0", CultureInfo.CurrentCulture);
        ExperienceAaGained = rows.Sum(r => r.AaGained).ToString("N0", CultureInfo.CurrentCulture);
        ExperienceHint = rows.Count == 0
            ? "Nothing gained yet this session. Experience, level-ups and AA points your characters gain show here as they happen."
            : "Only lines read since the session started count. A kill whose line shows no percentage (at the level cap or with AA experience) counts as a kill but adds no XP. "
              + "Next level is worked out from the last level-up in the log and this session's XP per hour.";

        static string Percent(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + "%";
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes}m";
    }

    private static string Ago(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{Math.Max(0, (int)span.TotalSeconds)} s ago"
        : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
        : $"{(int)span.TotalHours} h ago";

    // ─────────────────────────── mob history: resists ───────────────────────────

    /// <summary>True shows the raid's figures (you plus shares); false only your characters'.</summary>
    [ObservableProperty]
    private bool _resistRaidView;

    [ObservableProperty]
    private IReadOnlyList<ResistTypeRow> _resistTypeRows = Array.Empty<ResistTypeRow>();

    [ObservableProperty]
    private IReadOnlyList<ResistContributorRow> _resistContributorRows = Array.Empty<ResistContributorRow>();

    [ObservableProperty]
    private string _resistPanelTitle = "Select a mob to see its resists";

    [ObservableProperty]
    private string _resistPanelHint = string.Empty;

    /// <summary>The other side of <see cref="ResistRaidView"/>, for the Mine button.</summary>
    public bool ResistMineView
    {
        get => !ResistRaidView;
        set => ResistRaidView = !value;
    }

    partial void OnResistRaidViewChanged(bool value)
    {
        OnPropertyChanged(nameof(ResistMineView));
        RebuildBossHistory();
    }

    [RelayCommand]
    private void ShowMineResists() => ResistRaidView = false;

    [RelayCommand]
    private void ShowRaidResists() => ResistRaidView = true;

    private BossHistoryRow WithResists(BossHistoryRow row)
    {
        MobResistReport report = _pipeline.ResistStore.Report(row.Boss, ResistRaidView);
        int shares = _pipeline.ResistStore.ShareCount(row.Boss);
        return row with
        {
            Magic = Cell(report, SpellResist.Magic),
            Fire = Cell(report, SpellResist.Fire),
            Cold = Cell(report, SpellResist.Cold),
            Poison = Cell(report, SpellResist.Poison),
            Disease = Cell(report, SpellResist.Disease),
            SharedBy = shares == 0 ? "–" : $"{shares} player{(shares == 1 ? string.Empty : "s")}",
        };

        static string Cell(MobResistReport report, int type) =>
            report.ByType.TryGetValue(type, out ResistCount count) && count.Total > 0
                ? count.Percent.ToString("0.0", CultureInfo.CurrentCulture) + "%"
                : "–";
    }

    private void RebuildResistPanel()
    {
        BossHistoryRow? selected = SelectedBossHistoryRow;
        if (selected is null)
        {
            ResistPanelTitle = "Select a mob to see its resists";
            ResistTypeRows = Array.Empty<ResistTypeRow>();
            ResistContributorRows = Array.Empty<ResistContributorRow>();
            ResistPanelHint = string.Empty;
            return;
        }

        MobResistReport report = _pipeline.ResistStore.Report(selected.Boss, ResistRaidView);
        ResistPanelTitle = ResistRaidView ? $"{selected.Boss}: raid resists by type" : $"{selected.Boss}: your resists by type";
        ResistTypeRows = SpellResist.All
            .Where(t => report.ByType.TryGetValue(t, out ResistCount c) && c.Total > 0)
            .Select(t => new ResistTypeRow(
                SpellResist.Name(t), report.ByType[t].Landed, report.ByType[t].Resisted,
                report.ByType[t].Percent.ToString("0.0", CultureInfo.CurrentCulture) + "%"))
            .ToList();

        DateTime nowUtc = DateTime.UtcNow;
        ResistContributorRows = report.Contributors.Select(c => new ResistContributorRow(
                c.Player,
                c.SharedUtc is { } when ? AgoLong(nowUtc - when) : "Your logs",
                Contribution(c, SpellResist.Magic), Contribution(c, SpellResist.Fire), Contribution(c, SpellResist.Cold),
                Contribution(c, SpellResist.Poison), Contribution(c, SpellResist.Disease),
                c.LeftOut.Count == 0 ? "Counted" : string.Join(", ", c.LeftOut.Select(SpellResist.Name)) + " left out (outlier)",
                c.LeftOut.Count > 0))
            .ToList();

        ResistPanelHint = ResistTypeRows.Count == 0
            ? "No resist numbers for this mob yet. Your characters' spells on it are counted as they land or are resisted."
            : ResistRaidView
                ? "Raid: your characters plus every player who shared. A player's newest share replaces their older one. With 3 or more contributors, " +
                  "anyone more than 30 points from the median for a type is left out of that type."
                : "Mine: only your characters' spells, from their logs.";

        static string Contribution(ResistContributor c, int type) =>
            c.Counts.TryGetValue(type, out ResistCount count) && count.Total > 0
                ? $"{count.Percent.ToString("0.0", CultureInfo.CurrentCulture)}% ({count.Total:N0})"
                : "–";
    }

    private static string AgoLong(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{Math.Max(0, (int)span.TotalMinutes)} min ago"
        : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
        : $"{(int)span.TotalDays} d ago";

    /// <summary>Copies the selected mob's token (your characters' numbers) for pasting into /rs or /gu.</summary>
    [RelayCommand]
    private void ShareResists()
    {
        BossHistoryRow? selected = SelectedBossHistoryRow;
        if (selected is null)
        {
            StatusMessage = "Select a mob first.";
            return;
        }

        string? token = ResistShareCodec.Encode(selected.Boss, _pipeline.ResistStore.Own(selected.Boss));
        if (token is null)
        {
            StatusMessage = $"Your characters have no resist numbers for {selected.Boss} to share.";
            return;
        }

        CopyShare(token, $"Copied {selected.Boss}'s resists. Paste the line into /rs or /gu.");
    }

    /// <summary>Copies one chat line of tokens for the mobs your characters cast on most recently; each click copies the next line.</summary>
    [RelayCommand]
    private void ShareAllResists()
    {
        IReadOnlyList<string> tokens = _pipeline.ResistStore.MobsByRecentOwn()
            .Select(m => ResistShareCodec.Encode(m, _pipeline.ResistStore.Own(m)))
            .OfType<string>()
            .ToList();
        IReadOnlyList<string> lines = ResistShareCodec.PackLines(tokens);
        if (lines.Count == 0)
        {
            StatusMessage = "Your characters have no resist numbers to share yet.";
            return;
        }

        int page = _shareAllPage % lines.Count;
        _shareAllPage = page + 1;
        CopyShare(lines[page], lines.Count == 1
            ? "Copied your resists for every mob. Paste the line into /rs or /gu."
            : $"Copied line {page + 1} of {lines.Count} (most recent mobs first). Paste it into /rs or /gu, then click Share all again for the next line.");
    }

    private void CopyShare(string text, string done)
    {
        try
        {
            _clipboard.SetText(text);
            StatusMessage = done;
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy: " + ex.Message;
        }
    }

    // ─────────────────────────── log search tab ───────────────────────────

    [ObservableProperty]
    private string _logSearchText = string.Empty;

    /// <summary>"All characters" or one character's name.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _logSearchCharacters = new[] { AllCharacters };

    [ObservableProperty]
    private string _logSearchCharacter = AllCharacters;

    public IReadOnlyList<string> LogSearchRanges { get; } = new[] { "Last 7 days", "Last 30 days", "Last 90 days", "All time" };

    [ObservableProperty]
    private int _logSearchRangeIndex = 1;

    public IReadOnlyList<ChannelToggle> LogSearchChannels { get; } = new[]
    {
        new ChannelToggle("Tells", ChatChannel.Tells, true),
        new ChannelToggle("Guild", ChatChannel.Guild, true),
        new ChannelToggle("Raid", ChatChannel.Raid, true),
        new ChannelToggle("Group", ChatChannel.Group, false),
        new ChannelToggle("Say", ChatChannel.Say, false),
        new ChannelToggle("OOC / Shout / Auction", ChatChannel.OocShoutAuction, false),
        new ChannelToggle("Chat channels", ChatChannel.ChatChannels, false),
        new ChannelToggle("Everything else", ChatChannel.Other, false),
    };

    [ObservableProperty]
    private IReadOnlyList<LogSearchRow> _logSearchRows = Array.Empty<LogSearchRow>();

    [ObservableProperty]
    private string _logSearchStatus = "Type words to find (\"a phrase\" in quotes stays together), pick channels, then Search.";

    [ObservableProperty]
    private bool _isLogSearching;

    [ObservableProperty]
    private double _logSearchProgress;

    private const string AllCharacters = "All characters";

    /// <summary>Every EverQuest log next to the watched ones (eqlog_Name_server.txt), by character.</summary>
    private List<(string Character, string Path)> SearchableLogs()
    {
        var found = new Dictionary<string, (string Character, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Logs.Select(l => Path.GetDirectoryName(l.FilePath)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "eqlog_*.txt"))
                {
                    if (LogFileName.TryParse(file, out string character, out _))
                    {
                        found[file] = (character, file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that cannot be listed is skipped
            }
        }

        foreach (WatchedLogConfig log in Logs)
        {
            found.TryAdd(log.FilePath, (log.CharacterName, log.FilePath));
        }

        return found.Values.OrderBy(f => f.Character, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fills the character list (called when the tab is opened and when logs change).</summary>
    [RelayCommand]
    private void RefreshLogSearchCharacters()
    {
        string current = LogSearchCharacter;
        LogSearchCharacters = new[] { AllCharacters }
            .Concat(SearchableLogs().Select(f => f.Character).Distinct(StringComparer.OrdinalIgnoreCase))
            .ToList();
        LogSearchCharacter = LogSearchCharacters.Contains(current) ? current : AllCharacters;
    }

    [RelayCommand]
    private async Task SearchLogsAsync()
    {
        if (IsLogSearching)
        {
            return;
        }

        IReadOnlyList<string> terms = LogSearchService.ParseQuery(LogSearchText);
        ChatChannel channels = LogSearchChannels.Where(c => c.IsOn).Aggregate(ChatChannel.None, (all, c) => all | c.Channel);
        if (terms.Count == 0)
        {
            LogSearchStatus = "Type at least one word to search for.";
            return;
        }

        if (channels == ChatChannel.None)
        {
            LogSearchStatus = "Pick at least one channel.";
            return;
        }

        List<(string Character, string Path)> files = SearchableLogs();
        if (LogSearchCharacter != AllCharacters)
        {
            files = files.Where(f => f.Character.Equals(LogSearchCharacter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (files.Count == 0)
        {
            LogSearchStatus = "No logs to search. Add a character's log on the Characters tab.";
            return;
        }

        DateTime? from = LogSearchRangeIndex switch
        {
            0 => _clock().AddDays(-7),
            1 => _clock().AddDays(-30),
            2 => _clock().AddDays(-90),
            _ => null,
        };

        _searchCancel = new CancellationTokenSource();
        CancellationToken token = _searchCancel.Token;
        IsLogSearching = true;
        LogSearchProgress = 0;
        LogSearchStatus = $"Searching {files.Count} log{(files.Count == 1 ? string.Empty : "s")}…";
        var progress = new Progress<double>(p => LogSearchProgress = p * 100);
        try
        {
            string query = LogSearchText;
            LogSearchResult result = await Task.Run(() => LogSearchService.Search(files, query, channels, from, LogSearchService.DefaultMaxHits, progress, token), token);
            LogSearchRows = result.Hits.Select(h => new LogSearchRow(
                    h.Time.ToString("ddd MMM dd HH:mm:ss", CultureInfo.CurrentCulture), h.Character, ChannelName(h.Channel), h.Message))
                .ToList();
            string size = result.BytesSearched >= 1L << 30
                ? (result.BytesSearched / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                : (result.BytesSearched / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";
            LogSearchStatus =
                $"{result.Hits.Count:N0} match{(result.Hits.Count == 1 ? string.Empty : "es")} in {result.FilesSearched} log{(result.FilesSearched == 1 ? string.Empty : "s")} ({size}) " +
                $"in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s." +
                (result.HitLimit ? $" Stopped at {LogSearchService.DefaultMaxHits:N0}; narrow the search to see the rest." : string.Empty) +
                " Double-click a line to copy it.";
        }
        catch (OperationCanceledException)
        {
            LogSearchStatus = "Search cancelled.";
        }
        catch (Exception ex)
        {
            LogSearchStatus = "The search failed: " + ex.Message;
        }
        finally
        {
            IsLogSearching = false;
            LogSearchProgress = 0;
            _searchCancel?.Dispose();
            _searchCancel = null;
        }
    }

    [RelayCommand]
    private void CancelLogSearch() => _searchCancel?.Cancel();

    [RelayCommand]
    private void CopyLogSearchRow(LogSearchRow? row)
    {
        if (row is null)
        {
            return;
        }

        CopyShare($"[{row.Time}] {row.Line}", "Copied the line.");
    }

    private static string ChannelName(ChatChannel channel) => channel switch
    {
        ChatChannel.Tells => "Tells",
        ChatChannel.Guild => "Guild",
        ChatChannel.Raid => "Raid",
        ChatChannel.Group => "Group",
        ChatChannel.Say => "Say",
        ChatChannel.OocShoutAuction => "OOC/Shout",
        ChatChannel.ChatChannels => "Channel",
        _ => "Other",
    };
}
