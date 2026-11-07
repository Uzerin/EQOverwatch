using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core;
using EQOverwatch.Core.Models;
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

    private IUpdateInstaller? _updateInstaller;
    private string _installExePath = string.Empty;
    private Action<string>? _restartApp;
    private UpdateAssets? _updateAssets;

    /// <summary>
    /// Switches on the "Install update" button. <paramref name="exePath"/> is the running EQOverwatch.exe; <paramref name="restart"/> starts it
    /// again and ends this process (it is only called after the new exe is in place and settings are saved).
    /// </summary>
    public void ConfigureUpdateInstall(IUpdateInstaller installer, string? exePath, Action<string> restart)
    {
        _updateInstaller = installer;
        _restartApp = restart;
        _installExePath = !string.IsNullOrWhiteSpace(exePath)
                          && Path.GetFileName(exePath).Equals(UpdateInstaller.ExeName, StringComparison.OrdinalIgnoreCase)
            ? exePath
            : string.Empty;
        RefreshInstallState();
    }

    /// <summary>The folder EQOverwatch.exe runs from, which an install would replace the exe in (empty when it cannot be installed).</summary>
    public string UpdateInstallFolder => _installExePath.Length == 0 ? string.Empty : Path.GetDirectoryName(_installExePath) ?? string.Empty;

    /// <summary>True when the new release can be installed from the app: it carries a checksum and this is the real exe.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _updateCanInstall;

    /// <summary>True while a combat line has been seen in the last few seconds. The install is not offered then.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isInCombat;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isInstallingUpdate;

    /// <summary>"Not available during combat" or the install progress; empty otherwise.</summary>
    [ObservableProperty]
    private string _updateInstallHint = string.Empty;

    partial void OnIsInCombatChanged(bool value) => RefreshInstallHint();

    partial void OnIsInstallingUpdateChanged(bool value) => RefreshInstallHint();

    private void RefreshInstallState()
    {
        UpdateCanInstall = _updateAssets is not null && _updateInstaller is not null && _installExePath.Length > 0;
        OnPropertyChanged(nameof(UpdateInstallFolder));
    }

    private void RefreshInstallHint()
    {
        if (!IsInstallingUpdate)
        {
            UpdateInstallHint = IsInCombat && UpdateCanInstall ? "Installing is not available during combat." : string.Empty;
        }
    }

    private bool CanInstallUpdate() => UpdateCanInstall && !IsInCombat && !IsInstallingUpdate;

    /// <summary>The confirmation text: which version, which folder, and what will happen.</summary>
    public string InstallConfirmationText(string version) =>
        $"Install {AppName} {version} now?\n\n" +
        $"{AppName} will close, replace EQOverwatch.exe in this folder:\n{UpdateInstallFolder}\nand start again.\n\n" +
        "Your settings and saved buff timers are kept. The download is checked against the SHA-256 checksum published with the release, " +
        "and nothing is installed if it does not match.";

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (_updateInstaller is null || _updateAssets is null || _installExePath.Length == 0 || IsInCombat)
        {
            return;
        }

        if (!_dialogs.Confirm($"Install {_latestVersion}", InstallConfirmationText(_latestVersion)))
        {
            return;
        }

        IsInstallingUpdate = true;
        string staging = Path.Combine(UpdateInstallFolder, UpdateSwapper.StagingFolderName);
        try
        {
            var progress = new Progress<string>(text => UpdateInstallHint = text);
            string newExe = await _updateInstaller.PrepareAsync(_updateAssets, staging, progress, CancellationToken.None);

            if (IsInCombat)
            {
                throw new UpdateInstallException("A fight started while the update was downloading, so it was not installed. Try again after the fight.");
            }

            UpdateSwapper.Swap(_installExePath, newExe);
        }
        catch (UpdateInstallException ex)
        {
            UpdateStatus = ex.Message;
            StatusMessage = ex.Message;
            UpdateInstallHint = ex.Message;
            IsInstallingUpdate = false;
            return;
        }

        // The new exe is in place: save everything, then start it. Nothing runs after the restart call.
        UpdateInstallHint = "Restarting…";
        Shutdown();
        _restartApp?.Invoke(_installExePath);
    }

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
                _updateAssets = result.Assets;
                RefreshInstallState();
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
