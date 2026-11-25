using System.IO;
using System.Diagnostics;
using System.Windows;
using EQOverwatch.App.Overlays;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Theming;
using EQOverwatch.Core.Updates;
using System.Net.Http;
using System.Runtime.InteropServices;

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
    private EQOverwatch.App.Services.TrayIconService? _trayIcon;
    private HttpClient? _http;
    private HttpClient? _updateHttp;
    private EQOverwatch.App.Services.TellToastService? _tellToasts;
    private HttpClient? _sharedDropsHttp;

    /// <summary>Passed to the old copy when an update could not be put in place, so it says so when it opens again.</summary>
    private const string UpdateFailedArgument = "--update-failed";

    private string _crashFolder = string.Empty;

    private void WriteCrashLog(string source, Exception error)
    {
        try
        {
            CrashLog.Write(_crashFolder, source, error, AppInfo.Version, DateTime.UtcNow);
            _settingsService?.Flush(); // settings wait up to a minute before they are written: keep them
        }
        catch
        {
            // Nothing more can be done about it while closing.
        }
    }

    /// <summary>The one-time welcome window, then the usual start. Its choices are saved, so it never shows again.</summary>
    private async Task WelcomeThenStartAsync(Window owner)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var welcome = new WelcomeWindow { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            welcome.ShowDialog();
            await _viewModel.ApplyWelcomeAsync(welcome.Folder, welcome.AnonymousSharingOn, welcome.ServerFirstOn);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Welcome window", ex);
        }

        await _viewModel.StartSessionAsync();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unhandled errors go to crash.log in the settings folder, so a crash can be looked into.
        string crashFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName);
        _crashFolder = crashFolder;
        DispatcherUnhandledException += (_, args) => WriteCrashLog("UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("background thread", args.ExceptionObject as Exception ?? new InvalidOperationException("Unknown error"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashLog.Write(crashFolder, "unobserved task", args.Exception, AppInfo.Version, DateTime.UtcNow);

        // Before any file is touched: after Install update, wait for the old copy to finish saving and close; a second copy opened by
        // accident brings the open one to the front instead.
        if (!PassInstanceGate(e.Args))
        {
            Shutdown();
            return;
        }

        // The brushes every window refers to must exist before the first window is created; MainViewModel then applies the saved theme.
        var themeManager = new ThemeManagerService();
        themeManager.Apply(ThemePresets.ObsidianSlate);
        EQOverwatch.App.Interop.DarkWindowChrome.UseDarkTitleBars(); // windows with the standard title bar get a dark one

        // A restore chosen in the last run is applied here, before any setting or data file is read.
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName);
        ImportEditionSettings(dataFolder);
        try
        {
            EQOverwatch.Core.Backup.BackupService.ApplyPendingRestore(dataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Restore could not be applied: {ex.Message}");
        }

        _settingsService = new AppSettingsService();
        bool firstRun = !File.Exists(_settingsService.FilePath);
        AppSettings settings = _settingsService.Load();
        firstRun &= !_settingsService.MigratedFromLegacy;
        firstRun |= e.Args.Contains("--welcome", StringComparer.OrdinalIgnoreCase); // shows the first-run window again

        _pipeline = new CompanionPipeline(TimeSpan.FromSeconds(settings.EncounterTimeoutSeconds));
        _audio = new AudioNotificationService(_pipeline.Triggers);
        _audio.Error += ex => Debug.WriteLine($"Audio error: {ex}");

        var windows = new Dictionary<OverlayKind, OverlayWindowBase>();
        DpsOverlayViewModel? dpsVm = null;
        var mobCardVm = new MobCardOverlayViewModel();
        foreach (OverlayKind kind in OverlayKindExtensions.All)
        {
            if (kind.Category() is { } category)
            {
                // Each timer overlay shows only its own category of the trigger engine's timers.
                windows[kind] = new TimerOverlayWindow(new TimerOverlayViewModel(_pipeline.Triggers, category, isCharacterActive: _pipeline.IsBarShown), kind);
            }
            else if (kind == OverlayKind.Tells)
            {
                windows[kind] = new TellsOverlayWindow(new TellsOverlayViewModel(_pipeline.TellInbox, notices: _pipeline.Notices, zones: _pipeline.DynamicZones));
            }
            else if (kind == OverlayKind.MobCard)
            {
                windows[kind] = new MobCardOverlayWindow(mobCardVm);
            }
            else if (kind.IsFloatingText())
            {
                windows[kind] = new FloatingTextOverlayWindow(kind);
            }
            else
            {
                dpsVm = new DpsOverlayViewModel(() => _pipeline.LiveDps.Snapshot, _pipeline.Roster.GetClassLabel, _pipeline.IsMonitoredCharacter)
                {
                    ClassOnServer = (name, server) => _pipeline.Roster.GetClassLabel(name, server),
                };
                windows[kind] = new DpsOverlayWindow(dpsVm);
            }
        }

        // Restores logs, triggers, raid timers, spells, timeout, mute, theme and every overlay's bounds / visibility / lock state.
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName);
        _pipeline.Loot.SetStoragePath(Path.Combine(appData, "loot.json"));
        _pipeline.Guilds.SetStoragePath(Path.Combine(appData, "guilds.json")); // players' guilds from /who
        _pipeline.TimerPause.SetStoragePath(Path.Combine(appData, "bufftimers.json")); // puts back the buff and debuff bars of the last run
        var bossHistory = new EQOverwatch.Core.History.BossHistoryStore();
        bossHistory.SetStoragePath(Path.Combine(appData, "bosshistory.json"));
        _pipeline.BossAbilities.SetStoragePath(Path.Combine(appData, "bossabilities.json"));
        _pipeline.MobHits.SetStoragePath(Path.Combine(appData, "mobhits.json"));
        var mobLevels = new EQOverwatch.Core.History.MobLevelStore();
        mobLevels.SetStoragePath(Path.Combine(appData, "moblevels.json"));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var overlayHost = new OverlayHost(windows)
        {
            // Your own overlays: timer and text overlays that show the triggers routed to them.
            CustomFactory = (id, name) => new TimerOverlayWindow(
                new TimerOverlayViewModel(_pipeline.Triggers, null, isCharacterActive: _pipeline.IsBarShown) { CustomOverlayId = id }, name),
        };
        _viewModel = new MainViewModel(
            _pipeline, _settingsService, settings, overlayHost, _audio, new WpfDialogService(), themeManager, new WpfClipboardService(),
            updateChecker: new GitHubUpdateChecker(_http),
            openUrl: url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
            characterDataPresenter: new WpfCharacterDataPresenter(),
            characterData: new CharacterDataStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName, "characters")),
            raidRoster: new RaidRosterStore(Path.Combine(appData, "attendance")),
            remoteDataDirectory: Path.Combine(appData, "remote"),
            bossHistory: bossHistory,
            mobLevels: mobLevels,
            dataDirectory: appData);
        OnEditionViewModelCreated(_viewModel);

        if (dpsVm is not null)
        {
            // The overlay's Chat / Img buttons use the same export code as the DPS tab.
            MainViewModel vm = _viewModel;
            dpsVm.CopyChatRequested += encounter => vm.CopyChat(encounter);
            dpsVm.CopyImageRequested += encounter => vm.CopyImage(encounter);
            dpsVm.Progress = vm.FightProgressFor; // how far along a fight is, from the recorded kills of the same mob
            dpsVm.Regen = e => vm.RegenFor(e.TargetName); // the mob's estimated regen and hit points (every server)
            dpsVm.FirstPossible = vm.ServerFirstPossible; // a boss nobody on the server has killed yet
        }

        // Floating combat text: the numbers collected on the tailing thread are shown about twenty times a second.
        MainViewModel fctVm = _viewModel;
        var fctTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        fctTimer.Tick += (_, _) =>
        {
            foreach (EQOverwatch.Core.Services.FloatingNumber number in _pipeline.FloatingCombat.TakeAll())
            {
                (OverlayKind kind, string hex, double size, bool shown) = fctVm.FloatingLook(number);
                if (shown && windows.TryGetValue(kind, out OverlayWindowBase? window) && window is FloatingTextOverlayWindow fct
                    && EQOverwatch.Core.Theming.ThemeColors.TryParse(hex, out EQOverwatch.Core.Theming.Rgba c))
                {
                    var look = new FloatingLook(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B), size, fctVm.FctFontFamily, fctVm.FctFontWeight,
                        number.IsIncoming, fctVm.FctMergeHits);
                    string prefix = number.IsHeal ? "+" : number.Panel == EQOverwatch.Core.Services.FloatingPanel.Taken ? "-" : string.Empty;
                    fct.Add(MainViewModel.FloatingText(number), number.Character, number.IsCritical, look,
                        $"{number.Character}|{number.Target}|{number.Style}", number.Word is null ? number.Amount : 0, prefix);
                }
            }
        };
        fctTimer.Start();

        // The mob card: built on the tailing thread when you /con a mob, shown on the UI thread.
        _viewModel.MobCardReady += card => Dispatcher.BeginInvoke(() => mobCardVm.Show(card));

        // Tell toasts: a Windows notification for a new tell while EverQuest is not the window in front.
        _tellToasts = new EQOverwatch.App.Services.TellToastService();
        _viewModel.TellToastRequested += tell =>
        {
            if (!EQOverwatch.App.Interop.GameWindow.IsInFront())
            {
                _tellToasts.Show($"{tell.From} tells {tell.Character}", tell.Text);
            }
        };

        // Install update: a long timeout for the download, and a restart that starts the new exe once the old one has saved and swapped.
        _updateHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.SettingsFolderName);
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            EQOverwatch.Core.Updates.UpdateSwapper.CleanUp(exePath); // the leftovers of the last update
            EQOverwatch.App.Services.InstalledAppEntry.SyncVersion(exePath); // Installed apps shows the version Install update put in
        }

        _viewModel.ConfigureUpdateInstall(
            new EQOverwatch.Core.Updates.UpdateInstaller(_updateHttp), exePath,
            (exe, installed) =>
            {
                if (_viewModel.UpdateShutdownError is { } problem)
                {
                    WriteCrashLog("Update install", problem);
                }

                try
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                        Arguments = $"{InstanceGate.AfterUpdateArgument} {Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                                    + (installed ? string.Empty : " " + UpdateFailedArgument),
                    });
                }
                catch (Exception ex)
                {
                    WriteCrashLog("Update restart", ex);
                }

                // End at once: the exe this process runs from has been replaced, so no more of it may be loaded.
                Environment.Exit(0);
            });

        // The online shared drop table (both of its switches start off).
        _sharedDropsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _sharedDropsHttp.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.SettingsFolderName);
        _viewModel.ConfigureSharedDrops(new EQOverwatch.Core.Drops.HttpSharedDropsService(_sharedDropsHttp));
        _viewModel.ConfigureServerFirsts(new EQOverwatch.Core.Firsts.HttpServerFirstService(_sharedDropsHttp));
        _viewModel.ConfigureMobStats(new EQOverwatch.Core.History.HttpMobStatsService(_sharedDropsHttp));
        _viewModel.ConfigureResourceHunter(new EQOverwatch.Core.ResourceHunter.HttpResourceHunterService(_sharedDropsHttp));
        _viewModel.ConfigureItemFocusLookup(new EQOverwatch.Core.Spells.HttpItemFocusLookup(_sharedDropsHttp), Path.Combine(appData, "itemfoci.json"));
        _viewModel.NoticeRequested += notice => _tellToasts?.Show(notice.Title, notice.Text);
        _viewModel.LiveNoticeRequested += (notice, onlyWhenAway) => Dispatcher.BeginInvoke(() =>
        {
            if (!onlyWhenAway || !EQOverwatch.App.Interop.GameWindow.IsInFront())
            {
                _tellToasts?.Show(notice.Title, notice.Text);
            }
        });
        _viewModel.ShowFightPictureRequested += fight =>
        {
            var image = EQOverwatch.App.Services.EncounterImageRenderer.Render(fight, _viewModel.ActivePalette, _viewModel.WindowTitle);
            var window = new Window
            {
                Title = "Server First: " + fight.TargetName,
                Owner = MainWindow,
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxWidth = SystemParameters.WorkArea.Width * 0.95,
                MaxHeight = SystemParameters.WorkArea.Height * 0.95,
                Content = new System.Windows.Controls.ScrollViewer
                {
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    Content = new System.Windows.Controls.Image { Source = image, Stretch = System.Windows.Media.Stretch.None },
                },
            };
            window.Show();
        };
        _viewModel.ConfigureLeveling(new EQOverwatch.Core.Leveling.HttpLevelingService(_sharedDropsHttp), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName, "leveling.json"));

        var main = new MainWindow(_viewModel);
        MainWindow = main; // closing it ends the app (ShutdownMode=OnMainWindowClose)
        if (e.Args.Contains(StartupRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase))
        {
            main.WindowState = WindowState.Minimized; // started with Windows
        }

        main.Show();
        _trayIcon = new EQOverwatch.App.Services.TrayIconService(main, _viewModel); // minimize to the tray
        _viewModel.StartWithWindowsChanged += on => StartupRegistration.Set(on, Environment.ProcessPath);

        if (e.Args.Contains(UpdateFailedArgument, StringComparer.OrdinalIgnoreCase))
        {
            _viewModel.ReportFailedUpdate();
        }

        // Catch the DPS meter up from every log and start live monitoring, so opening the app mid-fight shows the fight. The very first
        // time, the welcome window picks the EverQuest folder and the sharing choices first.
        _ = firstRun ? WelcomeThenStartAsync(main) : _viewModel.StartSessionAsync();

        // Layouts saved for a monitor setup: switch when Windows says the monitors changed (only if the user turned that on).
        _viewModel.OnDisplayConfigurationChanged();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Tell the user when a newer version has been published (does nothing when switched off in Settings; failures are silent).
        _ = _viewModel.StartUpdateCheckAsync();
    }

    /// <summary>True when this copy may start: no other copy from this folder is running (or the one being replaced has closed).</summary>
    /// <summary>Lets an edition set up its own parts once the view model exists.</summary>
    partial void OnEditionViewModelCreated(MainViewModel viewModel);

    /// <summary>On an edition's first start, copies the other edition's settings folder so it opens with the same setup.</summary>
    private static void ImportEditionSettings(string dataFolder)
    {
        if (Edition.ImportFolderName is not { } from)
        {
            return;
        }

        try
        {
            EQOverwatch.Core.Settings.SettingsFolderImport.CopyIfMissing(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), from), dataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Settings could not be copied: {ex.Message}");
        }
    }

    private static bool PassInstanceGate(string[] args)
    {
        string? exe = Environment.ProcessPath;
        string? folder = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(folder))
        {
            return true;
        }

        (InstanceGateResult result, int otherId) = InstanceGate.Wait(
            () => OtherCopies(exe, folder), InstanceGate.UpdatedFrom(args), InstanceGate.DefaultTimeout, () => DateTime.UtcNow, Thread.Sleep);
        switch (result)
        {
            case InstanceGateResult.AlreadyRunning:
                BringToFront(otherId);
                return false;
            case InstanceGateResult.StillRunning:
                MessageBox.Show("EQ Overwatch is already running. Close it, then open EQ Overwatch again.", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Other EQOverwatch processes started from this folder. The old copy's file is renamed to EQOverwatch.exe.old during an update, so
    /// the folder is compared, not the full path.
    /// </summary>
    private static IReadOnlyList<OtherInstance> OtherCopies(string exe, string folder)
    {
        var found = new List<OtherInstance>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited)
                    {
                        continue;
                    }

                    string? path = process.MainModule?.FileName;
                    if (path is null || !string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Refresh();
                    found.Add(new OtherInstance(process.Id, process.MainWindowHandle != IntPtr.Zero));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // exited meanwhile, or not ours to look at
                }
            }
        }

        return found;
    }

    private static void BringToFront(int processId)
    {
        try
        {
            using Process other = Process.GetProcessById(processId);
            IntPtr window = other.MainWindowHandle;
            if (window == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(window))
            {
                ShowWindow(window, 9); // SW_RESTORE
            }

            SetForegroundWindow(window);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // it closed meanwhile
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _viewModel?.OnDisplayConfigurationChanged()));

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _tellToasts?.Dispose();
        _trayIcon?.Dispose();
        _pipeline?.Guilds.Flush();
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
