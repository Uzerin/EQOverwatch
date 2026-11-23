using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.ResourceHunter;

namespace EQOverwatch.App.ViewModels;

/// <summary>A zone on a Resource Hunter table: its levels, where the day's bonus came from, and what it had in the 30 days up to that day.</summary>
public sealed record ResourceZoneView(string Zone, string Levels, string Bonus, string Source, string Last30);

/// <summary>One Resource Hunter table: the zones that had one bonus (or none, or nothing known) on the shown day, highest levels first.</summary>
public sealed record ResourceGroupView(string Title, IReadOnlyList<ResourceZoneView> Rows);

/// <summary>A row of the picked zone's breakdown: "Loot", "12", "8.3%".</summary>
public sealed record ResourceShareView(string Bonus, string Days, string Percent);

// Release 82: Resource Hunter zone bonuses (Leveling tab, Tells overlay, shared through the EQ Overwatch server) and personal best notices.
public sealed partial class MainViewModel
{
    public const string ResourceDayToday = "Today";

    /// <summary>How soon a failed download of the bonuses is tried again.</summary>
    public static readonly TimeSpan ResourceRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>How often the logs are read for bonus lines and new findings are shared.</summary>
    public static readonly TimeSpan ResourceScanEvery = TimeSpan.FromMinutes(2);

    /// <summary>How often today's bonuses are fetched from the EQ Overwatch server and the server's website.</summary>
    public static readonly TimeSpan ResourceDownloadEvery = TimeSpan.FromHours(1);

    /// <summary>A personal best is only announced for a fight that ended this recently (older fights caught up from the logs are filed quietly).</summary>
    public static readonly TimeSpan PersonalBestRecent = TimeSpan.FromMinutes(10);

    /// <summary>"Karnor's Castle: AA bonus" on the Tells overlay when one of your characters enters a zone with a bonus.</summary>
    [ObservableProperty]
    private bool _resourceHunterNotices = true;

    /// <summary>Fill in zones your logs have not seen from the server's tracking website (Frostreaver and Fangbreaker).</summary>
    [ObservableProperty]
    private bool _useResourceWebsites = true;

    /// <summary>Share the bonuses your logs find (zone, bonus and day only), and use what other players shared.</summary>
    [ObservableProperty]
    private bool _shareResourceBonuses = true;

    /// <summary>A Windows notification when one of your characters beats its best DPS, overall or against a mob.</summary>
    [ObservableProperty]
    private bool _personalBestToast = true;

    /// <summary>The day the Resource Hunter tab shows: <see cref="ResourceDayToday"/> or "2026-11-20".</summary>
    [ObservableProperty]
    private string? _selectedResourceDay = ResourceDayToday;

    /// <summary>"Today" and every past day anything is known for, newest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _resourceDayChoices = new[] { ResourceDayToday };

    /// <summary>"Acrylia Caverns: 205 days since 2026-05-01" over the picked zone's breakdown.</summary>
    [ObservableProperty]
    private string _resourceBreakdownTitle = "Pick a zone to see how often it had each bonus.";

    [ObservableProperty]
    private string _resourceHunterStatus = string.Empty;

    /// <summary>The shown day's tables: each bonus, then "No bonus", then "Unknown".</summary>
    public ObservableCollection<ResourceGroupView> ResourceGroups { get; } = new();

    /// <summary>How often the picked zone had each bonus over its whole history.</summary>
    public ObservableCollection<ResourceShareView> ResourceBreakdown { get; } = new();

    private static readonly ResourceBonus[] ResourceTableOrder =
    {
        ResourceBonus.Experience, ResourceBonus.Aa, ResourceBonus.Loot, ResourceBonus.Coin, ResourceBonus.RareSpawn, ResourceBonus.Respawn,
        ResourceBonus.Skill, ResourceBonus.Faction, ResourceBonus.None, ResourceBonus.Unknown,
    };

    /// <summary>The pause between past days read from a website (tests set none).</summary>
    public TimeSpan ResourceBackfillPause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The last reading of past days from the websites, for tests.</summary>
    public Task ResourceBackfillTask { get; private set; } = Task.CompletedTask;

    private bool _backfillBusy;
    private DateTime _nextBackfill = DateTime.MinValue;
    private string? _backfillProgress;
    private string? _resourcePickedZone;
    private ResourceZoneView? _resourcePickedRow;
    private bool _showingResources;

