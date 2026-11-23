using EQOverwatch.Core.Tlp;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class TlpServersTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Fact]
    public void Frostreaver_knows_its_next_unlock_cap_bonuses_and_open_expansions()
    {
        TlpServer frost = TlpServers.Find("Frostreaver")!;
        TlpUnlock next = frost.Next(Today)!;
        Assert.Equal(new[] { 5, 6 }, next.Expansions);
        Assert.Equal(new DateOnly(2027, 2, 4), next.Date);
        Assert.Equal(65, next.LevelCap);
        Assert.Equal(60, frost.LevelCap(Today));
        Assert.Equal(new[] { 1, 2, 3, 4 }, frost.OpenExpansions(Today).Order());
        Assert.Equal(new[] { "Loot 125%", "Faction 200%", "AA EXP 175%" }, frost.CurrentBonuses(Today));
        Assert.Equal(100, frost.ExperienceOn(Today));
        Assert.Equal(115, frost.ExperienceOn(new DateOnly(2027, 5, 1)));
        Assert.Contains("EXP 200%", frost.CurrentBonuses(new DateOnly(2030, 4, 1)));
        Assert.True(frost.HasBonuses && frost.Legacy && frost.ResourceHunter);
    }

    [Fact]
    public void Cadence_servers_use_the_posted_days_and_work_out_the_rest()
    {
        TlpServer oak = TlpServers.Find("oakwynd")!;
        Assert.Equal(new DateOnly(2025, 11, 6), oak.Unlocks.Single(u => u.Expansions.Contains(17)).Date);
        TlpUnlock next = oak.Next(Today)!;
        Assert.Equal(new[] { 23 }, next.Expansions); // The Broken Mirror after The Darkened Sea
        Assert.True(next.Estimated);
        Assert.Equal(new DateOnly(2027, 1, 28), next.Date); // The Darkened Sea raised the cap: 12 weeks
        Assert.Null(oak.Unlocks.Single(u => u.Expansions.Contains(5)).Date); // a past day no post gives is not guessed

        Assert.Same(TlpServers.Find("teek"), TlpServers.Find("Tormax"));
        Assert.Equal(25, TlpServers.Find("yelinak")!.ExperienceOn(new DateOnly(2022, 6, 1)));
        Assert.Equal(100, TlpServers.Find("yelinak")!.ExperienceOn(Today));
        Assert.Equal(120, TlpServers.Find("vaniki")!.LevelCap(Today));
        Assert.False(TlpServers.Find("lethar")!.HasBonuses);
        Assert.Null(TlpServers.Find("bristlebane"));
        Assert.Null(TlpServers.Find("agnarr")!.ExperienceOn(Today));
    }
}

public sealed class BonusSizeAndChallengeTests
{
    [Fact]
    public void Shared_sizes_are_read_for_a_day_or_a_zone()
    {
        var day = EQOverwatch.Core.ResourceHunter.HttpResourceHunterService.ParseSizes(
            """{"day":"2026-11-22","sizes":[{"z":"Velketor's Labyrinth","k":"xp","m":42.5,"n":6},{"z":"Bad","k":"loot","m":1,"n":1}]}""", "2026-11-22", null);
        var one = Assert.Single(day);
        Assert.Equal(("2026-11-22", "Velketor's Labyrinth", "xp", 42.5, 6), (one.Day, one.Zone, one.Kind, one.Percent, one.Reports));

        var zone = EQOverwatch.Core.ResourceHunter.HttpResourceHunterService.ParseSizes(
            """{"zone":"Velketor's Labyrinth","sizes":[{"d":"2026-11-12","k":"aa","m":30,"n":4}]}""", null, "Velketor's Labyrinth");
        Assert.Equal(("2026-11-12", "Velketor's Labyrinth", "aa"), (zone[0].Day, zone[0].Zone, zone[0].Kind));
    }

    [Fact]
    public void Challenges_match_achievements_by_their_boss()
    {
        Assert.Equal(new[] { "Lethar" }, new TlpChallenge(65, "", "Ashengate - Lethar", "").Keywords);
        Assert.Equal(new[] { "Queen Pyrilonis", "King Gelaqua" }, new TlpChallenge(60, "", "Queen Pyrilonis or King Gelaqua", "").Keywords);
        Assert.Equal(new[] { "Curse Reborn" }, new TlpChallenge(55, "", "The Curse Reborn (raid)", "").Keywords);
        Assert.True(new TlpChallenge(40, "", "Cazic-Thule", "").Matches("Challenge: Cazic-Thule by Level 40"));
        Assert.Equal(33, TlpServers.Find("frostreaver")!.ChallengeList!.Count);
    }
}
