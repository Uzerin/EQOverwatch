using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class KeyRingAndAchievementTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 20, 12, 16, DateTimeKind.Utc);

    [Fact]
    public void The_achievements_file_is_read_with_status_and_steps()
    {
        List<AchievementEntry> rows = CharacterOutputParser.ParseAchievements(new[]
        {
            "General: Keys",
            "I\tVex Thal Palace Key",
            "I\t\tPalace Key",
            "C\tThe Scepter of Shadows",
            "C\t\tThe Scepter of Shadows",
            "Shadows of Luclin: Exploration",
            "I\tLuclin Explorer",
            "C\t\tVisit Vex Thal",
            "C\t\tVisit Akheva Ruins",
            "I\t\tVisit Umbral Plains",
            "L\tPiecing Together History",
        });

        Assert.Equal(4, rows.Count);
        Assert.Equal(new AchievementEntry("General: Keys", "The Scepter of Shadows", "C", 1, 1), rows[1]);
        Assert.True(rows[1].IsDone);
        Assert.Equal(new AchievementEntry("Shadows of Luclin: Exploration", "Luclin Explorer", "I", 2, 3), rows[2]);
        Assert.Equal("L", rows[3].Status);
        Assert.True(CharacterOutputParser.TryParseFileName("Stackprime_frostreaver-Achievements.txt", out OutputFileInfo info));
        Assert.Equal(OutputFileKind.Achievements, info.Kind);
    }

    [Fact]
    public void Key_ring_and_achievement_lines_are_kept_per_character_once_each()
    {
        var store = new ProgressStore();
        store.OnLine("Stackprime", "frostreaver", "The Scepter of Shadows has been added to your key ring.", T0);
        store.OnLine("Stackprime", "frostreaver", "The Scepter of Shadows has been added to your key ring.", T0.AddSeconds(30));
        store.OnLine("Stackprime", "frostreaver", "You have completed achievement: Vex Thal Traveler", T0.AddSeconds(28));
        store.OnLine("Stackprime", "frostreaver", "Your guildmate Ream has completed The Scepter of Shadows achievement.", T0);

        CharacterFlag key = Assert.Single(store.KeyRingAdds("Stackprime", "frostreaver"));
        Assert.Equal(("The Scepter of Shadows", T0), (key.Text, key.Utc));
        Assert.Equal("Vex Thal Traveler", Assert.Single(store.AchievementsDone("Stackprime", "frostreaver")).Text);
        Assert.Empty(store.KeyRingAdds("Ream", "frostreaver"));
    }
}
