using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.ResourceHunter;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.ViewModels;

/// <summary>A zone on the Leveling tab.</summary>
public sealed record LevelingZoneView(string Zone, string Expansion, string XpPerHour, string KillsPerHour, int Players, string Hours, string Data,
    string PlatPerHour = "—", string MobLevels = "—", string BonusToday = "—", string WithBonus = "—");

/// <summary>A mob in the selected zone: its levels and the experience per kill your characters got near your level.</summary>
public sealed record BestMobView(string Mob, string Levels, string XpPerKill, int Kills);

/// <summary>One level band of the leveling path.</summary>
public sealed record LevelingPathView(string Levels, string Zone, string XpPerHour);

/// <summary>An expansion checkbox on the Leveling tab.</summary>
public sealed partial class ExpansionChoice : ObservableObject
{
    private readonly Action _changed;

    public ExpansionChoice(int number, string name, bool isOn, Action changed)
    {
        Number = number;
        Name = name;
        _isOn = isOn;
        _changed = changed;
    }

    public int Number { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();
}

// The Leveling tab: the best zones for a level band and a path to the top, from your logs and (when switched on) everyone's.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan LevelingScanEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LevelingDownloadEvery = TimeSpan.FromHours(1);
    private const int TopZones = 3;

    private readonly LevelingStore _leveling = new();
    private readonly Dictionary<string, SharedLeveling> _sharedLeveling = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextLevelingDownload = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextLevelingUpload = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<int>> _levelingExpansions = new(StringComparer.OrdinalIgnoreCase);
    private ILevelingService? _levelingService;
    private DateTime _nextLevelingScan = DateTime.MinValue;
    private bool _levelingBusy;
    private bool _levelingShowing;
    private string _levelingId = string.Empty;
    private string _levelingKey = string.Empty;
    private ZoneDirectory? _levelingZones;
    private string _levelingZonesFolder = string.Empty;

    public ObservableCollection<string> LevelingServers { get; } = new();
    public ObservableCollection<ExpansionChoice> LevelingExpansionChoices { get; } = new();
    public ObservableCollection<LevelingZoneView> LevelingZoneRows { get; } = new();
    public ObservableCollection<LevelingPathView> LevelingPathRows { get; } = new();

    [ObservableProperty]
    private string? _selectedLevelingServer;

    [ObservableProperty]
    private int _levelingLevel = 1;

    [ObservableProperty]
    private bool _levelingGroup = true;

    [ObservableProperty]
    private bool _useSharedLeveling;

    [ObservableProperty]
    private bool _shareMyLeveling;

    [ObservableProperty]
    private string _levelingBandText = "1–5";

    [ObservableProperty]
    private string _levelingStatus = "Reading your logs for leveling…";

    [ObservableProperty]
    private string _levelingExpansionHint = string.Empty;

    /// <summary>The last background scan, upload or download (for tests).</summary>
    public Task LevelingTask { get; private set; } = Task.CompletedTask;

    /// <summary>Connects the shared leveling service and where your stretches are kept.</summary>
    public void ConfigureLeveling(ILevelingService service, string storagePath)
    {
        _levelingService = service;
        _leveling.SetStoragePath(storagePath);
        _nextLevelingScan = DateTime.MinValue;
        ShowLeveling();
    }

    private void RestoreLeveling(Core.Settings.AppSettings settings)
    {
        _levelingId = settings.LevelingId ?? string.Empty;
        _levelingKey = settings.LevelingKey ?? string.Empty;
        _levelingExpansions = new Dictionary<string, List<int>>(settings.LevelingExpansions ?? new(), StringComparer.OrdinalIgnoreCase);
        LevelingGroup = settings.LevelingGroup;
        UseSharedLeveling = settings.UseSharedLeveling;
        ShareMyLeveling = settings.ShareMyLeveling;
        ShareServerFirsts = settings.ShareServerFirsts;
    }

