using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Discovery;
using Microsoft.Win32;

namespace EQOverwatch.App;

/// <summary>One EverQuest folder offered in the first-run window.</summary>
public sealed partial class EqInstallChoice : ObservableObject
{
    public EqInstallChoice(string folder, string description)
    {
        Folder = folder;
        Description = description;
    }

    public string Folder { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One overlay offered in the first-run window.</summary>
public sealed partial class OverlayChoice : ObservableObject
{
    public OverlayChoice(Services.OverlayKind kind, bool selected)
    {
        Kind = kind;
        _isSelected = selected;
    }

    public Services.OverlayKind Kind { get; }

    public string Title => Services.OverlayKindExtensions.IsFloatingText(Kind)
        ? Services.OverlayKindExtensions.Title(Kind) + " (floating)"
        : Services.OverlayKindExtensions.Title(Kind);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Shown once, the first time the app starts: the EverQuest folder (found on this PC or browsed to) and the sharing choices. Closing it
/// keeps the folder that is picked, the sharing defaults and the overlays ticked.
/// It stays on top of the overlays, can be dragged by any empty spot, and Esc closes it.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly ObservableCollection<EqInstallChoice> _choices = new();

    private readonly List<OverlayChoice> _overlays;

    public WelcomeWindow()
        : this(Array.Empty<Services.OverlayKind>())
    {
    }

    public WelcomeWindow(IReadOnlyCollection<Services.OverlayKind> overlaysOn)
    {
        InitializeComponent();
        _overlays = Services.OverlayKindExtensions.All.Select(kind => new OverlayChoice(kind, overlaysOn.Contains(kind))).ToList();
        OverlayChoices.ItemsSource = _overlays;
        ContentRendered += (_, _) => Activate(); // in front of any window that is already up
        Interop.DarkWindowChrome.Apply(this, resizable: false);
        Installs.ItemsSource = _choices;
        Heading.Text = "Welcome to " + Core.AppInfo.Name;
        Title = Heading.Text;
        Logo.Source = LargestIconFrame();
        Loaded += async (_, _) => await FindInstallsAsync();
    }

    /// <summary>The app icon's sharpest picture (the icon file holds several sizes; WPF would show the first, small one).</summary>
    private static System.Windows.Media.ImageSource? LargestIconFrame()
    {
        try
        {
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                new Uri("pack://application:,,,/Assets/EQOverwatch.ico"), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>The EverQuest folder picked, or null when none was found or picked.</summary>
    public string? Folder => _choices.FirstOrDefault(c => c.IsSelected)?.Folder;

    public bool AnonymousSharingOn => AnonymousSharing.IsChecked == true;

    public bool ServerFirstOn => ServerFirst.IsChecked == true;

    /// <summary>The overlays ticked to open.</summary>
    public IReadOnlyCollection<Services.OverlayKind> Overlays => _overlays.Where(o => o.IsSelected).Select(o => o.Kind).ToList();

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }

    private async Task FindInstallsAsync()
    {
        IReadOnlyList<EqInstall> found = await Task.Run(() => EqInstallFinder.Find(Candidates()));
        foreach (EqInstall install in found)
        {
            if (!_choices.Any(c => string.Equals(c.Folder, install.Folder, StringComparison.OrdinalIgnoreCase)))
            {
                _choices.Add(new EqInstallChoice(install.Folder, install.Description));
            }
        }

        if (_choices.Count > 0 && !_choices.Any(c => c.IsSelected))
        {
            _choices[0].IsSelected = true;
        }

        SearchStatus.Text = _choices.Count switch
        {
            0 => "No EverQuest folder was found. Click Browse… and pick the folder that holds EverQuest's Logs folder.",
            1 => "Found this EverQuest folder:",
            _ => "Found these EverQuest folders. Pick the one you play from:",
        };
    }

    /// <summary>Folders Windows lists as installed, the usual install folders and folders near the root of each fixed drive.</summary>
    private static IEnumerable<string?> Candidates()
    {
        foreach (string folder in RunningGameFolders())
        {
            yield return folder;
        }

        foreach (string folder in InstalledFolders())
        {
            yield return folder;
        }

        foreach (string folder in EqInstallFinder.WellKnownFolders(
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } docs ? Path.GetDirectoryName(docs) : null,
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
        {
            yield return folder;
        }

        foreach (string folder in SteamFolders())
        {
            yield return folder;
        }

        foreach (string? home in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                 })
        {
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
            {
                continue;
            }

            foreach (string folder in EqInstallFinder.FoldersUnder(home, depth: 1, deepDepth: 3))
            {
                if (File.Exists(Path.Combine(folder, EqInstallFinder.GameExeName))
                    || Directory.Exists(Path.Combine(folder, EqDirectoryScanner.LogsFolderName)))
                {
                    yield return folder;
                }
            }
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            foreach (string folder in EqInstallFinder.FoldersUnder(drive.RootDirectory.FullName))
            {
                if (File.Exists(Path.Combine(folder, EqInstallFinder.GameExeName))
                    || Directory.Exists(Path.Combine(folder, EqDirectoryScanner.LogsFolderName)))
                {
                    yield return folder;
                }
            }
        }
    }

    /// <summary>The folder of each EverQuest that is running now (a box PC often has it open already).</summary>
    private static IEnumerable<string> RunningGameFolders()
    {
        var found = new List<string>();
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(EqInstallFinder.GameExeName)))
        {
            try
            {
                if (ImagePath(process) is { Length: > 0 } exe && Path.GetDirectoryName(exe) is { Length: > 0 } folder)
                {
                    found.Add(folder);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Not allowed to look at that process: passed over.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    /// <summary>A process's exe path; asks only for limited rights, so it works for a game that runs as administrator too.</summary>
    private static string? ImagePath(System.Diagnostics.Process process)
    {
        const uint QueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>EverQuest in every Steam library, from Steam's list of library folders.</summary>
    private static IEnumerable<string> SteamFolders()
    {
        var found = new List<string>();
        foreach (string? root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            try
            {
                string vdf = Path.Combine(root, "Steam", "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    found.AddRange(EqInstallFinder.SteamLibraryFolders(File.ReadAllText(vdf)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable list is passed over.
            }
        }

        return found;
    }

    /// <summary>Install folders of programs named EverQuest in Windows' list of installed programs.</summary>
    private static IEnumerable<string> InstalledFolders()
    {
        var found = new List<string>();
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using RegistryKey? uninstall = hive.OpenSubKey(path);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? app = uninstall.OpenSubKey(name);
                        if (app?.GetValue("DisplayName") is string display && display.Contains("EverQuest", StringComparison.OrdinalIgnoreCase)
                            && app.GetValue("InstallLocation") is string location && location.Length > 0)
                        {
                            found.Add(location);
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // An unreadable key is passed over.
                }
            }
        }

        return found;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select your EverQuest folder (the one that contains Logs)", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string folder = EqDirectoryScanner.Normalize(dialog.FolderName);
        EqInstallChoice? choice = _choices.FirstOrDefault(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            string description = EqInstallFinder.Inspect(folder)?.Description ?? "No EverQuest files found here yet";
            choice = new EqInstallChoice(folder, description);
            _choices.Add(choice);
        }

        foreach (EqInstallChoice other in _choices)
        {
            other.IsSelected = ReferenceEquals(other, choice);
        }

        SearchStatus.Text = "Pick the folder you play from:";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
