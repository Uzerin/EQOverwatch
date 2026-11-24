using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Presentation;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.App.ViewModels;

/// <summary>One row of the drop table: a mob (or item) heading, or an item (or mob) under it.</summary>
public sealed record DropRowView(string Name, string Zone, string Kills, string Drops, string Rate, double RateBar, bool IsHeading, string? StatsItem);

/// <summary>An imported drops file in the sharing view.</summary>
public sealed record DropSourceView(string Tag, string Server, string Text);

// The Drop table tab: which mobs drop which items and how often, per server, from the logs and from imported drops files.
public sealed partial class MainViewModel
{
    public const string AllDropZones = "All zones";
    public const string DropSourceAll = "You and imported files";
    public const string DropSourceMine = "Only yours";
    private static readonly TimeSpan DropsFlushEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DropsRebuildEvery = TimeSpan.FromSeconds(3);

    private long _dropsVersionShown = -1;
    private DateTime _lastDropsFlush;
    private DateTime _lastDropsRebuild;
    private bool _dropsScanning;
    private bool _rebuildingDrops;
    private IReadOnlyList<DropMobRow> _dropTable = Array.Empty<DropMobRow>();

    public ObservableCollection<string> DropServers { get; } = new();

    public ObservableCollection<string> DropZones { get; } = new() { AllDropZones };

    public ObservableCollection<string> DropSources { get; } = new() { DropSourceAll, DropSourceMine };

    public ObservableCollection<DropSourceView> DropImports { get; } = new();

    [ObservableProperty]
    private string? _selectedDropServer;

    [ObservableProperty]
    private string _selectedDropZone = AllDropZones;

    [ObservableProperty]
    private string _selectedDropSource = DropSourceAll;

    [ObservableProperty]
    private string _dropSearch = string.Empty;

    /// <summary>0 By mob, 1 By item, 2 Sharing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDropTableView), nameof(IsDropSharingView), nameof(DropFirstColumn), nameof(DropSecondColumn))]
    private int _dropView;

    [ObservableProperty]
    private IReadOnlyList<DropRowView> _dropRows = Array.Empty<DropRowView>();

    [ObservableProperty]
    private string _dropStatus = "Fills in from your logs: kills, and items looted from corpses. Each kill or drop counts once, however many of your logs saw it.";

    [ObservableProperty]
    private string _dropShareStatus = string.Empty;

    [ObservableProperty]
    private string _dropTag = string.Empty;

    [ObservableProperty]
    private string _dropExportServer = string.Empty;

    public bool IsDropTableView => DropView != 2;

    public bool IsDropSharingView => DropView == 2;

    public bool IsDropsByMob
    {
        get => DropView == 0;
        set
        {
            if (value)
            {
                DropView = 0;
            }
        }
    }

    public bool IsDropsByItem
    {
        get => DropView == 1;
        set
        {
            if (value)
            {
                DropView = 1;
            }
        }
    }

    public bool IsDropsSharing
    {
        get => DropView == 2;
        set
        {
            if (value)
            {
                DropView = 2;
            }
        }
    }

    public string DropFirstColumn => DropView == 1 ? "Item / mob" : "Mob / item";

    public string DropSecondColumn => "Zone";

    partial void OnDropViewChanged(int value)
    {
        OnPropertyChanged(nameof(IsDropsByMob));
        OnPropertyChanged(nameof(IsDropsByItem));
        OnPropertyChanged(nameof(IsDropsSharing));
        if (value == 2)
        {
            RefreshDropImports();
        }
        else
        {
            RebuildDropRows();
        }
    }

    partial void OnSelectedDropServerChanged(string? value) => RebuildDrops(tableToo: true);

    partial void OnSelectedDropZoneChanged(string value) => RebuildDropRows();

    partial void OnSelectedDropSourceChanged(string value) => RebuildDrops(tableToo: true);

    partial void OnDropSearchChanged(string value) => RebuildDropRows();

    partial void OnDropTagChanged(string value)
    {
        if (value != _pipeline.Drops.Tag)
        {
            DropShareStatus = _pipeline.Drops.SetTag(value)
                ? $"Your drops files will carry the name {value.Trim()}."
                : "A tag is 1 to 24 letters, digits, spaces, - or _.";
        }
    }

    /// <summary>Loads the saved table and reads the history of any watched log not read before.</summary>
    private void InitializeDrops()
    {
        if (_dataDirectory is not null)
        {
            _pipeline.Drops.SetStoragePath(Path.Combine(_dataDirectory, "drops.json"));
        }

        DropTag = _pipeline.Drops.Tag;
        if (_dataDirectory is not null)
        {
            List<(string, string, string)> unread = WatchedLogsForDrops().Where(l => !_pipeline.Drops.IsScanned(l.Character, l.Server)).ToList();
            if (unread.Count > 0)
            {
                _ = ScanDropsAsync(unread);
            }
        }

        RebuildDrops(tableToo: true);
        _ = RereadLootOnceAsync();
    }

