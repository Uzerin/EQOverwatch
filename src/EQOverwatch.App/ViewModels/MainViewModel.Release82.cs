using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.ResourceHunter;

namespace EQOverwatch.App.ViewModels;

/// <summary>A zone on the Resource Hunter list: today's bonus, where that came from, and what your logs saw there in the last 30 days.</summary>
public sealed record ResourceZoneView(string Zone, string Bonus, string Source, string Last30);

// Release 82: Resource Hunter zone bonuses (Leveling tab, Tells overlay, shared through the EQ Overwatch server) and personal best notices.
public sealed partial class MainViewModel
{
    public const string ResourceFilterAll = "All";

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

    [ObservableProperty]
    private string _selectedResourceFilter = ResourceFilterAll;

    [ObservableProperty]
    private string _resourceHunterStatus = string.Empty;

    public ObservableCollection<ResourceZoneView> ResourceRows { get; } = new();

    public IReadOnlyList<string> ResourceFilters { get; } = new[] { ResourceFilterAll }
        .Concat(new[] { ResourceBonus.Experience, ResourceBonus.Aa, ResourceBonus.Loot, ResourceBonus.Coin, ResourceBonus.RareSpawn,
            ResourceBonus.Respawn, ResourceBonus.Skill, ResourceBonus.Faction }.Select(ResourceHunterText.Label)).ToList();

    /// <summary>The last background read and share, for tests.</summary>
    public Task ResourceHunterTask { get; private set; } = Task.CompletedTask;

    private readonly PersonalBestStore _personalBests = new();
    private IResourceHunterService? _resourceService;
    private DateTime _nextResourceScan = DateTime.MinValue;
    private readonly Dictionary<string, (DateTime Next, string Day)> _nextResourceDownload = new(StringComparer.OrdinalIgnoreCase);
    private bool _resourceBusy;
    private long _resourceVersion = -1;
    private string? _resourceShownFor;
    private string _resourceShownFilter = string.Empty;

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

    partial void OnSelectedResourceFilterChanged(string value) => ShowResourceRows(DateTime.UtcNow);

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

        if (_pipeline.ResourceHunter.Version != _resourceVersion || _resourceShownFor != SelectedLevelingServer)
        {
            ShowResourceRows(nowUtc);
        }
    }

    /// <summary>Reads the logs, shares today's new findings and fetches today's bonuses where due (also called by tests).</summary>
    public async Task RunResourceHunterAsync(IReadOnlyList<(string Path, string Server)> logs, DateTime nowUtc)
    {
        _resourceBusy = true;
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
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
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
                if (ShareResourceBonuses)
                {
                    try
                    {
                        store.SetReported(server, ResourceBonusSource.Shared, day, await service.SharedAsync(server, day));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
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
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        problems.Add(ex.Message);
                    }
                }
                else
                {
                    store.SetReported(server, ResourceBonusSource.Website, day, Array.Empty<ResourceZoneBonus>());
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
        }
    }

    private string? _resourceProblem;

    /// <summary>Fills the Resource Hunter list for the Leveling tab's server and the chosen bonus type.</summary>
    public void ShowResourceRows(DateTime nowUtc)
    {
        ResourceHunterStore store = _pipeline.ResourceHunter;
        _resourceVersion = store.Version;
        _resourceShownFor = SelectedLevelingServer;
        bool filterChanged = _resourceShownFilter != SelectedResourceFilter;
        _resourceShownFilter = SelectedResourceFilter;
        ResourceRows.Clear();
        if (SelectedLevelingServer is not { } server)
        {
            ResourceHunterStatus = "Pick a server above.";
            return;
        }

        IReadOnlyList<ResourceZoneBonus> today = store.Today(server, nowUtc);
        string? site = ResourceHunterText.WebsiteFor(server);
        foreach (ResourceZoneBonus z in today.Where(z => SelectedResourceFilter == ResourceFilterAll || ResourceHunterText.Label(z.Bonus) == SelectedResourceFilter))
        {
            string source = z.Source switch
            {
                ResourceBonusSource.Logs => "Your logs",
                ResourceBonusSource.Shared => z.Reports == 1 ? "1 player" : $"{z.Reports.ToString(CultureInfo.CurrentCulture)} players",
                _ => site is null ? "Website" : new Uri(site).Host,
            };
            string last30 = string.Join(", ", store.History(server, z.Zone, nowUtc)
                .Select(h => $"{ResourceHunterText.Label(h.Bonus)} {h.Days.ToString(CultureInfo.CurrentCulture)}"));
            ResourceRows.Add(new ResourceZoneView(z.Zone, ResourceHunterText.Label(z.Bonus), source, last30.Length == 0 ? "—" : last30));
        }

        int withBonus = today.Count(z => z.Bonus != ResourceBonus.None);
        string from = site is null ? "your logs and other EQ Overwatch players" : $"your logs, other EQ Overwatch players and {new Uri(site).Host}";
        ResourceHunterStatus = today.Count == 0
            ? "No Resource Hunter bonuses known for today on this server yet. They show up as your characters enter zones that have one."
            : $"{withBonus.ToString(CultureInfo.CurrentCulture)} zones with a bonus today (Pacific day {ResourceHunterText.Day(nowUtc)}), from {from}."
              + (_resourceProblem is { } problem ? $" Last problem: {problem}" : string.Empty);

        if (!filterChanged)
        {
            ShowLeveling(rebuildExpansions: false); // the Bonus Today column
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