    /// <summary>The zone row clicked on any table (the tables only set it; clearing a table's selection keeps the pick).</summary>
    public ResourceZoneView? ResourcePickedRow
    {
        get => _resourcePickedRow;
        set
        {
            if (value is null || _showingResources)
            {
                return;
            }

            _resourcePickedRow = value;
            _resourcePickedZone = value.Zone;
            ShowResourceBreakdown(DateTime.UtcNow);
        }
    }

    /// <summary>The last background read and share, for tests.</summary>
    public Task ResourceHunterTask { get; private set; } = Task.CompletedTask;

    private readonly PersonalBestStore _personalBests = new();
    private IResourceHunterService? _resourceService;
    private DateTime _nextResourceScan = DateTime.MinValue;
    private readonly Dictionary<string, (DateTime Next, string Day)> _nextResourceDownload = new(StringComparer.OrdinalIgnoreCase);
    private bool _resourceBusy;
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private long _resourceVersion = -1;
    private string? _resourceShownFor;

    partial void OnResourceHunterNoticesChanged(bool value) => SaveUnlessRestoring();

    partial void OnUseResourceWebsitesChanged(bool value)
    {
        _nextResourceDownload.Clear();
        _nextResourceScan = DateTime.MinValue;
        SaveUnlessRestoring();
    }

    partial void OnShareResourceBonusesChanged(bool value)
    {
        _nextResourceDownload.Clear();
        _nextResourceScan = DateTime.MinValue;
        SaveUnlessRestoring();
    }

    partial void OnPersonalBestToastChanged(bool value) => SaveUnlessRestoring();

    partial void OnSelectedResourceDayChanged(string? value)
    {
        if (!_showingResources)
        {
            ShowResourceRows(DateTime.UtcNow);
        }
    }

    /// <summary>Downloads today's bonuses (and any missing past days) again now.</summary>
    [RelayCommand]
    private void RefreshResourceBonuses()
    {
        _nextResourceDownload.Clear();
        _nextResourceScan = DateTime.MinValue;
        _nextBackfill = DateTime.MinValue;
    }

    /// <summary>The personal bests, for tests.</summary>
    public PersonalBestStore PersonalBests => _personalBests;

    /// <summary>The app supplies the HTTP service; tests a fake.</summary>
    public void ConfigureResourceHunter(IResourceHunterService service) => _resourceService = service;

    private void InitializeRelease82()
    {
        if (_dataDirectory is { } folder)
        {
            _pipeline.ResourceHunter.SetStoragePath(Path.Combine(folder, "resourcehunter.json"));
            _personalBests.SetStoragePath(Path.Combine(folder, "personalbests.json"));
        }

        _pipeline.ResourceHunter.Found += OnResourceBonusFound;
    }

    private void RestoreRelease82(Core.Settings.AppSettings settings)
    {
        ResourceHunterNotices = settings.ResourceHunterNotices;
        UseResourceWebsites = settings.UseResourceWebsites;
        ShareResourceBonuses = settings.ShareResourceBonuses;
        PersonalBestToast = settings.PersonalBestToast;
    }

    private void SaveRelease82(Core.Settings.AppSettings settings)
    {
        settings.ResourceHunterNotices = ResourceHunterNotices;
        settings.UseResourceWebsites = UseResourceWebsites;
        settings.ShareResourceBonuses = ShareResourceBonuses;
        settings.PersonalBestToast = PersonalBestToast;
    }

    // On the tailing thread: one of your characters' logs just showed the bonus of the zone it entered.
    private void OnResourceBonusFound(string character, string server, string zone, ResourceBonus bonus)
    {
        if (ResourceHunterNotices)
        {
            _pipeline.Notices.Add("bonus", $"{zone}: {ResourceHunterText.Label(bonus)} bonus", BoxAlertClock());
        }

        _nextResourceScan = DateTime.MinValue; // share it at the next tick
    }