    /// <summary>
    /// Once (1.0.87): reads the last 7 days of loot from the logs again, so loot filed under the wrong server or kept twice is put right.
    /// </summary>
    private async Task RereadLootOnceAsync()
    {
        if (_dataDirectory is null)
        {
            return;
        }

        string marker = Path.Combine(_dataDirectory, "loot.reread-1.0.87");
        IReadOnlyList<string> paths = ExistingLogPaths();
        if (File.Exists(marker) || paths.Count == 0)
        {
            return;
        }

        try
        {
            int changed = await Task.Run(() => _pipeline.Loot.Repair(paths, DateTime.Now.AddDays(-7)));
            File.WriteAllText(marker, changed.ToString(CultureInfo.InvariantCulture));
            _lootDirty = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // tried again next start
        }
    }

    private List<(string Path, string Character, string Server)> WatchedLogsForDrops()
    {
        var logs = new List<(string, string, string)>();
        foreach (WatchedLogConfig log in Logs)
        {
            if (LogFileName.TryParse(log.FilePath, out string character, out string server))
            {
                logs.Add((log.FilePath, character, server));
            }
        }

        return logs;
    }

    /// <summary>Reads every watched log from the start again.</summary>
    [RelayCommand]
    private Task ScanDrops() => ScanDropsAsync(WatchedLogsForDrops());

    private async Task ScanDropsAsync(List<(string Path, string Character, string Server)> logs)
    {
        if (_dropsScanning || logs.Count == 0)
        {
            return;
        }

        _dropsScanning = true;
        try
        {
            int done = 0;
            foreach ((string path, string character, string server) in logs)
            {
                DropStatus = $"Reading the log history of {character} for drops ({done + 1} of {logs.Count})…";
                await Task.Run(() => _pipeline.Drops.ScanLog(path, character, server));
                done++;
            }

            DropStatus = $"Read the drops in the log history of {done} character{(done == 1 ? string.Empty : "s")}. Each kill or drop counts once, however many logs saw it.";
        }
        finally
        {
            _dropsScanning = false;
        }

        RebuildDrops(tableToo: true);
    }

    private void RefreshDrops(DateTime nowUtc)
    {
        _pipeline.Drops.ResolveExpired(nowUtc);
        if (nowUtc - _lastDropsFlush >= DropsFlushEvery)
        {
            _lastDropsFlush = nowUtc;
            _ = Task.Run(_pipeline.Drops.Flush);
        }

        RefreshSharedDrops(nowUtc);
        if (_pipeline.Drops.Version != _dropsVersionShown && nowUtc - _lastDropsRebuild >= DropsRebuildEvery)
        {
            RebuildDrops(tableToo: true);
        }
    }

