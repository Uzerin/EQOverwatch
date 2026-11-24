using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Drops;

namespace EQOverwatch.App.ViewModels;

// The online shared drop table: two switches, both off to start. "Share my drops" uploads this PC's new kills and drops (no character
// names) at most once a day; "Use shared drops" downloads the merged table the service makes from everyone's uploads at 10:00 UTC each
// day, where a fight several players logged counts once. The anonymous player count (on unless switched off) is sent once a day.
public sealed partial class MainViewModel
{
    public const string DropSourceEveryone = "You and all players";

    /// <summary>How often the tick looks whether an upload or a merged table is due (the checks themselves are local).</summary>
    private static readonly TimeSpan SharedDropsSyncEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SharedDropsRetryEvery = TimeSpan.FromMinutes(15);

    /// <summary>Each server gets at most one upload a day.</summary>
    public static readonly TimeSpan SharedDropsUploadEvery = TimeSpan.FromHours(24);

    /// <summary>After the daily merge, the next one is looked for this long after it is due (it takes a few minutes to run).</summary>
    private static readonly TimeSpan MergedCheckGrace = TimeSpan.FromMinutes(20);

    private readonly Dictionary<string, DateTime> _nextMergedCheckUtc = new(StringComparer.OrdinalIgnoreCase);
    private ISharedDropsService? _sharedDrops;
    private DateTime _nextSharedDropsSyncUtc = DateTime.MinValue;
    private bool _sharedDropsBusy;
    private string _sharedDropsId = string.Empty;
    private string _sharedDropsKey = string.Empty;
    private string _playerCountId = string.Empty;
    private string _playerCountDay = string.Empty;
    private DateTime _nextPlayerCountUtc = DateTime.MinValue;
    private bool _playerCountBusy;

    [ObservableProperty]
    private bool _useSharedDrops;

    [ObservableProperty]
    private bool _shareMyDrops;

    /// <summary>Send the anonymous player count once a day: a random id and the app version, nothing else.</summary>
    [ObservableProperty]
    private bool _countMeIn = true;

    [ObservableProperty]
    private string _sharedDropsStatus = "Both are off. Nothing is sent or downloaded until you switch one on.";

    /// <summary>The last sync (for tests).</summary>
    public Task SharedDropsTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last player count (for tests).</summary>
    public Task PlayerCountTask { get; private set; } = Task.CompletedTask;

    /// <summary>Connects the online shared drop table and the player count. Without it the switches do nothing.</summary>
    public void ConfigureSharedDrops(ISharedDropsService service)
    {
        _sharedDrops = service;
        _nextSharedDropsSyncUtc = DateTime.MinValue;
        _nextPlayerCountUtc = DateTime.MinValue;
    }

    private void RestoreSharedDrops(Core.Settings.AppSettings settings)
    {
        _sharedDropsId = settings.SharedDropsId ?? string.Empty;
        _sharedDropsKey = settings.SharedDropsKey ?? string.Empty;
        _playerCountId = settings.PlayerCountId ?? string.Empty;
        _playerCountDay = settings.PlayerCountDay ?? string.Empty;
        CountMeIn = settings.CountMeIn;
        _allLogsLoadOffered = settings.AllLogsLoadOffered;
        UseSharedDrops = settings.UseSharedDrops;
        PoolNormalLootDrops = settings.PoolNormalLootDrops;
        ShareMyDrops = settings.ShareMyDrops;
        UpdateDropSources();
    }

    partial void OnCountMeInChanged(bool value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>Once a day (UTC), counts this install as using the app: a random id and the version. Retries hourly when it fails.</summary>
    private void RefreshPlayerCount(DateTime nowUtc)
    {
        string today = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_sharedDrops is null || !CountMeIn || _playerCountBusy || today == _playerCountDay || nowUtc < _nextPlayerCountUtc)
        {
            return;
        }

        _nextPlayerCountUtc = nowUtc.AddHours(1);
        PlayerCountTask = SendPlayerCountAsync(today);
    }

    private async Task SendPlayerCountAsync(string today)
    {
        if (_playerCountId.Length == 0)
        {
            _playerCountId = HttpSharedDropsService.NewInstall().Id;
            RequestSave();
        }

        _playerCountBusy = true;
        try
        {
            await _sharedDrops!.PingAsync(_playerCountId, Core.AppInfo.Version);
            _playerCountDay = today;
            RequestSave();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // tried again in an hour
        }
        finally
        {
            _playerCountBusy = false;
        }
    }

