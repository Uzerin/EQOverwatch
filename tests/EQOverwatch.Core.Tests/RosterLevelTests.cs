using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Settings;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public class RosterLevelTests
{
    [Theory]
    [InlineData("[65 Archmage (Magician)] Oodles (Gnome) <Guild>", 65)]
    [InlineData("[125 Grandmaster (Monk)] Kicker (Human)", 125)]
    [InlineData("[ANONYMOUS] Stackprime", 0)]
    public void A_who_line_gives_the_level_when_it_shows_one(string message, int level)
    {
        Assert.True(RosterLineParser.TryParse(message, out RosterObservation seen));
        Assert.Equal(level, seen.Level);
    }

    [Theory]
    [InlineData("You have gained a level! Welcome to level 126!", 126)]
    [InlineData("You have gained a level! Welcome to level 9!", 9)]
    public void A_level_up_line_gives_the_new_level(string message, int level)
    {
        Assert.True(RosterLineParser.TryParseLevelUp(message, out int seen));
        Assert.Equal(level, seen);
    }

    [Theory]
    [InlineData("You have gained a level! Welcome to level 0!")]
    [InlineData("You have gained a level! Welcome to level 999!")]
    [InlineData("You have gained a level! Welcome to level abc!")]
    [InlineData("You have gained experience!")]
    [InlineData("Oodles tells the raid, 'You have gained a level! Welcome to level 70!'")]
    public void Other_lines_are_not_level_ups(string message)
    {
        Assert.False(RosterLineParser.TryParseLevelUp(message, out _));
    }

    [Fact]
    public void The_level_is_kept_when_a_later_sighting_has_none_and_replaced_when_it_has_one()
    {
        var roster = new PlayerRoster();
        roster.Observe("[65 Archmage (Magician)] Oodles (Gnome)");
        roster.Observe("[ANONYMOUS] Oodles");

        Assert.Equal(65, roster.GetLevel("Oodles"));
        Assert.Equal("Magician", roster.GetClass("Oodles"));

        roster.Observe("[66 Archmage (Magician)] Oodles (Gnome)");
        Assert.Equal(66, roster.GetLevel("Oodles"));
        Assert.Equal(0, roster.GetLevel("Nobody"));
    }

    [Fact]
    public async Task A_level_up_in_a_characters_own_log_updates_that_character()
    {
        var roster = new PlayerRoster();
        roster.LearnPlayer("Stackprime", "Cleric", 125);

        await RunLinesAsync(new[] { Line("You have gained a level! Welcome to level 126!") }, "Stackprime", LogRoute.Triggers, triggerSink: roster);

        Assert.Equal(126, roster.GetLevel("Stackprime"));
        Assert.Equal("Cleric", roster.GetClass("Stackprime"));
    }

    [Fact]
    public void Levels_are_saved_and_a_nonsense_level_is_dropped()
    {
        var roster = new PlayerRoster();
        roster.LearnPlayer("Oodles", "Magician", 125);
        RosterSettings saved = roster.ToSettings();
        Assert.Equal(125, Assert.Single(saved.Players).Level);

        RosterSettings clean = PlayerRoster.Sanitize(new RosterSettings
        {
            Players = { new RosterPlayerEntry { Name = "Ream", Class = "Warrior", Level = 5000 } },
        });
        Assert.Equal(0, Assert.Single(clean.Players).Level);
    }
}