    /// <summary>Rebuilds the server list, the table and its rows. A selection change made here never starts another rebuild.</summary>
    private void RebuildDrops(bool tableToo)
    {
        if (_rebuildingDrops)
        {
            return;
        }

        _rebuildingDrops = true;
        try
        {
            DropStore store = _pipeline.Drops;
            _dropsVersionShown = store.Version;
            _lastDropsRebuild = DateTime.UtcNow;

            List<string> servers = store.Servers().ToList();
            string? keep = SelectedDropServer;
            ObservableSync.SyncValues(DropServers, servers);
            string? primaryServer = LastPlayedLog()?.ServerName;
            string? server = keep is not null && servers.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep
                : servers.FirstOrDefault(s => string.Equals(s, primaryServer, StringComparison.OrdinalIgnoreCase)) ?? servers.FirstOrDefault();
            if (server != SelectedDropServer)
            {
                SelectedDropServer = server; // the guard stops this from rebuilding again
            }

            if (string.IsNullOrEmpty(DropExportServer) || !servers.Contains(DropExportServer, StringComparer.OrdinalIgnoreCase))
            {
                DropExportServer = server ?? string.Empty;
            }

            if (tableToo)
            {
                IReadOnlyList<string> pool = server is null || SelectedDropScope != DropScope.Everyone ? Array.Empty<string>() : PoolFor(server);
                _dropTable = server is null ? Array.Empty<DropMobRow>() : store.Table(server, SelectedDropScope, pool);
                DropPoolText = PoolText(server, pool);
                List<string> zones = new[] { AllDropZones }
                    .Concat(_dropTable.Select(r => r.Zone).Where(z => z.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                string keepZone = SelectedDropZone;
                ObservableSync.SyncValues(DropZones, zones);
                string zone = zones.Contains(keepZone) ? keepZone : AllDropZones;
                if (zone != SelectedDropZone)
                {
                    SelectedDropZone = zone;
                }
            }

            RebuildDropRowsNow();
        }
        finally
        {
            _rebuildingDrops = false;
        }
    }

    private void RebuildDropRows()
    {
        if (_rebuildingDrops)
        {
            return;
        }

        _rebuildingDrops = true;
        try
        {
            RebuildDropRowsNow();
        }
        finally
        {
            _rebuildingDrops = false;
        }
    }

    private void RebuildDropRowsNow()
    {
        string search = DropSearch.Trim();
        bool Matches(string text) => search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase);
        IEnumerable<DropMobRow> mobs = _dropTable.Where(r => SelectedDropZone == AllDropZones || string.Equals(r.Zone, SelectedDropZone, StringComparison.OrdinalIgnoreCase));
        var rows = new List<DropRowView>();
        if (DropView == 1)
        {
            // By item: each item, then the mobs that dropped it.
            var byItem = mobs.SelectMany(m => m.Items.Select(i => (Item: i.Item, Mob: m, i.Drops)))
                .GroupBy(x => x.Item, StringComparer.OrdinalIgnoreCase)
                .Where(g => Matches(g.Key) || g.Any(x => Matches(x.Mob.Mob)))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, (string Item, DropMobRow Mob, int Drops)> item in byItem)
            {
                rows.Add(new DropRowView(item.Key, string.Empty, string.Empty, Count(item.Sum(x => x.Drops)), string.Empty, 0, true, item.Key));
                foreach ((string _, DropMobRow mob, int drops) in item.OrderByDescending(x => (double)x.Drops / x.Mob.Kills))
                {
                    rows.Add(new DropRowView(mob.Mob, mob.Zone, Count(mob.Kills), Count(drops), Rate(drops, mob.Kills), Bar(drops, mob.Kills), false, null));
                }
            }
        }
        else
        {
            foreach (DropMobRow mob in mobs.Where(m => Matches(m.Mob) || m.Items.Any(i => Matches(i.Item))))
            {
                rows.Add(new DropRowView(mob.Mob, mob.Zone, Count(mob.Kills), string.Empty, string.Empty, 0, true, null));
                foreach (DropItemRow item in mob.Items)
                {
                    rows.Add(new DropRowView(item.Item, string.Empty, string.Empty, Count(item.Drops), Rate(item.Drops, mob.Kills), Bar(item.Drops, mob.Kills), false, item.Item));
                }
            }
        }

        DropRows = rows;

        static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

        static string Rate(int drops, int kills) =>
            kills == 0 ? string.Empty : (100.0 * drops / kills).ToString("0.0", CultureInfo.CurrentCulture) + "%";

        static double Bar(int drops, int kills) => kills == 0 ? 0 : Math.Clamp(60.0 * drops / kills, 2, 60);
    }

    /// <summary>Opens the item's Allakhazam search page in the browser.</summary>
    [RelayCommand]
    private void OpenItemStats(string? item)
    {
        if (!string.IsNullOrWhiteSpace(item))
        {
            _openUrl?.Invoke("https://everquest.allakhazam.com/search.html?q=" + Uri.EscapeDataString(item));
        }
    }

    [RelayCommand]
    private void ExportDrops()
    {
        string server = DropExportServer;
        if (server.Length == 0)
        {
            DropShareStatus = "There are no drops to export yet.";
            return;
        }

        string suggested = $"drops-{server}-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.eqodrops";
        if (_dialogs.PickDropsExportPath(suggested) is not { } path)
        {
            return;
        }

        try
        {
            int drops = _pipeline.Drops.Export(server, path);
            DropShareStatus = $"Saved {Path.GetFileName(path)}: {drops:N0} drops on {server}, as {_pipeline.Drops.Tag}. Others add it with Import drops file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DropShareStatus = $"Couldn't save the drops file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportDrops()
    {
        if (_dialogs.PickDropsImportFile() is not { } path)
        {
            return;
        }

        try
        {
            DropImportResult result = _pipeline.Drops.Import(path);
            DropShareStatus = (result.Replaced ? $"Updated {result.Tag}'s drops on {result.Server}: " : $"Imported {result.Tag}'s drops on {result.Server}: ")
                + $"{result.Drops:N0} drops and {result.Kills:N0} kills. A fight you both logged counts once.";
            _ = Task.Run(_pipeline.Drops.Flush);
            RefreshDropImports();
            RebuildDrops(tableToo: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DropShareStatus = $"Couldn't import that file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveDropImport(DropSourceView? source)
    {
        if (source is null)
        {
            return;
        }

        _pipeline.Drops.RemoveSource(source.Tag, source.Server);
        DropShareStatus = $"Took {source.Tag}'s drops on {source.Server} out of your table.";
        _ = Task.Run(_pipeline.Drops.Flush);
        RefreshDropImports();
        RebuildDrops(tableToo: true);
    }

    [RelayCommand]
    private void NewDropTag() => DropTag = DropStore.NewTag();

    private void RefreshDropImports()
    {
        List<DropSourceView> sources = _pipeline.Drops.Sources()
            .Select(s => new DropSourceView(s.Tag, s.Server,
                $"{s.Tag} on {s.Server}: {s.Drops:N0} drops, {s.Kills:N0} kills, imported {s.ImportedUtc.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture)}"))
            .ToList();
        ObservableSync.SyncValues(DropImports, sources);
    }
}