    partial void OnUseSharedDropsChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        UpdateDropSources();
        if (value)
        {
            SelectedDropSource = DropSourceEveryone;
            _nextMergedCheckUtc.Clear();
            SharedDropsTask = SyncSharedDropsAsync();
        }
        else
        {
            _pipeline.Drops.RemoveShared();
            _ = Task.Run(_pipeline.Drops.Flush);
            SharedDropsStatus = "Other players' drops are out of your table.";
            RebuildDrops(tableToo: true);
        }
    }

    partial void OnShareMyDropsChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        if (value)
        {
            SharedDropsTask = SyncSharedDropsAsync();
        }
        else
        {
            SharedDropsTask = DeleteMySharedDropsAsync();
        }
    }

    /// <summary>"You and all players" is offered only while shared drops are used.</summary>
    private void UpdateDropSources()
    {
        var sources = new List<string> { DropSourceAll, DropSourceMine };
        if (UseSharedDrops)
        {
            sources.Insert(0, DropSourceEveryone);
        }

        Presentation.ObservableSync.SyncValues(DropSources, sources);
        if (!sources.Contains(SelectedDropSource))
        {
            SelectedDropSource = DropSourceAll;
        }
    }

    private DropScope SelectedDropScope => SelectedDropSource switch
    {
        DropSourceMine => DropScope.Mine,
        DropSourceEveryone => DropScope.Everyone,
        _ => DropScope.ImportedFiles,
    };

    /// <summary>Called on every refresh: every few minutes, uploads or downloads whatever is due (not while the log history is read).</summary>
    private void RefreshSharedDrops(DateTime nowUtc)
    {
        RefreshPlayerCount(nowUtc);
        if (_sharedDrops is null || (!UseSharedDrops && !ShareMyDrops) || _dropsScanning || _sharedDropsBusy || nowUtc < _nextSharedDropsSyncUtc)
        {
            return;
        }

        _nextSharedDropsSyncUtc = nowUtc + SharedDropsSyncEvery;
        SharedDropsTask = SyncSharedDropsAsync();
    }

    [RelayCommand]
    private Task SyncSharedDropsNow() => SharedDropsTask = SyncSharedDropsAsync(force: true);

    /// <summary>
    /// Uploads this PC's new drops (when sharing, at most once a day per server) and downloads a server's merged table when a newer one
    /// is out (when using shared drops: after each daily merge). <paramref name="force"/> ("Check now") skips the local waits.
    /// </summary>
    private async Task SyncSharedDropsAsync(bool force = false)
    {
        if (_sharedDrops is null || _sharedDropsBusy || (!UseSharedDrops && !ShareMyDrops))
        {
            return;
        }

        _sharedDropsBusy = true;
        DropStore store = _pipeline.Drops;
        int uploaded = 0, read = 0;
        try
        {
            List<string> servers = store.Servers()
                .Concat(WatchedLogsForDrops().Select(l => l.Server))
                .Where(s => s.Length > 0)
                .Select(s => s.ToLowerInvariant())
                .Distinct()
                .ToList();

            if (ShareMyDrops)
            {
                EnsureSharedDropsInstall();
                foreach (string server in servers)
                {
                    DateTime now = DateTime.UtcNow;
                    if (!force && store.LastSharedUpload(server) is { } last && now - last < SharedDropsUploadEvery)
                    {
                        continue; // once a day
                    }

                    SharedDropsUpload? upload = await Task.Run(() => store.ExportShared(server));
                    if (upload is null)
                    {
                        continue; // nothing new
                    }

                    SharedDropsStatus = "Uploading your new drops…";
                    if (await _sharedDrops.UploadAsync(_sharedDropsId, _sharedDropsKey, upload))
                    {
                        store.RecordSharedUpload(server, now, upload.Through);
                        uploaded++;
                    }
                }
            }

            if (UseSharedDrops)
            {
                await RefreshMergedServersAsync(force);
                foreach (string server in servers.Concat(servers.SelectMany(PoolFor)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    DateTime now = DateTime.UtcNow;
                    if (!force && _nextMergedCheckUtc.TryGetValue(server, out DateTime next) && now < next)
                    {
                        continue;
                    }

                    SharedDropsStatus = "Checking the shared drops…";
                    MergedDropsDownload? merged = await _sharedDrops.DownloadMergedAsync(server, store.MergedInfo(server)?.Updated ?? 0);
                    if (merged?.Gzip is { } gzip)
                    {
                        try
                        {
                            await Task.Run(() => store.ImportMerged(server, merged.Updated, merged.Players, gzip));
                            read++;
                        }
                        catch (InvalidDataException)
                        {
                            // a damaged file is fetched again next time
                        }
                    }

                    _nextMergedCheckUtc[server] = NextMergedCheck(merged?.LastMerge ?? 0, now);
                }

                _ = Task.Run(store.Flush);
            }

            SharedDropsStatus = SharedDropsSummary(servers, uploaded, read);
            ReportSharing("Drops", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            SharedDropsStatus = $"Couldn't reach the shared drops service ({ex.Message}). It tries again in a few minutes.";
            ReportSharing("Drops", ex.Message);
            _nextSharedDropsSyncUtc = DateTime.UtcNow + SharedDropsRetryEvery;
        }
        finally
        {
            _sharedDropsBusy = false;
        }

        RebuildDrops(tableToo: true);
    }

    /// <summary>
    /// When to look for a server's next merged table: shortly after the next daily merge is due (a day after the last one finished), or in
    /// an hour when that time has passed and no new merge was seen yet.
    /// </summary>
    public static DateTime NextMergedCheck(long lastMergeUnix, DateTime nowUtc)
    {
        if (lastMergeUnix <= 0)
        {
            return nowUtc.AddHours(6);
        }

        DateTime due = DateTimeOffset.FromUnixTimeSeconds(lastMergeUnix).UtcDateTime + TimeSpan.FromDays(1) + MergedCheckGrace;
        return due > nowUtc ? due : nowUtc.AddHours(1);
    }

    /// <summary>"Shared drops from 12 players, updated today 10:04 UTC. Your new drops are uploaded once a day (last: today 9:12 AM)."</summary>
    private string SharedDropsSummary(IReadOnlyList<string> servers, int uploaded, int read)
    {
        DropStore store = _pipeline.Drops;
        DateTime nowUtc = DateTime.UtcNow;
        var parts = new List<string>();
        if (UseSharedDrops)
        {
            (string Server, MergedDropsInfo Info)? best = servers
                .Select(s => (Server: s, Info: store.MergedInfo(s)))
                .Where(x => x.Info is not null)
                .Select(x => ((string, MergedDropsInfo)?)(x.Server, x.Info!))
                .OrderByDescending(x => x!.Value.Item2.Players)
                .FirstOrDefault();
            if (best is { } b)
            {
                DateTime made = DateTimeOffset.FromUnixTimeSeconds(b.Info.Updated).UtcDateTime;
                string when = made.Date == nowUtc.Date ? "today" : made.Date == nowUtc.Date.AddDays(-1) ? "yesterday"
                    : made.ToString("MMM d", CultureInfo.InvariantCulture);
                parts.Add($"Shared drops from {b.Info.Players:N0} player{(b.Info.Players == 1 ? string.Empty : "s")}, updated {when} " +
                          $"{made.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC" + (read > 0 ? " (just downloaded)." : "."));
            }
            else
            {
                parts.Add("No merged shared drops yet: the service merges everyone's uploads at 10:00 UTC each day.");
            }
        }

        if (ShareMyDrops)
        {
            DateTime? last = servers.Select(store.LastSharedUpload).Where(d => d is not null).Max();
            parts.Add(uploaded > 0
                ? "Your new drops were uploaded; they join the table at the next merge."
                : last is { } l
                    ? $"Your new drops are uploaded once a day (last: {l.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture)})."
                    : "Your drops are uploaded once there is a drop to share.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>Takes this install's files off the service when sharing is switched off.</summary>
    private async Task DeleteMySharedDropsAsync()
    {
        if (_sharedDrops is null || _sharedDropsId.Length == 0)
        {
            SharedDropsStatus = "Your drops are not shared.";
            return;
        }

        try
        {
            List<string> servers = _pipeline.Drops.Servers().Concat(WatchedLogsForDrops().Select(l => l.Server))
                .Where(s => s.Length > 0).Select(s => s.ToLowerInvariant()).Distinct().ToList();
            foreach (string server in servers)
            {
                await _sharedDrops.DeleteAsync(server, _sharedDropsId, _sharedDropsKey);
            }

            _pipeline.Drops.ForgetSharedUploads();
            SharedDropsStatus = "Your drops are no longer shared; they were taken off the service and leave the table at the next merge.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SharedDropsStatus = $"Sharing is off, but your files couldn't be taken off the service yet ({ex.Message}).";
        }
    }

    private void EnsureSharedDropsInstall()
    {
        if (_sharedDropsId.Length > 0 && _sharedDropsKey.Length > 0)
        {
            return;
        }

        (_sharedDropsId, _sharedDropsKey) = HttpSharedDropsService.NewInstall();
        RequestSave();
    }
}
