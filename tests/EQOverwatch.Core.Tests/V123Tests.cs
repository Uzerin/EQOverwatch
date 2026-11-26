using EQOverwatch.Core.Discovery;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V123Tests
{
    [Fact]
    public void EverQuest_three_or_four_levels_down_is_found_through_game_named_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "eqo-v123-" + Guid.NewGuid().ToString("N"));
        try
        {
            string deep = Path.Combine(root, "Games", "Daybreak", "EverQuest");
            string hidden = Path.Combine(root, "Work", "Stuff", "Other", "EverQuest");
            Directory.CreateDirectory(deep);
            Directory.CreateDirectory(hidden);

            List<string> found = EqInstallFinder.FoldersUnder(root).ToList();
            Assert.Contains(deep, found);
            Assert.DoesNotContain(hidden, found); // ordinary folders are not searched past two levels
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Game_folder_names_are_recognised()
    {
        Assert.True(EqInstallFinder.IsGameFolderName("Games"));
        Assert.True(EqInstallFinder.IsGameFolderName("Daybreak Game Company"));
        Assert.True(EqInstallFinder.IsGameFolderName("SteamLibrary"));
        Assert.True(EqInstallFinder.IsGameFolderName("EQ"));
        Assert.False(EqInstallFinder.IsGameFolderName("Requests"));
        Assert.False(EqInstallFinder.IsGameFolderName("Photos"));
    }

    [Fact]
    public void Steam_libraries_on_other_drives_are_checked_for_EverQuest()
    {
        const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\Program Files (x86)\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\SteamLibrary\"\n\t}\n}";
        Assert.Equal(
            new[]
            {
                Path.Combine(@"C:\Program Files (x86)\Steam", "steamapps", "common", "EverQuest"),
                Path.Combine(@"D:\SteamLibrary", "steamapps", "common", "EverQuest"),
            },
            EqInstallFinder.SteamLibraryFolders(vdf));
        Assert.Empty(EqInstallFinder.SteamLibraryFolders(null));
    }
}
