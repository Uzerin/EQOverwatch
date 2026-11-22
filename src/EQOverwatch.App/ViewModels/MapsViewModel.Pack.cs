using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.ViewModels;

// The Maps tab's "Install/Update Overwatch Maps" button.
public sealed partial class MapsViewModel
{
    private static readonly Lazy<HttpClient> PackHttp = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    private Func<bool> _isInCombat = () => false;
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallMapPackCommand))]
    private bool _isInstallingPack;

    /// <summary>The install's progress from 0 to 100.</summary>
    [ObservableProperty]
    private double _packProgress;

    public const string PackButtonDefault = "Install/Update Overwatch Maps";
    public const string PackButtonNew = "Update Overwatch Maps (new)";

    /// <summary>The button's text: it says "(new)" when a newer pack than the one installed is published.</summary>
    [ObservableProperty]
    private string _packButtonText = PackButtonDefault;

    /// <summary>Looks for a newer published pack than the installed one (quietly; called with the app's update check).</summary>
    public async Task CheckPackUpdateAsync()
    {
        if (_dataFolder.Length == 0 || IsInstallingPack)
        {
            return;
        }

        try
        {
            MapPackRecord record = MapPackRecord.Load(Path.Combine(_dataFolder, "map-pack.json"));
            if (record.InstalledTag.Length == 0)
            {
                return;
            }

            var installer = new MapPackInstaller(PackHttp.Value, AppInfo.MapPackRepository, $"{AppInfo.SettingsFolderName}/{AppInfo.Version.TrimStart('v')}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            MapPackRelease? release = await installer.FindLatestAsync(timeout.Token);
            if (release is not null && !IsInstallingPack)
            {
                PackButtonText = record.IsOutdated(release.Tag) ? PackButtonNew : PackButtonDefault;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }
    }

    /// <summary>What the last check or install did, or why it could not run.</summary>
    [ObservableProperty]
    private string _packStatus = string.Empty;

    /// <summary>Hands the button what it needs from the main window: combat state and the app's data folder.</summary>
    public void ConfigurePack(Func<bool> isInCombat, string dataFolder)
    {
        _isInCombat = isInCombat;
        _dataFolder = dataFolder;
    }

    /// <summary>Call when the combat state or the EQ folder changes, so the button enables and disables itself.</summary>
    public void RefreshPackButton() => InstallMapPackCommand.NotifyCanExecuteChanged();

    private bool CanInstallMapPack() => !IsInstallingPack && _eqFolder().Length > 0 && !_isInCombat();

    [RelayCommand(CanExecute = nameof(CanInstallMapPack))]
    private async Task InstallMapPackAsync()
    {
        string eqFolder = _eqFolder();
        if (eqFolder.Length == 0 || !Directory.Exists(eqFolder))
        {
            PackStatus = "Set your EverQuest folder on the Settings tab first.";
            return;
        }

        if (_isInCombat())
        {
            PackStatus = "Installing maps is not available during combat.";
            return;
        }

        IsInstallingPack = true;
        PackProgress = 0;
        string zipPath = Path.Combine(Path.GetTempPath(), $"OverwatchMaps-{Guid.NewGuid():N}.zip");
        try
        {
            string packFolder = Path.Combine(eqFolder, "maps", MapPackInstaller.PackFolderName);
            string recordPath = Path.Combine(_dataFolder, "map-pack.json");
            var installer = new MapPackInstaller(PackHttp.Value, AppInfo.MapPackRepository, $"{AppInfo.SettingsFolderName}/{AppInfo.Version.TrimStart('v')}");

            PackStatus = "Looking for the newest map pack…";
            MapPackRelease? release = await installer.FindLatestAsync();
            if (release is null)
            {
                PackStatus = "No Overwatch map pack is published yet.";
                return;
            }

            MapPackManifest manifest = await installer.DownloadManifestAsync(release);
            PackStatus = $"Checking {manifest.Files.Count.ToString("N0", CultureInfo.CurrentCulture)} map files…";
            MapPackRecord record = MapPackRecord.Load(recordPath);
            IReadOnlyList<MapFileAudit> audits = await Task.Run(() => MapPackInstaller.Audit(packFolder, manifest, record));
            PackProgress = 10;

            var edited = audits.Where(a => a.State == MapFileState.Edited).Select(a => a.File.Name).ToList();
            IReadOnlyDictionary<string, MapEditChoice> choices = new Dictionary<string, MapEditChoice>();
            if (edited.Count > 0)
            {
                choices = _dialogs.ChooseEditedMaps(edited) ?? choices;
            }

            bool needsDownload = audits.Any(a => a.State is MapFileState.Missing or MapFileState.Update
                || (a.State == MapFileState.Edited && choices.GetValueOrDefault(a.File.Name) != MapEditChoice.KeepMine));
            MapInstallResult result;
            if (needsDownload)
            {
                PackStatus = "Downloading the map pack…";
                var progress = new Progress<double>(p => PackProgress = 10 + p * 80);
                await installer.DownloadZipAsync(release, zipPath, progress);
                PackStatus = "Installing…";
                string backups = Path.Combine(_dataFolder, "map-backups", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                result = await Task.Run(() => MapPackInstaller.Install(zipPath, packFolder, manifest, audits, choices, record, backups));
            }
            else
            {
                result = new MapInstallResult(0, 0, audits.Count(a => a.State == MapFileState.Current), edited.Count, 0, 0, 0);
                foreach (MapFileAudit audit in audits.Where(a => a.State == MapFileState.Current))
                {
                    record.Files[audit.File.Name] = audit.File.Sha256;
                }

                record.Version = manifest.Version;
            }

            record.Tag = release.Tag;
            record.Save(recordPath);
            PackButtonText = PackButtonDefault;
            PackProgress = 100;
            PackStatus = Describe(result, release.Tag);
            Reload();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            PackStatus = "The map pack could not be installed: " + ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
            }

            IsInstallingPack = false;
        }
    }

    /// <summary>"12 installed, 3 updated, 1,044 already current." plus kept, overwritten and merged edited files.</summary>
    public static string Describe(MapInstallResult r, string tag)
    {
        string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
        var parts = new List<string> { $"{N(r.Installed)} installed", $"{N(r.Updated)} updated", $"{N(r.Current)} already current" };
        if (r.Overwritten > 0)
        {
            parts.Add($"{N(r.Overwritten)} of your edited files replaced");
        }

        if (r.Merged > 0)
        {
            parts.Add($"{N(r.Merged)} merged");
        }

        if (r.Kept > 0)
        {
            parts.Add($"{N(r.Kept)} of your edited files kept");
        }

        if (r.Skipped > 0)
        {
            parts.Add($"{N(r.Skipped)} failed their check and were skipped");
        }

        return string.Join(", ", parts) + $" ({tag}).";
    }
}
