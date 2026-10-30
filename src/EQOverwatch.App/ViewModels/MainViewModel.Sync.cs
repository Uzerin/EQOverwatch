using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Sync;

namespace EQOverwatch.App.ViewModels;

// Multi-PC character data: this PC either receives other PCs' /outputfile files (Host) or sends its own to the main PC (Send).
public sealed partial class MainViewModel
{
    public IReadOnlyList<string> SyncModes { get; } = new[]
    {
        "Off",
        "Main PC: receive from my other PCs",
        "Other PC: send to my main PC",
    };

    private string? _remoteDirectory;
    private CharacterSyncHost? _syncHost;
    private CharacterSyncSender? _syncSender;
    private volatile string? _syncStatusPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncHostMode))]
    [NotifyPropertyChangedFor(nameof(IsSyncSendMode))]
    private int _syncModeIndex;

    public bool IsSyncHostMode => SyncModeIndex == 1;
    public bool IsSyncSendMode => SyncModeIndex == 2;

    [ObservableProperty]
    private string _syncCode = string.Empty;

    [ObservableProperty]
    private string _syncPcName = Environment.MachineName;

    [ObservableProperty]
    private string _syncHostAddress = string.Empty;

    [ObservableProperty]
    private string _syncStatus = "Off. Character files stay on this PC.";

    partial void OnSyncModeIndexChanged(int value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        ApplySyncMode();
    }

    partial void OnSyncCodeChanged(string value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnSyncPcNameChanged(string value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnSyncHostAddressChanged(string value)
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    private static string SyncModeToText(int index) => index switch { 1 => "Host", 2 => "Send", _ => "Off" };

    private static int SyncModeFromText(string? text) => text switch { "Host" => 1, "Send" => 2, _ => 0 };

    /// <summary>Starts or stops the listener or sender to match the chosen mode.</summary>
    private void ApplySyncMode()
    {
        StopSync();
        switch (SyncModeIndex)
        {
            case 1:
                if (SyncSettingsCode().Length == 0)
                {
                    SyncCode = SyncProtocol.NewCode();
                }

                string folder = _remoteDirectory ?? Path.Combine(Path.GetTempPath(), "eqo-remote");
                var host = new CharacterSyncHost(folder, () => SyncCode);
                host.StatusChanged += message => _syncStatusPending = message;
                host.Received += OnSyncReceived;
                _syncHost = host;
                host.Start();
                break;
            case 2:
                var sender = new CharacterSyncSender(
                    () => EqFolderPath,
                    () => SyncCode,
                    () => string.IsNullOrWhiteSpace(SyncPcName) ? Environment.MachineName : SyncPcName,
                    () => SyncHostAddress ?? string.Empty);
                sender.StatusChanged += message => _syncStatusPending = message;
                _syncSender = sender;
                SyncStatus = "Looking for the main PC…";
                sender.Start();
                break;
            default:
                SyncStatus = "Off. Character files stay on this PC.";
                break;
        }
    }

    private string SyncSettingsCode() => SyncProtocol.NormalizeCode(SyncCode);

    private void StopSync()
    {
        _syncHost?.Dispose();
        _syncHost = null;
        _syncSender?.Dispose();
        _syncSender = null;
    }

    /// <summary>Called on a background thread when another PC's files arrive: reads them like any other output file.</summary>
    private void OnSyncReceived(string pc, IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            _characterData.Import(path, pc);
        }
    }

    /// <summary>Called from <see cref="Refresh"/>: shows the last message from the sync threads.</summary>
    private void RefreshSyncStatus()
    {
        string? pending = _syncStatusPending;
        if (pending is not null)
        {
            _syncStatusPending = null;
            SyncStatus = pending;
        }
    }

    [RelayCommand]
    private void NewSyncCode()
    {
        SyncCode = SyncProtocol.NewCode();
        SyncStatus = "New pairing code made. Type it into your other PCs; PCs using the old code are no longer accepted.";
    }

    [RelayCommand]
    private void CopySyncCode()
    {
        if (SyncSettingsCode().Length == 0)
        {
            return;
        }

        try
        {
            _clipboard.SetText(SyncCode);
            SyncStatus = "Pairing code copied.";
        }
        catch (Exception ex)
        {
            SyncStatus = $"The clipboard could not be used: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SendCharacterFilesNowAsync()
    {
        if (_syncSender is null)
        {
            SyncStatus = "Switch this PC to \"Other PC: send to my main PC\" first.";
            return;
        }

        SyncStatus = "Sending…";
        await _syncSender.SendNowAsync();
    }

    [RelayCommand]
    private async Task FindMainPcAsync()
    {
        SyncStatus = "Looking for the main PC…";
        System.Net.IPAddress? found = await CharacterSyncSender.DiscoverHostAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        if (found is null)
        {
            SyncStatus = "No main PC answered. Check it is running Overwatch in Main PC mode, or type its address.";
            return;
        }

        SyncStatus = $"Found the main PC at {found}.";
    }
}
