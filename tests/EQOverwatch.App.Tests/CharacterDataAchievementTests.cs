using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Characters;
using Xunit;

namespace EQOverwatch.App.Tests;

public class CharacterDataAchievementTests
{
    [Fact]
    public void Special_keys_and_the_achievements_tab_come_from_the_achievements_file_and_the_log()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-ach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CharacterDataStore();
            File.WriteAllText(Path.Combine(dir, "Stackprime_frostreaver-Inventory.txt"),
                "Location\tName\tID\tCount\tSlots\nHead\tHelm\t1549\t1\t6\nKeyRing\tName\tID\t\nMount\tGlowing Black Drum\t52021\n");
            File.WriteAllText(Path.Combine(dir, "Stackprime_frostreaver-Achievements.txt"), string.Join("\n",
                "General: Keys",
                "C\tTrakanon Idol", "C\t\tTrakanon Idol",
                "I\tVex Thal Palace Key", "I\t\tPalace Key",
                "I\tKey to Charasis", "I\t\tKey to Charasis",
                "Shadows of Luclin: Exploration",
                "I\tVex Thal Traveler", "I\t\tVisit Vex Thal",
                "L\tPiecing Together History") + "\n");
            Assert.True(store.Import(Path.Combine(dir, "Stackprime_frostreaver-Inventory.txt")));
            Assert.True(store.Import(Path.Combine(dir, "Stackprime_frostreaver-Achievements.txt")));

            var progress = new CharacterProgressSource(
                _ => Array.Empty<SkillRowView>(), () => Array.Empty<TradeskillRowView>(), _ => Array.Empty<FactionRowView>(),
                KeyRingAdds: () => new[] { "The Scepter of Shadows", "Trakanon Idol" },
                AchievementsDone: () => new[] { "Vex Thal Traveler" });
            var vm = new CharacterDataViewModel(store, () => dir, "Stackprime", "frostreaver", () => "Warrior",
                CharacterDataViewModel.AchievementsTab, progress: progress);

            Assert.Equal(
                new[] { ("Mount", "Glowing Black Drum", "52021"), ("Special", "The Scepter of Shadows", ""), ("Special", "Trakanon Idol", "") },
                vm.KeyRingRows.Select(k => (k.Type, k.Name, k.ItemId)));

            Assert.Equal(5, vm.AchievementRows.Count);
            Assert.Equal(new[] { "All categories", "General: Keys", "Shadows of Luclin: Exploration" }, vm.AchievementCategories);
            AchievementRowView traveler = vm.AchievementRows.Single(a => a.Achievement == "Vex Thal Traveler");
            Assert.Equal(("Done", "1/1"), (traveler.Status, traveler.Steps)); // completed in the log since the file
            Assert.Equal("Locked", vm.AchievementRows.Single(a => a.Achievement == "Piecing Together History").Status);
            Assert.StartsWith("2 of 5 completed", vm.AchievementSummary);

            vm.OnlyCompletedAchievements = true;
            Assert.Equal(new[] { "Trakanon Idol", "Vex Thal Traveler" }, vm.AchievementRows.Select(a => a.Achievement));
            vm.OnlyCompletedAchievements = false;
            vm.AchievementCategory = "General: Keys";
            Assert.Equal(3, vm.AchievementRows.Count);
            vm.FilterText = "charasis";
            Assert.Equal("Key to Charasis", Assert.Single(vm.AchievementRows).Achievement);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class Release70AppTests
{
    [Fact]
    public void Played_and_plat_per_hour_are_written_short()
    {
        Assert.Equal("13d 22h 59m", MainViewModel.FormatMinutes(13 * 1440 + 22 * 60 + 59));
        Assert.Equal("5h 02m", MainViewModel.FormatMinutes(302));
        Assert.Equal("12m", MainViewModel.FormatMinutes(12));
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("100", MainViewModel.PlatPerHour(200, 7200, culture));
        Assert.Equal(string.Empty, MainViewModel.PlatPerHour(5, 120, culture));
    }
}
