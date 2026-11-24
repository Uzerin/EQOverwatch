using EQOverwatch.Core.Discovery;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class EqInstallFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqo-finder-" + Guid.NewGuid().ToString("N"));

    public EqInstallFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeInstall(string name, bool game, DateTime lastPlayedUtc, params string[] logs)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "Logs"));
        if (game)
        {
            File.WriteAllText(Path.Combine(folder, EqInstallFinder.GameExeName), string.Empty);
        }

        foreach (string log in logs)
        {
            string path = Path.Combine(folder, "Logs", log);
            File.WriteAllText(path, "[Mon Nov 24 10:00:00 2026] Welcome to EverQuest!\n");
            File.SetLastWriteTimeUtc(path, lastPlayedUtc);
        }

        return folder;
    }

    [Fact]
    public void Most_recently_played_install_comes_first_and_duplicates_are_dropped()
    {
        string old = MakeInstall("Old", true, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            "eqlog_Tankard_frostreaver.txt", "eqlog_Mendwell_frostreaver.txt", "eqlog_Porter_frostreaver.txt");
        string current = MakeInstall("Current", true, new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), "eqlog_Shieldwall_vaniki.txt");
        string notEq = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(notEq);

        IReadOnlyList<EqInstall> found = EqInstallFinder.Find(new[] { old, current, current + Path.DirectorySeparatorChar, notEq, null });

        Assert.Equal(new[] { current, old }, found.Select(f => f.Folder));
        Assert.Equal(3, found[1].CharacterCount);
        Assert.Equal("Logs found for 1 character", found[0].Description);
    }

    [Fact]
    public void Game_folder_without_logs_is_offered_and_logs_folder_picks_its_parent()
    {
        string fresh = MakeInstall("Fresh", true, default);
        IReadOnlyList<EqInstall> found = EqInstallFinder.Find(new[] { Path.Combine(fresh, "Logs") });

        EqInstall install = Assert.Single(found);
        Assert.Equal(fresh, install.Folder);
        Assert.Equal(0, install.CharacterCount);
        Assert.StartsWith("No logs yet", install.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_under_a_root_go_two_levels_deep()
    {
        string deep = MakeInstall(Path.Combine("Games", "EverQuest"), true, default);
        Directory.CreateDirectory(Path.Combine(deep, "Too", "Deep"));

        List<string> folders = EqInstallFinder.FoldersUnder(_root).ToList();

        Assert.Contains(deep, folders);
        Assert.DoesNotContain(Path.Combine(deep, "Logs"), folders);
    }

    [Fact]
    public void Well_known_folders_include_the_launcher_and_steam()
    {
        string publicFolder = Path.Combine(_root, "Public");
        string programs = Path.Combine(_root, "Programs");
        List<string> folders = EqInstallFinder.WellKnownFolders(publicFolder, programs, null).ToList();

        Assert.Contains(Path.Combine(publicFolder, "Daybreak Game Company", "Installed Games", "EverQuest"), folders);
        Assert.Contains(Path.Combine(programs, "Steam", "steamapps", "common", "EverQuest"), folders);
    }
}