    /// <summary>Every tick: reads the logs, shares and downloads when due, and keeps the list current.</summary>
    private void RefreshRelease82(DateTime nowUtc)
    {
        if (!_resourceBusy && nowUtc >= _nextResourceScan)
        {
            _nextResourceScan = nowUtc + ResourceScanEvery;
            var logs = Logs.Where(l => !string.IsNullOrEmpty(l.FilePath) && !string.IsNullOrEmpty(l.ServerName))
                .Select(l => (l.FilePath, l.ServerName)).Distinct().ToList();
            ResourceHunterTask = RunResourceHunterAsync(logs, nowUtc);
        }

        if (!_backfillBusy && nowUtc >= _nextBackfill && _resourceService is not null && UseResourceWebsites)
        {
            _nextBackfill = nowUtc + ResourceDownloadEvery;
            var servers = Logs.Where(l => !string.IsNullOrEmpty(l.ServerName) && ResourceHunterText.WebsiteFor(l.ServerName) is not null)
                .Select(l => l.ServerName.ToLowerInvariant()).Distinct().ToList();
            if (servers.Count > 0)
            {
                ResourceBackfillTask = BackfillResourceHistoryAsync(servers, nowUtc);
            }
        }

        if (_pipeline.ResourceHunter.Version != _resourceVersion || _resourceShownFor != SelectedLevelingServer)
        {
            ShowResourceRows(nowUtc);
        }
    }

