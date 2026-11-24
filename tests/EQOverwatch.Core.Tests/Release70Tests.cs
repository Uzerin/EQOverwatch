using System.IO;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Progress;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Session;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release70Tests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 22, 18, 3, DateTimeKind.Utc);

    [Fact]
    public void Written_files_are_noted_by_name_and_paths_are_refused()
    {
        var watch = new OutputFileWatch();
        watch.OnLine("Stackprime", "Outputfile Complete: Stackprime_frostreaver-WAR-Factions.txt", T0);
        watch.OnLine("Stackprime", "Outputfile Complete: ..\\evil.txt", T0);
        watch.OnLine("Stackprime", "You have entered The Maiden's Eye.", T0);

        Assert.Equal(("Stackprime", "Stackprime_frostreaver-WAR-Factions.txt"), Assert.Single(watch.TakeAll()));
        Assert.Empty(watch.TakeAll());
    }

    [Fact]
    public void A_guild_dump_is_read_and_the_raid_shows_groups_and_who_missed_it()
    {
        Assert.True(GuildRoster.TryParseFileName("Leadership Optional_frostreaver-20261118-221837.txt", out string guild, out string server, out DateTime taken));
        Assert.Equal(("Leadership Optional", "frostreaver", new DateTime(2026, 11, 18, 22, 18, 37)), (guild, server, taken));
        Assert.False(GuildRoster.TryParseFileName("RaidRoster_frostreaver-20261118-221837.txt", out _, out _, out _));

        List<GuildMember> members = GuildRoster.Parse(new[]
        {
            "Raideraa\t60\tCleric\tOfficer\t\t11/17/26\t\t\t\toff\toff\t0\t\t\t",
            "Raiderab\t60\tWarrior\tMember\t\t11/16/26\t\t\t\toff\toff\t0\t\t\t",
            "Raiderac\t60\tWizard\tMember\t\t08/01/26\t\t\t\toff\toff\t0\t\t\t",
            "Raiderad\t1\tMonk\tRecruit\t\t11/17/26\t\t\t\toff\toff\t0\t\t\t",
        });
        Assert.Equal(4, members.Count);
        Assert.Equal(("Officer", new DateTime(2026, 11, 17)), (members[0].Rank, members[0].LastOn!.Value));

        var raid = new RaidAttendance
        {
            Server = "frostreaver",
            Taken = new DateTime(2026, 11, 18, 20, 0, 0),
            Members = new List<RaidMember>
            {
                new(1, "Raideraa", 60, "Cleric", string.Empty),
                new(1, "Raiderae", 60, "Rogue", string.Empty),
                new(2, "Raideraf", 60, "Warrior", string.Empty),
            },
        };
        IReadOnlyList<RaidGroup> groups = RaidGroups.Of(raid.Members);
        Assert.Equal(new[] { (1, true), (2, false) }, groups.Select(g => (g.Group, g.HasHealer)));
        Assert.Equal("Raiderab", Assert.Single(RaidGroups.Missed(raid, members)).Name); // not the long-gone wizard or the level-1 monk
    }

    [Fact]
    public void A_faction_file_gives_standings_and_the_log_moves_them_on()
    {
        List<FactionStanding> rows = CharacterOutputParser.ParseFactions(new[] { "ID\tName\tStandingValue\tPointsToMax", "65\tBrownies of Faydwer\t1050\t950" });
        Assert.Equal(new FactionStanding(65, "Brownies of Faydwer", 1050, 950), Assert.Single(rows));
        Assert.Equal("Warmly", CharacterOutputParser.StandingName(1050));
        Assert.Equal("Ally", CharacterOutputParser.StandingName(1100));
        Assert.Equal("Scowling", CharacterOutputParser.StandingName(-751));
        Assert.True(CharacterOutputParser.TryParseFileName("Stackprime_frostreaver-WAR-Factions.txt", out OutputFileInfo info));
        Assert.Equal((OutputFileKind.Factions, "WAR"), (info.Kind, info.ClassCode));

        var store = new ProgressStore();
        store.OnLine("Stackprime", "frostreaver", "Your faction standing with Brownies of Faydwer has been adjusted by 10.", T0.AddMinutes(-5));
        store.OnLine("Stackprime", "frostreaver", "Your faction standing with Brownies of Faydwer has been adjusted by 25.", T0.AddMinutes(5));
        var changes = store.FactionChangesSince("Stackprime", "frostreaver", null, T0);
        Assert.Equal(25, changes["Brownies of Faydwer"].Change);
    }

    [Fact]
    public void Played_time_is_read_from_the_played_line()
    {
        Assert.Equal(13 * 1440 + 22 * 60 + 59, ProgressStore.PlayedMinutes("Total time playing Stackprime: 13 days, 22 hours, and 59 minutes"));
        Assert.Equal(65, ProgressStore.PlayedMinutes("Total time playing Ream: 1 hour and 5 minutes"));
        Assert.Null(ProgressStore.PlayedMinutes("Total time entitled on this account"));

        var store = new ProgressStore();
        store.OnLine("Stackprime", "frostreaver", "Total time playing Stackprime: 13 days, 22 hours, and 59 minutes", T0);
        Assert.Equal((13 * 1440 + 22 * 60 + 59L, T0), store.Played("Stackprime", "frostreaver"));
        Assert.Null(store.Played("Ream", "frostreaver"));
    }

    [Fact]
    public void Tradeskill_skill_ups_go_to_the_combine_in_the_same_second()
    {
        var store = new ProgressStore();
        void Line(string text, int second) => store.OnLine("Oodles", "frostreaver", text, T0.AddSeconds(second));
        Line("You have fashioned the items together to create something new: Metal Bits.", 0);
        Line("You lacked the skills to fashion Metal Bits.", 3);
        Line("You have become better at Blacksmithing! (5)", 3); // after its result
        Line("You have fashioned the items together to create something new: Metal Bits.", 6);
        Line("You have become better at Blacksmithing! (6)", 9); // before its result
        Line("You have fashioned the items together to create something new: Metal Bits.", 9);
        Line("You have become better at Offense! (100)", 10);

        TradeskillProgress bits = Assert.Single(store.Tradeskills("Oodles", "frostreaver"));
        Assert.Equal((4, 2), (bits.Combines, bits.SkillUps));
    }

    [Fact]
    public void Experience_is_counted_per_zone_and_the_next_aa_is_timed()
    {
        var day = new DayAccumulator("Stackprime");
        DateTime t = new(2026, 11, 18, 19, 0, 0);
        day.Line("You have entered The Maiden's Eye.", t);
        day.Line("You gain party experience! (2.5%)", t.AddMinutes(1));
        day.Line("You gain party experience! (1.5%)", t.AddMinutes(2));
        day.Line("You have entered Umbral Plains.", t.AddMinutes(3));
        day.Line("You gain experience! (1.0%)", t.AddMinutes(4));
        SessionSummary s = day.Summary(t.AddMinutes(5));
        Assert.Equal((4.0, 2), s.ZoneXp!["The Maiden's Eye"]);
        Assert.Equal((1.0, 1), s.ZoneXp["Umbral Plains"]);

        var xp = new ExperienceTracker(T0);
        for (int minute = 0; minute <= 40; minute++)
        {
            xp.OnLine("Stackprime", "You say, 'still here'", T0.AddMinutes(minute));
        }

        xp.OnLine("Stackprime", "You have gained an ability point!  You now have 1 ability point.", T0.AddMinutes(30));
        ExperienceRow row = Assert.Single(xp.Rows(T0.AddMinutes(40), (Func<string, string, int>?)null));
        Assert.Equal(TimeSpan.FromMinutes(30), row.NextAaIn); // one point an hour (40 minutes, 1 point), 10 minutes since it
    }

    [Fact]
    public void Boss_ability_timing_is_learned_after_two_kills()
    {
        var learner = new BossAbilityLearner();
        EncounterSnapshot Fight(int id, DateTime start) => new(id, "Emperor Ssraeshza", start, start.AddSeconds(120), 120, 100_000, false,
            Array.Empty<ParticipantSnapshot>(),
            BossCasts: new[]
            {
                new BossCastSnapshot(start.AddSeconds(10), "Emperor's Ward"),
                new BossCastSnapshot(start.AddSeconds(40), "Emperor's Ward"),
                new BossCastSnapshot(start.AddSeconds(70), "Emperor's Ward"),
            },
            Killed: true);

        var day = new DateTime(2026, 11, 16, 21, 0, 0);
        Assert.Equal(1, learner.Learn(new[] { Fight(1, day) }));
        Assert.Empty(learner.For("Emperor Ssraeshza"));
        Assert.Equal(0, learner.Learn(new[] { Fight(1, day) })); // the same fight again
        learner.Learn(new[] { Fight(2, day.AddDays(1)) });
        LearnedAbility ward = Assert.Single(learner.For("Emperor Ssraeshza"));
        Assert.Equal((30.0, 10.0), (ward.EverySeconds!.Value, ward.FirstSeconds!.Value));
    }
}
