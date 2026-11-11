using System.IO;
using EQOverwatch.App.ViewModels;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The Experience by mob table on the Experience tab.</summary>
public partial class MainViewModelTests
{
    [Fact]
    public async Task Scanning_log_history_fills_the_experience_by_mob_table_with_a_level_filter()
    {
        using var h = new Harness();
        string path = Path.Combine(h.Dir, "eqlog_Kaelith_frostreaver.txt");
        File.WriteAllLines(path, new[]
        {
            "[Wed May 27 12:00:00 2026] You gain experience! (12.000%)",
            "[Wed May 27 12:00:00 2026] You have slain a werebat!",
            "[Wed May 27 12:01:00 2026] You gain experience! (8.000%)",
            "[Wed May 27 12:01:00 2026] A werebat was slain by Kaelith!",
            "[Wed May 27 12:02:00 2026] You have gained a level! Welcome to level 25!",
            "[Wed May 27 12:02:00 2026] You gain party experience! (2.500%)",
            "[Wed May 27 12:02:00 2026] A ghoul was slain by Borvik!",
            "[Wed May 27 12:03:00 2026] You gain party experience! (6.000%)",
            "[Wed May 27 12:03:00 2026] A ghoul was slain by Borvik!",
            "[Wed May 27 12:04:00 2026] You gain experience! (1.000%)",
        });
        h.Dialogs.LogFiles = new[] { path };
        h.Vm.AddLogCommand.Execute(null);

        await h.Vm.ScanMobExperienceCommand.ExecuteAsync(null);

        MobExperienceCharacterChoice choice = Assert.Single(h.Vm.MobExperienceCharacters);
        Assert.Equal("Kaelith (frostreaver)", choice.Label);
        Assert.Equal(choice, h.Vm.SelectedMobExperienceCharacter);
        Assert.Equal(new[] { "All", "25", "24" }, h.Vm.MobExperienceLevels);
        Assert.Equal(
            new[] { ("25", "a ghoul", 1, "6.000%"), ("", "(unknown mob)", 1, "1.000%"), ("24", "a werebat", 2, "10.000%"), ("", "a ghoul", 1, "2.500%") },
            h.Vm.MobExperienceRows.Select(r => (r.Level, r.Mob, r.Kills, r.AveragePercent)).ToArray());

        h.Vm.SelectedMobExperienceLevel = "24";
        Assert.Equal(new[] { "a werebat", "a ghoul" }, h.Vm.MobExperienceRows.Select(r => r.Mob).ToArray());
        Assert.Contains("1 character", h.Vm.MobExperienceStatus);
    }
}