    partial void OnUseSharedLevelingChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        _nextLevelingDownload.Clear();
        _nextLevelingScan = DateTime.MinValue;
        ShowLeveling();
    }

    partial void OnShareMyLevelingChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        _nextLevelingUpload.Clear();
        _nextLevelingScan = DateTime.MinValue;
    }

    /// <summary>The Solo button.</summary>
    public bool LevelingSolo
    {
        get => !LevelingGroup && !LevelingAa;
        set
        {
            if (value)
            {
                LevelingAa = false;
                LevelingGroup = false;
            }
        }
    }

    /// <summary>The Group button.</summary>
    public bool LevelingGroupChoice
    {
        get => LevelingGroup && !LevelingAa;
        set
        {
            if (value)
            {
                LevelingAa = false;
                LevelingGroup = true;
            }
        }
    }

    partial void OnLevelingGroupChanged(bool value)
    {
        OnPropertyChanged(nameof(LevelingSolo));
        OnPropertyChanged(nameof(LevelingGroupChoice));
        if (!_restoring)
        {
            RequestSave();
        }

        ShowLeveling();
    }

    partial void OnLevelingLevelChanged(int value) => ShowLeveling();

    partial void OnSelectedLevelingServerChanged(string? value)
    {
        if (_levelingShowing || value is null)
        {
            return;
        }

        LevelingStretch? latest = _leveling.Stretches(value).MaxBy(s => s.Start);
        _levelingShowing = true;
        LevelingLevel = latest?.Level ?? 1;
        int top = _leveling.Stretches(value).Select(s => s.Level).DefaultIfEmpty(0).Max();
        LevelingAa = top > 0 && LevelingLevel >= top && LevelingLevel >= 50; // at the server's highest level seen: AA points
        _levelingShowing = false;
        ShowLeveling(rebuildExpansions: true);
    }

    /// <summary>Goes back to the expansions found from the zones seen on the server.</summary>
    [RelayCommand]
    private void UseZonesSeenForExpansions()
    {
        if (SelectedLevelingServer is { } server && _levelingExpansions.Remove(server))
        {
            RequestSave();
        }

        ShowLeveling(rebuildExpansions: true);
    }

    /// <summary>Every tick: reads new log lines for leveling every few minutes, then shares and downloads when switched on.</summary>
    private void RefreshLeveling(DateTime nowUtc)
    {
        if (_levelingBusy || nowUtc < _nextLevelingScan || Logs.Count == 0)
        {
            return;
        }

        _nextLevelingScan = nowUtc + LevelingScanEvery;
        var logs = Logs.Where(l => !string.IsNullOrEmpty(l.FilePath) && !string.IsNullOrEmpty(l.ServerName))
            .Select(l => (l.FilePath, l.ServerName)).Distinct().ToList();
        LevelingTask = RunLevelingAsync(logs, nowUtc);
    }

    private async Task RunLevelingAsync(List<(string Path, string Server)> logs, DateTime nowUtc)
    {
        _levelingBusy = true;
        try
        {
            await Task.Run(() => _leveling.Scan(logs, nowUtc));
            IEnumerable<string> servers = _leveling.Servers().Concat(logs.Select(l => l.Server.ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (_levelingService is { } remover && _leveling.ReadAgain)
            {
                // Read again under a newer power-leveling rule: what was shared under the old one is taken down, then shared again.
                if (_levelingId.Length > 0 && _levelingKey.Length > 0)
                {
                    foreach (string server in servers)
                    {
                        await remover.DeleteAsync(server, _levelingId, _levelingKey);
                    }
                }

                _leveling.ReadAgainHandled();
            }

            if (_levelingService is { } service)
            {
                foreach (string server in servers)
                {
                    await ShareLevelingAsync(service, server, nowUtc);
                    await DownloadLevelingAsync(service, server, nowUtc);
                }

                await DownloadOtherServersAsync(service, servers, nowUtc);

                ReportSharing("Leveling", null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LevelingStatus = "Shared leveling: " + ex.Message;
            ReportSharing("Leveling", ex.Message);
            _nextLevelingUpload.Clear(); // try again at the next scan rather than in a day
        }
        finally
        {
            _levelingBusy = false;
        }

        // The first scan of a log can finish after the tab was filled: pick up the level and the expansions it found.
        if (SelectedLevelingServer is { } current)
        {
            if (LevelingLevel <= 1 && _leveling.Stretches(current).MaxBy(s => s.Start) is { } latest)
            {
                OnSelectedLevelingServerChanged(current);
            }
            else
            {
                ShowLeveling(rebuildExpansions: !_levelingExpansions.ContainsKey(current));
            }
        }

        UpdateShareSummaries();
    }

    private async Task ShareLevelingAsync(ILevelingService service, string server, DateTime nowUtc)
    {
        if (!ShareMyLeveling || nowUtc < _nextLevelingUpload.GetValueOrDefault(server))
        {
            return;
        }

        IReadOnlyList<LevelingStretch> unshared = _leveling.Unshared(server);
        _nextLevelingUpload[server] = nowUtc + SharedDropsUploadEvery;
        if (unshared.Count == 0)
        {
            return;
        }

        EnsureSharingId();
        List<LevelingStretch> batch = unshared.OrderBy(s => s.Start).Take(20_000).ToList();
        if (await service.UploadAsync(server, _levelingId, _levelingKey, batch))
        {
            _leveling.MarkShared(server, batch[^1].Start);
        }
    }

    private async Task DownloadLevelingAsync(ILevelingService service, string server, DateTime nowUtc)
    {
        if (!UseSharedLeveling || nowUtc < _nextLevelingDownload.GetValueOrDefault(server))
        {
            return;
        }

        _nextLevelingDownload[server] = nowUtc + LevelingDownloadEvery;
        _sharedLeveling[server] = await service.DownloadAsync(server, _levelingId.Length > 0 ? _levelingId : "none");
        _sharedLevelingAt[server] = DateTime.UtcNow;
    }

    private ZoneDirectory LevelingZones()
    {
        string folder = EqFolderPath ?? string.Empty;
        if (_levelingZones is null || !folder.Equals(_levelingZonesFolder, StringComparison.OrdinalIgnoreCase))
        {
            _levelingZones = ZoneDirectory.Load(folder);
            _levelingZonesFolder = folder;
        }

        return _levelingZones;
    }

    /// <summary>Refills the Leveling tab from your stretches and the shared rows (UI thread).</summary>
    private void ShowLeveling(bool rebuildExpansions = false)
    {
        if (_levelingShowing)
        {
            return;
        }

        _levelingShowing = true;
        try
        {
            List<string> servers = _leveling.Servers()
                .Concat(Logs.Select(l => l.ServerName).Where(s => !string.IsNullOrEmpty(s)).Select(s => s.ToLowerInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (!servers.SequenceEqual(LevelingServers, StringComparer.OrdinalIgnoreCase))
            {
                string? keep = SelectedLevelingServer;
                LevelingServers.Clear();
                foreach (string s in servers)
                {
                    LevelingServers.Add(s);
                }

                SelectedLevelingServer = keep is not null && servers.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : servers.FirstOrDefault();
                if (SelectedLevelingServer is { } first && keep is null)
                {
                    LevelingLevel = _leveling.Stretches(first).MaxBy(s => s.Start)?.Level ?? 1;
                }

                rebuildExpansions = true;
            }

            string? server = SelectedLevelingServer;
            LevelingZoneRows.Clear();
            LevelingPathRows.Clear();
            if (server is null)
            {
                LevelingStatus = "Add a character's log on the Characters tab to see leveling zones.";
                return;
            }

            IReadOnlyList<LevelingStretch> mine = _leveling.Stretches(server);
            SharedLeveling? shared = UseSharedLeveling ? _sharedLeveling.GetValueOrDefault(server) : null;
            List<LevelingRow> rows = LevelingRowsFor(server);
            LevelingMode mode = LevelingMode_;

            ZoneDirectory zones = LevelingZones();
            IReadOnlySet<int> open = LevelingOpenExpansions(server, zones, mine.Select(s => s.Zone).Concat(shared?.Zones ?? Array.Empty<string>()), rebuildExpansions);
            Func<string, bool> allowed = zone => LevelingRanking.ExpansionOf(zones, zone) is not { } e || e == 0 || open.Contains(e);

            CultureInfo culture = CultureInfo.CurrentCulture;
            int band = LevelingRanking.BandOf(LevelingLevel);
            LevelingBandText = LevelingRanking.BandText(band);
            IReadOnlyList<ZoneRank> ranked = LevelingRanking.Rank(rows, band, mode, allowed);
            string Rate(double value) => mode == LevelingMode.Aa ? value.ToString("0.0", culture) + " AA" : value.ToString("0.0", culture) + "%";
            string? keepZone = SelectedLevelingZone?.Zone;
            // On Experience bonus days a zone's rate with today's bonus counts for the ranking.
            double? Boosted(ZoneRank r) => mode != LevelingMode.Aa && BonusTodayIn(r.Zone) == ResourceHunterText.Label(ResourceBonus.Experience)
                && TodayExperienceSize(server, r.Zone) is { } pct ? r.XpPerHour * (1 + (pct / 100)) : null;
            var shown = ranked.Select(r => (Rank: r, Boost: Boosted(r)))
                .OrderByDescending(x => x.Rank.Enough).ThenByDescending(x => x.Boost ?? x.Rank.XpPerHour).Select(x => x.Rank).ToList();
            foreach (ZoneRank r in shown.Where(r => r.Enough).Take(TopZones).Concat(shown.Where(r => !r.Enough)).Take(TopZones))
            {
                LevelingZoneRows.Add(new LevelingZoneView(
                    r.Zone,
                    LevelingRanking.ExpansionOf(zones, r.Zone) is { } e && e > 0 ? ZoneDirectory.ExpansionName(e) : "—",
                    Rate(r.XpPerHour),
                    r.KillsPerHour.ToString("0", culture),
                    r.Players,
                    r.Hours.ToString("0.0", culture),
                    (r.Enough ? "Ranked" : "Not enough data") + (_levelingAdjustedZones.Contains(r.Zone) ? " · adjusted" : string.Empty),
                    r.PlatPerHour > 0 ? r.PlatPerHour.ToString("N0", culture) + "p" : "—",
                    ZoneMobLevels(r.Zone),
                    BonusTodayIn(r.Zone),
                    Boosted(r) is { } boosted ? "≈ " + Rate(boosted) : "—"));
            }

            if (!ranked.Any(r => r.Enough) && mode != LevelingMode.Aa)
            {
                // Nothing ranked on this server for the band yet: what other servers' players found, scaled to this server's XP rate.
                foreach (ZoneRank r in OtherServersRanking(server, band, mode, allowed).Where(r => r.Enough).Take(TopZones))
                {
                    LevelingZoneRows.Add(new LevelingZoneView(
                        r.Zone,
                        LevelingRanking.ExpansionOf(zones, r.Zone) is { } e && e > 0 ? ZoneDirectory.ExpansionName(e) : "—",
                        Rate(r.XpPerHour),
                        r.KillsPerHour.ToString("0", culture),
                        r.Players,
                        r.Hours.ToString("0.0", culture),
                        "From other servers",
                        r.PlatPerHour > 0 ? r.PlatPerHour.ToString("N0", culture) + "p" : "—",
                        ZoneMobLevels(r.Zone),
                        BonusTodayIn(r.Zone)));
                }
            }

            SelectedLevelingZone = LevelingZoneRows.FirstOrDefault(z => z.Zone == keepZone) ?? LevelingZoneRows.FirstOrDefault();
            foreach ((int pathBand, ZoneRank? best) in LevelingRanking.Path(rows, LevelingLevel, mode, allowed))
            {
                LevelingPathRows.Add(new LevelingPathView(
                    LevelingRanking.BandText(pathBand),
                    best?.Zone ?? "Not enough data",
                    best is null ? "—" : Rate(best.XpPerHour)));
            }

            LevelingEtaText = LevelingEta(server, mode, ranked.FirstOrDefault(r => r.Enough) ?? ranked.FirstOrDefault());

            double hours = mine.Sum(s => s.Seconds) / 3600;
            string sharedText = !UseSharedLeveling ? "Shared leveling is off, so only your own logs are used."
                : shared is null ? "Shared leveling: not downloaded yet."
                : $"Shared leveling: {shared.Players.ToString(culture)} other player(s) on {server}, from {AgoText(DateTime.UtcNow - _sharedLevelingAt.GetValueOrDefault(server, DateTime.UtcNow))}.";
            LevelingStatus = $"Your logs: {mine.Count.ToString(culture)} stretches on {server} ({hours.ToString("0.0", culture)} h). {sharedText} " +
                             $"A zone is ranked once {LevelingRanking.MinPlayers} players and {LevelingRanking.MinHours.ToString(culture)} hours are in.";
        }
        finally
        {
            _levelingShowing = false;
        }
    }

    /// <summary>The server's expansions: the ones set by hand on this tab, or every expansion up to the newest zone seen there.</summary>
    private IReadOnlySet<int> LevelingOpenExpansions(string server, ZoneDirectory zones, IEnumerable<string> seen, bool rebuild)
    {
        IReadOnlySet<int> inferred = LevelingRanking.InferExpansions(zones, seen);
        bool byHand = _levelingExpansions.TryGetValue(server, out List<int>? chosen);
        Core.Tlp.TlpServer? tlp = Core.Tlp.TlpServers.Find(server);
        // A TLP server offers only the expansions it has open (its unlock schedule); you can still untick some of them.
        IReadOnlySet<int>? tlpOpen = tlp?.OpenExpansions(Core.Tlp.TlpServers.Today(DateTime.UtcNow));
        IReadOnlySet<int> open = tlpOpen is not null
            ? byHand ? tlpOpen.Where(n => chosen!.Contains(n) || n > chosen!.DefaultIfEmpty(0).Max()).ToHashSet() : tlpOpen // a newer unlock starts ticked
            : byHand ? chosen!.ToHashSet() : inferred;
        LevelingExpansionHint = zones.Expansions.Any()
            ? tlpOpen is not null ? "The expansions open on this server, from its unlock dates. Untick any to leave their zones out."
            : byHand ? "Set by hand for this server."
            : "Found from the zones seen on this server. Tick or untick to change."
            : "Choose the EQ folder on the Characters tab to filter zones by expansion.";

        if (rebuild || LevelingExpansionChoices.Count == 0)
        {
            IEnumerable<int> offered = tlpOpen is not null
                ? tlpOpen.Order()
                : Enumerable.Range(1, Math.Min(ZoneDirectory.ExpansionNames.Count, Math.Max(5, Math.Max(inferred.DefaultIfEmpty(1).Max(), open.DefaultIfEmpty(1).Max()) + 3)));
            LevelingExpansionChoices.Clear();
            foreach (int n in offered)
            {
                LevelingExpansionChoices.Add(new ExpansionChoice(n, ZoneDirectory.ExpansionName(n), open.Contains(n), OnExpansionTicked));
            }
        }

        return open;
    }

    private void OnExpansionTicked()
    {
        if (_levelingShowing || SelectedLevelingServer is not { } server)
        {
            return;
        }

        _levelingExpansions[server] = LevelingExpansionChoices.Where(c => c.IsOn).Select(c => c.Number).ToList();
        RequestSave();
        ShowLeveling();
    }
}
