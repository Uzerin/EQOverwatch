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

/// <summary>
/// Shown once, the first time the app starts: the EverQuest folder (found on this PC or browsed to) and the sharing choices. Closing it
/// keeps the folder that is picked and the sharing defaults.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly ObservableCollection<EqInstallChoice> _choices = new();

    public WelcomeWindow()
    {
        InitializeComponent();
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
