using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Drops;

namespace EQOverwatch.App.ViewModels;

// The online shared drop table: two switches, both off to start. "Share my drops" uploads this PC's own kills and drops (no character
// names) once in a while; "Use shared drops" downloads the other players' files and adds them to the table, where a fight several
// players logged still counts once.
public sealed partial class MainViewModel
{
    public const string DropSourceEveryone = "You and all players";
    private static readonly TimeSpan SharedDropsSyncEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan SharedDropsRetryEvery = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SharedDropsUploadEvery = TimeSpan.FromHours(6);

    private readonly Dictionary<string, (int Kills, int Drops, DateTime AtUtc)> _sharedUploads = new(StringComparer.OrdinalIgnoreCase);
    private ISharedDropsService? _sharedDrops;
    private DateTime _nextSharedDropsSyncUtc = DateTime.MinValue;
    private bool _sharedDropsBusy;
    private string _sharedDropsId = string.Empty;
    private string _sharedDropsKey = string.Empty;

    [ObservableProperty]
    private bool _useSharedDrops;

    [ObservableProperty]
    private bool _shareMyDrops;

    [ObservableProperty]
    private string _sharedDropsStatus = "Both are off. Nothing is sent or downloaded until you switch one on.";

    /// <summary>The last sync (for tests).</summary>
    public Task SharedDropsTask { get; private set; } = Task.CompletedTask;

    /// <summary>Connects the online shared drop table. Without it the switches do nothing.</summary>
    public void ConfigureSharedDrops(ISharedDropsService service)
    {
        _sharedDrops = service;
        _nextSharedDropsSyncUtc = DateTime.MinValue;
    }

    private void RestoreSharedDrops(Core.Settings.AppSettings settings)
    {
        _sharedDropsId = settings.SharedDropsId ?? string.Empty;
        _sharedDropsKey = settings.SharedDropsKey ?? string.Empty;
        UseSharedDrops = settings.UseSharedDrops;
        ShareMyDrops = settings.ShareMyDrops;
        UpdateDropSources();
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
            _sharedUploads.Clear();
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

    /// <summary>Called on every refresh: syncs once an hour while a switch is on (not while the log history is being read).</summary>
    private void RefreshSharedDrops(DateTime nowUtc)
    {
        if (_sharedDrops is null || (!UseSharedDrops && !ShareMyDrops) || _dropsScanning || _sharedDropsBusy || nowUtc < _nextSharedDropsSyncUtc)
        {
            return;
        }

        _nextSharedDropsSyncUtc = nowUtc + SharedDropsSyncEvery;
        SharedDropsTask = SyncSharedDropsAsync();
    }

    [RelayCommand]
    private Task SyncSharedDropsNow() => SharedDropsTask = SyncSharedDropsAsync(force: true);

    /// <summary>Uploads this PC's drops (when sharing) and reads other players' new files (when using shared drops).</summary>
    private async Task SyncSharedDropsAsync(bool force = false)
    {
        if (_sharedDrops is null || _sharedDropsBusy || (!UseSharedDrops && !ShareMyDrops))
        {
            return;
        }

        _sharedDropsBusy = true;
        DropStore store = _pipeline.Drops;
        int uploaded = 0, read = 0, players = 0;
        try
        {
            SharedDropsStatus = "Talking to the shared drops service…";
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
                    SharedDropsUpload? upload = await Task.Run(() => store.ExportShared(server));
                    if (upload is null)
                    {
                        continue;
                    }

                    DateTime now = DateTime.UtcNow;
                    if (_sharedUploads.TryGetValue(server, out var last)
                        && ((last.Kills == upload.Kills && last.Drops == upload.Drops) || (!force && now - last.AtUtc < SharedDropsUploadEvery)))
                    {
                        continue; // nothing new, or uploaded a short while ago
                    }

                    if (await _sharedDrops.UploadAsync(_sharedDropsId, _sharedDropsKey, upload))
                    {
                        _sharedUploads[server] = (upload.Kills, upload.Drops, now);
                        uploaded++;
                    }
                }
            }

            if (UseSharedDrops)
            {
                foreach (string server in servers)
                {
                    IReadOnlyList<SharedDropsEntry> files = await _sharedDrops.ListAsync(server);
                    IReadOnlyDictionary<string, long> have = store.SharedFiles(server);
                    foreach (SharedDropsEntry file in files)
                    {
                        if (file.Id == _sharedDropsId || file.Id.Length == 0)
                        {
                            continue; // our own upload is already in the table
                        }

                        players++;
                        if (have.TryGetValue(file.Id, out long updated) && updated >= file.Updated)
                        {
                            continue;
                        }

                        try
                        {
                            byte[] bytes = await _sharedDrops.DownloadAsync(server, file.Id);
                            await Task.Run(() => store.ImportShared(file.Id, server, file.Updated, bytes));
                            read++;
                        }
                        catch (InvalidDataException)
                        {
                            // a damaged file is skipped
                        }
                    }

                    var listed = files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (string gone in have.Keys.Where(id => !listed.Contains(id)))
                    {
                        store.RemoveShared(server, gone); // taken down by its owner or the admin
                    }
                }

                _ = Task.Run(store.Flush);
            }

            string time = _clock().ToString("t", CultureInfo.CurrentCulture);
            var parts = new List<string>();
            if (ShareMyDrops)
            {
                parts.Add(uploaded > 0 ? $"uploaded your drops on {uploaded} server{(uploaded == 1 ? string.Empty : "s")}" : "your shared drops are up to date");
            }

            if (UseSharedDrops)
            {
                parts.Add($"{players:N0} other player{(players == 1 ? string.Empty : "s")} shared drops" + (read > 0 ? $" ({read:N0} new or updated)" : string.Empty));
            }

            string text = string.Join("; ", parts);
            SharedDropsStatus = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] + $". Checked at {time}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            SharedDropsStatus = $"Couldn't reach the shared drops service ({ex.Message}). It tries again in a few minutes.";
            _nextSharedDropsSyncUtc = DateTime.UtcNow + SharedDropsRetryEvery;
        }
        finally
        {
            _sharedDropsBusy = false;
        }

        RebuildDrops(tableToo: true);
    }

    /// <summary>Takes this install's files off the service when sharing is switched off.</summary>
    private async Task DeleteMySharedDropsAsync()
    {
        _sharedUploads.Clear();
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

            SharedDropsStatus = "Your drops are no longer shared; your files were taken off the service.";
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
