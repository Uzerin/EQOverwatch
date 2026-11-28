using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release81Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Notice_lines_from_real_logs_are_read()
    {
        Assert.Equal(new NoticeLine(NoticeKind.FollowBroke, "Stackprime"), NoticeLineParser.Parse("You are no longer auto-following Stackprime."));
        Assert.Equal(new NoticeLine(NoticeKind.Linkdead, "Beatrixe"), NoticeLineParser.Parse("Beatrixe has gone Linkdead."));
        Assert.Equal(new NoticeLine(NoticeKind.Roll, "Honshu", 66, 0, 100),
            NoticeLineParser.Parse("**A Magic Die is rolled by Honshu. It could have been any number from 0 to 100, but this time it turned up a 66."));
        Assert.Equal(new NoticeLine(NoticeKind.Roll, "Handley", 21, 0, 1000),
            NoticeLineParser.Parse("**A Magic Die is rolled by Handley. It could have been any number from 0 to 1000, but this time it turned up a 21"));
        Assert.Equal(new NoticeLine(NoticeKind.PetBuffFaded, "Storm Strength"), NoticeLineParser.Parse("Your pet's Storm Strength spell has worn off."));
        Assert.Equal(new NoticeLine(NoticeKind.LevelUp, string.Empty, 60), NoticeLineParser.Parse("You have gained a level! Welcome to level 60!"));
        Assert.Equal(NoticeKind.None, NoticeLineParser.Parse("Raytlen tells NewPlayers:1, 'Your pet plays for you'").Kind);
        Assert.Equal(NoticeKind.StunEnded, NoticeLineParser.Parse("You are no longer stunned.").Kind); // read since 1.0.128 for the stun alert
    }

    [Fact]
    public void Bonus_xp_starts_when_the_last_kills_give_well_over_the_usual_and_ends_when_they_drop_back()
    {
        var detector = new BonusXpDetector();
        var changes = new List<bool>();
        detector.Changed += changes.Add;
        for (int i = 0; i < 4; i++)
        {
            detector.Observe(1.0, 10, T0.AddMinutes(i));
        }

        detector.Observe(5.0, 1, T0.AddMinutes(5)); // a usual from too few kills
        Assert.False(detector.Active);
        for (int i = 0; i < 3; i++)
        {
            detector.Observe(1.5, 10, T0.AddMinutes(6 + i));
        }

        Assert.True(detector.Active);
        Assert.Equal(1.5, detector.Boost, 3);
        for (int i = 0; i < 3; i++)
        {
            detector.Observe(1.0, 10, T0.AddMinutes(10 + i));
        }

        Assert.False(detector.Active);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Live_kills_are_compared_with_the_usual_experience_for_the_mob_at_that_level()
    {
        var store = new MobExperienceStore();
        store.SetLevelProvider((_, _) => 55);
        var ratios = new List<(double Ratio, int Usual)>();
        store.LiveKill += (ratio, usual, _) => ratios.Add((ratio, usual));
        DateTime t = T0;
        void Kill(double percent)
        {
            store.OnLine("Ream", "frostreaver", "You have slain a sarnak dragoon!", t);
            store.OnLine("Ream", "frostreaver", $"You gain party experience! ({percent.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}%)", t);
            store.OnLine("Ream", "frostreaver", "You receive 2 platinum as your split.", t.AddSeconds(30));
            t = t.AddMinutes(1);
        }

        Kill(1.0);
        Kill(1.0);
        Kill(2.0);
        Assert.Equal(2, ratios.Count);
        Assert.Equal(1.0, ratios[0].Ratio, 3);
        Assert.Equal((2.0, 2), (Math.Round(ratios[1].Ratio, 3), ratios[1].Usual));
    }

    [Fact]
    public void Overlay_notices_show_once_for_thirty_seconds()
    {
        var feed = new OverlayNoticeFeed();
        Assert.True(feed.Add("roll", "Roll  Honshu  66  (0–100)", T0));
        Assert.False(feed.Add("roll", "Roll  Honshu  66  (0–100)", T0.AddSeconds(1))); // another log saw the same roll
        Assert.True(feed.Add("linkdead", "Beatrixe went linkdead", T0.AddSeconds(2)));
        Assert.Equal("linkdead", feed.Current(T0.AddSeconds(3))[0].Kind);
        Assert.Single(feed.Current(T0.AddSeconds(31)));
        Assert.Empty(feed.Current(T0.AddSeconds(33)));
    }
}
