using System.IO;
using System.Diagnostics;
using System.Windows;
using EQOverwatch.App.Overlays;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Theming;
using EQOverwatch.Core.Updates;
using System.Net.Http;

namespace EQOverwatch.App;

/// <summary>
/// Composition root: loads settings, applies a theme, builds the Core pipeline, the audio service and all six overlays
/// (Buffs, Debuffs, Songs, Raid Triggers, CH Chain, DPS), restores the saved state through <see cref="MainViewModel"/>, and shows the
/// main window. Closing the main window ends the app.
/// </summary>
public partial class CompanionApp : Application
{
    private AppSettingsService? _settingsService;
    private CompanionPipeline? _pipeline;
    private AudioNotificationService? _audio;
    private MainViewModel? _viewModel;
    private HttpClient? _http;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The brushes every window refers to must exist before the first window is created; MainViewModel then applies the saved theme.
        var themeManager = new ThemeManagerService();
        themeManager.Apply(ThemePresets.ObsidianSlate);

        _settingsService = new AppSettingsService();
        AppSettings settings = _settingsService.Load();

        _pipeline = new CompanionPipeline(TimeSpan.FromSeconds(settings.EncounterTimeoutSeconds));
        _audio = new AudioNotificationService(_pipeline.Triggers);
        _audio.Error += ex => Debug.WriteLine($"Audio error: {ex}");

        var windows = new Dictionary<OverlayKind, OverlayWindowBase>();
        DpsOverlayViewModel? dpsVm = null;
        foreach (OverlayKind kind in OverlayKindExtensions.All)
        {
            if (kind.Category() is { } category)
            {
                // Each timer overlay shows only its own category of the trigger engine's timers.
                windows[kind] = new TimerOverlayWindow(new TimerOverlayViewModel(_pipeline.Triggers, category), kind);
            }
            else
            {
                dpsVm = new DpsOverlayViewModel(() => _pipeline.LiveDps.Snapshot, _pipeline.Roster.GetClassLabel);
                windows[kind] = new DpsOverlayWindow(dpsVm);
            }
        }

        // Restores logs, triggers, raid timers, spells, timeout, mute, theme and every overlay's bounds / visibility / lock state.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _viewModel = new MainViewModel(
            _pipeline, _settingsService, settings, new OverlayHost(windows), _audio, new WpfDialogService(), themeManager, new WpfClipboardService(),
            updateChecker: new GitHubUpdateChecker(_http),
            openUrl: url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
            characterDataPresenter: new WpfCharacterDataPresenter(),
            characterData: new CharacterDataStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName, "characters")));

        if (dpsVm is not null)
        {
            // The overlay's Chat / Img buttons use the same export code as the DPS tab.
            MainViewModel vm = _viewModel;
            dpsVm.CopyChatRequested += encounter => vm.CopyChat(encounter);
            dpsVm.CopyImageRequested += encounter => vm.CopyImage(encounter);
        }

        var main = new MainWindow(_viewModel);
        MainWindow = main; // closing it ends the app (ShutdownMode=OnMainWindowClose)
        main.Show();

        // Catch the DPS meter up from the Primary DPS log and start live monitoring, so opening the app mid-fight shows the fight.
        _ = _viewModel.StartSessionAsync();

        // Tell the user when a newer version has been published (does nothing when switched off in Settings; failures are silent).
        _ = _viewModel.StartUpdateCheckAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Shutdown(); // no-op if the main window's Closing handler already ran it
        }
        else
        {
            // Startup failed part-way: still release what exists.
            _audio?.Dispose();
            if (_pipeline is not null)
            {
                Task.Run(() => _pipeline.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }

            _settingsService?.Dispose();
        }

        base.OnExit(e);
    }
}
