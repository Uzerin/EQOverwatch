using System.IO;
using EQCompanion.Core.Discovery;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class EqDirectoryScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-" + Guid.NewGuid().ToString("N"));

    public EqDirectoryScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Logs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Log(string name) => File.WriteAllText(Path.Combine(_dir, "Logs", name), string.Empty);

    [Fact]
    public void Normalize_turns_a_Logs_folder_into_its_parent_and_trims_separators()
    {
        string logs = Path.Combine(_dir, "Logs");

        Assert.Equal(_dir, EqDirectoryScanner.Normalize(logs));
        Assert.Equal(_dir, EqDirectoryScanner.Normalize(logs + Path.DirectorySeparatorChar));
        Assert.Equal(_dir, EqDirectoryScanner.Normalize("  " + _dir + Path.DirectorySeparatorChar + "  "));
        Assert.Equal(_dir, EqDirectoryScanner.Normalize(_dir));
        Assert.Equal(string.Empty, EqDirectoryScanner.Normalize("   "));
        Assert.Equal(string.Empty, EqDirectoryScanner.Normalize(null));
    }

    [Fact]
    public void Discover_finds_character_logs_sorted_by_server_then_character()
    {
        Log("eqlog_Zed_Teek.txt");
        Log("eqlog_Alice_Teek.txt");
        Log("eqlog_Bob_Frostreaver.txt");
        Log("eqlog_Carl_frostreaver.txt");
        Log("notalog.txt");
        Log("eqlog_Broken.txt");

        IReadOnlyList<DiscoveredLog> found = EqDirectoryScanner.Discover(_dir);

        Assert.Equal(
            new[] { "Frostreaver/Bob", "frostreaver/Carl", "Teek/Alice", "Teek/Zed" },
            found.Select(l => $"{l.ServerName}/{l.CharacterName}"));
        Assert.All(found, l => Assert.True(File.Exists(l.FilePath)));
    }

    [Fact]
    public void Discover_of_a_missing_or_blank_folder_is_empty()
    {
        Assert.Empty(EqDirectoryScanner.Discover(null));
        Assert.Empty(EqDirectoryScanner.Discover(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void The_spell_file_is_looked_for_in_the_game_folder()
    {
        Assert.False(EqDirectoryScanner.HasSpellsFile(_dir));
        File.WriteAllText(EqDirectoryScanner.GetSpellsFilePath(_dir), "x");
        Assert.True(EqDirectoryScanner.HasSpellsFile(_dir));
        Assert.EndsWith("spells_us.txt", EqDirectoryScanner.GetSpellsFilePath(_dir));
    }
}
