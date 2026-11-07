using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class AvoidanceAndTimelineTests
{
    private const string Giant = "a frost giant";

    private static CombatEvent Hit(string source, int amount, string time) =>
        new(At(time), CombatEventKind.Damage, source, Giant, amount, DamageKind.Melee, "slash");

    [Fact]
    public void Attacks_the_target_makes_that_miss_a_player_count_as_avoided()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", 500, "10:00:00"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:01"), CombatEventKind.Damage, Giant, "Ream", 200, DamageKind.Melee, "hit"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:02"), CombatEventKind.Miss, Giant, "Ream", 0, DamageKind.None, "hit"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:03"), CombatEventKind.Miss, Giant, "Ream", 0, DamageKind.None, "hit"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:04"), CombatEventKind.Damage, Giant, "Ream", 100, DamageKind.Melee, "hit"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        DamageTakenSnapshot ream = Assert.Single(fight.TakenRows);
        Assert.Equal(2, ream.Hits);
        Assert.Equal(2, ream.Avoided);
        Assert.Equal(50.0, ream.AvoidPercent, 3);
        Assert.Equal(300, ream.TotalDamage);
    }

    [Fact]
    public void A_tank_who_avoids_everything_still_shows_and_the_players_own_misses_are_not_avoidance()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", 500, "10:00:00"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:01"), CombatEventKind.Miss, Giant, "Gruk", 0, DamageKind.None, "hit"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:02"), CombatEventKind.Miss, "Stackprime", Giant, 0, DamageKind.None, "slash"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        DamageTakenSnapshot gruk = Assert.Single(fight.TakenRows);
        Assert.Equal("Gruk", gruk.Name);
        Assert.Equal(1, gruk.Avoided);
        Assert.Equal(100.0, gruk.AvoidPercent, 3);
        Assert.Equal(1, fight.Participants.Single(p => p.Name == "Stackprime").Misses);
    }

    [Fact]
    public void Avoidance_without_a_fight_in_progress_is_ignored()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:01"), CombatEventKind.Miss, Giant, "Gruk", 0, DamageKind.None, "hit"));
        Assert.Empty(tracker.Snapshot.Active);
    }

    [Fact]
    public void The_timeline_has_the_group_line_and_the_top_players_in_buckets()
    {
        var tracker = new LiveDpsTracker();
        for (int i = 0; i < 30; i++)
        {
            tracker.OnCombatEvent(Hit("Stackprime", 100, $"10:00:{i:00}"));
            tracker.OnCombatEvent(Hit("Ream", 50, $"10:00:{i:00}"));
        }

        DpsTimeline timeline = Assert.IsType<DpsTimeline>(Assert.Single(tracker.Snapshot.Active).Timeline);
        Assert.Equal(2, timeline.BucketSeconds);
        Assert.Equal(15, timeline.Count);
        Assert.All(timeline.Group, v => Assert.Equal(150.0, v, 3));
        Assert.Equal(new[] { "Stackprime", "Ream" }, timeline.Players.Select(p => p.Name).ToArray());
        Assert.All(timeline.Players[0].Dps, v => Assert.Equal(100.0, v, 3));
        Assert.All(timeline.Players[1].Dps, v => Assert.Equal(50.0, v, 3));
        Assert.Equal(150.0, timeline.Peak, 3);
    }

    [Fact]
    public void Only_the_top_five_players_get_a_line_and_a_very_short_fight_has_no_graph()
    {
        var tracker = new LiveDpsTracker();
        for (int i = 0; i < 20; i++)
        {
            for (int p = 0; p < 7; p++)
            {
                tracker.OnCombatEvent(Hit($"{(char)('A' + p)}ayer", 100 + (p * 10), $"10:00:{i:00}"));
            }
        }

        DpsTimeline timeline = Assert.IsType<DpsTimeline>(Assert.Single(tracker.Snapshot.Active).Timeline);
        Assert.Equal(5, timeline.Players.Count);
        Assert.Equal("Gayer", timeline.Players[0].Name);

        var brief = new LiveDpsTracker();
        brief.OnCombatEvent(Hit("Stackprime", 100, "10:00:00"));
        brief.OnCombatEvent(Hit("Stackprime", 100, "10:00:02"));
        Assert.Null(Assert.Single(brief.Snapshot.Active).Timeline);
    }

    [Fact]
    public void Identical_fights_produce_equal_snapshots_including_the_timeline()
    {
        static EncounterSnapshot Fight()
        {
            var tracker = new LiveDpsTracker();
            for (int i = 0; i < 12; i++)
            {
                tracker.OnCombatEvent(Hit("Stackprime", 100, $"10:00:{i:00}"));
            }

            return Assert.Single(tracker.Snapshot.Active);
        }

        Assert.Equal(Fight().Timeline, Fight().Timeline);
    }
}