    /// <summary>Reads the logs, shares today's new findings and fetches today's bonuses where due (also called by tests).</summary>
    public async Task RunResourceHunterAsync(IReadOnlyList<(string Path, string Server)> logs, DateTime nowUtc)
    {
        _resourceBusy = true;
        await _resourceGate.WaitAsync();
        try
        {
            ResourceHunterStore store = _pipeline.ResourceHunter;
            await Task.Run(() => store.Scan(logs, nowUtc));
            if (_resourceService is not { } service)
            {
                return;
            }

            string day = ResourceHunterText.Day(nowUtc);
            List<string> servers = logs.Select(l => l.Server.ToLowerInvariant()).Concat(store.Servers()).Distinct().ToList();
            var problems = new List<string>();
            foreach (string server in servers)
            {
                if (ShareResourceBonuses && store.Unshared(server, nowUtc) is { Count: > 0 } found)
                {
                    try
                    {
                        EnsureSharingId();
                        if (await service.UploadAsync(server, _levelingId, _levelingKey, found))
                        {
                            store.MarkShared(server, found);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        problems.Add(ex.Message);
                    }
                }

                (DateTime Next, string Day) due = _nextResourceDownload.GetValueOrDefault(server);
                if (nowUtc < due.Next && due.Day == day)
                {
                    continue;
                }

                _nextResourceDownload[server] = (nowUtc + ResourceDownloadEvery, day);
                int problemsBefore = problems.Count;
                if (ShareResourceBonuses)
                {
                    try
                    {
                        store.SetReported(server, ResourceBonusSource.Shared, day, await service.SharedAsync(server, day));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        problems.Add(ex.Message);
                    }
                }
                else
                {
                    store.SetReported(server, ResourceBonusSource.Shared, day, Array.Empty<ResourceZoneBonus>());
                }

                if (UseResourceWebsites && ResourceHunterText.WebsiteFor(server) is not null)
                {
                    try
                    {
                        store.SetReported(server, ResourceBonusSource.Website, day, await service.WebsiteAsync(server, day));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        problems.Add(ex.Message);
                    }
                }
                else
                {
                    store.SetReported(server, ResourceBonusSource.Website, day, Array.Empty<ResourceZoneBonus>());
                }

                if (problems.Count > problemsBefore)
                {
                    _nextResourceDownload[server] = (nowUtc + ResourceRetryAfter, day);
                }
            }

            _resourceProblem = problems.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _resourceProblem = ex.Message;
        }
        finally
        {
            _resourceBusy = false;
            _resourceGate.Release();
        }
    }

    private string? _resourceProblem;

    /// <summary>Reads the past days each website has that are not kept yet, one at a time (also called by tests).</summary>
    public async Task BackfillResourceHistoryAsync(IReadOnlyList<string> servers, DateTime nowUtc)
    {
        if (_resourceService is not { } service)
        {
            return;
        }

        _backfillBusy = true;
        ResourceHunterStore store = _pipeline.ResourceHunter;
        try
        {
            foreach (string server in servers)
            {
                IReadOnlyList<string> missing = store.MissingDays(server, nowUtc);
                for (int i = 0; i < missing.Count; i++)
                {
                    _backfillProgress = $"Reading past days from {new Uri(ResourceHunterText.WebsiteFor(server)!).Host}: {(i + 1).ToString(CultureInfo.CurrentCulture)} of {missing.Count.ToString(CultureInfo.CurrentCulture)}.";
                    store.RecordWebsiteDay(server, missing[i], await service.WebsiteDayAsync(server, missing[i]), save: i % 20 == 19);
                    if (ResourceBackfillPause > TimeSpan.Zero)
                    {
                        await Task.Delay(ResourceBackfillPause);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _resourceProblem = ex.Message;
            _nextBackfill = nowUtc + ResourceRetryAfter;
        }
        finally
        {
            store.Flush();
            _backfillProgress = null;
            _backfillBusy = false;
            _resourceVersion = -1; // show the status without the progress
        }
    }

    /// <summary>Fills the Resource Hunter tables for the server picked at the top and the chosen day.</summary>
    public void ShowResourceRows(DateTime nowUtc)
    {
        ResourceHunterStore store = _pipeline.ResourceHunter;
        bool dataChanged = _resourceVersion != store.Version || _resourceShownFor != SelectedLevelingServer;
        _resourceVersion = store.Version;
        _resourceShownFor = SelectedLevelingServer;
        _showingResources = true;
        try
        {
            ResourceGroups.Clear();
            if (SelectedLevelingServer is not { } server)
            {
                ResourceHunterStatus = "Pick a server above.";
                return;
            }

            string today = ResourceHunterText.Day(nowUtc);
            IReadOnlyList<string> days = new[] { ResourceDayToday }.Concat(store.KnownDays(server).Where(d => d != today)).ToList();
            string picked = SelectedResourceDay is { } chosen && days.Contains(chosen) ? chosen : ResourceDayToday;
            if (!days.SequenceEqual(ResourceDayChoices))
            {
                ResourceDayChoices = days;
            }

            SelectedResourceDay = picked;
            string day = picked == ResourceDayToday ? today : picked;

            // A moment on that Pacific day, for the 30 days before it.
            DateTime dayUtc = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddHours(20);
            DateTime at = day == today ? nowUtc : DateTime.SpecifyKind(dayUtc, DateTimeKind.Utc);
            var zones = store.OnDay(server, day).ToList();
            string? site = ResourceHunterText.WebsiteFor(server);
            HashSet<string> killed = KilledMobs();
            if (site is null)
            {
                // Without a website, every zone your /cons saw that nobody reported is unknown.
                var keys = zones.Select(z => ResourceHunterText.ZoneKey(z.Zone)).ToHashSet(StringComparer.Ordinal);
                foreach (string zone in _mobLevels.All().Select(l => l.Zone).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (keys.Add(ResourceHunterText.ZoneKey(zone)))
                    {
                        zones.Add(new ResourceZoneBonus(zone, ResourceBonus.Unknown, ResourceBonusSource.Logs));
                    }
                }
            }

            CultureInfo culture = CultureInfo.CurrentCulture;
            foreach (ResourceBonus bonus in ResourceTableOrder)
            {
                var rows = zones.Where(z => z.Bonus == bonus)
                    .Select(z => (Zone: z, Range: z.MaxLevel > 0 ? (z.MinLevel, z.MaxLevel) : ZoneMobRange(z.Zone, killed)))
                    .OrderByDescending(z => z.Range?.Item2 ?? -1).ThenByDescending(z => z.Range?.Item1 ?? -1).ThenBy(z => z.Zone.Zone, StringComparer.OrdinalIgnoreCase)
                    .Select(z =>
                    {
                        string source = z.Zone.Bonus == ResourceBonus.Unknown ? "—" : z.Zone.Source switch
                        {
                            ResourceBonusSource.Logs => "Your logs",
                            ResourceBonusSource.Shared => z.Zone.Reports == 1 ? "1 player" : $"{z.Zone.Reports.ToString(culture)} players",
                            _ => site is null ? "Website" : new Uri(site).Host,
                        };
                        string last30 = string.Join(", ", store.History(server, z.Zone.Zone, at)
                            .Select(h => $"{ResourceHunterText.Label(h.Bonus)} {h.Days.ToString(culture)}"));
                        string levels = z.Range is { } r ? $"{r.Item1.ToString(culture)}–{r.Item2.ToString(culture)}" : "—";
                        return new ResourceZoneView(z.Zone.Zone, levels, ResourceHunterText.Label(bonus), source, last30.Length == 0 ? "—" : last30);
                    })
                    .ToList();
                if (rows.Count > 0)
                {
                    string title = bonus == ResourceBonus.None ? "No bonus" : ResourceHunterText.Label(bonus);
                    ResourceGroups.Add(new ResourceGroupView($"{title} ({rows.Count.ToString(culture)})", rows));
                }
            }

            int withBonus = zones.Count(z => z.Bonus is not ResourceBonus.None and not ResourceBonus.Unknown);
            string from = site is null ? "your logs and other EQ Overwatch players" : $"your logs, other EQ Overwatch players and {new Uri(site).Host}";
            string dayText = day == today ? $"today (Pacific day {day})" : $"on {day}";
            ResourceHunterStatus = (zones.Count == 0
                    ? $"No Resource Hunter bonuses known {dayText} on this server yet. They show up as your characters enter zones that have one."
                    : $"{withBonus.ToString(culture)} zones with a bonus {dayText}, from {from}.")
                + (_backfillProgress is { } progress ? " " + progress : string.Empty)
                + (_resourceProblem is { } problem ? $" Last problem: {problem}" : string.Empty);
        }
        finally
        {
            _showingResources = false;
        }

        ShowResourceBreakdown(nowUtc);
        if (dataChanged)
        {
            ShowLeveling(rebuildExpansions: false); // the Bonus Today column
        }
    }

    /// <summary>Fills the picked zone's share of each bonus over its whole history.</summary>
    private void ShowResourceBreakdown(DateTime nowUtc)
    {
        ResourceBreakdown.Clear();
        if (_resourcePickedZone is not { } zone || SelectedLevelingServer is not { } server)
        {
            return;
        }

        (IReadOnlyList<ResourceShare> shares, string from, int days) = _pipeline.ResourceHunter.Breakdown(server, zone, nowUtc);
        CultureInfo culture = CultureInfo.CurrentCulture;
        ResourceBreakdownTitle = $"{zone}: {days.ToString(culture)} days since {from}";
        foreach (ResourceShare share in shares)
        {
            ResourceBreakdown.Add(new ResourceShareView(
                share.Bonus == ResourceBonus.None ? "No bonus" : ResourceHunterText.Label(share.Bonus),
                share.Days.ToString(culture),
                share.Percent.ToString("0.0", culture) + "%"));
        }
    }

    /// <summary>"Loot" when today's bonus of the zone is known on the Leveling tab's server, otherwise "—".</summary>
    private string BonusTodayIn(string zone) =>
        SelectedLevelingServer is { } server && _pipeline.ResourceHunter.TodayIn(server, zone, DateTime.UtcNow) is { } bonus
            ? ResourceHunterText.Label(bonus.Bonus)
            : "—";

    // ───────────────────────── personal bests ─────────────────────────

    /// <summary>Checks finished live fights for new personal bests; recent ones are announced.</summary>
    private void CheckPersonalBests(IReadOnlyList<EncounterSnapshot> fights)
    {
        DateTime now = _clock();
        foreach (EncounterSnapshot fight in fights)
        {
            IReadOnlyList<PersonalBest> beaten = _personalBests.Check(fight, name => _ownCharacters.ContainsKey(name));
            if (!PersonalBestToast || now - fight.EndTime > PersonalBestRecent)
            {
                continue;
            }

            CultureInfo culture = CultureInfo.CurrentCulture;
            foreach (PersonalBest best in beaten)
            {
                string dps = best.Dps.ToString("N0", culture);
                string before = best.Previous.ToString("N0", culture);
                LiveNoticeRequested?.Invoke(best.Overall
                    ? new NoticeInfo($"{best.Character}: new personal best", $"{dps} DPS against {best.Mob} (was {before}).")
                    : new NoticeInfo($"{best.Character}: best DPS against {best.Mob}", $"{dps} DPS (was {before})."), false);
            }
        }
    }

    /// <summary>Marks the fights that hold one of your characters' best DPS (gold in the fight list).</summary>
    private void MarkPersonalBestRows()
    {
        IReadOnlySet<string> bests = _personalBests.BestFights();
        foreach (EncounterRowViewModel row in Encounters)
        {
            row.IsPersonalBest = bests.Count > 0 && row.Snapshot is { } fight
                && fight.Participants.Any(p => _ownCharacters.ContainsKey(p.Name) && bests.Contains(PersonalBestStore.FightKey(fight, p.Name)));
        }
    }
}
