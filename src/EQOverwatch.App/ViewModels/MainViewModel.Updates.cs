using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core;
using EQOverwatch.Core.Updates;

namespace EQOverwatch.App.ViewModels;

/// <summary>"A new version is available": checks GitHub when the app opens and shows a banner with a link to the release page.</summary>
public sealed partial class MainViewModel
{
    private string _latestVersion = string.Empty;
    private string _updateUrl = string.Empty;
    private string _skippedUpdateVersion = string.Empty;

    /// <summary>True while the "update available" banner is showing.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>"EQ Overwatch v1.0.12 is available (you have v1.0.11)."</summary>
    [ObservableProperty]
    private string _updateMessage = string.Empty;

    /// <summary>The start of the new release's notes.</summary>
    [ObservableProperty]
    private string _updateNotes = string.Empty;

    /// <summary>The result of the last check, for the Settings tab.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>Check for a newer version when the app opens.</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    /// <summary>Completes when the last update check has finished.</summary>
    public Task UpdateCheckTask { get; private set; } = Task.CompletedTask;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>The check done once when the app opens (does nothing when update checks are switched off).</summary>
    public Task StartUpdateCheckAsync() => UpdateCheckTask = RunUpdateCheckAsync(manual: false);

    [RelayCommand]
    private Task CheckUpdatesNow() => UpdateCheckTask = RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (_updateChecker is null || (!manual && !CheckForUpdates))
        {
            return;
        }

        UpdateStatus = "Checking for updates…";
        UpdateCheckResult result;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            result = await _updateChecker.CheckAsync(AppVersion, timeout.Token);
        }
        catch (Exception ex)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                _latestVersion = result.LatestVersion;
                _updateUrl = result.Url;
                bool skipped = !manual && result.LatestVersion.Equals(_skippedUpdateVersion, StringComparison.OrdinalIgnoreCase);
                UpdateStatus = skipped
                    ? $"{result.LatestVersion} is available (you chose to skip it)."
                    : $"{result.LatestVersion} is available.";
                if (!skipped)
                {
                    UpdateMessage = $"{AppName} {result.LatestVersion} is available (you have {AppVersion}).";
                    UpdateNotes = result.Notes;
                    IsUpdateAvailable = true;
                }

                break;

            case UpdateCheckStatus.UpToDate:
                UpdateStatus = $"You have the latest version ({AppVersion}).";
                break;

            case UpdateCheckStatus.NoReleases:
                UpdateStatus = "No newer version has been published yet.";
                break;

            default:
                UpdateStatus = $"Could not check for updates: {result.Error}";
                break;
        }
    }

    /// <summary>Opens the new release's page in the browser.</summary>
    [RelayCommand]
    private void OpenUpdate()
    {
        string url = string.IsNullOrWhiteSpace(_updateUrl) ? AppInfo.ReleasesUrl : _updateUrl;
        try
        {
            _openUrl?.Invoke(url);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open {url}: {ex.Message}";
        }
    }

    /// <summary>Hides the banner until the next time the app opens.</summary>
    [RelayCommand]
    private void DismissUpdate() => IsUpdateAvailable = false;

    /// <summary>Hides the banner and does not announce this version again (a newer one still will be).</summary>
    [RelayCommand]
    private void SkipUpdateVersion()
    {
        _skippedUpdateVersion = _latestVersion;
        IsUpdateAvailable = false;
        RequestSave();
    }
}
